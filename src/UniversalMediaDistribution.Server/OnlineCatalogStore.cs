using System.Globalization;
using System.Security.Cryptography;
using UniversalMediaDistribution.Catalog.Sync;
using UniversalMediaDistribution.Catalog.Transfer;

namespace UniversalMediaDistribution.Server;

internal sealed record OnlineCatalogState(
    long Revision,
    DateTimeOffset UpdatedAt,
    string Sha256,
    CatalogTransferDocument Document);

internal sealed record OnlineCatalogMetadataResponse(
    long Revision,
    DateTimeOffset UpdatedAt,
    string Sha256,
    int ItemCount);

internal sealed partial class SqliteOnlineCatalogStore
{
    private readonly ServerDatabase _database;
    public SqliteOnlineCatalogStore(string dataDirectory) => _database = new ServerDatabase(dataDirectory);

    public static CatalogTransferDocument Search(
        OnlineCatalogState state,
        string query,
        string? mediaType,
        int limit)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (query.Trim().Length > 200)
        {
            throw new ArgumentException(
                "Catalog search text cannot exceed 200 characters.",
                nameof(query));
        }

        if (limit is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                "Catalog search limits must be between 1 and 500.");
        }

        string? normalizedMediaType = null;
        if (!string.IsNullOrWhiteSpace(mediaType))
        {
            normalizedMediaType = CatalogSubtypeResolver
                .ParseMediaType(mediaType)
                .ToString();
        }

        var normalizedQuery = query.Trim();
        var matches = state.Document.Items
            .Where(item =>
                normalizedMediaType is null ||
                string.Equals(
                    CatalogSubtypeResolver
                        .ParseMediaType(item.MediaType)
                        .ToString(),
                    normalizedMediaType,
                    StringComparison.OrdinalIgnoreCase))
            .Where(item => Matches(item, normalizedQuery))
            .Take(limit)
            .ToArray();
        return new CatalogTransferDocument
        {
            ExportedAt = state.UpdatedAt,
            SourceLabel = state.Document.SourceLabel ??
                "Universal Media Distribution online catalog",
            Items = matches
        };
    }

    private static bool Matches(
        CatalogTransferItem item,
        string query)
    {
        static bool Contains(string? value, string queryValue) =>
            !string.IsNullOrWhiteSpace(value) &&
            CultureInfo.InvariantCulture.CompareInfo.IndexOf(
                value,
                queryValue,
                CompareOptions.IgnoreCase |
                CompareOptions.IgnoreNonSpace) >= 0;

        return Contains(item.Name, query) ||
               Contains(item.OriginalName, query) ||
               Contains(item.Description, query) ||
               CatalogTransferMapper.GetAlternativeNames(item)
                   .Any(value => Contains(value.Name, query)) ||
               item.Genres.Any(value => Contains(value, query)) ||
               item.Categories.Any(value => Contains(value, query)) ||
               item.Contributors.Any(value => Contains(value.Name, query));
    }

    private static string ComputeDocumentHash(
        CatalogTransferDocument document)
    {
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            document,
            ServerJsonSerializerContext.Default.CatalogTransferDocument);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}
