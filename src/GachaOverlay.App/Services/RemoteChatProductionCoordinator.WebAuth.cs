using System.IO;
using System.Net.Http;
using LSOverlay.Protocol;
using LSOverlay.RemoteClient;

namespace GachaOverlay.App.Services;

internal sealed partial class RemoteChatProductionCoordinator
{
    private async Task<bool> TryWebLoginAsync(ILSOverlayDiscordWebAuthClient web, Uri endpoint, CancellationToken cancellationToken)
    {
        DiscordWebAuthStartResponse? session = null;
        var claimed = false;
        try
        {
            session = await web.StartDiscordWebAuthAsync(GetOrCreateInstallationId(), cancellationToken).ConfigureAwait(false);
            if (session is null)
            {
                if (endpoint.IsLoopback) return false; // Temporary developer/admin local fallback only.
                SetHealth(RemoteChatHealthState.PairingRequired, "WebAuthUnavailable");
                return true;
            }
            cancellationToken.ThrowIfCancellationRequested();
            UpdateSnapshot(current => current with
            {
                Health = RemoteChatHealthState.PairingInProgress,
                Detail = "WebAuthWaiting",
                PairingCode = null,
                PairingExpiresAt = session.ExpiresAt,
            });
            _openBrowser(new Uri(session.AuthorizationUrl));
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromMinutes(5));
            while (DateTimeOffset.UtcNow < session.ExpiresAt)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), deadline.Token).ConfigureAwait(false);
                var result = await web.GetDiscordWebAuthStatusAsync(session.SessionId, session.ClaimSecret, deadline.Token).ConfigureAwait(false);
                deadline.Token.ThrowIfCancellationRequested();
                if (result.Status == DiscordWebAuthStatus.Pending) continue;
                if (result.Status == DiscordWebAuthStatus.Approved && !string.IsNullOrWhiteSpace(result.AccessToken))
                {
                    claimed = true;
                    if (!_credentialStore.Save(result.AccessToken))
                    {
                        SetHealth(RemoteChatHealthState.Error, "ProtectedSaveFailed");
                        return true;
                    }
                    UpdateSnapshot(current => current with
                    {
                        Health = RemoteChatHealthState.Disconnected,
                        Detail = "WebAuthCompleted",
                        HasProtectedCredential = true,
                        PairingCode = null,
                        PairingExpiresAt = null,
                    });
                    _logger.Information("REMOTE", "Discord browser login completed; Remote credential protected.");
                    StartSession();
                    return true;
                }
                var failure = result.Failure == DiscordWebAuthFailure.None ? DiscordWebAuthFailure.InvalidRequest : result.Failure;
                SetHealth(RemoteChatHealthState.PairingRequired, "WebAuth" + failure);
                return true;
            }
            SetHealth(RemoteChatHealthState.PairingRequired, "WebAuthSessionExpired");
        }
        catch (OperationCanceledException)
        {
            SetHealth(RemoteChatHealthState.PairingRequired, cancellationToken.IsCancellationRequested ? "WebAuthCancelled" : "WebAuthTemporaryFailure");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or
            System.ComponentModel.Win32Exception or InvalidOperationException or System.Text.Json.JsonException)
        {
            _logger.Warning("REMOTE", "Browser login could not complete; retry is available.");
            SetHealth(RemoteChatHealthState.PairingRequired, "WebAuthTemporaryFailure");
        }
        finally
        {
            if (session is not null && !claimed)
            {
                // Stop polling immediately; cancellation is best-effort with its own short deadline.
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await web.CancelDiscordWebAuthAsync(session.SessionId, session.ClaimSecret, cleanup.Token).ConfigureAwait(false); }
                catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or IOException) { }
            }
        }
        return true;
    }
}
