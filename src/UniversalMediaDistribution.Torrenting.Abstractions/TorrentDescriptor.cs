namespace UniversalMediaDistribution.Torrenting;

public sealed class TorrentDescriptor
{
    private readonly byte[] _torrentMetadata;

    public TorrentDescriptor(string? magnetUri = null, ReadOnlySpan<byte> torrentMetadata = default)
    {
        if (string.IsNullOrWhiteSpace(magnetUri) && torrentMetadata.IsEmpty)
        {
            throw new ArgumentException("A magnet URI or complete torrent metadata is required.");
        }

        if (!string.IsNullOrWhiteSpace(magnetUri) &&
            (!Uri.TryCreate(magnetUri, UriKind.Absolute, out var uri) ||
             !string.Equals(uri.Scheme, "magnet", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("The supplied magnet URI is invalid.", nameof(magnetUri));
        }

        MagnetUri = string.IsNullOrWhiteSpace(magnetUri) ? null : magnetUri.Trim();
        _torrentMetadata = torrentMetadata.ToArray();
    }

    public string? MagnetUri { get; }

    public ReadOnlyMemory<byte> TorrentMetadata => _torrentMetadata;
}

