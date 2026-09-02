namespace GachaOverlay.Core.Providers;

public static class OverlayProtocolVersion
{
    public const int Current = 1;
}

/// <summary>
/// Transport-neutral ordering metadata reserved for a future remote provider.
/// M9.0 does not serialize, send, or consume this value at runtime.
/// </summary>
public readonly record struct OverlayEventPosition
{
    public OverlayEventPosition(
        int protocolVersion,
        long eventSequence,
        long bootstrapGeneration)
    {
        if (protocolVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(protocolVersion));
        }

        if (eventSequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(eventSequence));
        }

        if (bootstrapGeneration < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bootstrapGeneration));
        }

        ProtocolVersion = protocolVersion;
        EventSequence = eventSequence;
        BootstrapGeneration = bootstrapGeneration;
    }

    public int ProtocolVersion { get; }

    public long EventSequence { get; }

    public long BootstrapGeneration { get; }
}
