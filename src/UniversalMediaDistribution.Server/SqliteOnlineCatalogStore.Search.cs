using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.Domain.Catalog;
using UniversalMediaDistribution.Catalog.Transfer;

namespace UniversalMediaDistribution.Server;

internal sealed partial class SqliteOnlineCatalogStore
{
    public CatalogPageResponse SearchDatabasePage(ApiContentSearchRequest request, int limit, CancellationToken cancellationToken, bool summary = false)
    {
        if (limit is < 1 or > 500) throw new ArgumentException("Invalid catalog search bounds.");
        var after = request.ContinuationToken is null ? "" : Guid.TryParseExact(request.ContinuationToken, "N", out var id)
            ? id.ToString("N") : throw new ArgumentException("Invalid search cursor.");
        using var connection = _database.Open();
        connection.CreateFunction<string?, bool>("umd_genre", label => label is not null && request.Genre is { } genre && genre.MatchesAny([label]), isDeterministic: true);
        connection.CreateFunction<string?, string?>("umd_label", label => label?.ToUpperInvariant(), isDeterministic: true);
        connection.CreateFunction<string?, long>("umd_status", status => (long)CatalogTransferMapper.ParsePublicationStatus(status), isDeterministic: true);
        connection.CreateFunction<long?, string?, long?>("umd_year", (year, date) => CatalogReleaseYear.Resolve(year is null ? null : checked((int)year.Value), date), isDeterministic: true);
        connection.CreateFunction<string, string, bool>("umd_status_applies", (media, subtype) =>
            PublicationStatusApplicability.AppliesTo(CatalogSubtypeResolver.ParseMediaType(media), CatalogSubtypeResolver.ResolveCode(CatalogSubtypeResolver.ParseMediaType(media), subtype)), isDeterministic: true);
        connection.CreateFunction<string, string, bool>("umd_group", (media, subtype) =>
            MediaSubtypeGroups.Matches(request.SubtypeGroup, CatalogSubtypeResolver.ParseMediaType(media), CatalogSubtypeResolver.ResolveCode(CatalogSubtypeResolver.ParseMediaType(media), subtype)), isDeterministic: true);
        using var transaction = connection.BeginTransaction(deferred: true);
        var state = ReadState(connection, transaction) ?? (0L, DateTimeOffset.UnixEpoch, SourceLabel);
        var projection = summary ? "json_remove(payload,'$.units','$.seasons','$.sources','$.distributionRights')" : "payload";
        using var command = Command(connection, transaction, "SELECT " + projection + " FROM catalog_items " + """
            WHERE id > $after AND ($media IS NULL OR media_type=$media)
            AND ($query IS NULL OR instr(search_text,$query)>0)
            AND ($group=0 OR umd_group(media_type,subtype))
            AND ($year IS NULL OR umd_year(json_extract(payload,'$.releaseYear'),json_extract(payload,'$.releaseDate'))=$year)
            AND ($genre=0 OR EXISTS(SELECT 1 FROM json_each(payload,'$.genres') WHERE umd_genre(value))
                OR EXISTS(SELECT 1 FROM json_each(payload,'$.categories') WHERE umd_genre(value)))
            AND ($category IS NULL OR EXISTS(SELECT 1 FROM json_each(payload,'$.categories') WHERE umd_label(value)=$category)
                OR EXISTS(SELECT 1 FROM json_each(payload,'$.genres') WHERE umd_label(value)=$category))
            AND ($status IS NULL OR (umd_status_applies(media_type,subtype) AND umd_status(json_extract(payload,'$.publicationStatus'))=$status))
            AND ($provider IS NULL OR EXISTS(SELECT 1 FROM json_each(payload,'$.contributors') c
                WHERE umd_label(json_extract(c.value,'$.name'))=$provider AND EXISTS(
                    SELECT 1 FROM json_each(c.value,'$.credits') WHERE lower(json_extract(value,'$.role'))='distributor')))
            ORDER BY id LIMIT $limit;
            """);
        command.Parameters.AddWithValue("$after", after);
        command.Parameters.AddWithValue("$media", request.MediaType is { } type ? type.ToString().ToLowerInvariant() : DBNull.Value);
        command.Parameters.AddWithValue("$query", request.Query is { } query ? SearchText(query) : DBNull.Value);
        command.Parameters.AddWithValue("$group", request.SubtypeGroup is null ? 0 : 1);
        command.Parameters.AddWithValue("$genre", request.Genre is null ? 0 : 1);
        command.Parameters.AddWithValue("$year", (object?)request.ReleaseYear ?? DBNull.Value);
        command.Parameters.AddWithValue("$category", (object?)request.Category?.ToUpperInvariant() ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", request.Status is { } status ? (long)status : DBNull.Value);
        command.Parameters.AddWithValue("$provider", (object?)request.ProviderName?.ToUpperInvariant() ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", limit + 1);
        var items = ReadItemsBounded(command, cancellationToken, out var truncated, truncate: true);
        var selected = items.Take(limit).ToArray();
        return new(state.Item1, (truncated || items.Count > limit) && selected.Length > 0 ? selected[^1].Id!.Value.ToString("N") : null,
            Document(state, selected));
    }

    public CatalogTransferDocument RecentlyAdded(CancellationToken cancellationToken)
    {
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = Command(connection, transaction, """
            SELECT json_remove(payload,'$.units','$.seasons','$.sources','$.distributionRights')
            FROM catalog_arrivals a JOIN catalog_items c ON c.id=a.id ORDER BY a.rowid DESC LIMIT 12;
            """);
        var items = ReadItemsBounded(command, cancellationToken, out _, truncate: true);
        return Document(ReadState(connection, transaction) ?? (0L, DateTimeOffset.UnixEpoch, SourceLabel), items);
    }

    public CatalogTransferDocument? ReadPublicItem(Guid id, CancellationToken cancellationToken)
    {
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = Command(connection, transaction, "SELECT payload FROM catalog_items WHERE id=$id;");
        command.Parameters.AddWithValue("$id", id.ToString("N"));
        var items = ReadItemsBounded(command, cancellationToken, out _, truncate: false);
        return items.Count == 0 ? null : Document(ReadState(connection, transaction) ?? (0L, DateTimeOffset.UnixEpoch, SourceLabel), items);
    }
}
