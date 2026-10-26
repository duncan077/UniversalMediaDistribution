namespace UniversalMediaDistribution.Catalog.Sqlite;

public sealed record DatabaseRestoreResult(
    string RestoredFromPath,
    string SafetyBackupPath);
