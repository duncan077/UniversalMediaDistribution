namespace UniversalMediaDistribution.Torrenting;

public sealed record TorrentAddOptions(
    bool IsPrivate,
    bool PreferSequentialDownload,
    IReadOnlyList<TrackerEndpoint> Trackers,
    bool SelectAllFiles = true)
{
    public static TorrentAddOptions PublicDefault { get; } = new(false, false, []);

    public override string ToString() =>
        $"Torrent add options: private={IsPrivate}, sequential={PreferSequentialDownload}, " +
        $"selectAll={SelectAllFiles}, trackers={Trackers.Count}";
}
