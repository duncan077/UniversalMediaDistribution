namespace UniversalMediaDistribution.Catalog.Transfer;

public sealed record CatalogImportPreview(
    IReadOnlyList<string> Creates,
    IReadOnlyList<string> Updates)
{
    public int CreatedCount => Creates.Count;

    public int UpdatedCount => Updates.Count;
}

public sealed record CatalogImportResult(
    int CreatedCount,
    int UpdatedCount,
    int UnchangedCount,
    IReadOnlyList<string> AffectedNames);

