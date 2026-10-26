namespace UniversalMediaDistribution.Catalog.Backup;

public interface IFullDatabaseBackupService
{
    ValueTask ExportAsync(
        Stream destination,
        CancellationToken cancellationToken = default);

    ValueTask<FullDatabaseRestoreResult> RestoreAsync(
        Stream source,
        CancellationToken cancellationToken = default);
}

public sealed record FullDatabaseRestoreResult(string SafetyBackupPath);
