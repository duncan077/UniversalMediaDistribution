namespace UniversalMediaDistribution.UI.Diagnostics;

/// <summary>
/// Records how much of the process footprint is the managed heap. Platform
/// tools cannot answer that on their own: Android's dumpsys reports the .NET
/// heap only as an unattributed "Unknown" region, and on Windows the working
/// set counts shared and memory-mapped pages, so a large number there says
/// nothing about whether allocation or native mappings are responsible.
/// Snapshots are taken around the daily refresh because that is the window
/// where the footprint grows.
/// </summary>
public static class MemoryDiagnostics
{
    /// <summary>Allows a platform log to expose numeric snapshots without exporting user data.</summary>
    public static event Action<string>? SnapshotRecorded;

    /// <param name="counts">
    /// What produced the footprint. Bytes on their own cannot distinguish a
    /// large per-item cost from the same items being retained several times
    /// over, which is the whole question here.
    /// </param>
    public static void Snapshot(string stage, string? counts = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        try
        {
            // Unforced first: this is the heap as the app actually leaves it,
            // garbage included, and collecting before reading would hide it.
            var managedBytes = GC.GetTotalMemory(forceFullCollection: false);
            var info = GC.GetGCMemoryInfo();
            // The same figure after a blocking full collection, which is what
            // separates genuinely retained objects from load-time garbage. Off
            // unless asked for: this runs on the UI thread, and a gen2 pause on
            // a memory-pressured television is the sort of stall that misses the
            // five second input dispatch deadline.
            var liveBytes = IsFullCollectionRequested
                ? GC.GetTotalMemory(forceFullCollection: true)
                : (long?)null;
            var message = $"stage={stage} " +
                $"managed={Megabytes(managedBytes)} " +
                (liveBytes is { } live ? $"live={Megabytes(live)} " : string.Empty) +
                $"heap-committed={Megabytes(info.TotalCommittedBytes)} " +
                $"heap-size={Megabytes(info.HeapSizeBytes)} " +
                $"fragmented={Megabytes(info.FragmentedBytes)} " +
                $"working-set={Megabytes(Environment.WorkingSet)} " +
                $"gc={GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}" +
                (counts is null ? string.Empty : $" {counts}");
            ApplicationDiagnosticLog.Information("memory.snapshot", message);
            SnapshotRecorded?.Invoke(message);
        }
        catch (Exception exception)
        {
            // A diagnostic must never be the reason a refresh fails.
            ApplicationDiagnosticLog.Warning(
                "memory.snapshot",
                $"Could not record the {stage} memory snapshot: {exception.Message}");
        }
    }

    /// <summary>
    /// Set <c>UMD_MEMORY_DIAGNOSTICS_FULL=1</c> to add the post-collection
    /// reading. Read per call so it can be turned on for one run.
    /// </summary>
    private static bool IsFullCollectionRequested =>
        Environment.GetEnvironmentVariable("UMD_MEMORY_DIAGNOSTICS_FULL")
            is "1" or "true" or "TRUE";

    private static string Megabytes(long bytes) =>
        $"{bytes / (1024d * 1024d):F1}MiB";
}
