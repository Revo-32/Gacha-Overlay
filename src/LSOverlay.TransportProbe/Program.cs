using LSOverlay.Protocol;
using LSOverlay.RemoteClient;
using System.Threading.Channels;

namespace LSOverlay.TransportProbe;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var endpoint = args.Length > 0
            ? args[0]
            : "http://127.0.0.1:5188";
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var baseUri) ||
            !TransportEndpointSecurity.IsAllowed(baseUri))
        {
            Console.Error.WriteLine("Transport Probe endpoint is invalid or insecure.");
            return 2;
        }

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };

        try
        {
            await using var client = new LSOverlayRemoteClient(baseUri);
            var installationId = Guid.NewGuid();
            var pairing = await client.CreatePairingAsync(installationId, shutdown.Token);
            Console.WriteLine("Pairing: Waiting");
            Console.WriteLine($"LS Overlay pairing code: {pairing.UserCode}");
            Console.WriteLine($"In Discord run: /lsoverlay pair {pairing.UserCode}");

            string? accessToken = null;
            while (!shutdown.IsCancellationRequested &&
                   DateTimeOffset.UtcNow < pairing.ExpiresAt)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), shutdown.Token);
                var status = await client.GetPairingAsync(
                    pairing.PairingId,
                    pairing.PairingClaimSecret,
                    shutdown.Token);
                if (status.State == PairingState.Approved &&
                    !string.IsNullOrWhiteSpace(status.AccessToken))
                {
                    accessToken = status.AccessToken;
                    break;
                }

                if (status.State is PairingState.Expired or PairingState.Consumed)
                {
                    Console.Error.WriteLine($"Pairing: {status.State}");
                    return 3;
                }
            }

            if (accessToken is null)
            {
                Console.Error.WriteLine("Pairing: Expired");
                return 3;
            }

            Console.WriteLine("Pairing: Approved");
            Console.WriteLine("Authenticated: Yes");
            var bootstrap = await client.GetBootstrapAsync(accessToken, shutdown.Token);
            Console.WriteLine("Bootstrap: OK");
            foreach (var host in bootstrap.TrackedHosts)
            {
                PrintHost(host);
            }

            var catalog = await client.GetChatChannelsAsync(accessToken, shutdown.Token);
            if (catalog.Channels.Count == 0)
            {
                Console.Error.WriteLine("Chat catalog: no channels authorized for both user and Bot.");
                return 4;
            }

            PrintCatalog(catalog);
            var selected = ReadChannelNumber(catalog);
            var chatBootstrap = await client.GetChatBootstrapAsync(
                accessToken,
                selected.ChannelId,
                shutdown.Token);
            Console.WriteLine("Chat Bootstrap: OK");
            Console.WriteLine($"Recent Messages: {chatBootstrap.RecentMessages.Count}");
            foreach (var message in chatBootstrap.RecentMessages)
            {
                PrintMessage("bootstrap", message);
            }

            client.StreamLive += () => Console.WriteLine("Stream: Live");
            client.HostPresenceChanged += PrintHost;
            client.ChatChannelReady += ready => Console.WriteLine(
                $"Chat Stream: Live #{ready.Channel.Name} sequence={ready.LatestSequence}");
            client.ChatMutationReceived += mutation =>
            {
                if (mutation.Message is not null)
                {
                    PrintMessage(mutation.EventType, mutation.Message);
                }
                else
                {
                    Console.WriteLine(
                        $"{mutation.EventType}: channel={mutation.ChannelId} message={mutation.MessageId}");
                }
            };
            client.ChatStreamStatusChanged += (channelId, status) =>
                Console.WriteLine($"Chat status: channel={channelId} {status}");

            var switches = Channel.CreateBounded<ChatBootstrapResponse>(4);
            _ = ReadSwitchesAsync(
                client,
                accessToken,
                catalog,
                switches.Writer,
                shutdown.Token);
            await client.StreamChatAsync(
                accessToken,
                bootstrap,
                chatBootstrap,
                switches.Reader,
                shutdown.Token);
            return 0;
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"Transport Probe failed category={exception.GetType().Name}.");
            return 1;
        }
    }

    private static void PrintHost(HostPresenceSnapshot host)
    {
        if (host.State == HostPresenceState.GtaOnline)
        {
            Console.WriteLine(
                $"Host[{host.HostSlot}]: GTA Online {host.CurrentPlayers} / {host.MaximumPlayers}");
        }
        else
        {
            Console.WriteLine($"Host[{host.HostSlot}]: {host.State}");
        }
    }

    private static void PrintCatalog(ChatChannelCatalogResponse catalog)
    {
        Console.WriteLine("Authorized Channels:");
        for (var index = 0; index < catalog.Channels.Count; index++)
        {
            var channel = catalog.Channels[index];
            Console.WriteLine(
                $"  {index + 1}. #{channel.Name}{(channel.IsAnnouncement ? " [news]" : string.Empty)}");
        }
    }

    private static ChatChannelDescriptor ReadChannelNumber(
        ChatChannelCatalogResponse catalog)
    {
        while (true)
        {
            Console.Write("Select Main Channel NUMBER: ");
            var input = Console.ReadLine();
            if (int.TryParse(input, out var number) &&
                number >= 1 && number <= catalog.Channels.Count)
            {
                return catalog.Channels[number - 1];
            }

            Console.WriteLine("Enter one of the channel numbers shown above.");
        }
    }

    private static async Task ReadSwitchesAsync(
        LSOverlayRemoteClient client,
        string accessToken,
        ChatChannelCatalogResponse catalog,
        ChannelWriter<ChatBootstrapResponse> writer,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                Console.Write("Switch channel NUMBER (Ctrl+C to stop): ");
                var input = await Task.Run(Console.ReadLine, cancellationToken)
                    .ConfigureAwait(false);
                if (!int.TryParse(input, out var number) ||
                    number < 1 || number > catalog.Channels.Count)
                {
                    Console.WriteLine("Invalid channel number.");
                    continue;
                }

                var channel = catalog.Channels[number - 1];
                var bootstrap = await client.GetChatBootstrapAsync(
                        accessToken,
                        channel.ChannelId,
                        cancellationToken)
                    .ConfigureAwait(false);
                await writer.WriteAsync(bootstrap, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal probe shutdown.
        }
        finally
        {
            writer.TryComplete();
        }
    }

    private static void PrintMessage(string operation, ChatMessage message)
    {
        Console.WriteLine(ChatProbeFormatter.Format(operation, message));
    }
}
