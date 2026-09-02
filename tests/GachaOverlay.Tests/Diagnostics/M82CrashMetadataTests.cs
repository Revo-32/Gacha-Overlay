using GachaOverlay.Infrastructure.Diagnostics;
using GachaOverlay.Tests.TestSupport;

namespace GachaOverlay.Tests.Diagnostics;

public sealed class M82CrashMetadataTests
{
    [Fact]
    public void Crash_metadata_excludes_exception_message_and_redacts_context()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.File("crash-summary.json");
        var writer = new CrashMetadataWriter(path);
        var exception = CaptureException("private discord body access_token=top-secret");

        Assert.True(writer.TryWrite(exception, "authorization=Bearer token-value"));

        var content = File.ReadAllText(path);
        Assert.DoesNotContain("private discord body", content, StringComparison.Ordinal);
        Assert.DoesNotContain("top-secret", content, StringComparison.Ordinal);
        Assert.DoesNotContain("token-value", content, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", content, StringComparison.Ordinal);
        Assert.Contains(typeof(InvalidOperationException).FullName!, content, StringComparison.Ordinal);
    }

    private static Exception CaptureException(string message)
    {
        try
        {
            throw new InvalidOperationException(message);
        }
        catch (Exception exception)
        {
            return exception;
        }
    }
}
