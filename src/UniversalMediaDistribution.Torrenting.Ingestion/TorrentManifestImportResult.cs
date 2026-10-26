using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Torrenting;

namespace UniversalMediaDistribution.Torrenting.Ingestion;

public enum TorrentManifestImportStatus
{
    NotFound = 1,
    Imported = 2,
    Pending = 3
}

public sealed record TorrentManifestImportResult(
    TorrentManifestImportStatus Status,
    string? ManifestPath = null,
    CatalogImportResult? CatalogImport = null)
{
    public static TorrentManifestImportResult NotFound { get; } = new(TorrentManifestImportStatus.NotFound);

    public static TorrentManifestImportResult Pending { get; } = new(TorrentManifestImportStatus.Pending);
}

public sealed class TorrentManifestImportCompletedEventArgs(
    TorrentSessionId sessionId,
    TorrentManifestImportResult? result,
    string? warning) : EventArgs
{
    public TorrentSessionId SessionId { get; } = sessionId;

    public TorrentManifestImportResult? Result { get; } = result;

    public string? Warning { get; } = warning;
}
