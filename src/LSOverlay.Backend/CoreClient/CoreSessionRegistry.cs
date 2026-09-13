using System.Collections.Concurrent;
using LSOverlay.Backend.Security;

namespace LSOverlay.Backend.CoreClient;

// One live connection per installation. No global current channel/media owner.
internal sealed class CoreSessionRegistry
{
    private readonly ConcurrentDictionary<Guid, CoreUserSession> _sessions = new();
    public bool Add(CoreUserSession session) => _sessions.TryAdd(session.Identity.ClientInstallationId, session);
    public CoreUserSession? Find(AuthenticatedClientIdentity identity) =>
        _sessions.TryGetValue(identity.ClientInstallationId, out var session) && session.Identity == identity && !session.Cancelled ? session : null;
    public void Remove(CoreUserSession session) =>
        ((ICollection<KeyValuePair<Guid, CoreUserSession>>)_sessions).Remove(new(session.Identity.ClientInstallationId, session));
}
