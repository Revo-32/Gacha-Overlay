using System.IO;
using System.Security.Cryptography;
using System.Text;
using GachaOverlay.Core.Logging;

namespace GachaOverlay.App.Services;

internal sealed class DpapiRemoteAccessCredentialStore : IRemoteAccessCredentialStore
{
    private static readonly byte[] OptionalEntropy =
        Encoding.UTF8.GetBytes("LSOverlay.M9.4.RemoteAccessCredential");

    private readonly string _path;
    private readonly IAppLogger _logger;

    public DpapiRemoteAccessCredentialStore(string path, IAppLogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public RemoteCredentialStatus Status => !File.Exists(_path)
        ? RemoteCredentialStatus.Missing
        : TryLoad(out _)
            ? RemoteCredentialStatus.Available
            : RemoteCredentialStatus.Unreadable;

    public bool TryLoad(out string? accessToken)
    {
        accessToken = null;
        if (!File.Exists(_path))
        {
            return false;
        }

        byte[]? clearBytes = null;
        try
        {
            clearBytes = ProtectedData.Unprotect(
                File.ReadAllBytes(_path),
                OptionalEntropy,
                DataProtectionScope.CurrentUser);
            accessToken = Encoding.UTF8.GetString(clearBytes);
            return !string.IsNullOrWhiteSpace(accessToken);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or CryptographicException)
        {
            _logger.Warning(
                "REMOTE",
                $"Protected remote credential could not be read ({exception.GetType().Name}).");
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

    public bool Save(string accessToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        var temporaryPath = $"{_path}.{Environment.ProcessId}.tmp";
        byte[]? clearBytes = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)
                ?? throw new InvalidOperationException("Remote credential directory is invalid."));
            clearBytes = Encoding.UTF8.GetBytes(accessToken);
            var protectedBytes = ProtectedData.Protect(
                clearBytes,
                OptionalEntropy,
                DataProtectionScope.CurrentUser);
            File.WriteAllBytes(temporaryPath, protectedBytes);
            File.Move(temporaryPath, _path, overwrite: true);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or CryptographicException)
        {
            TryDelete(temporaryPath);
            _logger.Error("REMOTE", "Protected remote credential could not be saved.", exception);
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

    public bool Clear()
    {
        try
        {
            File.Delete(_path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.Warning(
                "REMOTE",
                $"Protected remote credential could not be cleared ({exception.GetType().Name}).");
            return false;
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
