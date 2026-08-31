using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO;
using GachaOverlay.Core.Logging;
using GachaOverlay.Infrastructure.Discord.Authentication;

namespace GachaOverlay.App.Services;

internal sealed class DpapiDiscordProtectedCredentialStore : IDiscordProtectedCredentialStore
{
    private static readonly byte[] OptionalEntropy =
        Encoding.UTF8.GetBytes("GachaOverlay.M4.4.DiscordProtectedStorage");

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly string _clientSecretPath;
    private readonly string _oauthTokenPath;
    private readonly IAppLogger _logger;

    public DpapiDiscordProtectedCredentialStore(
        string clientSecretPath,
        string oauthTokenPath,
        IAppLogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientSecretPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(oauthTokenPath);
        _clientSecretPath = clientSecretPath;
        _oauthTokenPath = oauthTokenPath;
        _logger = logger;
    }

    public ProtectedCredentialStatus ClientSecretStatus => GetStatus<string>(_clientSecretPath);

    public ProtectedCredentialStatus OAuthTokenStatus =>
        GetStatus<DiscordOAuthToken>(_oauthTokenPath);

    public bool TryLoadClientSecret(out string? clientSecret) =>
        TryLoad(_clientSecretPath, out clientSecret);

    public bool SaveClientSecret(string clientSecret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientSecret);
        return Save(_clientSecretPath, clientSecret);
    }

    public bool TryLoadOAuthToken(out DiscordOAuthToken? token) =>
        TryLoad(_oauthTokenPath, out token);

    public bool SaveOAuthToken(DiscordOAuthToken token)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentException.ThrowIfNullOrWhiteSpace(token.AccessToken);
        return Save(_oauthTokenPath, token);
    }

    public void ClearOAuthToken()
    {
        try
        {
            File.Delete(_oauthTokenPath);
        }
        catch (Exception exception)
        {
            _logger.Warning(
                "AUTH",
                $"Protected OAuth state could not be cleared ({exception.GetType().Name}).");
        }
    }

    private ProtectedCredentialStatus GetStatus<T>(string path) =>
        !File.Exists(path)
            ? ProtectedCredentialStatus.Missing
            : TryLoad<T>(path, out _)
                ? ProtectedCredentialStatus.Available
                : ProtectedCredentialStatus.Unreadable;

    private bool TryLoad<T>(string path, out T? value)
    {
        value = default;
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            var protectedBytes = File.ReadAllBytes(path);
            var clearBytes = ProtectedData.Unprotect(
                protectedBytes,
                OptionalEntropy,
                DataProtectionScope.CurrentUser);
            try
            {
                value = JsonSerializer.Deserialize<T>(clearBytes, SerializerOptions);
                return value is not null;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(clearBytes);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        {
            _logger.Warning(
                "AUTH",
                $"Protected credential state could not be read ({exception.GetType().Name}); user action is required.");
            return false;
        }
    }

    private bool Save<T>(string path, T value)
    {
        var temporaryPath = $"{path}.{Environment.ProcessId}.tmp";
        byte[]? clearBytes = null;
        try
        {
            var directory = Path.GetDirectoryName(path)
                ?? throw new InvalidOperationException("Protected credential directory is invalid.");
            Directory.CreateDirectory(directory);
            clearBytes = JsonSerializer.SerializeToUtf8Bytes(value, SerializerOptions);
            var protectedBytes = ProtectedData.Protect(
                clearBytes,
                OptionalEntropy,
                DataProtectionScope.CurrentUser);
            File.WriteAllBytes(temporaryPath, protectedBytes);
            File.Move(temporaryPath, path, overwrite: true);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or CryptographicException)
        {
            TryDelete(temporaryPath);
            _logger.Error(
                "AUTH",
                "Protected credential state could not be saved; the previous state was preserved.",
                exception);
            return false;
        }
        finally
        {
            if (clearBytes is not null)
            {
                CryptographicOperations.ZeroMemory(clearBytes);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }
}
