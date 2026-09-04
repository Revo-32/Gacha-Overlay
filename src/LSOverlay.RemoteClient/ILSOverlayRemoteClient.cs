using System.Threading.Channels;
using LSOverlay.Protocol;

namespace LSOverlay.RemoteClient;

public interface ILSOverlayRemoteClient : IAsyncDisposable
{
    event Action? StreamLive;

    event Action<HostPresenceSnapshot>? HostPresenceChanged;

    event Action<ChatBootstrapResponse>? ChatChannelReady;

    event Action<ChatMutationEnvelope>? ChatMutationReceived;

    event Action<ulong, string>? ChatStreamStatusChanged;

    Task<BootstrapResponse> GetBootstrapAsync(
        string accessToken,
        CancellationToken cancellationToken = default);

    Task<ChatChannelCatalogResponse> GetChatChannelsAsync(
        string accessToken,
        CancellationToken cancellationToken = default);

    Task<ChatBootstrapResponse> GetChatBootstrapAsync(
        string accessToken,
        ulong channelId,
        CancellationToken cancellationToken = default);

    Task StreamChatAsync(
        string accessToken,
        BootstrapResponse presenceBootstrap,
        ChatBootstrapResponse initialChatBootstrap,
        ChannelReader<ChatBootstrapResponse> channelSwitches,
        CancellationToken cancellationToken = default);

}

public interface ILSOverlayRemoteSalesClient
{
    event Action<SalesBootstrapResponse>? SalesReady;

    event Action<SalesMutationEnvelope>? SalesMutationReceived;

    event Action<string>? SalesStreamStatusChanged;

    Task<SalesBootstrapResponse> GetSalesBootstrapAsync(
        string accessToken,
        CancellationToken cancellationToken = default);

    Task<SalesStatusActionResponse> SetSalesStatusAsync(
        string accessToken,
        SalesStatusActionRequest request,
        CancellationToken cancellationToken = default);

    Task StreamChatAndSalesAsync(
        string accessToken,
        BootstrapResponse presenceBootstrap,
        ChatBootstrapResponse initialChatBootstrap,
        SalesBootstrapResponse salesBootstrap,
        ChannelReader<ChatBootstrapResponse> channelSwitches,
        ChannelReader<SalesBootstrapResponse> salesResyncs,
        CancellationToken cancellationToken = default);
}

public interface ILSOverlayGtaCompanionClient
{
    event Action<GtaCompanionSnapshot>? GtaCompanionSnapshotReceived;
}
