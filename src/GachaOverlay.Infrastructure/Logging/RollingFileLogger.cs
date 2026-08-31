using System.Diagnostics;
using System.Text;
using GachaOverlay.Core.Logging;

namespace GachaOverlay.Infrastructure.Logging;

public sealed class RollingFileLogger : IAppLogger, IDisposable
{
    private readonly object _sync = new();
    private readonly string _filePath;
    private readonly long _maxFileBytes;
    private readonly int _maxFileCount;
    private FileStream? _stream;
    private StreamWriter? _writer;
    private bool _disposed;

    public RollingFileLogger(
        string logDirectory,
        long maxFileBytes = 2 * 1024 * 1024,
        int maxFileCount = 5)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);

        if (maxFileBytes < 128)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFileBytes));
        }

        if (maxFileCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFileCount));
        }

        _filePath = Path.Combine(logDirectory, "gacha-overlay.log");
        _maxFileBytes = maxFileBytes;
        _maxFileCount = maxFileCount;
    }

    public void Information(string category, string message) =>
        Write("INF", category, message, null);

    public void Warning(string category, string message) =>
        Write("WRN", category, message, null);

    public void Error(string category, string message, Exception? exception = null) =>
        Write("ERR", category, message, exception);

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CloseWriter();
        }
    }

    private void Write(string level, string category, string message, Exception? exception)
    {
        var line = FormatLine(level, category, message, exception);
        var entrySize = Encoding.UTF8.GetByteCount(line + Environment.NewLine);

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                EnsureWriter();
                if (_stream is null || _writer is null)
                {
                    return;
                }

                if (_stream.Length > 0 && _stream.Length + entrySize > _maxFileBytes)
                {
                    CloseWriter();
                    RotateFiles();
                    EnsureWriter();
                }

                _writer?.WriteLine(line);
            }
            catch (Exception loggingException)
            {
                CloseWriter();
                Debug.WriteLine($"Logging failed: {loggingException}");
            }
        }
    }

    private void EnsureWriter()
    {
        if (_writer is not null)
        {
            return;
        }

        var directory = Path.GetDirectoryName(_filePath)
            ?? throw new InvalidOperationException("The log directory is invalid.");

        Directory.CreateDirectory(directory);
        _stream = new FileStream(
            _filePath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan);
        _writer = new StreamWriter(_stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true,
        };
    }

    private void RotateFiles()
    {
        if (_maxFileCount == 1)
        {
            File.Delete(_filePath);
            return;
        }

        for (var index = _maxFileCount - 1; index >= 1; index--)
        {
            var source = index == 1 ? _filePath : $"{_filePath}.{index - 1}";
            var destination = $"{_filePath}.{index}";

            if (File.Exists(destination))
            {
                File.Delete(destination);
            }

            if (File.Exists(source))
            {
                File.Move(source, destination);
            }
        }
    }

    private void CloseWriter()
    {
        _writer?.Dispose();
        _writer = null;
        _stream?.Dispose();
        _stream = null;
    }

    private static string FormatLine(
        string level,
        string category,
        string message,
        Exception? exception)
    {
        var safeCategory = SensitiveDataRedactor.Sanitize(
            CollapseLineBreaks(category)).ToUpperInvariant();
        var safeMessage = SensitiveDataRedactor.Sanitize(CollapseLineBreaks(message));
        var exceptionText = exception is null
            ? string.Empty
            : $" | {SensitiveDataRedactor.Sanitize(CollapseLineBreaks(exception.ToString()))}";

        return $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] [{safeCategory}] {safeMessage}{exceptionText}";
    }

    private static string CollapseLineBreaks(string value) =>
        value.Replace("\r\n", " | ", StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Replace('\n', ' ');
}
