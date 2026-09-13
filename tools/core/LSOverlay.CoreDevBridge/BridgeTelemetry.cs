using System.Net.WebSockets;
using System.Text.Json;
using LSOverlay.Protocol;
using LSOverlay.RemoteClient;

namespace LSOverlay.CoreDevBridge;
internal enum BridgeSignal { Connections, Active, Hello, UpstreamConnected, ChatBootstrap, ChatMutation, SalesBootstrap, SalesMutation, Presence, Snapshots, HeartbeatAck, HttpIdentity, HttpCatalog, HttpChat, HttpSales, MediaRequests, MediaDelivered, MediaDenied, MediaFailures, MediaCacheHits, MediaBytes }
internal sealed class BridgeTelemetry
{
    private readonly long[] _counts=new long[Enum.GetValues<BridgeSignal>().Length];
    private readonly object _gate=new();
    private object? _sales, _lastFailure;
    private readonly Dictionary<string,(long Count,double Total,double Maximum)> _timings=new();
    public void Timing(string stage,double ms){lock(_gate){var old=_timings.GetValueOrDefault(stage);_timings[stage]=(old.Count+1,old.Total+ms,Math.Max(old.Maximum,ms));}}
    public void Count(BridgeSignal signal,long amount=1) => Interlocked.Add(ref _counts[(int)signal],amount);
    public void Sales(SalesBootstrapResponse snapshot)
    {
        lock (_gate) _sales=new {messages=snapshot.RecentMessages.Count,observations=snapshot.CompletionObservations.Count,
            sold=snapshot.CompletionObservations.Count(item=>item.IsSold),coverage=snapshot.Coverage.ToString(),observedAt=DateTimeOffset.UtcNow};
    }
    public void Failure(string stage,Exception error)
    {
        // Only static code-owned classifications. Never exception bodies, URLs,
        // channel/message/user identifiers or credentials, even from HTTP errors.
        var type=error switch { RemoteAuthenticationRequiredException=>"authentication",RemoteResyncRequiredException=>"upstream-resync",
            InvalidDataException=>"state-validation",JsonException=>"json",WebSocketException=>"websocket",HttpRequestException=>"http",
            OperationCanceledException=>"cancelled",_=>"internal" };
        lock (_gate) _lastFailure=new {stage,type,at=DateTimeOffset.UtcNow};
    }
    public object Snapshot()
    {
        lock (_gate) return new {at=DateTimeOffset.UtcNow,counters=Enum.GetValues<BridgeSignal>().ToDictionary(signal=>signal.ToString(),signal=>Interlocked.Read(ref _counts[(int)signal])),
            sales=_sales,stages=_timings.ToDictionary(p=>p.Key,p=>new{count=p.Value.Count,totalMs=p.Value.Total,maxMs=p.Value.Maximum}),lastFailure=_lastFailure};
    }
}
