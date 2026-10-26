using UniversalMediaDistribution.UI.Playback;

namespace UniversalMediaDistribution.Mobile;

public enum AndroidPlaybackEngine { System, LibVlc }

/// <summary>Portable selection/options policy, exercised by the PC tests.</summary>
public static class AndroidPlaybackEnginePolicy
{
    // LibVLC 3 cannot set arbitrary Origin headers. Keep those streams on the
    // system player rather than silently dropping a provider requirement.
    public static bool CanUseLibVlc(PlatformMediaSource source) =>
        source.HttpOrigin is null && !source.RequiresClearKeyDecryption;

    public static AndroidPlaybackEngine Initial(bool television, bool live, PlatformMediaSource source) =>
        CanUseLibVlc(source)
            ? AndroidPlaybackEngine.LibVlc : AndroidPlaybackEngine.System;

    public static AndroidPlaybackEngine? Fallback(
        bool television, PlatformMediaSource source, AndroidPlaybackEngine current,
        IReadOnlySet<AndroidPlaybackEngine> attempted)
    {
        var next = current == AndroidPlaybackEngine.System ? AndroidPlaybackEngine.LibVlc : AndroidPlaybackEngine.System;
        return attempted.Contains(next) || (next == AndroidPlaybackEngine.LibVlc && !CanUseLibVlc(source)) ||
            (next == AndroidPlaybackEngine.System && source.RequiresClearKeyDecryption)
                ? null : next;
    }

    public static IReadOnlyList<string> LibVlcOptions(PlatformMediaSource source, bool live)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.RequiresClearKeyDecryption)
            throw new NotSupportedException("The bundled Android LibVLC does not support ClearKey DASH.");
        if (!CanUseLibVlc(source))
            throw new NotSupportedException("This stream requires an Origin header; use the Android player.");

        var options = new List<string>
        {
            ":http-reconnect",
            $":http-user-agent={source.UserAgent ?? "Mozilla/5.0"}",
            live ? ":network-caching=1500" : ":network-caching=1000"
        };
        if (source.Referrer is { } referrer) options.Add($":http-referrer={referrer.AbsoluteUri}");
        return options;
    }
}
