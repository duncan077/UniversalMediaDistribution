namespace UniversalMediaDistribution.UI.Playback;

/// <summary>
/// Plays an authorized embedded-video page in a platform browser without
/// replacing the browser's user agent or JavaScript environment.
/// </summary>
public interface IBackgroundWebVideoPlayer
{
    ValueTask PlayAsync(
        Uri source,
        CancellationToken cancellationToken = default);
}

public sealed class UnavailableBackgroundWebVideoPlayer :
    IBackgroundWebVideoPlayer
{
    public static UnavailableBackgroundWebVideoPlayer Instance { get; } = new();

    private UnavailableBackgroundWebVideoPlayer()
    {
    }

    public ValueTask PlayAsync(
        Uri source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        return ValueTask.FromException(new PlatformNotSupportedException(
            "Background web-video playback is not enabled in this build."));
    }
}
