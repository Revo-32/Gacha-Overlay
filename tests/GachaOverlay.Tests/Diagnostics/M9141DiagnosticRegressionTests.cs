using System.IO.Compression;
using System.Text;
using System.Text.Json;
using GachaOverlay.Core.Logging;
using GachaOverlay.Core.Sales;
using GachaOverlay.Infrastructure.Diagnostics;
using GachaOverlay.Infrastructure.Logging;
using GachaOverlay.Tests.TestSupport;

namespace GachaOverlay.Tests.Diagnostics;

public sealed class M9141DiagnosticRegressionTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    public async Task Numeric_sales_state_and_credential_presence_remain_typed(int state, bool hasCredential)
    {
        using var temporary = new TemporaryDirectory();
        var request = Request(temporary.File("diagnostics.zip"));
        var artifacts = (Dictionary<string, object>)request.JsonArtifacts;
        artifacts["diagnostic-summary.json"] = new
        {
            Sales = SalesFeatureHealthSnapshot.Disabled with { State = (SalesFeatureHealthState)state },
            Remote = new { HasProtectedCredential = hasCredential },
        };
        var result = await Exporter().ExportAsync(request);
        Assert.True(result.IsSuccess);
        using var zip = ZipFile.OpenRead(request.DestinationPath);
        using var json = JsonDocument.Parse(Read(zip, "diagnostic-summary.json"));
        Assert.Equal(state, json.RootElement.GetProperty("sales").GetProperty("state").GetInt32());
        Assert.Equal(hasCredential, json.RootElement.GetProperty("remote").GetProperty("hasProtectedCredential").GetBoolean());
        Assert.Equal("unavailable", json.RootElement.GetProperty("optionalData").GetProperty("logs").GetProperty("status").GetString());
    }

    [Fact]
    public async Task OAuth_remote_secrets_bodies_and_identifiers_are_removed_recursively()
    {
        using var temporary = new TemporaryDirectory();
        var request = Request(temporary.File("diagnostics.zip"));
        ((Dictionary<string, object>)request.JsonArtifacts)["diagnostic-summary.json"] = new
        {
            State = "sentinel-state",
            Code = "sentinel-code",
            ClientSecret = "sentinel-secret",
            OAuthAccessToken = "sentinel-access",
            RefreshToken = "sentinel-refresh",
            LoginClaimSecret = "sentinel-claim",
            CodeVerifier = "sentinel-verifier",
            Authorization = "Bearer sentinel-remote",
            Credential = "sentinel-credential",
            Content = "sentinel body with whitespace and \"quotes\" 한글",
            RawHttpPayload = new { Unnamed = "sentinel-raw" },
            HostId = 711618877947379794L,
            Children = new object[] { new { Token = "sentinel-token", Body = new[] { "sentinel-body" } } },
            SafeText = "callback https://example.test/auth/discord/callback?code=sentinel-query&state=sentinel-query-state",
        };
        Assert.True((await Exporter().ExportAsync(request)).IsSuccess);
        using var zip = ZipFile.OpenRead(request.DestinationPath);
        foreach (var entry in zip.Entries)
        {
            var content = Read(zip, entry.FullName);
            Assert.DoesNotContain("sentinel", content, StringComparison.Ordinal);
            Assert.DoesNotContain("711618877947379794", content, StringComparison.Ordinal);
            using var json = JsonDocument.Parse(content);
        }
    }

    [Fact]
    public async Task Historical_credential_filename_mentions_do_not_fail_active_log_export()
    {
        using var temporary = new TemporaryDirectory();
        var logs = temporary.File("logs");
        using var logger = new RollingFileLogger(logs);
        logger.Information("AUTH", "Retired discord-client-secret.dat and discord-oauth-token.dat; remote-access-token.dat is protected.");
        var request = Request(temporary.File("diagnostics.zip")) with { LogDirectory = logs };
        Assert.True((await Exporter().ExportAsync(request)).IsSuccess);
        using var zip = ZipFile.OpenRead(request.DestinationPath);
        var text = Read(zip, "logs/log-1.txt");
        Assert.Contains("Retired [REDACTED-FILE]", text);
        Assert.DoesNotContain(".dat", text);
    }

    [Fact]
    public async Task Optional_log_with_unlabelled_credential_blob_is_explicitly_skipped()
    {
        using var temporary = new TemporaryDirectory();
        var logs = temporary.File("logs");
        Directory.CreateDirectory(logs);
        await File.WriteAllTextAsync(Path.Combine(logs, "gacha-overlay.log"), "AQAAANCMnd8BFdERjHoAwE/Cl+sBA-synthetic-only");
        var request = Request(temporary.File("diagnostics.zip")) with { LogDirectory = logs };
        Assert.True((await Exporter().ExportAsync(request)).IsSuccess);
        using var zip = ZipFile.OpenRead(request.DestinationPath);
        Assert.Null(zip.GetEntry("logs/log-1.txt"));
        using var json = JsonDocument.Parse(Read(zip, "diagnostic-summary.json"));
        var logSummary = json.RootElement.GetProperty("optionalData").GetProperty("logs");
        Assert.Equal("skipped", logSummary.GetProperty("status").GetString());
        Assert.Equal("privacyBoundary", logSummary.GetProperty("skipped")[0].GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Utf16_optional_log_is_bounded_and_sanitized_without_reading_unallowlisted_files()
    {
        using var temporary = new TemporaryDirectory();
        var logs = temporary.File("logs");
        Directory.CreateDirectory(logs);
        await File.WriteAllTextAsync(Path.Combine(logs, "gacha-overlay.log"),
            "한글 정상 로그\nhost_id=711618877947379794\ncontent=private message including spaces\n", Encoding.Unicode);
        await File.WriteAllTextAsync(Path.Combine(logs, "remote-access-token.dat"), "never-read-credential");
        var request = Request(temporary.File("diagnostics.zip")) with { LogDirectory = logs };
        Assert.True((await Exporter().ExportAsync(request)).IsSuccess);
        using var zip = ZipFile.OpenRead(request.DestinationPath);
        var text = Read(zip, "logs/log-1.txt");
        Assert.Contains("한글 정상 로그", text);
        Assert.DoesNotContain("711618877947379794", text);
        Assert.DoesNotContain("private", text);
        Assert.DoesNotContain("message including spaces", text);
        Assert.DoesNotContain(zip.Entries, entry => entry.FullName.EndsWith(".dat", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Oversized_utf16_tail_keeps_encoding_and_drops_partial_first_line()
    {
        using var temporary = new TemporaryDirectory();
        var logs = temporary.File("logs");
        Directory.CreateDirectory(logs);
        await File.WriteAllTextAsync(Path.Combine(logs, "gacha-overlay.log"),
            "content=" + new string('x', 1_200_000) + " private-partial-line\n정상 로그\nclient_secret=tail-secret", Encoding.Unicode);
        var request = Request(temporary.File("diagnostics.zip")) with { LogDirectory = logs };
        Assert.True((await Exporter().ExportAsync(request)).IsSuccess);
        using var zip = ZipFile.OpenRead(request.DestinationPath);
        var text = Read(zip, "logs/log-1.txt");
        Assert.Contains("정상 로그", text);
        Assert.DoesNotContain("private-partial-line", text);
        Assert.DoesNotContain("tail-secret", text);
        Assert.DoesNotContain('\0', text);
    }

    [Fact]
    public async Task Missing_required_snapshot_fails_instead_of_silently_omitting_it()
    {
        using var temporary = new TemporaryDirectory();
        var request = Request(temporary.File("diagnostics.zip"));
        ((Dictionary<string, object>)request.JsonArtifacts).Remove("health-snapshot.json");
        var result = await Exporter().ExportAsync(request);
        Assert.Equal(DiagnosticBundleExportStatus.Failed, result.Status);
        Assert.Equal(DiagnosticExportStage.ValidateEntries, result.FailureStage);
        Assert.False(File.Exists(request.DestinationPath));
    }

    [Fact]
    public async Task Locked_destination_preserves_previous_zip_and_cleans_only_owned_temporary_file()
    {
        using var temporary = new TemporaryDirectory();
        var request = Request(temporary.File("diagnostics.zip"));
        var exporter = Exporter();
        Assert.True((await exporter.ExportAsync(request)).IsSuccess);
        var before = await File.ReadAllBytesAsync(request.DestinationPath);
        var unrelated = temporary.File("unrelated.tmp");
        await File.WriteAllTextAsync(unrelated, "keep");
        using (var locked = new FileStream(request.DestinationPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = await exporter.ExportAsync(request);
            Assert.Equal(DiagnosticExportStage.FinalizeArchive, result.FailureStage);
            Assert.Equal(DiagnosticBundleExportStatus.Failed, result.Status);
        }
        Assert.Equal(before, await File.ReadAllBytesAsync(request.DestinationPath));
        Assert.Equal(new[] { unrelated }, Directory.GetFiles(temporary.Path, "*.tmp"));
        Assert.True((await exporter.ExportAsync(request)).IsSuccess);
    }

    [Fact]
    public async Task Relative_destination_is_rejected_without_current_directory_writes()
    {
        var result = await Exporter().ExportAsync(Request("must-not-write.zip"));
        Assert.Equal(DiagnosticBundleExportStatus.Failed, result.Status);
        Assert.Equal(DiagnosticExportStage.WriteArchive, result.FailureStage);
    }

    [Fact]
    public async Task Repeated_export_and_missing_optional_sources_are_safe()
    {
        using var temporary = new TemporaryDirectory();
        var request = Request(temporary.File("diagnostics.zip")) with
        {
            LogDirectory = temporary.File("absent-logs"),
            CrashSummaryPath = temporary.File("absent-crash.json"),
        };
        var exporter = Exporter();
        for (var i = 0; i < 5; i++)
        {
            Assert.True((await exporter.ExportAsync(request)).IsSuccess);
            using var zip = ZipFile.OpenRead(request.DestinationPath);
            Assert.Equal(6, zip.Entries.Count);
            Assert.All(zip.Entries, entry => { using var json = JsonDocument.Parse(Read(zip, entry.FullName)); });
        }
        Assert.Empty(Directory.GetFiles(temporary.Path, "*.tmp"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{ incomplete state=")]
    [InlineData("state=[REDACTED] code=[REDACTED]")]
    [InlineData("LSOAuthClaim [REDACTED]")]
    [InlineData("https://example.test/auth/discord/callback?code=private&state=private")]
    [InlineData("{\"code\":\"secret with spaces and \\\"quote\\\"\",\"state\":null}")]
    [InlineData("한글 \ud800 malformed unicode code=secret")]
    [InlineData("client_secret='sentinel with spaces'")]
    [InlineData("credential='sentinel with spaces'")]
    public void Text_redactors_are_non_throwing_and_idempotent(string? input)
    {
        var oauth = OAuthDataRedactor.Sanitize(input!);
        Assert.Equal(oauth, OAuthDataRedactor.Sanitize(oauth));
        var sensitive = SensitiveDataRedactor.Sanitize(input!);
        Assert.Equal(sensitive, SensitiveDataRedactor.Sanitize(sensitive));
        Assert.DoesNotContain("secret with spaces", sensitive ?? "");
        Assert.DoesNotContain("sentinel with spaces", sensitive ?? "");
        Assert.DoesNotContain("with spaces", sensitive ?? "");
    }

    [Fact]
    public void Large_input_is_fail_closed_and_idempotent()
    {
        var text = new string('x', 5 * 1024 * 1024) + " code=never-leak";
        Assert.Equal("[REDACTED]", OAuthDataRedactor.Sanitize(text));
        Assert.Equal("[REDACTED]", SensitiveDataRedactor.Sanitize(text));
    }

    private static DiagnosticBundleExporter Exporter() => new(NullAppLogger.Instance);

    private static DiagnosticBundleRequest Request(string path) => new(path,
        DiagnosticBundleExporter.AllowedJsonEntryNames.ToDictionary(name => name, _ => (object)new { Version = "test" }));

    private static string Read(ZipArchive zip, string name)
    {
        using var reader = new StreamReader(zip.GetEntry(name)!.Open());
        return reader.ReadToEnd();
    }
}
