using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.Server;

internal sealed record CatalogPageResponse(long Revision, string? ContinuationToken, CatalogTransferDocument Document);
internal sealed record CatalogMergeResult(Guid ItemId, bool Changed);
internal sealed class CatalogSnapshotTooLargeException() : InvalidOperationException("The catalog is too large for one snapshot. Use /api/v1/catalog/items with pagination.");

internal sealed partial class SqliteOnlineCatalogStore
{
    private const string SourceLabel = "Universal Media Distribution online catalog";

    // Hash the same JSON as the legacy snapshot without materializing the
    // entire catalog. Metadata remains usable after backfill grows beyond the
    // client's single-document size bound.
    public OnlineCatalogMetadataResponse? Metadata(CancellationToken cancellationToken)
    {
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        var state = ReadState(connection, transaction);
        if (state is null) return null;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var sink = new HashStream(hash);
        using var writer = new Utf8JsonWriter(sink);
        writer.WriteStartObject();
        writer.WriteString("format", CatalogTransferDocument.CurrentFormat);
        writer.WriteNumber("version", CatalogTransferDocument.CurrentVersion);
        writer.WriteString("exportedAt", state.Value.UpdatedAt);
        writer.WriteString("sourceLabel", state.Value.Source ?? SourceLabel);
        writer.WriteStartArray("items");
        using var command = Command(connection, transaction, "SELECT payload FROM catalog_items ORDER BY id;");
        using var reader = command.ExecuteReader();
        var count = 0;
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            writer.WriteRawValue(reader.GetString(0), skipInputValidation: true);
            writer.Flush();
            count++;
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        return new(state.Value.Revision, state.Value.UpdatedAt, Convert.ToHexString(hash.GetHashAndReset()), count);
    }

    public ValueTask<OnlineCatalogState?> GetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        var state = ReadState(connection, transaction);
        if (state is null) return ValueTask.FromResult<OnlineCatalogState?>(null);
        using var command = Command(connection, transaction, "SELECT payload FROM catalog_items ORDER BY id;");
        var items = ReadItems(command, cancellationToken);
        var document = Document(state.Value, items);
        transaction.Commit();
        return ValueTask.FromResult<OnlineCatalogState?>(new(state.Value.Revision, state.Value.UpdatedAt, ComputeDocumentHash(document), document));
    }

    public ValueTask<OnlineCatalogState> SaveAsync(CatalogTransferDocument document, CancellationToken cancellationToken)
    {
        CatalogTransferValidator.Validate(document);
        ValidateDistinctIds(document);
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var revision = checked((ReadState(connection, transaction)?.Revision ?? 0) + 1);
        using (var clear = Command(connection, transaction, """
            UPDATE crawler_titles SET item_id=NULL;
            DELETE FROM catalog_redirects;
            DELETE FROM catalog_names;
            DELETE FROM catalog_items;
            """)) clear.ExecuteNonQuery();
        var now = DateTimeOffset.UtcNow;
        var savedItems = new List<CatalogTransferItem>(document.Items.Count);
        foreach (var item in document.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var savedItem = item with { Id = item.Id ?? Guid.CreateVersion7() };
            WriteItem(connection, transaction, savedItem, revision, now);
            savedItems.Add(savedItem);
        }
        WriteState(connection, transaction, revision, now, document.SourceLabel);
        var savedDocument = Document((revision, now, document.SourceLabel), savedItems.OrderBy(item => item.Id).ToArray());
        var saved = new OnlineCatalogState(revision, now, ComputeDocumentHash(savedDocument), savedDocument);
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        // Do not re-read through the bounded legacy snapshot endpoint after
        // committing an otherwise valid larger upload.
        return ValueTask.FromResult(saved);
    }

    internal static void ImportLegacy(SqliteConnection connection, SqliteTransaction transaction, OnlineCatalogState state)
    {
        CatalogTransferValidator.Validate(state.Document);
        ValidateDistinctIds(state.Document);
        foreach (var item in state.Document.Items)
            WriteItem(connection, transaction, item with { Id = item.Id ?? Guid.CreateVersion7() }, state.Revision, state.UpdatedAt);
        WriteState(connection, transaction, state.Revision, state.UpdatedAt, state.Document.SourceLabel);
    }

    public CatalogPageResponse Page(string? cursor, int limit, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        var after = cursor is null ? "" : Guid.TryParseExact(cursor, "N", out var id) ? id.ToString("N") :
            throw new ArgumentException("Invalid catalog cursor.", nameof(cursor));
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        var state = ReadState(connection, transaction) ?? (0L, DateTimeOffset.UnixEpoch, SourceLabel);
        using var command = Command(connection, transaction, "SELECT payload FROM catalog_items WHERE id > $after ORDER BY id LIMIT $limit;");
        command.Parameters.AddWithValue("$after", after);
        command.Parameters.AddWithValue("$limit", limit + 1);
        var items = ReadItemsBounded(command, cancellationToken, out var truncated, truncate: true);
        return new CatalogPageResponse(state.Revision, items.Count > limit || truncated ? items[Math.Min(items.Count, limit) - 1].Id!.Value.ToString("N") : null,
            Document(state, items.Take(limit).ToArray()));
    }

    public CatalogTransferDocument SearchDatabase(string query, string? mediaType, int limit, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (query.Trim().Length > 200 || limit is < 1 or > 500) throw new ArgumentException("Invalid catalog search bounds.");
        var media = string.IsNullOrWhiteSpace(mediaType) ? null : CatalogSubtypeResolver.ParseMediaType(mediaType).ToString().ToLowerInvariant();
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        var state = ReadState(connection, transaction) ?? (0L, DateTimeOffset.UnixEpoch, SourceLabel);
        using var command = Command(connection, transaction, """
            SELECT payload FROM catalog_items WHERE ($media IS NULL OR media_type=$media)
            AND instr(search_text,$query)>0 ORDER BY name,id LIMIT $limit;
            """);
        command.Parameters.AddWithValue("$media", (object?)media ?? DBNull.Value);
        command.Parameters.AddWithValue("$query", SearchText(query));
        command.Parameters.AddWithValue("$limit", limit);
        return Document(state, ReadItemsBounded(command, cancellationToken, out _, truncate: true));
    }

    public CatalogMergeResult MergeScraped(string providerId, string externalId, CatalogTransferItem incoming, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var candidate = CatalogTransferMapper.ToCanonicalTransferItem(CatalogMetadataOnly.Sanitize(incoming));
        CatalogTransferValidator.Validate(new CatalogTransferDocument { Items = [candidate] });
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var candidates = new Dictionary<Guid, CatalogTransferItem>();
        CatalogTransferItem? boundItem = null;
        using (var bound = Command(connection, transaction, """
            SELECT c.payload FROM catalog_items c JOIN crawler_titles t ON t.item_id=c.id
            WHERE t.provider_id=$provider AND t.external_id=$external;
            """))
        {
            bound.Parameters.AddWithValue("$provider", providerId);
            bound.Parameters.AddWithValue("$external", externalId);
            if (bound.ExecuteScalar() is string payload)
            {
                var known = Parse(payload);
                if (CatalogTransferIdentity.AreCompatible(known, candidate))
                {
                    candidates.Add(known.Id!.Value, known);
                    boundItem = known;
                }
            }
        }
        foreach (var name in CatalogTransferIdentity.GetNameKeys(candidate))
        {
            using var find = Command(connection, transaction, "SELECT c.payload FROM catalog_items c JOIN catalog_names n ON c.id=n.item_id WHERE n.name_key=$name;");
            find.Parameters.AddWithValue("$name", name);
            foreach (var match in ReadItems(find, cancellationToken))
                if (CatalogTransferIdentity.GetMatchScore(match, candidate) > 0) candidates.TryAdd(match.Id!.Value, match);
        }
        // A provider omitting the year must not join two known remakes.
        // Preserve a known provider binding, or keep an ambiguous entry apart
        // until a later response supplies enough metadata to disambiguate it.
        if (candidates.Values.Select(ReleaseYear).OfType<int>().Distinct().Skip(1).Any())
        {
            var boundYear = boundItem is null ? null : ReleaseYear(boundItem);
            foreach (var match in candidates.Values.ToArray())
                if (match.Id != boundItem?.Id && (boundItem is not null && boundYear is null ||
                    ReleaseYear(match) is { } year && year != boundYear))
                    candidates.Remove(match.Id!.Value);
        }
        // A scraped synonym must never coalesce every matching row. In
        // particular a bridge/placeholder used to drag whole unrelated series
        // (and all their chapters) into one transitive catalog component.
        var selected = boundItem ?? CatalogTransferIdentity.FindBestMatch(candidates.Values, candidate);
        var itemId = selected?.Id ?? Guid.CreateVersion7();
        var merged = candidate with { Id = itemId, SyncVersion = null, Deleted = false };
        if (selected is not null)
        {
            merged = CatalogTransferIdentity.CombineConverged(selected, CatalogTransferIdentity.ConvergeIdentity(selected, candidate));
            merged = CatalogMetadataOnly.Sanitize(merged) with { Id = itemId, SyncVersion = null, Deleted = false };
        }
        var payloadHash = Hash(Serialize(merged));
        using var previousHash = Command(connection, transaction, "SELECT content_hash FROM catalog_items WHERE id=$id;");
        previousHash.Parameters.AddWithValue("$id", itemId.ToString("N"));
        if (previousHash.ExecuteScalar() as string == payloadHash)
        {
            transaction.Commit();
            return new(itemId, false);
        }
        var revision = checked((ReadState(connection, transaction)?.Revision ?? 0) + 1);
        var now = DateTimeOffset.UtcNow;
        WriteItem(connection, transaction, merged, revision, now);
        WriteState(connection, transaction, revision, now, SourceLabel);
        transaction.Commit();
        return new(itemId, true);
    }

    private static void ValidateDistinctIds(CatalogTransferDocument document)
    {
        var ids = document.Items.Where(item => item.Id.HasValue).Select(item => item.Id!.Value).ToArray();
        if (ids.Distinct().Count() != ids.Length)
            throw new CatalogTransferValidationException(["A public catalog cannot reuse one sync ID for different items."]);
    }

    private static int? ReleaseYear(CatalogTransferItem item) => CatalogReleaseYear.Resolve(item.ReleaseYear, item.ReleaseDate);

    private static void WriteItem(SqliteConnection connection, SqliteTransaction transaction, CatalogTransferItem item, long revision, DateTimeOffset now)
    {
        var id = item.Id!.Value.ToString("N");
        var payload = Serialize(item);
        using var write = Command(connection, transaction, """
            INSERT INTO catalog_items(id,media_type,subtype,name,search_text,payload,content_hash,revision,updated_at)
            VALUES($id,$media,$subtype,$name,$search,$payload,$hash,$revision,$now)
            ON CONFLICT(id) DO UPDATE SET media_type=excluded.media_type,subtype=excluded.subtype,name=excluded.name,
                search_text=excluded.search_text,payload=excluded.payload,content_hash=excluded.content_hash,revision=excluded.revision,updated_at=excluded.updated_at;
            DELETE FROM catalog_names WHERE item_id=$id;
            """);
        write.Parameters.AddWithValue("$id", id);
        write.Parameters.AddWithValue("$media", CatalogSubtypeResolver.ParseMediaType(item.MediaType).ToString().ToLowerInvariant());
        write.Parameters.AddWithValue("$subtype", item.Subtype);
        write.Parameters.AddWithValue("$name", item.Name);
        write.Parameters.AddWithValue("$search", SearchText(string.Join('\n', CatalogTransferIdentity.GetNameKeys(item)
            .Concat(item.Genres).Concat(item.Categories).Concat(item.Contributors.Select(value => value.Name)).Append(item.Description ?? ""))));
        write.Parameters.AddWithValue("$payload", payload);
        write.Parameters.AddWithValue("$hash", Hash(payload));
        write.Parameters.AddWithValue("$revision", revision);
        write.Parameters.AddWithValue("$now", now.ToString("O"));
        write.ExecuteNonQuery();
        foreach (var name in CatalogTransferIdentity.GetNameKeys(item))
        {
            using var alias = Command(connection, transaction, "INSERT INTO catalog_names(name_key,item_id) VALUES($name,$id);");
            alias.Parameters.AddWithValue("$name", name);
            alias.Parameters.AddWithValue("$id", id);
            alias.ExecuteNonQuery();
        }
    }

    private static (long Revision, DateTimeOffset UpdatedAt, string? Source)? ReadState(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = Command(connection, transaction, "SELECT revision,updated_at,source_label FROM catalog_state WHERE singleton=1;");
        using var reader = command.ExecuteReader();
        return reader.Read() ? (reader.GetInt64(0), DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture), reader.IsDBNull(2) ? null : reader.GetString(2)) : null;
    }

    private static void WriteState(SqliteConnection connection, SqliteTransaction transaction, long revision, DateTimeOffset now, string? source)
    {
        using var command = Command(connection, transaction, """
            INSERT INTO catalog_state(singleton,revision,updated_at,source_label) VALUES(1,$revision,$now,$source)
            ON CONFLICT(singleton) DO UPDATE SET revision=excluded.revision,updated_at=excluded.updated_at,source_label=excluded.source_label;
            """);
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$source", (object?)source ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static List<CatalogTransferItem> ReadItems(SqliteCommand command, CancellationToken cancellationToken)
        => ReadItemsBounded(command, cancellationToken, out _, truncate: false);

    private static List<CatalogTransferItem> ReadItemsBounded(SqliteCommand command, CancellationToken cancellationToken, out bool truncated, bool truncate)
    {
        using var reader = command.ExecuteReader();
        var result = new List<CatalogTransferItem>();
        var bytes = 1024L;
        truncated = false;
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payload = reader.GetString(0);
            bytes += Encoding.UTF8.GetByteCount(payload) + 1;
            if (bytes > (truncate ? 8 : 30) * 1024L * 1024)
            {
                if (!truncate || result.Count == 0) throw new CatalogSnapshotTooLargeException();
                truncated = true;
                break;
            }
            result.Add(Parse(payload));
        }
        return result;
    }

    private static CatalogTransferDocument Document((long Revision, DateTimeOffset UpdatedAt, string? Source) state, IReadOnlyList<CatalogTransferItem> items) =>
        new() { ExportedAt = state.UpdatedAt, SourceLabel = state.Source ?? SourceLabel, Items = items };
    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }
    private static CatalogTransferItem Parse(string payload) => JsonSerializer.Deserialize(payload, ServerJsonSerializerContext.Default.CatalogTransferItem)!;
    private static string Serialize(CatalogTransferItem item) => JsonSerializer.Serialize(item, ServerJsonSerializerContext.Default.CatalogTransferItem);
    private static string Hash(string payload) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    private static string SearchText(string value) => string.Concat(value.Normalize(NormalizationForm.FormD)
        .Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)).ToUpperInvariant().Normalize(NormalizationForm.FormC);

    private sealed class HashStream(IncrementalHash hash) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override void Write(byte[] buffer, int offset, int count) => hash.AppendData(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer) => hash.AppendData(buffer);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

internal static class CatalogMetadataOnly
{
    // Scraped public metadata only; never apply this to private user snapshots.
    internal static CatalogTransferItem Sanitize(CatalogTransferItem item) => item with
    {
        AlternativeNames = DeduplicateAliases(item),
        Aliases = null,
        ThumbnailRef = null,
        BackdropRef = null,
        Sources = [],
        DistributionRights = [],
        Contributors = item.Contributors.Select(contributor => contributor with { Url = null, ThumbnailRef = null }).ToArray(),
        Units = item.Units.Select(Unit).ToArray(),
        Seasons = item.Seasons.Select(season => season with { ThumbnailRef = null, Units = season.Units.Select(Unit).ToArray() }).ToArray()
    };
    private static IReadOnlyList<CatalogTransferAlias> DeduplicateAliases(CatalogTransferItem item)
    {
        var aliases = new List<CatalogTransferAlias>();
        var keys = new HashSet<(string Name, string Language, string Kind)>();
        foreach (var alias in CatalogTransferMapper.GetAlternativeNames(item))
        {
            // Keep malformed entries so the normal validator still reports
            // missing names/types and invalid language tags; only duplicates
            // of otherwise valid provider aliases are harmless to collapse.
            if (alias is null || string.IsNullOrWhiteSpace(alias.Name) || string.IsNullOrWhiteSpace(alias.AliasType))
            {
                aliases.Add(alias!);
                continue;
            }
            CatalogAlternativeName normalized;
            try { normalized = new(alias.Name, alias.Language, alias.AliasType); }
            catch (ArgumentException)
            {
                aliases.Add(alias);
                continue;
            }
            if (keys.Add((normalized.NameKey, normalized.Language, normalized.Kind))) aliases.Add(alias);
        }
        return aliases;
    }

    private static CatalogTransferUnit Unit(CatalogTransferUnit unit) => unit with { ThumbnailRef = null, Sources = [] };
}
