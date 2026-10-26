namespace UniversalMediaDistribution.Torrenting.Ingestion;

public sealed record RegisteredTorrentSession(TorrentSessionId SessionId, bool IsPrivate);

public sealed class TorrentSessionRegistry
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, RegisteredTorrentSession> _sessions = new(StringComparer.Ordinal);

    public bool TryGet(IEnumerable<string> identities, out RegisteredTorrentSession session)
    {
        ArgumentNullException.ThrowIfNull(identities);
        lock (_gate)
        {
            foreach (var identity in identities)
            {
                if (!string.IsNullOrWhiteSpace(identity) &&
                    _sessions.TryGetValue(identity.Trim(), out session!))
                {
                    return true;
                }
            }
        }

        session = null!;
        return false;
    }

    public void Register(
        IEnumerable<string> identities,
        TorrentSessionId sessionId,
        bool isPrivate)
    {
        ArgumentNullException.ThrowIfNull(identities);
        if (sessionId.Value == Guid.Empty)
        {
            throw new ArgumentException("A torrent session identifier is required.", nameof(sessionId));
        }

        lock (_gate)
        {
            var session = new RegisteredTorrentSession(sessionId, isPrivate);
            foreach (var identity in identities
                         .Where(static identity => !string.IsNullOrWhiteSpace(identity))
                         .Select(static identity => identity.Trim())
                         .Distinct(StringComparer.Ordinal))
            {
                _sessions[identity] = session;
            }
        }
    }
}
