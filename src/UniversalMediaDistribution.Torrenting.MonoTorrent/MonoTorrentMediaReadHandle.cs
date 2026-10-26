namespace UniversalMediaDistribution.Torrenting.MonoTorrent;

internal sealed class MonoTorrentMediaReadHandle(Stream stream) : IMediaReadHandle
{
    public Stream? Stream { get; } = stream ?? throw new ArgumentNullException(nameof(stream));

    public Uri? PlaybackUri => null;

    public async ValueTask DisposeAsync()
    {
        if (Stream is not null)
        {
            await Stream.DisposeAsync();
        }
    }
}
