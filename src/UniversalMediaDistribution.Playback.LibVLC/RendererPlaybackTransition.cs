namespace UniversalMediaDistribution.Playback.LibVLC;

internal sealed record RendererTransitionResult(string? RendererId, bool IsPlaying, Exception? Error);

/// <summary>Once a remote output fails, restore the same media locally.</summary>
internal static class RendererPlaybackTransition
{
    public static async Task<RendererTransitionResult> RunAsync(
        string? rendererId,
        Func<string?, Task> restart)
    {
        try
        {
            await restart(rendererId);
            return new(rendererId, true, null);
        }
        catch (Exception exception)
        {
            if (rendererId is null) return new(null, false, exception);
            try
            {
                await restart(null);
                return new(null, true, exception);
            }
            catch (Exception recoveryException)
            {
                return new(null, false, new AggregateException(exception, recoveryException));
            }
        }
    }
}
