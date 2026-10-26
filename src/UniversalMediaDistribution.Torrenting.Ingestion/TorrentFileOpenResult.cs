using UniversalMediaDistribution.Torrenting;

namespace UniversalMediaDistribution.Torrenting.Ingestion;

public enum TorrentFileOpenStatus
{
    EngineUnavailable = 1,
    Opened = 2,
    AlreadyOpen = 3
}

public sealed record TorrentFileOpenResult(
    TorrentFileOpenStatus Status,
    TorrentMetadataSummary Metadata,
    TorrentSessionId? SessionId = null,
    TorrentManifestImportResult? ManifestImport = null,
    string? Warning = null);
