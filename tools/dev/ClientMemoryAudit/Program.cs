using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Text.Json;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;

// Only opt-in synthetic testhost checkpoints are accepted. Never attach to Discord/game/user app.
// Heap mode requests a diagnostic GC, so it is a SEPARATE run, never part of performance comparisons.
if (args.Length != 3 || args[1] is not ("heap" or "allocation"))
    throw new ArgumentException("Usage: ClientMemoryAudit <synthetic-ready.json> <heap|allocation> <new-output.json>");
var readyPath = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[2]);
if (File.Exists(output)) throw new IOException("Output already exists.");
using var ready = JsonDocument.Parse(File.ReadAllText(readyPath));
var info = ready.RootElement;
if (info.GetProperty("Boundary").GetString() != "LSOverlay synthetic testhost; no ApplicationHost/credentials/network")
    throw new InvalidOperationException("Not an authorized synthetic checkpoint.");
using var process = Process.GetProcessById(info.GetProperty("ProcessId").GetInt32());
var expectedPath = info.GetProperty("Executable").GetString();
var expectedStart = info.GetProperty("ProcessStartedUtc").GetDateTimeOffset();
if (!string.Equals(process.MainModule?.FileName, expectedPath, StringComparison.OrdinalIgnoreCase) ||
    !process.ProcessName.StartsWith("testhost", StringComparison.OrdinalIgnoreCase) ||
    process.StartTime.ToUniversalTime() != expectedStart.UtcDateTime)
    throw new InvalidOperationException("Synthetic process identity mismatch.");

var nodes = new Dictionary<ulong, (ulong Type, ulong Bytes)>();
var types = new Dictionary<ulong, string>();
var allocation = new Dictionary<string, (long Ticks, long Bytes)>(StringComparer.Ordinal);
var keyword = ClrTraceEventParser.Keywords.GC | ClrTraceEventParser.Keywords.Type;
if (args[1] == "heap") keyword |= ClrTraceEventParser.Keywords.GCHeapDump |
    ClrTraceEventParser.Keywords.GCHeapCollect | ClrTraceEventParser.Keywords.GCHeapAndTypeNames;
using var session = new DiagnosticsClient(process.Id).StartEventPipeSession(
    [new EventPipeProvider("Microsoft-Windows-DotNETRuntime", EventLevel.Verbose, (long)keyword)], false, 64);
using var source = new EventPipeEventSource(session.EventStream);
source.Clr.TypeBulkType += data =>
{
    for (var i = 0; i < data.Count; i++)
    {
        var value = data.Values(i);
        types[value.TypeID] = value.TypeName;
    }
};
source.Clr.GCBulkNode += data =>
{
    for (var i = 0; i < data.Count; i++)
    {
        var value = data.Values(i);
        if (nodes.Count >= 2_000_000) throw new InvalidOperationException("Synthetic heap safety bound exceeded.");
        nodes[value.Address] = (value.TypeID, value.Size);
    }
};
source.Clr.GCAllocationTick += data =>
{
    var name = data.TypeName ?? "<unknown>";
    var previous = allocation.GetValueOrDefault(name);
    allocation[name] = (previous.Ticks + 1, previous.Bytes + data.AllocationAmount64);
};
var clock = Stopwatch.StartNew();
var readTask = Task.Run(() => source.Process());
await Task.Delay(TimeSpan.FromSeconds(10));
session.Stop();
await readTask.WaitAsync(TimeSpan.FromSeconds(15));
if (source.EventsLost != 0) throw new InvalidOperationException("Incomplete capture: runtime events were lost.");
var heap = nodes.Values.GroupBy(node => types.GetValueOrDefault(node.Type, "<unknown>"))
    .Select(group => new { Type = group.Key, Count = group.LongCount(), Bytes = group.Sum(node => checked((long)node.Bytes)) })
    .OrderByDescending(row => row.Bytes).ToArray();
if (args[1] == "heap" && heap.Length == 0) throw new InvalidOperationException("No heap nodes collected; not a valid zero-byte result.");
var report = new
{
    Boundary = "Synthetic EventPipe aggregate; object contents/addresses/edges and raw trace are NOT saved. Heap mode requests a diagnostic GC, excluded from perf runs. Allocation ticks are sampled attribution, not exact allocation-by-type. Unknown native is not inferred by subtraction.",
    Mode = args[1],
    Scenario = info.GetProperty("Scenario").GetString(),
    Seconds = clock.Elapsed.TotalSeconds,
    EventsLost = source.EventsLost,
    HeapObjects = nodes.Count,
    HeapBytes = heap.Sum(row => row.Bytes),
    HeapByType = heap,
    AllocationTicks = allocation.OrderByDescending(pair => pair.Value.Bytes)
        .Select(pair => new { Type = pair.Key, pair.Value.Ticks, EstimatedBytes = pair.Value.Bytes }).ToArray(),
};
await using (var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write))
    await JsonSerializer.SerializeAsync(stream, report, new JsonSerializerOptions { WriteIndented = true });
await File.WriteAllTextAsync(readyPath + ".continue", "complete");
Console.WriteLine($"{args[1]}: heap objects={nodes.Count}, bytes={report.HeapBytes}, events lost={source.EventsLost}; {output}");
