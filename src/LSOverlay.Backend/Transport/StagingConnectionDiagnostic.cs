using System.Diagnostics;

namespace LSOverlay.Backend.Transport;

// Temporary bounded diagnostics: no identities, credentials, payloads or raw exceptions.
internal static class StagingConnectionDiagnostic
{
    private static readonly bool Enabled = string.Equals(
        Environment.GetEnvironmentVariable("RAILWAY_ENVIRONMENT_NAME"), "staging", StringComparison.Ordinal);
    private static int _remaining = 1000;
    private static long _next;

    public static IDisposable Stage(string name) => Enabled ? new Timing(name) : Noop.Instance;

    public static Task<T> MeasureAsync<T>(string name, Task<T> task) =>
        Enabled ? MeasureEnabledAsync(name, task) : task;

    private static async Task<T> MeasureEnabledAsync<T>(string name, Task<T> task)
    {
        using var timing = Stage(name);
        return await task.ConfigureAwait(false);
    }

    private sealed class Noop : IDisposable
    {
        public static readonly Noop Instance = new();
        public void Dispose() { }
    }

    public static void Note(string name)
    {
        if (Enabled && Interlocked.Decrement(ref _remaining) >= 0)
            Console.WriteLine($"STAGING_DIAG at={DateTimeOffset.UtcNow:O} {name}");
    }

    private sealed class Timing : IDisposable
    {
        private readonly string _name;
        private readonly long _id = Interlocked.Increment(ref _next);
        private readonly long _started = Stopwatch.GetTimestamp();
        public Timing(string name)
        {
            _name = name;
            Note($"stage={name} id={_id} start");
        }
        public void Dispose() => Note($"stage={_name} id={_id} end ms={Stopwatch.GetElapsedTime(_started).TotalMilliseconds:F3}");
    }
}
