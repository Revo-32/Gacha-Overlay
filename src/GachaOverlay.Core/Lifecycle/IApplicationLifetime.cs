namespace GachaOverlay.Core.Lifecycle;

public interface IApplicationLifetime
{
    CancellationToken Stopping { get; }
}
