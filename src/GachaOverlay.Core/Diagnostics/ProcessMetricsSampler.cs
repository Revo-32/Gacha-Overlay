using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace GachaOverlay.Core.Diagnostics;

public sealed record ProcessMetricsSnapshot(
    DateTimeOffset CapturedAt,
    double UptimeSeconds,
    double? CpuPercent,
    long TotalWorkingSetBytes,
    long? PrivateWorkingSetBytes,
    long PrivateCommitBytes,
    int? HandleCount,
    int ThreadCount,
    long ManagedHeapBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    int? GdiObjectCount,
    int? UserObjectCount)
{
    [JsonIgnore]
    public long WorkingSetBytes => TotalWorkingSetBytes;

    [JsonIgnore]
    public long PrivateBytes => PrivateCommitBytes;

    [JsonIgnore]
    public long GcTotalMemoryBytes => ManagedHeapBytes;
}

public sealed class ProcessMetricsSampler
{
    private readonly object _sync = new();
    private readonly Func<DateTimeOffset> _clock;
    private readonly DateTimeOffset _startedAt;
    private DateTimeOffset? _lastSampleAt;
    private TimeSpan _lastCpuTime;

    public ProcessMetricsSampler(Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _startedAt = _clock();
    }

    public ProcessMetricsSnapshot Sample()
    {
        lock (_sync)
        {
            var now = _clock();
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            var cpuTime = process.TotalProcessorTime;
            double? cpuPercent = null;
            if (_lastSampleAt.HasValue)
            {
                var wallSeconds = (now - _lastSampleAt.Value).TotalSeconds;
                var cpuSeconds = (cpuTime - _lastCpuTime).TotalSeconds;
                if (wallSeconds > 0 && cpuSeconds >= 0)
                {
                    cpuPercent = Math.Max(
                        0,
                        cpuSeconds / (wallSeconds * Environment.ProcessorCount) * 100d);
                }
            }

            _lastSampleAt = now;
            _lastCpuTime = cpuTime;
            return new ProcessMetricsSnapshot(
                now,
                Math.Max(0, (now - _startedAt).TotalSeconds),
                cpuPercent,
                Math.Max(0, process.WorkingSet64),
                TryGetPrivateWorkingSet(process.Handle),
                Math.Max(0, process.PrivateMemorySize64),
                TryRead(() => process.HandleCount),
                Math.Max(0, TryRead(() => process.Threads.Count) ?? 0),
                Math.Max(0, GC.GetTotalMemory(forceFullCollection: false)),
                GC.CollectionCount(0),
                GC.CollectionCount(1),
                GC.CollectionCount(2),
                TryGetGuiResources(process.Handle, 0),
                TryGetGuiResources(process.Handle, 1));
        }
    }

    private static int? TryRead(Func<int> read)
    {
        try
        {
            return read();
        }
        catch
        {
            return null;
        }
    }

    private static int? TryGetGuiResources(IntPtr processHandle, uint flag)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            var count = GetGuiResources(processHandle, flag);
            return count == 0 ? null : checked((int)count);
        }
        catch
        {
            return null;
        }
    }

    private static long? TryGetPrivateWorkingSet(IntPtr processHandle)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            var counters = new ProcessMemoryCountersEx2
            {
                Size = checked((uint)Marshal.SizeOf<ProcessMemoryCountersEx2>()),
            };
            return GetProcessMemoryInfo(processHandle, ref counters, counters.Size)
                ? checked((long)counters.PrivateWorkingSetSize)
                : null;
        }
        catch (Exception exception) when (
            exception is DllNotFoundException or EntryPointNotFoundException or
                OverflowException or TypeLoadException)
        {
            return null;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetGuiResources(IntPtr hProcess, uint uiFlags);

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessMemoryInfo(
        IntPtr process,
        ref ProcessMemoryCountersEx2 counters,
        uint size);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCountersEx2
    {
        public uint Size;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
        public nuint PrivateUsage;
        public nuint PrivateWorkingSetSize;
        public ulong SharedCommitUsage;
    }
}
