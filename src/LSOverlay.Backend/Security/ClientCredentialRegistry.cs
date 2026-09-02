using System.Text.Json;
using LSOverlay.Backend.Configuration;

namespace LSOverlay.Backend.Security;

internal sealed record IssuedClientCredential(
    string AccessToken,
    DateTimeOffset ExpiresAt);

internal sealed record ClientCredentialRecord(
    Guid ClientInstallationId,
    ulong DiscordUserId,
    ulong GuildId,
    string AccessTokenHash,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

internal sealed record ClientCredentialRegistryDocument(
    int SchemaVersion,
    IReadOnlyList<ClientCredentialRecord> Credentials);

internal sealed class ClientCredentialRegistry
{
    public const int SchemaVersion = 1;
    public const int MaximumCredentials = 128;
    public static readonly TimeSpan CredentialLifetime = TimeSpan.FromDays(180);

    private readonly object _sync = new();
    private readonly string _path;
    private readonly string _backupPath;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ulong? _expectedGuildId;
    private IReadOnlyList<ClientCredentialRecord> _records = Array.Empty<ClientCredentialRecord>();
    private Exception? _loadFailure;

    public ClientCredentialRegistry(
        BackendConfiguration configuration,
        Func<DateTimeOffset>? clock = null)
        : this(configuration.StateDirectory, clock, configuration.TargetGuildId)
    {
    }

    internal ClientCredentialRegistry(
        string stateDirectory,
        Func<DateTimeOffset>? clock = null,
        ulong? expectedGuildId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _expectedGuildId = expectedGuildId;
        _path = Path.Combine(Path.GetFullPath(stateDirectory), "client-credentials.v1.json");
        _backupPath = _path + ".bak";
        Load();
    }

    public bool IsFaulted
    {
        get
        {
            lock (_sync)
            {
                return _loadFailure is not null;
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _records.Count;
            }
        }
    }

    public IssuedClientCredential Issue(
        Guid installationId,
        ulong discordUserId,
        ulong guildId)
    {
        if (installationId == Guid.Empty || discordUserId == 0 || guildId == 0)
        {
            throw new ArgumentException("A complete authenticated client identity is required.");
        }

        lock (_sync)
        {
            ThrowIfFaulted();
            var now = _clock();
            var retained = _records
                .Where(record => record.ExpiresAt > now &&
                    record.ClientInstallationId != installationId)
                .ToList();
            if (retained.Count >= MaximumCredentials)
            {
                throw new InvalidOperationException("Client credential registry capacity is exhausted.");
            }

            var token = CryptographicSecrets.CreateAccessToken();
            var expiresAt = now.Add(CredentialLifetime);
            retained.Add(new ClientCredentialRecord(
                installationId,
                discordUserId,
                guildId,
                CryptographicSecrets.HashHex(token),
                now,
                expiresAt));
            SaveValidated(retained);
            _records = retained;
            return new IssuedClientCredential(token, expiresAt);
        }
    }

    public AuthenticatedClientIdentity? Authenticate(string accessToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return null;
        }

        lock (_sync)
        {
            if (_loadFailure is not null)
            {
                return null;
            }

            var hash = CryptographicSecrets.Hash(accessToken);
            var now = _clock();
            foreach (var record in _records)
            {
                if (record.ExpiresAt <= now ||
                    (_expectedGuildId is ulong expectedGuildId &&
                     record.GuildId != expectedGuildId) ||
                    !TryDecodeHash(record.AccessTokenHash, out var expected) ||
                    !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                        hash,
                        expected))
                {
                    continue;
                }

                return new AuthenticatedClientIdentity(
                    record.ClientInstallationId,
                    record.DiscordUserId,
                    record.GuildId);
            }

            return null;
        }
    }

    internal IReadOnlyList<ClientCredentialRecord> Snapshot()
    {
        lock (_sync)
        {
            return _records.ToArray();
        }
    }

    private void Load()
    {
        lock (_sync)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                if (!File.Exists(_path) && !File.Exists(_backupPath))
                {
                    _records = Array.Empty<ClientCredentialRecord>();
                    return;
                }

                if (TryReadValidated(_path, out var primary))
                {
                    _records = primary;
                    return;
                }

                if (TryReadValidated(_backupPath, out var backup))
                {
                    _records = backup;
                    RestorePrimaryFromBackup();
                    return;
                }

                _loadFailure = new InvalidDataException(
                    "Both primary and backup client credential registries are invalid.");
            }
            catch (Exception exception)
            {
                _loadFailure = exception;
            }
        }
    }

    private void SaveValidated(IReadOnlyList<ClientCredentialRecord> records)
    {
        Validate(records);
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $"client-credentials.{Guid.NewGuid():N}.tmp");
        try
        {
            var document = new ClientCredentialRegistryDocument(SchemaVersion, records);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            if (!TryReadValidated(temporaryPath, out var validated) ||
                validated.Count != records.Count)
            {
                throw new InvalidDataException("Temporary credential registry validation failed.");
            }

            if (File.Exists(_path))
            {
                try
                {
                    File.Replace(temporaryPath, _path, _backupPath, ignoreMetadataErrors: true);
                }
                catch (PlatformNotSupportedException)
                {
                    File.Copy(_path, _backupPath, overwrite: true);
                    File.Move(temporaryPath, _path, overwrite: true);
                }
            }
            else
            {
                File.Move(temporaryPath, _path);
                File.Copy(_path, _backupPath, overwrite: true);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private void RestorePrimaryFromBackup()
    {
        var directory = Path.GetDirectoryName(_path)!;
        var recoveryPath = Path.Combine(
            directory,
            $"client-credentials.recovery.{Guid.NewGuid():N}.tmp");
        try
        {
            File.Copy(_backupPath, recoveryPath, overwrite: false);
            if (!TryReadValidated(recoveryPath, out _))
            {
                throw new InvalidDataException("Credential registry backup recovery validation failed.");
            }

            File.Move(recoveryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(recoveryPath))
            {
                File.Delete(recoveryPath);
            }
        }
    }

    private static bool TryReadValidated(
        string path,
        out IReadOnlyList<ClientCredentialRecord> records)
    {
        records = Array.Empty<ClientCredentialRecord>();
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            var bytes = File.ReadAllBytes(path);
            var document = JsonSerializer.Deserialize<ClientCredentialRegistryDocument>(
                bytes,
                JsonOptions);
            if (document is null || document.SchemaVersion != SchemaVersion)
            {
                return false;
            }

            Validate(document.Credentials);
            records = document.Credentials.ToArray();
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or
            InvalidDataException or ArgumentException)
        {
            return false;
        }
    }

    private static void Validate(IReadOnlyList<ClientCredentialRecord>? records)
    {
        if (records is null || records.Count > MaximumCredentials)
        {
            throw new InvalidDataException("Credential registry is invalid or exceeds capacity.");
        }

        var installations = new HashSet<Guid>();
        foreach (var record in records)
        {
            if (record.ClientInstallationId == Guid.Empty ||
                record.DiscordUserId == 0 ||
                record.GuildId == 0 ||
                record.CreatedAt >= record.ExpiresAt ||
                !installations.Add(record.ClientInstallationId) ||
                !TryDecodeHash(record.AccessTokenHash, out _))
            {
                throw new InvalidDataException("Credential registry contains an invalid record.");
            }
        }
    }

    private static bool TryDecodeHash(string value, out byte[] hash)
    {
        hash = Array.Empty<byte>();
        if (value.Length != 64)
        {
            return false;
        }

        try
        {
            hash = Convert.FromHexString(value);
            return hash.Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private void ThrowIfFaulted()
    {
        if (_loadFailure is not null)
        {
            throw new InvalidOperationException(
                "Remote authentication is unavailable because credential storage failed closed.",
                _loadFailure);
        }
    }

    private static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
}
