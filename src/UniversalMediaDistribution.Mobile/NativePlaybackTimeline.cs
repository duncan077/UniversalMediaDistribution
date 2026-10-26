using System.Globalization;

namespace UniversalMediaDistribution.Mobile;

/// <summary>Overflow-safe conversion between native milliseconds and the seek bar.</summary>
public static class NativePlaybackTimeline
{
    public const int MaximumProgress = 1000;

    public static int ToProgress(int milliseconds, int duration) => duration <= 0 ? 0 :
        (int)(Math.Clamp((long)milliseconds, 0, duration) * MaximumProgress / duration);

    public static int ToMilliseconds(int progress, int duration) =>
        (int)((long)Math.Clamp(progress, 0, MaximumProgress) * Math.Max(0, duration) / MaximumProgress);

    public static string FormatTime(int milliseconds)
    {
        var seconds = Math.Max(0, milliseconds) / 1000;
        return seconds >= 3600
            ? string.Create(CultureInfo.InvariantCulture, $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{seconds / 60}:{seconds % 60:00}");
    }
}
