namespace UniversalMediaDistribution.Torrenting;

public sealed record TorrentFileEntry(int Index, string Path, long Length, string? MimeType = null);

