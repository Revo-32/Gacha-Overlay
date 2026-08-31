using GachaOverlay.Core.Discord.Connection;
using GachaOverlay.Core.Diagnostics;
using GachaOverlay.Core.Logging;

namespace GachaOverlay.Core.Discord.Messages;

public sealed class DiscordMessagePipeline : IGuildNicknameObservationSink
{
    public const int MainChatRetentionLimit = 20;

    private readonly object _sync = new();
    private readonly List<DiscordMessageMutation> _liveBuffer = new();
    private readonly IGuildDisplayNameResolver _displayNameResolver;
    private readonly IAppLogger _logger;
    private readonly IRuntimeMetrics? _metrics;
    private readonly HashSet<string> _loggedNameSelections = new(StringComparer.Ordinal);
    private DiscordMessageStore _mainStore = new(MainChatRetentionLimit);
    private DiscordMessageStore _salesStore = new();
    private DiscordTargetChannels? _targets;
    private long _generation;
    private bool _isBootstrapping;
    private DiscordMessageState _current = DiscordMessageState.Empty;

    public DiscordMessagePipeline(
        IAppLogger? logger = null,
        IGuildDisplayNameResolver? displayNameResolver = null,
        IRuntimeMetrics? metrics = null)
    {
        _logger = logger ?? NullAppLogger.Instance;
        _displayNameResolver = displayNameResolver ?? new GuildDisplayNameResolver();
        _metrics = metrics;
    }

    public event Action<DiscordMessageState>? StateChanged;

    public DiscordMessageState Current
    {
        get
        {
            lock (_sync)
            {
                return _current;
            }
        }
    }

    public DiscordTargetChannels? Targets
    {
        get
        {
            lock (_sync)
            {
                return _targets;
            }
        }
    }

    public void SetAuthenticatedUser(string userId) =>
        _displayNameResolver.SetAccountScope(userId);

    public bool ObserveGuildNickname(GuildNicknameObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var resolution = _displayNameResolver.Observe(observation);
        if (resolution is null)
        {
            return false;
        }

        DiscordMessageState? state = null;
        lock (_sync)
        {
            var refreshed = _mainStore.RefreshGuildNickname(
                    observation.GuildId,
                    observation.AuthorId,
                    resolution.DisplayName,
                    resolution.ObservationSource) +
                _salesStore.RefreshGuildNickname(
                    observation.GuildId,
                    observation.AuthorId,
                    resolution.DisplayName,
                    resolution.ObservationSource);
            if (refreshed > 0)
            {
                state = CaptureState();
            }
        }

        if (state is not null)
        {
            StateChanged?.Invoke(state);
        }

        return true;
    }

    public bool StartBootstrap(long generation, DiscordTargetChannels targets)
    {
        ArgumentNullException.ThrowIfNull(targets);

        DiscordMessageState state;
        lock (_sync)
        {
            if (generation <= _generation)
            {
                _metrics?.Increment(RuntimeMetricNames.ChatStaleDiscards);
                return false;
            }

            _generation = generation;
            _targets = targets;
            _isBootstrapping = true;
            _liveBuffer.Clear();
            state = CaptureState();
        }

        StateChanged?.Invoke(state);
        return true;
    }

    public bool ReceiveLive(long generation, DiscordMessageMutation mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);

        DiscordMessageState? state = null;
        lock (_sync)
        {
            if (generation != _generation || _targets is null)
            {
                _metrics?.Increment(RuntimeMetricNames.ChatStaleDiscards);
                return false;
            }

            if (_isBootstrapping)
            {
                _liveBuffer.Add(mutation);
                return true;
            }

            if (!ApplyToTarget(mutation, _mainStore, _salesStore, _targets))
            {
                return false;
            }

            state = CaptureState();
        }

        StateChanged?.Invoke(state);
        return true;
    }

    public bool CompleteBootstrap(
        long generation,
        IEnumerable<DiscordMessagePatch> mainSnapshot,
        IEnumerable<DiscordMessagePatch> salesSnapshot)
    {
        ArgumentNullException.ThrowIfNull(mainSnapshot);
        ArgumentNullException.ThrowIfNull(salesSnapshot);

        DiscordMessageState state;
        lock (_sync)
        {
            if (generation != _generation || !_isBootstrapping || _targets is null)
            {
                _metrics?.Increment(RuntimeMetricNames.ChatStaleDiscards);
                return false;
            }

            var nextMain = new DiscordMessageStore(MainChatRetentionLimit);
            var nextSales = new DiscordMessageStore(seed: _salesStore.GetOrderedSnapshot());

            foreach (var patch in mainSnapshot)
            {
                var mutation = DiscordMessageMutation.Create(patch);
                nextMain.Apply(PrepareMutation(
                    mutation,
                    nextMain,
                    nextMain,
                    nextSales,
                    _targets.GuildId));
            }

            foreach (var patch in salesSnapshot)
            {
                var mutation = DiscordMessageMutation.Create(patch);
                nextSales.Apply(PrepareMutation(
                    mutation,
                    nextSales,
                    nextMain,
                    nextSales,
                    _targets.GuildId));
            }

            foreach (var mutation in _liveBuffer)
            {
                ApplyToTarget(mutation, nextMain, nextSales, _targets);
            }

            _mainStore = nextMain;
            _salesStore = nextSales;
            _liveBuffer.Clear();
            _isBootstrapping = false;
            state = CaptureState();
        }

        StateChanged?.Invoke(state);
        return true;
    }

    public bool AbortBootstrap(long generation)
    {
        DiscordMessageState state;
        lock (_sync)
        {
            if (generation != _generation || !_isBootstrapping)
            {
                return false;
            }

            _liveBuffer.Clear();
            _isBootstrapping = false;
            state = CaptureState();
        }

        StateChanged?.Invoke(state);
        return true;
    }

    public bool ReplaceMain(
        long generation,
        DiscordTargetChannels targets,
        IEnumerable<DiscordMessagePatch> mainSnapshot)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(mainSnapshot);

        DiscordMessageState state;
        lock (_sync)
        {
            if (generation != _generation || _isBootstrapping || _targets is null ||
                !string.Equals(_targets.GuildId, targets.GuildId, StringComparison.Ordinal) ||
                !string.Equals(
                    _targets.SalesChannelId,
                    targets.SalesChannelId,
                    StringComparison.Ordinal))
            {
                _metrics?.Increment(RuntimeMetricNames.ChatStaleDiscards);
                return false;
            }

            var nextMain = new DiscordMessageStore(MainChatRetentionLimit);
            foreach (var patch in mainSnapshot)
            {
                var mutation = DiscordMessageMutation.Create(patch);
                nextMain.Apply(PrepareMutation(
                    mutation,
                    nextMain,
                    nextMain,
                    _salesStore,
                    targets.GuildId));
            }

            _mainStore = nextMain;
            _targets = targets;
            state = CaptureState();
        }

        StateChanged?.Invoke(state);
        return true;
    }

    private bool ApplyToTarget(
        DiscordMessageMutation mutation,
        DiscordMessageStore mainStore,
        DiscordMessageStore salesStore,
        DiscordTargetChannels targets)
    {
        DiscordMessageStore targetStore;
        if (string.Equals(mutation.ChannelId, targets.MainChannelId, StringComparison.Ordinal))
        {
            targetStore = mainStore;
        }
        else if (string.Equals(mutation.ChannelId, targets.SalesChannelId, StringComparison.Ordinal))
        {
            targetStore = salesStore;
        }
        else
        {
            return false;
        }

        var existed = targetStore.TryGet(mutation.MessageId, out _);
        var previousCount = targetStore.Count;
        var result = targetStore.Apply(PrepareMutation(
            mutation,
            targetStore,
            mainStore,
            salesStore,
            targets.GuildId));
        if (ReferenceEquals(targetStore, mainStore) &&
            !existed &&
            result == MessageStoreMutationResult.Applied &&
            previousCount >= MainChatRetentionLimit)
        {
            _metrics?.Increment(RuntimeMetricNames.ChatRetentionEvictions);
        }

        return true;
    }

    private DiscordMessageMutation PrepareMutation(
        DiscordMessageMutation mutation,
        DiscordMessageStore targetStore,
        DiscordMessageStore mainStore,
        DiscordMessageStore salesStore,
        string guildId)
    {
        if (mutation.Kind == DiscordMessageMutationKind.Delete || mutation.Patch is null)
        {
            return mutation;
        }

        var patch = mutation.Patch with
        {
            GuildId = OptionalValue<string>.From(guildId),
        };
        targetStore.TryGet(mutation.MessageId, out var existing);
        var authorId = patch.AuthorId.HasValue &&
            !string.IsNullOrWhiteSpace(patch.AuthorId.Value)
                ? patch.AuthorId.Value
                : existing?.AuthorId;

        if (string.IsNullOrWhiteSpace(authorId))
        {
            return mutation.WithPatch(patch);
        }

        var currentExactNickname = patch.AuthorGuildNickname.HasValue &&
            !string.IsNullOrWhiteSpace(patch.AuthorGuildNickname.Value)
                ? patch.AuthorGuildNickname.Value
                : null;
        var previousObservationSource = existing?.AuthorGuildNicknameObservationSource ??
            DiscordDisplayNameSource.Unknown;
        if (previousObservationSource == DiscordDisplayNameSource.Unknown &&
            existing?.AuthorDisplayNameSource == DiscordDisplayNameSource.RpcGuildNickname)
        {
            previousObservationSource = DiscordDisplayNameSource.RpcGuildNickname;
        }

        var resolution = _displayNameResolver.Resolve(new GuildDisplayNameRequest(
            guildId,
            authorId,
            currentExactNickname,
            patch.AuthorDisplayName.HasValue
                ? patch.AuthorDisplayName.Value
                : existing?.AuthorDisplayName,
            patch.AuthorUsername.HasValue
                ? patch.AuthorUsername.Value
                : existing?.AuthorUsername,
            existing?.AuthorGuildNickname,
            existing?.AuthorDisplayNameSource ?? DiscordDisplayNameSource.Unknown,
            previousObservationSource));

        if (resolution.IsExactGuildNickname)
        {
            if (currentExactNickname is not null)
            {
                mainStore.RefreshGuildNickname(
                    guildId,
                    authorId,
                    resolution.DisplayName,
                    resolution.ObservationSource);
                salesStore.RefreshGuildNickname(
                    guildId,
                    authorId,
                    resolution.DisplayName,
                    resolution.ObservationSource);
            }

            patch = patch with
            {
                AuthorGuildNickname = OptionalValue<string?>.From(resolution.DisplayName),
                AuthorDisplayNameSource = OptionalValue<DiscordDisplayNameSource>.From(
                    resolution.Source),
                AuthorGuildNicknameObservationSource =
                    OptionalValue<DiscordDisplayNameSource>.From(
                        resolution.ObservationSource),
            };
            LogNameSelection(
                patch.MessageId,
                guildId,
                authorId,
                resolution.DisplayName,
                resolution.Source);
            return mutation.WithPatch(patch);
        }

        patch = patch with
        {
            AuthorGuildNickname = !string.IsNullOrWhiteSpace(existing?.AuthorGuildNickname)
                ? default
                : patch.AuthorGuildNickname,
            AuthorDisplayNameSource = OptionalValue<DiscordDisplayNameSource>.From(
                resolution.Source),
        };

        return mutation.WithPatch(patch);
    }

    private void LogNameSelection(
        string messageId,
        string guildId,
        string authorId,
        string nickname,
        DiscordDisplayNameSource source)
    {
        var key = $"{guildId}\u001f{authorId}\u001f{source}";
        if (_loggedNameSelections.Count >= 32 || !_loggedNameSelections.Add(key))
        {
            return;
        }

        _logger.Information(
            "NAME",
            $"message={Sanitize(messageId)} guild={Sanitize(guildId)} author={Sanitize(authorId)} source={source} value=\"{Sanitize(nickname)}\".");
    }

    private static string Sanitize(string value)
    {
        var sanitized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return sanitized.Length <= 80 ? sanitized : sanitized[..80];
    }

    private DiscordMessageState CaptureState()
    {
        _current = new DiscordMessageState(
            _generation,
            _isBootstrapping,
            _mainStore.GetOrderedSnapshot(),
            _salesStore.GetOrderedSnapshot());
        return _current;
    }
}
