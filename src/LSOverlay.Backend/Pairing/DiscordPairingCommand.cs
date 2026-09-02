using Discord;
using Discord.WebSocket;
using LSOverlay.Backend.Configuration;
using LSOverlay.Backend.Transport;
using Microsoft.Extensions.Logging;

namespace LSOverlay.Backend.Pairing;

internal sealed class DiscordPairingCommand
{
    private const string CommandName = "lsoverlay";
    private const string SubcommandName = "pair";
    private const string CodeOptionName = "code";

    private readonly DiscordSocketClient _client;
    private readonly BackendConfiguration _configuration;
    private readonly PairingService _pairing;
    private readonly PairingHealth _health;
    private readonly TransportMetrics _metrics;
    private readonly ILogger<DiscordPairingCommand> _logger;
    private readonly SemaphoreSlim _reconcileGate = new(1, 1);

    public DiscordPairingCommand(
        DiscordSocketClient client,
        BackendConfiguration configuration,
        PairingService pairing,
        PairingHealth health,
        TransportMetrics metrics,
        ILogger<DiscordPairingCommand> logger)
    {
        _client = client;
        _configuration = configuration;
        _pairing = pairing;
        _health = health;
        _metrics = metrics;
        _logger = logger;
    }

    public Task ReconcileAsync() => ReconcileAsync(_client.GetGuild(_configuration.TargetGuildId));

    internal async Task ReconcileAsync(IGuild? guild)
    {
        if (!await _reconcileGate.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            if (guild is null || guild.Id != _configuration.TargetGuildId)
            {
                _health.Set(PairingHealthState.Degraded);
                return;
            }

            // Guild command POST is an upsert by name and type. Always publish the
            // default too: Discord.Net's read model maps both API null and "0" to
            // zero, so a shape/permission-bit comparison cannot detect admin-only
            // defaults reliably. Do not delete the command or its Integration overrides.
            await guild.CreateApplicationCommandAsync(Build()).ConfigureAwait(false);

            _health.Set(PairingHealthState.Available);
            _logger.LogInformation("Discord pairing command: Available");
        }
        catch (Exception exception)
        {
            _health.Set(PairingHealthState.Degraded);
            _logger.LogWarning(
                "Discord pairing command registration unavailable category={Category}.",
                exception.GetType().Name);
        }
        finally
        {
            _reconcileGate.Release();
        }
    }

    public async Task HandleAsync(ISlashCommandInteraction command)
    {
        if (!string.Equals(command.Data.Name, CommandName, StringComparison.Ordinal))
        {
            return;
        }

        var guildId = command.GuildId ?? 0;
        var pairOption = command.Data.Options.FirstOrDefault(option =>
            option.Name == SubcommandName);
        var code = pairOption?.Options.FirstOrDefault(option =>
            option.Name == CodeOptionName)?.Value as string;
        var result = _pairing.Approve(
            guildId,
            command.User.Id,
            command.User.IsBot,
            code ?? string.Empty);
        if (result is PairingApprovalResult.Approved or PairingApprovalResult.AlreadyApproved)
        {
            if (result == PairingApprovalResult.Approved)
            {
                _metrics.Increment(TransportMetric.PairingApproved);
            }

            await command.RespondAsync(
                    "LS Overlay pairing approved. Return to LS Overlay to continue.",
                    ephemeral: true)
                .ConfigureAwait(false);
            return;
        }

        await command.RespondAsync(
                FailureMessage(result),
                ephemeral: true)
            .ConfigureAwait(false);
    }

    internal static SlashCommandProperties Build() => new SlashCommandBuilder()
        .WithName(CommandName)
        .WithDescription("LS Overlay account pairing")
        // Discord.Net 3.20.1 maps null to Unspecified, then serializes the guild
        // upsert's default_member_permissions as explicit JSON null. Numeric zero
        // is NOT unrestricted: Discord interprets "0" as disabled except for admins.
        .WithDefaultMemberPermissions(null)
        .WithContextTypes(InteractionContextType.Guild)
        .AddOption(new SlashCommandOptionBuilder()
            .WithName(SubcommandName)
            .WithDescription("Approve a pairing code")
            .WithType(ApplicationCommandOptionType.SubCommand)
            .AddOption(CodeOptionName, ApplicationCommandOptionType.String,
                "Temporary code displayed by LS Overlay", isRequired: true))
        .Build();

    private static string FailureMessage(PairingApprovalResult result) => result switch
    {
        PairingApprovalResult.Expired => "That LS Overlay pairing code has expired.",
        PairingApprovalResult.Consumed => "That LS Overlay pairing code was already used.",
        PairingApprovalResult.ApprovedByAnotherUser =>
            "That LS Overlay pairing code is already approved by another user.",
        PairingApprovalResult.InvalidGuild =>
            "LS Overlay pairing is available only in the configured server.",
        _ => "That LS Overlay pairing code is invalid.",
    };
}
