using Discord;
using Discord.Rest;
using Discord.WebSocket;
using LSOverlay.Backend.Chat;
using LSOverlay.Backend.Configuration;
using LSOverlay.Backend.Events;
using LSOverlay.Backend.Presence;
using LSOverlay.Backend.Runtime;
using LSOverlay.Backend.Sales;
using LSOverlay.Backend.Transport;
using Microsoft.Extensions.Logging;

namespace LSOverlay.Backend.Discord;

internal sealed class DiscordGatewayAdapter : IDiscordGatewayLifecycle
{
    private readonly object _lifecycleSync = new();
    private readonly BackendCallbackDrainGate _callbackGate = new();
    private readonly DiscordSocketClient _client;
    private readonly BackendConfiguration _configuration;
    private readonly TargetGuildFilter _guildFilter;
    private readonly BackendEventJournal _journal;
    private readonly BackendMetrics _metrics;
    private readonly BackendConnectionHealth _health;
    private readonly TrackedHostPresenceStore _presenceStore;
    private readonly GtaPresenceNormalizer _presenceNormalizer;
    private readonly ILogger<DiscordGatewayAdapter> _logger;
    private readonly IRemotePresencePublisher? _remotePublisher;
    private readonly TransportMetrics? _transportMetrics;
    private readonly RemoteChatService? _remoteChat;
    private readonly RemoteSalesService? _remoteSales;
    private readonly BotCustomStatus _customStatus;
    private int _started;
    private int _stopping;
    private int _hasCompletedReady;
    private Task? _stopTask;

    public DiscordGatewayAdapter(
        DiscordSocketClient client,
        BackendConfiguration configuration,
        TargetGuildFilter guildFilter,
        BackendEventJournal journal,
        BackendMetrics metrics,
        BackendConnectionHealth health,
        TrackedHostPresenceStore presenceStore,
        GtaPresenceNormalizer presenceNormalizer,
        ILogger<DiscordGatewayAdapter> logger,
        IRemotePresencePublisher? remotePublisher = null,
        TransportMetrics? transportMetrics = null,
        RemoteChatService? remoteChat = null,
        RemoteSalesService? remoteSales = null,
        BotCustomStatus? customStatus = null)
    {
        _client = client;
        _configuration = configuration;
        _guildFilter = guildFilter;
        _journal = journal;
        _metrics = metrics;
        _health = health;
        _presenceStore = presenceStore;
        _presenceNormalizer = presenceNormalizer;
        _logger = logger;
        _remotePublisher = remotePublisher;
        _transportMetrics = transportMetrics;
        _remoteChat = remoteChat;
        _remoteSales = remoteSales;
        _customStatus = customStatus ?? new BotCustomStatus(
            _client.SetCustomStatusAsync,
            () => _logger.LogWarning("Bot custom status could not be applied; next attempt is at Gateway Ready."));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (IsStopping)
        {
            throw new InvalidOperationException("The Discord Gateway adapter is stopping.");
        }

        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("The Discord Gateway adapter can only start once.");
        }

        var capabilities = DiscordSdkCapabilityAudit.Inspect();
        if (!DiscordSdkCapabilityAudit.HasRequiredSurface(capabilities))
        {
            throw new InvalidOperationException("Discord SDK capability audit failed.");
        }

        Subscribe();
        Transition(
            BackendConnectionHealthState.Connecting,
            BackendConnectionHealthReason.GatewayConnecting,
            "Discord: Connecting");
        try
        {
            await _client.LoginAsync(
                    TokenType.Bot,
                    _configuration.Credential.RevealForDiscordLogin())
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            await _client.StartAsync()
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            Unsubscribe();
            throw;
        }
    }

    public Task StopAsync()
    {
        lock (_lifecycleSync)
        {
            Volatile.Write(ref _stopping, 1);
            return _stopTask ??= StopCoreAsync();
        }
    }

    internal bool IsStopping => Volatile.Read(ref _stopping) != 0;

    internal bool CanProcessCallbacks => !IsStopping && _callbackGate.IsAccepting;

    private async Task StopCoreAsync()
    {
        var callbacksDrained = _callbackGate.CloseAsync();
        // Detach application callbacks before Discord.Net raises its virtual
        // GuildUnavailable events during normal socket teardown, then wait for
        // any callback that already entered the normalization boundary.
        Unsubscribe();
        await callbacksDrained.ConfigureAwait(false);
        await _client.LogoutAsync().ConfigureAwait(false);
    }

    private void Subscribe()
    {
        _client.Log += OnDiscordLogAsync;
        _client.Connected += OnConnectedAsync;
        _client.Ready += OnReadyAsync;
        _client.Disconnected += OnDisconnectedAsync;
        _client.GuildAvailable += OnGuildAvailableAsync;
        _client.GuildUnavailable += OnGuildUnavailableAsync;
        _client.MessageReceived += OnMessageReceivedAsync;
        _client.MessageUpdated += OnMessageUpdatedAsync;
        _client.MessageDeleted += OnMessageDeletedAsync;
        _client.MessagesBulkDeleted += OnMessagesBulkDeletedAsync;
        _client.ReactionAdded += OnReactionAddedAsync;
        _client.ReactionRemoved += OnReactionRemovedAsync;
        _client.ReactionsCleared += OnReactionsClearedAsync;
        _client.ReactionsRemovedForEmote += OnReactionsRemovedForEmoteAsync;
        _client.PollVoteAdded += OnPollVoteAddedAsync;
        _client.PollVoteRemoved += OnPollVoteRemovedAsync;
        _client.ChannelUpdated += OnChannelUpdatedAsync;
        _client.ChannelDestroyed += OnChannelDestroyedAsync;
        _client.RoleCreated += OnRoleCreatedAsync;
        _client.RoleUpdated += OnRoleUpdatedAsync;
        _client.RoleDeleted += OnRoleDeletedAsync;
        _client.GuildMemberUpdated += OnGuildMemberUpdatedAsync;
        _client.PresenceUpdated += OnPresenceUpdatedAsync;
    }

    private void Unsubscribe()
    {
        _client.Log -= OnDiscordLogAsync;
        _client.Connected -= OnConnectedAsync;
        _client.Ready -= OnReadyAsync;
        _client.Disconnected -= OnDisconnectedAsync;
        _client.GuildAvailable -= OnGuildAvailableAsync;
        _client.GuildUnavailable -= OnGuildUnavailableAsync;
        _client.MessageReceived -= OnMessageReceivedAsync;
        _client.MessageUpdated -= OnMessageUpdatedAsync;
        _client.MessageDeleted -= OnMessageDeletedAsync;
        _client.MessagesBulkDeleted -= OnMessagesBulkDeletedAsync;
        _client.ReactionAdded -= OnReactionAddedAsync;
        _client.ReactionRemoved -= OnReactionRemovedAsync;
        _client.ReactionsCleared -= OnReactionsClearedAsync;
        _client.ReactionsRemovedForEmote -= OnReactionsRemovedForEmoteAsync;
        _client.PollVoteAdded -= OnPollVoteAddedAsync;
        _client.PollVoteRemoved -= OnPollVoteRemovedAsync;
        _client.ChannelUpdated -= OnChannelUpdatedAsync;
        _client.ChannelDestroyed -= OnChannelDestroyedAsync;
        _client.RoleCreated -= OnRoleCreatedAsync;
        _client.RoleUpdated -= OnRoleUpdatedAsync;
        _client.RoleDeleted -= OnRoleDeletedAsync;
        _client.GuildMemberUpdated -= OnGuildMemberUpdatedAsync;
        _client.PresenceUpdated -= OnPresenceUpdatedAsync;
    }

    private Task OnDiscordLogAsync(LogMessage message)
    {
        if (IsStopping)
        {
            return Task.CompletedTask;
        }

        if (message.Severity <= LogSeverity.Warning)
        {
            _logger.LogWarning(
                "Discord SDK signal source={Source} severity={Severity} category={Category}.",
                SafeSource(message.Source),
                message.Severity,
                message.Exception is null ? "SdkSignal" : SafeExceptionCategory(message.Exception));
        }

        return Task.CompletedTask;
    }

    private Task OnConnectedAsync()
    {
        if (IsStopping)
        {
            return Task.CompletedTask;
        }

        _metrics.Increment(BackendMetric.DiscordConnected);
        Transition(
            BackendConnectionHealthState.Connecting,
            BackendConnectionHealthReason.GatewayConnecting,
            "Discord: Connected; synchronizing target Guild");
        return Task.CompletedTask;
    }

    private Task OnReadyAsync() => HandleSafelyAsync("Ready", async () =>
    {
        ProcessReady();
        await _customStatus.ApplyAfterReadyAsync().ConfigureAwait(false);
    });

    private void ProcessReady()
    {
        _metrics.Increment(BackendMetric.DiscordReady);
        Volatile.Write(ref _hasCompletedReady, 1);
        var guild = _client.GetGuild(_configuration.TargetGuildId);
        if (guild is null)
        {
            Transition(
                BackendConnectionHealthState.TargetGuildUnavailable,
                BackendConnectionHealthReason.TargetGuildMissing,
                "Target Guild: Unavailable");
            _logger.LogWarning(
                "The configured target Guild is unavailable to the Bot account.");
            return;
        }

        Transition(
            BackendConnectionHealthState.Ready,
            BackendConnectionHealthReason.GatewayReady,
            "Discord: Ready");
        _logger.LogInformation("Target Guild: Available");
        _logger.LogInformation("Session Hosts Configured: {Count}", _presenceStore.Count);
        var audit = DiscordPermissionAuditor.Audit(guild);
        _logger.LogInformation(
            "Permission audit: text={TextChannels} viewable={Viewable} history={History} " +
            "futureReactions={Reactions} requiredRead={RequiredRead}.",
            audit.TextChannelCount,
            audit.ViewableChannelCount,
            audit.HistoryReadableChannelCount,
            audit.ReactionCapableChannelCount,
            audit.HasRequiredReadAccess);
        if (!audit.HasRequiredReadAccess)
        {
            _logger.LogWarning(
                "No text channel currently provides both View Channel and Read Message History.");
        }

        if (audit.IsOverPrivileged)
        {
            _logger.LogWarning(
                "The Bot has permissions LS Overlay does not require; Administrator, management, " +
                "and Send Messages are not used.");
        }

        PublishInitialPresence(guild);
    }

    private Task OnGuildAvailableAsync(SocketGuild guild) =>
        HandleSafelyAsync("GuildAvailable", () =>
        {
            if (Volatile.Read(ref _hasCompletedReady) == 0 ||
                !_guildFilter.Accepts(guild.Id))
            {
                return;
            }

            Transition(
                BackendConnectionHealthState.Ready,
                BackendConnectionHealthReason.GatewayReady,
                "Discord: Ready; target Guild available");
            PublishInitialPresence(guild);
        });

    private Task OnGuildUnavailableAsync(SocketGuild guild) =>
        HandleSafelyAsync("GuildUnavailable", () =>
        {
            if (!_guildFilter.Accepts(guild.Id))
            {
                return;
            }

            Transition(
                BackendConnectionHealthState.TargetGuildUnavailable,
                BackendConnectionHealthReason.TargetGuildMissing,
                "Target Guild: Unavailable");
        });

    private Task OnDisconnectedAsync(Exception exception)
    {
        if (IsStopping)
        {
            return Task.CompletedTask;
        }

        _metrics.Increment(BackendMetric.DiscordDisconnected);
        _remoteSales?.MarkUncertain();
        var privilegedIntentFailure = IsPrivilegedIntentRejection(exception);
        Transition(
            BackendConnectionHealthState.Disconnected,
            privilegedIntentFailure
                ? BackendConnectionHealthReason.PrivilegedIntentsRejected
                : BackendConnectionHealthReason.GatewayDisconnected,
            "Discord: Disconnected");
        if (privilegedIntentFailure)
        {
            _logger.LogWarning(
                "Discord rejected required privileged intents. Enable Presence Intent and " +
                "Message Content Intent in the Discord Developer Portal.");
        }
        else
        {
            _logger.LogWarning(
                "Discord Gateway disconnected category={Category}; Discord.Net will manage recovery.",
                SafeExceptionCategory(exception));
        }

        return Task.CompletedTask;
    }

    private Task OnMessageReceivedAsync(SocketMessage message) =>
        HandleSafelyAsync("MessageReceived", async () =>
        {
            PublishMessage(BackendMessageOperation.Create, message);
            if (_remoteChat is not null && message.Channel is SocketGuildChannel guildChannel)
            {
                await _remoteChat.ReceiveCreateAsync(guildChannel.Guild.Id, message)
                    .ConfigureAwait(false);
                if (_remoteSales is not null)
                {
                    await _remoteSales.ReceiveCreateAsync(guildChannel.Guild.Id, message)
                        .ConfigureAwait(false);
                }
            }
        });

    private Task OnMessageUpdatedAsync(
        Cacheable<IMessage, ulong> before,
        SocketMessage after,
        ISocketMessageChannel channel) =>
        HandleSafelyAsync("MessageUpdated", async () =>
        {
            PublishMessage(BackendMessageOperation.Update, after);
            if (_remoteChat is not null)
            {
                await _remoteChat.ReceiveUpdateAsync(channel.Id, after.Id)
                    .ConfigureAwait(false);
            }

            if (_remoteSales is not null && channel is SocketGuildChannel guildChannel)
            {
                await _remoteSales.ReceiveUpdateAsync(
                        guildChannel.Guild.Id,
                        channel.Id,
                        after.Id)
                    .ConfigureAwait(false);
            }
        });

    private Task OnMessageDeletedAsync(
        Cacheable<IMessage, ulong> message,
        Cacheable<IMessageChannel, ulong> channel) =>
        HandleSafelyAsync("MessageDeleted", () =>
        {
            PublishDelete(message.Id, channel);
            _remoteChat?.ReceiveDelete(channel.Id, message.Id);
            if (_remoteSales is not null && TryResolveTargetGuild(channel, out var guildId))
            {
                _remoteSales.ReceiveDelete(guildId, channel.Id, message.Id);
            }
        });

    private Task OnMessagesBulkDeletedAsync(
        IReadOnlyCollection<Cacheable<IMessage, ulong>> messages,
        Cacheable<IMessageChannel, ulong> channel) =>
        HandleSafelyAsync("MessagesBulkDeleted", () =>
        {
            foreach (var message in messages)
            {
                PublishDelete(message.Id, channel);
                _remoteChat?.ReceiveDelete(channel.Id, message.Id);
                if (_remoteSales is not null && TryResolveTargetGuild(channel, out var guildId))
                {
                    _remoteSales.ReceiveDelete(guildId, channel.Id, message.Id);
                }
            }
        });

    private Task OnPollVoteAddedAsync(
        Cacheable<IUser, ulong> user,
        Cacheable<ISocketMessageChannel, IRestMessageChannel, IMessageChannel, ulong> channel,
        Cacheable<IUserMessage, ulong> message,
        Cacheable<SocketGuild, RestGuild, IGuild, ulong>? guild,
        ulong answerId) =>
        HandlePollVoteAsync("PollVoteAdded", channel.Id, message.Id);

    private Task OnPollVoteRemovedAsync(
        Cacheable<IUser, ulong> user,
        Cacheable<ISocketMessageChannel, IRestMessageChannel, IMessageChannel, ulong> channel,
        Cacheable<IUserMessage, ulong> message,
        Cacheable<SocketGuild, RestGuild, IGuild, ulong>? guild,
        ulong answerId) =>
        HandlePollVoteAsync("PollVoteRemoved", channel.Id, message.Id);

    private Task HandlePollVoteAsync(string eventName, ulong channelId, ulong messageId) =>
        HandleSafelyAsync(eventName, () => _remoteChat?.ReceiveUpdateAsync(
            channelId,
            messageId) ?? Task.CompletedTask);

    private Task OnChannelUpdatedAsync(SocketChannel before, SocketChannel after) =>
        HandleSafelyAsync("ChannelUpdated", () =>
        {
            if (after is SocketGuildChannel guildChannel &&
                _guildFilter.Accepts(guildChannel.Guild.Id))
            {
                _remoteChat?.InvalidateGuildAuthorization(guildChannel.Guild.Id);
            }
        });

    private Task OnChannelDestroyedAsync(SocketChannel channel) =>
        HandleSafelyAsync("ChannelDestroyed", () =>
        {
            if (channel is SocketGuildChannel guildChannel &&
                _guildFilter.Accepts(guildChannel.Guild.Id))
            {
                _remoteChat?.ReceiveChannelDeleted(
                    guildChannel.Guild.Id,
                    guildChannel.Id);
                _remoteSales?.ReceiveChannelDeleted(
                    guildChannel.Guild.Id,
                    guildChannel.Id);
            }
        });

    private Task OnRoleCreatedAsync(SocketRole role) =>
        InvalidateRoleAsync("RoleCreated", role);

    private Task OnRoleUpdatedAsync(SocketRole before, SocketRole after) =>
        InvalidateRoleAsync("RoleUpdated", after);

    private Task OnRoleDeletedAsync(SocketRole role) =>
        InvalidateRoleAsync("RoleDeleted", role);

    private Task InvalidateRoleAsync(string eventName, SocketRole role) =>
        HandleSafelyAsync(eventName, () =>
        {
            if (_guildFilter.Accepts(role.Guild.Id))
            {
                _remoteChat?.InvalidateGuildAuthorization(role.Guild.Id);
            }
        });

    private Task OnGuildMemberUpdatedAsync(
        Cacheable<SocketGuildUser, ulong> before,
        SocketGuildUser after) =>
        HandleSafelyAsync("GuildMemberUpdated", () =>
        {
            if (_guildFilter.Accepts(after.Guild.Id))
            {
                _remoteChat?.InvalidateGuildAuthorization(after.Guild.Id);
                _remoteChat?.InvalidateAuthor(after.Guild.Id, after.Id);
            }
        });

    private Task OnReactionAddedAsync(
        Cacheable<IUserMessage, ulong> message,
        Cacheable<IMessageChannel, ulong> channel,
        SocketReaction reaction) =>
        HandleReactionAsync(
            "ReactionAdded",
            BackendReactionOperation.Add,
            message.Id,
            channel,
            reaction.UserId,
            reaction.Emote);

    private Task OnReactionRemovedAsync(
        Cacheable<IUserMessage, ulong> message,
        Cacheable<IMessageChannel, ulong> channel,
        SocketReaction reaction) =>
        HandleReactionAsync(
            "ReactionRemoved",
            BackendReactionOperation.Remove,
            message.Id,
            channel,
            reaction.UserId,
            reaction.Emote);

    private Task OnReactionsClearedAsync(
        Cacheable<IUserMessage, ulong> message,
        Cacheable<IMessageChannel, ulong> channel) =>
        HandleReactionAsync(
            "ReactionsCleared",
            BackendReactionOperation.ClearAll,
            message.Id,
            channel,
            null,
            null);

    private Task OnReactionsRemovedForEmoteAsync(
        Cacheable<IUserMessage, ulong> message,
        Cacheable<IMessageChannel, ulong> channel,
        IEmote emote) =>
        HandleReactionAsync(
            "ReactionsRemovedForEmote",
            BackendReactionOperation.ClearEmoji,
            message.Id,
            channel,
            null,
            emote);

    private Task HandleReactionAsync(
        string eventName,
        BackendReactionOperation operation,
        ulong messageId,
        Cacheable<IMessageChannel, ulong> channel,
        ulong? userId,
        IEmote? emote) => HandleSafelyAsync(eventName, async () =>
        {
            PublishReaction(operation, messageId, channel, userId, emote);
            if (_remoteSales is not null && TryResolveTargetGuild(channel, out var guildId))
            {
                await _remoteSales.ReceiveReactionChangedAsync(
                        guildId,
                        channel.Id,
                        messageId)
                    .ConfigureAwait(false);
            }
        });

    private Task OnPresenceUpdatedAsync(
        SocketUser user,
        SocketPresence before,
        SocketPresence after) =>
        HandleSafelyAsync("PresenceUpdated", () =>
        {
            _metrics.Increment(BackendMetric.PresenceReceived);
            if (!_presenceStore.IsTracked(user.Id))
            {
                _metrics.Increment(BackendMetric.PresenceDiscardedUntracked);
                return;
            }

            if (user is not SocketGuildUser guildUser ||
                !_guildFilter.Accepts(guildUser.Guild.Id))
            {
                _metrics.Increment(BackendMetric.PresenceFilteredOtherGuild);
                return;
            }

            _metrics.Increment(BackendMetric.PresenceTracked);
            ProcessTrackedPresence(user.Id, after.Status, after.Activities);
        });

    private void PublishMessage(
        BackendMessageOperation operation,
        SocketMessage message)
    {
        if (message.Channel is not SocketGuildChannel guildChannel ||
            !_guildFilter.Accepts(guildChannel.Guild.Id))
        {
            _metrics.Increment(BackendMetric.MessageFilteredOtherGuild);
            return;
        }

        var userMessage = message as IUserMessage;
        _journal.Append(new BackendMessageSignal(
            operation,
            guildChannel.Guild.Id,
            message.Channel.Id,
            message.Id,
            message.Author?.Id,
            message.Timestamp,
            DateTimeOffset.UtcNow,
            message.Attachments.Count,
            message.Embeds.Count,
            message.Stickers.Count,
            message.Components.Count,
            userMessage?.ForwardedMessages.Count ?? 0,
            userMessage?.ReferencedMessage is not null,
            userMessage?.Poll is not null));
        _metrics.Increment(operation switch
        {
            BackendMessageOperation.Create => BackendMetric.MessageCreate,
            BackendMessageOperation.Update => BackendMetric.MessageUpdate,
            _ => BackendMetric.MessageDelete,
        });
    }

    private void PublishDelete(
        ulong messageId,
        Cacheable<IMessageChannel, ulong> channel)
    {
        if (!TryResolveTargetGuild(channel, out var guildId))
        {
            _metrics.Increment(BackendMetric.MessageFilteredOtherGuild);
            return;
        }

        _journal.Append(new BackendMessageSignal(
            BackendMessageOperation.Delete,
            guildId,
            channel.Id,
            messageId,
            null,
            null,
            DateTimeOffset.UtcNow,
            0,
            0,
            0,
            0,
            0,
            false,
            false));
        _metrics.Increment(BackendMetric.MessageDelete);
    }

    private void PublishReaction(
        BackendReactionOperation operation,
        ulong messageId,
        Cacheable<IMessageChannel, ulong> channel,
        ulong? userId,
        IEmote? emote)
    {
        if (!TryResolveTargetGuild(channel, out var guildId))
        {
            _metrics.Increment(BackendMetric.ReactionFilteredOtherGuild);
            return;
        }

        ulong? customEmojiId = emote is Emote custom ? custom.Id : null;
        _journal.Append(ReactionIdentityNormalizer.Create(
            operation,
            guildId,
            channel.Id,
            messageId,
            userId,
            customEmojiId,
            emote?.Name,
            DateTimeOffset.UtcNow));
        _metrics.Increment(operation switch
        {
            BackendReactionOperation.Add => BackendMetric.ReactionAdd,
            BackendReactionOperation.Remove => BackendMetric.ReactionRemove,
            _ => BackendMetric.ReactionClear,
        });
    }

    private bool TryResolveTargetGuild(
        Cacheable<IMessageChannel, ulong> channel,
        out ulong guildId)
    {
        var resolved = channel.HasValue
            ? channel.Value
            : _client.GetChannel(channel.Id) as IMessageChannel;
        if (resolved is SocketGuildChannel guildChannel &&
            _guildFilter.Accepts(guildChannel.Guild.Id))
        {
            guildId = guildChannel.Guild.Id;
            return true;
        }

        guildId = 0;
        return false;
    }

    private void PublishInitialPresence(SocketGuild guild)
    {
        foreach (var hostId in _configuration.SessionHostIds)
        {
            var user = guild.GetUser(hostId);
            if (user is null)
            {
                _logger.LogInformation(
                    "Host[{HostIndex}]: Awaiting Presence",
                    _presenceStore.GetStableIndex(hostId));
                continue;
            }

            ProcessTrackedPresence(user.Id, user.Status, user.Activities);
        }
    }

    private void ProcessTrackedPresence(
        ulong hostId,
        UserStatus status,
        IReadOnlyCollection<IActivity> activities)
    {
        var candidates = activities
            .OfType<RichGame>()
            .Select(activity => new BackendActivityCandidate(
                activity.ApplicationId,
                activity.Name,
                activity.Type == ActivityType.Playing,
                activity.State,
                activity.Party?.Members,
                activity.Party?.Capacity))
            .ToArray();
        var normalized = _presenceNormalizer.Normalize(
            hostId,
            MapStatus(status),
            candidates);
        if (normalized.GtaActivityPresent)
        {
            _metrics.Increment(BackendMetric.PresenceGtaActivityMatch);
        }

        if (normalized.CurrentPlayers is not null)
        {
            _metrics.Increment(BackendMetric.PresenceStructuredPartyAvailable);
        }

        if (!_presenceStore.TryUpdate(normalized, out var changed) || changed is null)
        {
            return;
        }

        _metrics.Increment(BackendMetric.PresenceNormalizedChange);
        _journal.Append(changed);
        _remotePublisher?.Publish(changed);
        _transportMetrics?.Increment(TransportMetric.HostPresencePublished);
        LogTrackedPresence(changed);
    }

    private void LogTrackedPresence(TrackedHostPresenceSnapshot snapshot)
    {
        var index = _presenceStore.GetStableIndex(snapshot.HostId);
        if (snapshot.GtaOnlineActive)
        {
            _logger.LogInformation(
                "Host[{HostIndex}]: GTA Online {Current} / {Maximum}",
                index,
                snapshot.CurrentPlayers,
                snapshot.MaximumPlayers);
        }
        else
        {
            _logger.LogInformation(
                "Host[{HostIndex}]: {Status}; GTA session unavailable",
                index,
                snapshot.DiscordStatus);
        }
    }

    private static BackendDiscordPresenceStatus MapStatus(UserStatus status) => status switch
    {
        UserStatus.Online => BackendDiscordPresenceStatus.Online,
        UserStatus.Idle or UserStatus.AFK => BackendDiscordPresenceStatus.Idle,
        UserStatus.DoNotDisturb => BackendDiscordPresenceStatus.DoNotDisturb,
        _ => BackendDiscordPresenceStatus.Offline,
    };

    private Task HandleSafelyAsync(string eventName, Action action)
    {
        if (!_callbackGate.TryEnter())
        {
            return Task.CompletedTask;
        }

        try
        {
            action();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "Discord event normalization failed event={Event} category={Category}.",
                eventName,
                SafeExceptionCategory(exception));
        }
        finally
        {
            _callbackGate.Exit();
        }

        return Task.CompletedTask;
    }

    private async Task HandleSafelyAsync(string eventName, Func<Task> action)
    {
        if (!_callbackGate.TryEnter())
        {
            return;
        }

        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "Discord event normalization failed event={Event} category={Category}.",
                eventName,
                SafeExceptionCategory(exception));
        }
        finally
        {
            _callbackGate.Exit();
        }
    }

    private void Transition(
        BackendConnectionHealthState state,
        BackendConnectionHealthReason reason,
        string safeMessage)
    {
        if (_health.Transition(state, reason))
        {
            _logger.LogInformation("{Status}", safeMessage);
        }
    }

    private static bool IsPrivilegedIntentRejection(Exception exception) =>
        exception.Message.Contains("4014", StringComparison.Ordinal);

    private static string SafeExceptionCategory(Exception? exception) =>
        exception?.GetType().Name ?? "None";

    private static string SafeSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return "Unknown";
        }

        var safe = new string(source
            .Where(character => char.IsAsciiLetterOrDigit(character) ||
                character is '.' or '-' or '_')
            .Take(32)
            .ToArray());
        return safe.Length == 0 ? "Unknown" : safe;
    }
}
