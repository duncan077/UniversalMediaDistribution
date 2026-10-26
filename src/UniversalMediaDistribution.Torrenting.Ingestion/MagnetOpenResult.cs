using UniversalMediaDistribution.Torrenting;

namespace UniversalMediaDistribution.Torrenting.Ingestion;

public enum MagnetOpenStatus
{
    ConfirmationRequired = 1,
    EngineUnavailable = 2,
    Opened = 3,
    AlreadyOpen = 4
}

public sealed record MagnetOpenResult(
    MagnetOpenStatus Status,
    MagnetLink MagnetLink,
    TorrentSessionId? SessionId = null,
    TorrentManifestImportResult? ManifestImport = null,
    string? Warning = null);
