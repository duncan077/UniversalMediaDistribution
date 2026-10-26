namespace UniversalMediaDistribution.UI.ViewModels;

/// <summary>Process-wide image budgets, configured before the Android UI starts.</summary>
public static class ImageMemoryPolicy
{
    internal static bool IsArtworkSuspended { get; private set; }
    internal static event Action? ArtworkActivityChanged;

    /// <summary>Called on the UI thread when the catalog activity enters/leaves the screen.</summary>
    public static void SetArtworkActive(bool active)
    {
        Avalonia.Threading.Dispatcher.UIThread.VerifyAccess();
        if (IsArtworkSuspended == !active) return;
        IsArtworkSuspended = !active;
        ArtworkActivityChanged?.Invoke();
        if (!active) ReleaseUnusedImages();
    }

    internal static bool UseSmallBudgets { get; private set; } = OperatingSystem.IsAndroid();
    internal static long ArtworkCacheBytes { get; private set; } =
        OperatingSystem.IsAndroid() ? 16L * 1024 * 1024 : 64L * 1024 * 1024;
    internal static int ArtworkLoadConcurrency => UseSmallBudgets ? 2 : Math.Clamp(Environment.ProcessorCount / 2, 2, 4);
    internal static int RetainedReaderPagesPerSide => UseSmallBudgets ? 1 : 4;
    internal static int PreloadedReaderPagesPerSide => UseSmallBudgets ? 1 : 2;
    internal static long ReaderBitmapBytes => (UseSmallBudgets ? 16L : 64L) * 1024 * 1024;
    internal static long EncodedReaderBytes => (UseSmallBudgets ? 24L : 96L) * 1024 * 1024;

    public static void ConfigureAndroid(bool isTelevision, bool isLowMemoryDevice)
    {
        UseSmallBudgets = true;
        ArtworkCacheBytes = (isTelevision || isLowMemoryDevice ? 8L : 16L) * 1024 * 1024;
        ReleaseUnusedImages();
    }

    /// <summary>Only cache-owned images are freed; visible images remain valid.</summary>
    public static void ReleaseUnusedImages() => DecodedArtworkCache.Clear();
}
