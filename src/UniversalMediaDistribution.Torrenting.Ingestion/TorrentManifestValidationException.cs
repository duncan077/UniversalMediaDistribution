namespace UniversalMediaDistribution.Torrenting.Ingestion;

public sealed class TorrentManifestValidationException : Exception
{
    public TorrentManifestValidationException(IEnumerable<string> errors)
        : base("The torrent-local catalog manifest is invalid.")
    {
        Errors = errors.ToArray();
    }

    public IReadOnlyList<string> Errors { get; }
}
