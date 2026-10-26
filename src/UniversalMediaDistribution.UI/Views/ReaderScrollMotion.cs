namespace UniversalMediaDistribution.UI.Views;

/// <summary>Pixel-based movement independent of frame rate, with bounded catch-up after a stall.</summary>
internal static class ReaderScrollMotion
{
    public const double PointerDeadZone = 12;

    public static double PointerVelocity(double distance)
    {
        if (!double.IsFinite(distance)) return 0;
        var beyondDeadZone = Math.Max(0, Math.Abs(distance) - PointerDeadZone);
        return Math.Sign(distance) * Math.Min(1200, beyondDeadZone * 4);
    }

    public static double Advance(double offset, double maximum, double velocity, double elapsedSeconds) =>
        Math.Clamp(offset + velocity * Math.Clamp(elapsedSeconds, 0, 0.05), 0, Math.Max(0, maximum));
}
