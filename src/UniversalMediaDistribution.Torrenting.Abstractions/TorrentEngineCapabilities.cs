namespace UniversalMediaDistribution.Torrenting;

public sealed record TorrentEngineCapabilities(
    TorrentEngineKind EngineKind,
    string DisplayName,
    PeerTransport PeerTransport,
    bool SupportsStreaming,
    bool SupportsDownloads,
    bool SupportsSeeding,
    bool SupportsPrivateTorrents,
    bool HasEncryptedPeerTransport,
    bool SupportsMessageStreamEncryption,
    bool SupportsDht,
    bool SupportsPeerExchange,
    bool SupportsLocalPeerDiscovery,
    IReadOnlyList<TrackerTransport> TrackerTransports)
{
    public static TorrentEngineCapabilities MonoTorrentBaseline { get; } = new(
        TorrentEngineKind.MonoTorrent,
        "MonoTorrent",
        PeerTransport.TcpUdp,
        SupportsStreaming: true,
        SupportsDownloads: true,
        SupportsSeeding: true,
        SupportsPrivateTorrents: true,
        HasEncryptedPeerTransport: true,
        SupportsMessageStreamEncryption: true,
        SupportsDht: true,
        SupportsPeerExchange: true,
        SupportsLocalPeerDiscovery: true,
        TrackerTransports: [TrackerTransport.Https, TrackerTransport.Udp]);

    public static TorrentEngineCapabilities WebTorrentBaseline { get; } = new(
        TorrentEngineKind.WebTorrent,
        "WebTorrent",
        PeerTransport.WebRtc,
        SupportsStreaming: true,
        SupportsDownloads: true,
        SupportsSeeding: true,
        SupportsPrivateTorrents: true,
        HasEncryptedPeerTransport: true,
        SupportsMessageStreamEncryption: false,
        SupportsDht: false,
        SupportsPeerExchange: true,
        SupportsLocalPeerDiscovery: false,
        TrackerTransports: [TrackerTransport.Wss]);
}

