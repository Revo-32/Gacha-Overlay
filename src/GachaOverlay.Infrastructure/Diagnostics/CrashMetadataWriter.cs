using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using GachaOverlay.Core.Logging;
using GachaOverlay.Infrastructure.Logging;

namespace GachaOverlay.Infrastructure.Diagnostics;

public sealed class CrashMetadataWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _path;
    private readonly IAppLogger _logger;

    public CrashMetadataWriter(string path, IAppLogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _logger = logger ?? NullAppLogger.Instance;
    }

    public bool TryWrite(Exception exception, string subsystemContext)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var temporary = $"{_path}.{Environment.ProcessId}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var metadata = new
            {
                Timestamp = DateTimeOffset.UtcNow,
                AppVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "unknown",
                ExceptionType = exception.GetType().FullName ?? exception.GetType().Name,
                SanitizedStack = SensitiveDataRedactor.Sanitize(
                    new StackTrace(exception, fNeedFileInfo: false).ToString()),
                SubsystemContext = SensitiveDataRedactor.Sanitize(subsystemContext),
            };
            File.WriteAllText(temporary, JsonSerializer.Serialize(metadata, JsonOptions));
            File.Move(temporary, _path, overwrite: true);
            return true;
        }
        catch (Exception writeException)
        {
            _logger.Warning(
                "CRASH",
                $"Crash metadata could not be persisted ({writeException.GetType().Name}).");
            return false;
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch
            {
            }
        }
    }
}
