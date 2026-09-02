namespace LSOverlay.Backend.Configuration;

using LSOverlay.Protocol;

internal static class BackendEnvironmentVariables
{
    public const string BotToken = "LSO_DISCORD_BOT_TOKEN";
    public const string GuildId = "LSO_DISCORD_GUILD_ID";
    public const string SessionHost1Id = "LSO_SESSION_HOST_1_ID";
    public const string SessionHost2Id = "LSO_SESSION_HOST_2_ID";
    public const string TrackedHostIds = "LSO_TRACKED_HOST_IDS";
    public const string StateDirectory = "LSO_STATE_DIRECTORY";
    public const string ListenUrl = "LSO_LISTEN_URL";
    public const string SalesChannelId = "LSO_DISCORD_SALES_CHANNEL_ID";
}

internal sealed class BackendBotCredential
{
    private readonly string _value;

    public BackendBotCredential(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A credential value is required.", nameof(value));
        }

        _value = value;
    }

    internal string RevealForDiscordLogin() => _value;

    public override string ToString() => "[REDACTED]";
}

internal sealed class BackendConfiguration
{
    public const int MaximumSessionHosts = 2;
    public const int MaximumTrackedHostConfigurationLength = 1024;

    public BackendConfiguration(
        BackendBotCredential credential,
        ulong targetGuildId,
        IReadOnlyList<ulong> sessionHostIds,
        string? stateDirectory = null,
        Uri? listenUri = null,
        ulong salesChannelId = RemoteSalesPolicy.ProductionSalesChannelId,
        BackendDeploymentOptions? deployment = null)
    {
        Credential = credential ?? throw new ArgumentNullException(nameof(credential));
        if (targetGuildId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetGuildId));
        }

        ArgumentNullException.ThrowIfNull(sessionHostIds);
        if (sessionHostIds.Count > MaximumSessionHosts)
        {
            throw new ArgumentOutOfRangeException(nameof(sessionHostIds));
        }

        if (sessionHostIds.Any(id => id == 0) ||
            sessionHostIds.Distinct().Count() != sessionHostIds.Count)
        {
            throw new ArgumentException(
                "Session hosts must contain unique non-zero Discord IDs.",
                nameof(sessionHostIds));
        }

        TargetGuildId = targetGuildId;
        if (salesChannelId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(salesChannelId));
        }

        SalesChannelId = salesChannelId;
        SessionHostIds = sessionHostIds.ToArray();
        Deployment = deployment;
        StateDirectory = Path.GetFullPath(deployment?.DataDirectory ?? stateDirectory ??
            Path.Combine(AppContext.BaseDirectory, "state"));
        ListenUri = deployment?.ListenUri ?? listenUri ?? new Uri("http://127.0.0.1:5188");
        if (ListenUri.Scheme is not ("http" or "https") ||
            (deployment?.IsRailway != true && !TransportEndpointSecurity.IsAllowed(ListenUri)))
        {
            throw new ArgumentException(
                "Backend listening URL must use HTTPS, or HTTP on loopback.",
                nameof(listenUri));
        }
    }

    public BackendBotCredential Credential { get; }

    public ulong TargetGuildId { get; }

    public IReadOnlyList<ulong> SessionHostIds { get; }

    public ulong SalesChannelId { get; }

    public string StateDirectory { get; }

    public Uri ListenUri { get; }

    public BackendDeploymentOptions? Deployment { get; }

    public override string ToString() =>
        $"TargetGuild=Configured, SalesChannel=Configured, SessionHosts={SessionHostIds.Count}, Credential=[REDACTED]";
}

internal sealed record BackendConfigurationLoadResult(
    bool IsValid,
    BackendConfiguration? Configuration,
    string? Error)
{
    public static BackendConfigurationLoadResult Success(BackendConfiguration configuration) =>
        new(true, configuration, null);

    public static BackendConfigurationLoadResult Failure(string error) =>
        new(false, null, error);
}

internal static class BackendConfigurationLoader
{
    public static BackendConfigurationLoadResult Load(
        Func<string, string?> environmentValueProvider)
    {
        ArgumentNullException.ThrowIfNull(environmentValueProvider);

        var token = environmentValueProvider(BackendEnvironmentVariables.BotToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            return BackendConfigurationLoadResult.Failure(
                $"{BackendEnvironmentVariables.BotToken} is not configured.");
        }

        var guildValue = environmentValueProvider(BackendEnvironmentVariables.GuildId);
        if (!ulong.TryParse(guildValue?.Trim(), out var guildId) || guildId == 0)
        {
            return BackendConfigurationLoadResult.Failure(
                $"{BackendEnvironmentVariables.GuildId} must be a valid Discord ID.");
        }

        var trackedResult = ParseSessionHostConfiguration(environmentValueProvider);
        if (!trackedResult.IsValid)
        {
            return BackendConfigurationLoadResult.Failure(trackedResult.Error!);
        }

        var salesChannelValue = GetOptionalEnvironmentValue(
            environmentValueProvider,
            BackendEnvironmentVariables.SalesChannelId);
        var salesChannelId = RemoteSalesPolicy.ProductionSalesChannelId;
        if (!string.IsNullOrWhiteSpace(salesChannelValue) &&
            (!ulong.TryParse(salesChannelValue.Trim(), out salesChannelId) || salesChannelId == 0))
        {
            return BackendConfigurationLoadResult.Failure(
                $"{BackendEnvironmentVariables.SalesChannelId} must be a valid Discord ID.");
        }

        try
        {
            var deployment = BackendDeploymentOptions.Resolve(environmentValueProvider);
            return BackendConfigurationLoadResult.Success(new BackendConfiguration(
                new BackendBotCredential(token),
                guildId,
                trackedResult.HostIds,
                salesChannelId: salesChannelId,
                deployment: deployment));
        }
        catch (BackendDeploymentException exception)
        {
            return BackendConfigurationLoadResult.Failure(exception.Message);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return BackendConfigurationLoadResult.Failure(
                $"Backend transport configuration is invalid ({exception.GetType().Name}).");
        }
    }

    public static TrackedHostIdParseResult ParseTrackedHostIds(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return TrackedHostIdParseResult.Success(Array.Empty<ulong>());
        }

        if (value.Length > BackendConfiguration.MaximumTrackedHostConfigurationLength)
        {
            return TrackedHostIdParseResult.Failure(
                $"{BackendEnvironmentVariables.TrackedHostIds} is too long.");
        }

        var unique = new HashSet<ulong>();
        var ordered = new List<ulong>(BackendConfiguration.MaximumSessionHosts);
        foreach (var segment in value.Split(
                     new[] { ',', ';' },
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!ulong.TryParse(segment, out var id) || id == 0)
            {
                return TrackedHostIdParseResult.Failure(
                    $"{BackendEnvironmentVariables.TrackedHostIds} contains an invalid Discord ID.");
            }

            if (!unique.Add(id))
            {
                return TrackedHostIdParseResult.Failure(
                    $"{BackendEnvironmentVariables.TrackedHostIds} contains duplicate Discord IDs.");
            }

            if (ordered.Count >= BackendConfiguration.MaximumSessionHosts)
            {
                return TrackedHostIdParseResult.Failure(
                    $"{BackendEnvironmentVariables.TrackedHostIds} supports at most " +
                    $"{BackendConfiguration.MaximumSessionHosts} hosts.");
            }

            ordered.Add(id);
        }

        return TrackedHostIdParseResult.Success(ordered.ToArray());
    }

    private static TrackedHostIdParseResult ParseSessionHostConfiguration(
        Func<string, string?> environmentValueProvider)
    {
        var host1Value = GetOptionalEnvironmentValue(
            environmentValueProvider,
            BackendEnvironmentVariables.SessionHost1Id);
        var host2Value = GetOptionalEnvironmentValue(
            environmentValueProvider,
            BackendEnvironmentVariables.SessionHost2Id);
        var hasExplicitConfiguration =
            !string.IsNullOrWhiteSpace(host1Value) ||
            !string.IsNullOrWhiteSpace(host2Value);
        if (!hasExplicitConfiguration)
        {
            return ParseTrackedHostIds(GetOptionalEnvironmentValue(
                environmentValueProvider,
                BackendEnvironmentVariables.TrackedHostIds));
        }

        if (!TryParseDiscordId(host1Value, out var host1))
        {
            return TrackedHostIdParseResult.Failure(
                $"{BackendEnvironmentVariables.SessionHost1Id} must be a valid Discord ID " +
                "when Session Host configuration is enabled.");
        }

        if (string.IsNullOrWhiteSpace(host2Value))
        {
            return TrackedHostIdParseResult.Success(new[] { host1 });
        }

        if (!TryParseDiscordId(host2Value, out var host2))
        {
            return TrackedHostIdParseResult.Failure(
                $"{BackendEnvironmentVariables.SessionHost2Id} must be a valid Discord ID.");
        }

        if (host1 == host2)
        {
            return TrackedHostIdParseResult.Failure(
                "Session Host 1 and Session Host 2 must use different Discord IDs.");
        }

        return TrackedHostIdParseResult.Success(new[] { host1, host2 });
    }

    private static bool TryParseDiscordId(string? value, out ulong id) =>
        ulong.TryParse(value?.Trim(), out id) && id != 0;

    private static string? GetOptionalEnvironmentValue(
        Func<string, string?> provider,
        string name)
    {
        try
        {
            return provider(name);
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
    }
}

internal sealed record TrackedHostIdParseResult(
    bool IsValid,
    IReadOnlyList<ulong> HostIds,
    string? Error)
{
    public static TrackedHostIdParseResult Success(IReadOnlyList<ulong> ids) =>
        new(true, ids, null);

    public static TrackedHostIdParseResult Failure(string error) =>
        new(false, Array.Empty<ulong>(), error);
}
