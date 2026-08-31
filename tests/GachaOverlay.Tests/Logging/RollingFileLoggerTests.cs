using GachaOverlay.Infrastructure.Logging;
using GachaOverlay.Tests.TestSupport;

namespace GachaOverlay.Tests.Logging;

public sealed class RollingFileLoggerTests
{
    [Fact]
    public void Logger_WritesLevelsAndExceptionDetails()
    {
        using var directory = new TemporaryDirectory();
        using (var logger = new RollingFileLogger(directory.Path))
        {
            logger.Information("APP", "Started");
            logger.Warning("SETTINGS", "Fallback used");
            logger.Error("APP", "Failure", new InvalidOperationException("sample"));
        }

        var content = System.IO.File.ReadAllText(directory.File("gacha-overlay.log"));
        Assert.Contains("[INF] [APP] Started", content);
        Assert.Contains("[WRN] [SETTINGS] Fallback used", content);
        Assert.Contains("[ERR] [APP] Failure", content);
        Assert.Contains("InvalidOperationException", content);
    }

    [Fact]
    public void Logger_RotationKeepsFileCountBounded()
    {
        using var directory = new TemporaryDirectory();
        using (var logger = new RollingFileLogger(
                   directory.Path,
                   maxFileBytes: 256,
                   maxFileCount: 3))
        {
            for (var index = 0; index < 100; index++)
            {
                logger.Information("TEST", $"Entry {index:D3} {new string('x', 64)}");
            }
        }

        var logFiles = Directory.GetFiles(directory.Path, "gacha-overlay.log*");
        Assert.InRange(logFiles.Length, 1, 3);
        Assert.All(logFiles, file => Assert.True(new FileInfo(file).Length > 0));
    }

    [Theory]
    [InlineData("client_secret=client-secret-value", "client-secret-value")]
    [InlineData("access_token: access-token-value", "access-token-value")]
    [InlineData("refreshToken=refresh-token-value", "refresh-token-value")]
    [InlineData("Authorization: Bearer authorization-value", "authorization-value")]
    [InlineData("credential='credential-value'", "credential-value")]
    [InlineData("rawCredentialBlob=protected-value", "protected-value")]
    [InlineData("{\"content\":\"private message body\"}", "private message body")]
    public void Logger_CentrallyRedactsSensitiveFields(string message, string secret)
    {
        using var directory = new TemporaryDirectory();
        using (var logger = new RollingFileLogger(directory.Path))
        {
            logger.Information("SECURITY", message);
        }

        var content = File.ReadAllText(directory.File("gacha-overlay.log"));
        Assert.DoesNotContain(secret, content, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Logger_RedactsSecretsEmbeddedInExceptionText()
    {
        using var directory = new TemporaryDirectory();
        using (var logger = new RollingFileLogger(directory.Path))
        {
            logger.Error(
                "AUTH",
                "Authentication failed",
                new InvalidOperationException("access_token=exception-token-value"));
        }

        var content = File.ReadAllText(directory.File("gacha-overlay.log"));
        Assert.DoesNotContain("exception-token-value", content, StringComparison.Ordinal);
        Assert.Contains("access_token=[REDACTED]", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Redaction_is_idempotent_for_an_already_redacted_unquoted_field()
    {
        using var directory = new TemporaryDirectory();
        using (var logger = new RollingFileLogger(directory.Path))
        {
            logger.Information("SECURITY", "content=private-message-body");
        }

        var first = File.ReadAllText(directory.File("gacha-overlay.log"));
        using (var logger = new RollingFileLogger(directory.Path))
        {
            logger.Information("SECURITY", first);
        }

        var second = File.ReadAllText(directory.File("gacha-overlay.log"));
        Assert.DoesNotContain("private-message-body", second, StringComparison.Ordinal);
        Assert.DoesNotContain("[REDACTED]REDACTED]", second, StringComparison.Ordinal);
        Assert.Contains("content=[REDACTED]", second, StringComparison.OrdinalIgnoreCase);
    }
}
