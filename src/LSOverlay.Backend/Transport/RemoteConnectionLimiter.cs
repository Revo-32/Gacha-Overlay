using LSOverlay.Backend.Security;

namespace LSOverlay.Backend.Transport;

internal sealed class RemoteConnectionLimiter
{
    public const int DefaultGlobalLimit = 64;
    public const int DefaultPerInstallationLimit = 2;

    private readonly object _sync = new();
    private readonly Dictionary<Guid, int> _perInstallation = new();
    private readonly int _globalLimit;
    private readonly int _perInstallationLimit;
    private int _active;

    public RemoteConnectionLimiter(
        int globalLimit = DefaultGlobalLimit,
        int perInstallationLimit = DefaultPerInstallationLimit)
    {
        if (globalLimit <= 0 || perInstallationLimit <= 0 ||
            perInstallationLimit > globalLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(globalLimit));
        }

        _globalLimit = globalLimit;
        _perInstallationLimit = perInstallationLimit;
    }

    public IDisposable? TryAcquire(AuthenticatedClientIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        lock (_sync)
        {
            _perInstallation.TryGetValue(identity.ClientInstallationId, out var count);
            if (_active >= _globalLimit || count >= _perInstallationLimit)
            {
                return null;
            }

            _active++;
            _perInstallation[identity.ClientInstallationId] = count + 1;
            return new Lease(this, identity.ClientInstallationId);
        }
    }

    internal int Active
    {
        get
        {
            lock (_sync)
            {
                return _active;
            }
        }
    }

    internal bool HasCapacity
    {
        get { lock (_sync) { return _active < _globalLimit; } }
    }

    private void Release(Guid installationId)
    {
        lock (_sync)
        {
            _active--;
            var count = _perInstallation[installationId] - 1;
            if (count == 0)
            {
                _perInstallation.Remove(installationId);
            }
            else
            {
                _perInstallation[installationId] = count;
            }
        }
    }

    private sealed class Lease : IDisposable
    {
        private readonly RemoteConnectionLimiter _owner;
        private readonly Guid _installationId;
        private int _disposed;

        public Lease(RemoteConnectionLimiter owner, Guid installationId)
        {
            _owner = owner;
            _installationId = installationId;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _owner.Release(_installationId);
            }
        }
    }
}
