namespace UniversalMediaDistribution.Torrenting;

public interface IMediaReadHandle : IAsyncDisposable
{
    Stream? Stream { get; }

    Uri? PlaybackUri { get; }
}

