namespace UniversalMediaDistribution.Torrenting;

public sealed record TorrentStoredFile(
    string CompletePath,
    string IncompletePath,
    long Length);

public sealed record TorrentSessionStorage(
    IReadOnlyList<TorrentStoredFile> Files);

public interface ITorrentDownloadStorage
{
    ValueTask<TorrentSessionStorage?> GetSessionStorageAsync(
        TorrentSessionId sessionId,
        CancellationToken cancellationToken = default);

    ValueTask DeleteStoredDataAsync(
        TorrentSessionStorage storage,
        CancellationToken cancellationToken = default);
}
