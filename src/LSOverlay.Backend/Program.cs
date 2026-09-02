using Discord.WebSocket;
using LSOverlay.Backend.Configuration;
using LSOverlay.Backend.Chat;
using LSOverlay.Backend.Discord;
using LSOverlay.Backend.Events;
using LSOverlay.Backend.Pairing;
using LSOverlay.Backend.Presence;
using LSOverlay.Backend.Runtime;
using LSOverlay.Backend.Security;
using LSOverlay.Backend.Sales;
using LSOverlay.Backend.Transport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LSOverlay.Backend;

internal static class Program
{
    public static async Task<int> Main()
    {
        var loadResult = BackendConfigurationLoader.Load(Environment.GetEnvironmentVariable);
        if (!loadResult.IsValid || loadResult.Configuration is null)
        {
            Console.Error.WriteLine($"Configuration error: {loadResult.Error}");
            return 2;
        }

        var configuration = loadResult.Configuration;
        try
        {
            var host = CreateHost(configuration);
            var exitState = BackendProcessExitState.Capture(host);
            await host.RunAsync().ConfigureAwait(false);
            return exitState.ExitCode;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Persistent storage unavailable or invalid; verify the attached Volume, " +
                "data directory permissions and credential registry. Startup/shutdown failed safely.");
            return 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"Backend stopped after {exception.GetType().Name}; see safe runtime diagnostics.");
            return 1;
        }
    }

    internal static IHost CreateHost(BackendConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        BackendStoragePreflight.Validate(configuration);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = Array.Empty<string>(),
            ApplicationName = typeof(Program).Assembly.GetName().Name,
        });

        builder.WebHost.UseUrls(configuration.ListenUri.AbsoluteUri);

        // Do not permit the framework's opt-in cloud switch to install an
        // additional, unrestricted forwarded-header middleware ahead of ours.
        builder.Configuration["ForwardedHeaders_Enabled"] = "false";

        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(options =>
        {
            options.SingleLine = true;
            options.TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff zzz ";
        });
        builder.Logging.SetMinimumLevel(LogLevel.Information);
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

        builder.Services.AddSingleton(configuration);
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(20));
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            // Minimal API request binding must use the same enum contract as the
            // protocol client. Without this converter WPF sends "selling" while
            // ASP.NET accepts only a numeric enum and rejects the request as 400
            // before authentication or RemoteSalesActionService can run.
            options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            options.SerializerOptions.PropertyNameCaseInsensitive = false;
            options.SerializerOptions.Converters.Add(
                new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        });
        builder.Services.AddSingleton(new TargetGuildFilter(configuration.TargetGuildId));
        builder.Services.AddSingleton(new TrackedHostPresenceStore(configuration.SessionHostIds));
        builder.Services.AddSingleton(new BackendEventJournal(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        builder.Services.AddSingleton<BackendConnectionHealth>();
        builder.Services.AddSingleton<BackendMetrics>();
        builder.Services.AddSingleton<GtaPresenceNormalizer>();
        builder.Services.AddSingleton<ClientCredentialRegistry>();
        builder.Services.AddSingleton<PairingService>();
        builder.Services.AddSingleton<PairingHealth>();
        builder.Services.AddSingleton<TransportMetrics>();
        builder.Services.AddSingleton<RemotePublicationHub>();
        builder.Services.AddSingleton<IRemotePresencePublisher>(services =>
            services.GetRequiredService<RemotePublicationHub>());
        builder.Services.AddSingleton<RemoteConnectionLimiter>();
        builder.Services.AddSingleton<BackendWebSocketSession>();
        builder.Services.AddSingleton(_ => new DiscordSocketClient(
            DiscordGatewayPolicy.CreateSocketConfiguration()));
        builder.Services.AddSingleton<IGuildMembershipVerifier, DiscordGuildMembershipVerifier>();
        builder.Services.AddSingleton<IChatDiscordSource, DiscordNetChatSource>();
        builder.Services.AddSingleton<IRemoteGuildMemberSource,
            DiscordNetRemoteGuildMemberSource>();
        builder.Services.AddSingleton<CanonicalRemoteAuthorResolver>();
        builder.Services.AddSingleton<IChatAuthorizationService, ChatAuthorizationService>();
        builder.Services.AddSingleton<DiscordChatMessageNormalizer>();
        builder.Services.AddSingleton<ActiveChatStreamRegistry>();
        builder.Services.AddSingleton<RemoteChatService>();
        builder.Services.AddSingleton<ActiveSalesStreamRegistry>();
        builder.Services.AddSingleton<RemoteSalesService>();
        builder.Services.AddSingleton<ISalesStatusDiscordSource,
            DiscordNetSalesStatusSource>();
        builder.Services.AddSingleton<RemoteSalesActionService>();
        builder.Services.AddHostedService<ActiveChatStreamEvictionWorker>();
        builder.Services.AddSingleton<DiscordPairingCommand>();
        builder.Services.AddSingleton<DiscordGatewayAdapter>();
        builder.Services.AddSingleton<IDiscordGatewayLifecycle>(services =>
            services.GetRequiredService<DiscordGatewayAdapter>());
        builder.Services.AddHostedService<DiscordBackendWorker>();
        builder.Services.AddHostedService<RemoteAuthenticationHealthReporter>();
        builder.Services.AddHostedService<DeveloperShutdownWatcher>();
        builder.Services.AddTransportRateLimiting();
        var app = builder.Build();
        if (app.Services.GetRequiredService<ClientCredentialRegistry>().IsFaulted)
        {
            app.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw new IOException("Credential storage validation failed; startup refused.");
        }

        app.Logger.LogInformation(
            "Startup: Environment={Environment}; Hosting={Hosting}; Listener={Listener}; " +
            "Persistent storage=Available; Discord configuration=Valid; Session hosts={Count}.",
            app.Environment.EnvironmentName,
            configuration.Deployment?.IsRailway == true ? "Railway" : "Local",
            configuration.Deployment?.IsRailway == true ? "Railway PORT / internal HTTP" : "Configured local endpoint",
            configuration.SessionHostIds.Count);
        app.MapTransportApi();
        return app;
    }
}

internal sealed class BackendProcessExitState
{
    private readonly BackendConnectionHealth _health;

    private BackendProcessExitState(BackendConnectionHealth health)
    {
        _health = health;
    }

    public int ExitCode => _health.HasFaulted ? 1 : 0;

    public static BackendProcessExitState Capture(IHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        return new BackendProcessExitState(
            host.Services.GetRequiredService<BackendConnectionHealth>());
    }
}
