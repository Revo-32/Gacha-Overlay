namespace LSOverlay.Backend.Transport;

internal enum TransportMetric
{
    PairingCreated,
    PairingApproved,
    PairingExpired,
    PairingClaimRejected,
    AuthAccepted,
    AuthRejected,
    BootstrapRequests,
    Connections,
    Disconnects,
    ReplayEventsSent,
    ResyncRequired,
    SlowClientDisconnects,
    HeartbeatTimeouts,
    HostPresencePublished,
    SalesStatusRequested,
    SalesStatusAccepted,
    SalesStatusNoOp,
    SalesStatusNotOwner,
    SalesStatusRateLimited,
    SalesStatusDeduplicated,
    SalesStatusFailed,
}

internal sealed class TransportMetrics
{
    private readonly long[] _values = new long[Enum.GetValues<TransportMetric>().Length];

    public void Increment(TransportMetric metric) =>
        Interlocked.Increment(ref _values[(int)metric]);

    public long Get(TransportMetric metric) =>
        Interlocked.Read(ref _values[(int)metric]);
}
