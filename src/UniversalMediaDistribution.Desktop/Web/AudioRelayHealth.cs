namespace UniversalMediaDistribution.Desktop.Web;

/// <summary>Audio must actually flow before taking ownership of browser output.</summary>
internal sealed class AudioRelayHealth(long startedAt)
{
    internal const long SilenceTimeoutMilliseconds = 1500;
    private long _lastSignal = startedAt;
    private long _lastWrite = startedAt;
    private bool _receivedSignal;
    private bool _wroteAudio;

    public void Observe(long now, bool signal, bool written)
    {
        if (signal) { _receivedSignal = true; _lastSignal = now; }
        if (written) { _wroteAudio = true; _lastWrite = now; }
    }

    public bool CanTakeOver => _receivedSignal && _wroteAudio;

    public static bool ContainsSignal(ReadOnlySpan<short> samples)
    {
        // PCM conversion can dither digital silence to +/-1. Counting that
        // noise as playback would disable the silence watchdog indefinitely.
        foreach (var sample in samples)
            if (sample is > 3 or < -3) return true;
        return false;
    }

    // Do not consult the source session meter: muting it can also zero its
    // meter on some systems. Even genuine silence should safely return to
    // browser playback instead of leaving a potentially dead relay in control.
    public bool MustRestoreBrowser(long now) =>
        now - _lastSignal >= SilenceTimeoutMilliseconds ||
        now - _lastWrite >= SilenceTimeoutMilliseconds;
}
