using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using GachaOverlay.Core.Discord.Messages;
using GachaOverlay.Core.Sales;
using LSOverlay.Backend.CoreClient;
using LSOverlay.Protocol;
using LSOverlay.RemoteClient;

namespace LSOverlay.Backend.CoreClient;

// One viewer per instance; caller serializes mutations under its connection gate.
// This server-side adapter reuses the existing SalesStateEngine, not a new parser.
public sealed class RelayState
{
    private readonly string _viewer;
    private readonly string _generation = Guid.NewGuid().ToString("N");
    private readonly Dictionary<ulong, NormalizedDiscordMessage> _chat = new(), _sales = new();
    private readonly Dictionary<ulong, SalesCompletionObservation> _evidence = new();
    private readonly Dictionary<int, HostPresenceSnapshot> _hosts = new();
    private readonly SalesStateEngine _engine;
    private readonly CoreSemanticProjection _projection;
    private readonly CoreSalesPresentationProjection _presenter = new(CoreSalesResources.Strings);
    private string? _chatGeneration, _salesGeneration;
    private ulong _chatChannel, _salesChannel;
    private long _chatSequence, _salesSequence, _observationGeneration, _revision;
    private bool _salesReady;
    private DateTimeOffset? _salesReadyAt;
    private RemoteSalesPresentationPhase _salesPhase = RemoteSalesPresentationPhase.Bootstrapping;
    public string ChatStatus { get; private set; } = "connecting";
    public ChannelSelection? Channels { get; set; }
    public bool IncludeSalesActionHints { get; init; }
    public ulong ChatChannel => _chatChannel;
    public void Switching() { _chat.Clear(); _chatGeneration = null; ChatStatus = "connecting"; }
    public RelayState(BootstrapResponse identity, ICoreMediaReferences? media = null)
    {
        _viewer = Id(identity.SelfDiscordUserId);
        _projection = new(media ?? new MetadataOnly());
        if (identity.ProtocolVersion != 1 || identity.SelfDiscordUserId == 0) throw new InvalidDataException("Invalid authorized identity.");
        var names = new GuildDisplayNameResolver(); names.SetAccountScope(_viewer);
        _engine = new(names, CoreSalesResources.Catalog, locale: "ko");
        _engine.SetAuthenticatedUser(_viewer); Presence(identity);
    }
    public void Presence(BootstrapResponse state)
    {
        if (Id(state.SelfDiscordUserId) != _viewer || state.ProtocolVersion != 1 || state.TrackedHosts.Count > 16) throw new InvalidDataException("Identity changed.");
        _hosts.Clear(); foreach (var host in state.TrackedHosts) _hosts[host.HostSlot] = host;
    }
    public string? ReadableChannel(string message)
    {
        if (!ulong.TryParse(message, out var id)) return null;
        if (ChatStatus == "live" && _chat.ContainsKey(id)) return Id(_chatChannel);
        if (_salesReady && _sales.ContainsKey(id)) return Id(_salesChannel);
        return null;
    }
    public void Presence(HostPresenceSnapshot host)
    {
        if (_hosts.ContainsKey(host.HostSlot)) _hosts[host.HostSlot] = host;
    }
    public void Chat(ChatBootstrapResponse state)
    {
        if (state.ProtocolVersion != 1 || string.IsNullOrWhiteSpace(state.Generation) || state.RecentMessages.Count > 100 ||
            state.RecentMessages.Any(message => message.ChannelId != state.Channel.ChannelId) ||
            state.RecentMessages.Select(message => message.MessageId).Distinct().Count() != state.RecentMessages.Count)
            throw new InvalidDataException("Invalid chat bootstrap.");
        _chatGeneration = state.Generation; _chatSequence = state.LatestSequence; _chatChannel = state.Channel.ChannelId;
        _chat.Clear(); foreach (var message in state.RecentMessages) _chat[message.MessageId] = RemoteChatIngressAdapter.MapNormalizedMessage(message);
        Trim(_chat, 20); ChatStatus = "live";
    }
    public void Chat(ChatMutationEnvelope mutation)
    {
        if (mutation.ProtocolVersion != 1 || mutation.Generation != _chatGeneration || mutation.ChannelId != _chatChannel || mutation.Sequence != _chatSequence + 1 ||
            (mutation.Message is not null && (mutation.Message.ChannelId != _chatChannel || mutation.Message.MessageId != mutation.MessageId)))
            throw new InvalidDataException("Chat cursor mismatch; reconnect required.");
        _chatSequence = mutation.Sequence;
        if (mutation.EventType == OverlayTransportProtocol.ChatMessageDelete) _chat.Remove(mutation.MessageId);
        else if (mutation.EventType is OverlayTransportProtocol.ChatMessageCreate or OverlayTransportProtocol.ChatMessageUpdate && mutation.Message is not null)
            _chat[mutation.MessageId] = RemoteChatIngressAdapter.MapNormalizedMessage(mutation.Message);
        else throw new InvalidDataException("Invalid chat event.");
        Trim(_chat, 20);
    }
    public void ChatState(ulong channel, string status)
    {
        if (_chatChannel != 0 && channel != _chatChannel) return;
        ChatStatus = status == OverlayTransportProtocol.ChatReady ? "live" : "recovering";
        if (status is OverlayTransportProtocol.ChatAccessRevoked or OverlayTransportProtocol.ChatAuthorizationUnavailable or OverlayTransportProtocol.ChatChannelUnavailable)
        { _chat.Clear(); _chatGeneration = null; ChatStatus = "unavailable"; }
    }
    public void Sales(SalesBootstrapResponse state)
    {
        // Same complete-window gate used by Full. Incomplete observations must
        // never silently promote an item or erase a previously trusted queue.
        var ids = state.RecentMessages.Select(message => message.MessageId).ToHashSet();
        if (state.ProtocolVersion != 1 || string.IsNullOrWhiteSpace(state.Generation) || state.Coverage != SalesBootstrapCoverage.Complete ||
            state.RecentMessages.Count > AuthoritativeSalesWindow.Size || ids.Count != state.RecentMessages.Count ||
            state.RecentMessages.Any(message => message.ChannelId != state.Channel.ChannelId) ||
            state.CompletionObservations.Count != ids.Count || state.CompletionObservations.Any(item => item.Coverage != SalesEvidenceCoverage.Complete) ||
            !state.CompletionObservations.Select(item => item.MessageId).ToHashSet().SetEquals(ids))
            throw new InvalidDataException("Incomplete canonical Sales window.");
        _salesGeneration = state.Generation; _salesSequence = state.LatestSequence; _salesChannel = state.Channel.ChannelId;
        _sales.Clear(); _evidence.Clear();
        foreach (var message in state.RecentMessages) _sales[message.MessageId] = RemoteChatIngressAdapter.MapNormalizedMessage(message);
        foreach (var item in state.CompletionObservations) _evidence[item.MessageId] = item;
        _salesReady = true; _salesReadyAt = DateTimeOffset.UtcNow; _salesPhase = RemoteSalesPresentationPhase.Live;
        ApplySales();
    }
    public void Sales(SalesMutationEnvelope mutation)
    {
        if (!_salesReady || mutation.ProtocolVersion != 1 || mutation.Generation != _salesGeneration || mutation.ChannelId != _salesChannel || mutation.Sequence != _salesSequence + 1 ||
            (mutation.Message is not null && (mutation.Message.ChannelId != _salesChannel || mutation.Message.MessageId != mutation.MessageId)) ||
            (mutation.CompletionObservation is { } item && (item.MessageId != mutation.MessageId || item.Coverage != SalesEvidenceCoverage.Complete)))
            throw new InvalidDataException("Sales cursor/evidence mismatch.");
        _salesSequence = mutation.Sequence;
        if (mutation.EventType == OverlayTransportProtocol.SalesMessageDelete)
        { _sales.Remove(mutation.MessageId); _evidence.Remove(mutation.MessageId); _engine.ApplySourceDelete(Id(mutation.MessageId)); }
        else if (mutation.EventType is OverlayTransportProtocol.SalesMessageCreate or OverlayTransportProtocol.SalesMessageUpdate or OverlayTransportProtocol.SalesCompletionEvidence)
        {
            if (mutation.Message is not null) _sales[mutation.MessageId] = RemoteChatIngressAdapter.MapNormalizedMessage(mutation.Message);
            if (mutation.CompletionObservation is { } observation) _evidence[mutation.MessageId] = observation;
        }
        else throw new InvalidDataException("Invalid Sales event.");
        Trim(_sales, AuthoritativeSalesWindow.Size);
        foreach (var id in _evidence.Keys.Where(id => !_sales.ContainsKey(id)).ToArray()) _evidence.Remove(id);
        if (!_sales.Keys.ToHashSet().SetEquals(_evidence.Keys)) throw new InvalidDataException("Missing live Sales evidence.");
        ApplySales();
    }
    private void ApplySales()
    {
        _engine.ApplyAuthoritativeWindowSnapshot(Ordered(_sales));
        var generation = ++_observationGeneration;
        _engine.ApplyObservationBatch(new(generation, DateTimeOffset.UtcNow, SalesObservationStatus.Live, true, SalesObservationCompleteness.Full,
            _evidence.Values.Select(item => new SaleReactionObservation(Id(item.MessageId), item.IsSold ? SaleReactionOutcome.Sold : SaleReactionOutcome.NotSold, true, item.ObservedAt, generation)).ToArray(),
            SalesCoverageState.Complete, TargetMessageCount: _evidence.Count, ObservedMessageCount: _evidence.Count));
    }
    public void SalesState(string status)
    {
        _salesPhase = status switch
        {
            OverlayTransportProtocol.SalesReady => _salesReady ? RemoteSalesPresentationPhase.Live : RemoteSalesPresentationPhase.Bootstrapping,
            OverlayTransportProtocol.SalesAccessRevoked => RemoteSalesPresentationPhase.AccessRevoked,
            OverlayTransportProtocol.SalesAuthorizationUnavailable => RemoteSalesPresentationPhase.AuthorizationUnavailable,
            OverlayTransportProtocol.SalesChannelUnavailable => RemoteSalesPresentationPhase.ChannelUnavailable,
            OverlayTransportProtocol.SalesFailed => RemoteSalesPresentationPhase.Failed,
            _ => RemoteSalesPresentationPhase.Resyncing
        };
        if (_salesPhase != RemoteSalesPresentationPhase.Live) _salesReady = false;
        if (_salesPhase is RemoteSalesPresentationPhase.AccessRevoked or RemoteSalesPresentationPhase.AuthorizationUnavailable or RemoteSalesPresentationPhase.ChannelUnavailable)
        { _sales.Clear(); _evidence.Clear(); _engine.ApplyAuthoritativeWindowSnapshot(Array.Empty<NormalizedDiscordMessage>()); }
    }
    public CoreSnapshot Capture()
    {
        var health = SalesFeatureHealthEvaluator.Evaluate(new(true, _salesPhase, _salesReady,
            _salesReady ? SalesCoverageState.Complete : SalesCoverageState.None, _salesReadyAt, _sales.Count, _evidence.Count));
        var canonical = _engine.Current with { ObservationStatus = health.SensorStatus };
        var snapshot = _projection.Capture(_generation, ++_revision, _viewer, Ordered(_chat), canonical, _hosts.Values.OrderBy(host => host.HostSlot).ToArray());
        var result = _presenter.Apply(snapshot, canonical, health) with { ChatConnectionState = ChatStatus, ChatSelection = Channels?.Describe(_chatChannel) };
        if (!IncludeSalesActionHints || !_salesReady || health.State != SalesFeatureHealthState.Live || string.IsNullOrEmpty(_salesGeneration)) return result;
        var targets = _engine.Records.Where(record => record.AuthorId == _viewer && record.DomainState != SaleDomainState.Deleted)
            .OrderByDescending(record => record.CreatedAt).ThenByDescending(record => record.MessageId)
            .Where(record => ulong.TryParse(record.MessageId, out var id) && _sales.ContainsKey(id) && _evidence.ContainsKey(id))
            .Select(record =>
            {
                var evidence = _evidence[ulong.Parse(record.MessageId, CultureInfo.InvariantCulture)];
                return new CoreSalesActionTarget(record.MessageId, !evidence.IsSold, evidence.BotCompletedMarkerPresent, evidence.BotCompletedMarkerPresent);
            }).ToArray();
        return result with { Sales = result.Sales with { Actions = new(_salesGeneration, _salesSequence, targets) } };
    }
    private static IReadOnlyList<NormalizedDiscordMessage> Ordered(Dictionary<ulong, NormalizedDiscordMessage> messages) => messages.OrderBy(pair => pair.Value.CreatedAt).ThenBy(pair => pair.Key).Select(pair => pair.Value).ToArray();
    private static void Trim(Dictionary<ulong, NormalizedDiscordMessage> messages, int limit)
    { foreach (var id in messages.OrderByDescending(pair => pair.Value.CreatedAt).ThenByDescending(pair => pair.Key).Skip(limit).Select(pair => pair.Key).ToArray()) messages.Remove(id); }
    private static string Id(ulong id) => id.ToString(CultureInfo.InvariantCulture);
    private sealed class MetadataOnly : ICoreMediaReferences
    {
        // No network URL or media-byte endpoint in the initial authenticated relay.
        public string RegisterCanonical(string messageId, string kind, string identity, string? assetUrl) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(messageId + "|" + kind + "|" + identity))).ToLowerInvariant();
    }
}
