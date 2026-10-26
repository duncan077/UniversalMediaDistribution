using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;

internal sealed record ArchivedRow(string Id, string MediaType, string Subtype, string Name,
    string SearchText, string Payload, string ContentHash, long Revision, string UpdatedAt);
internal sealed record ArchivedBinding(string ProviderId, string ExternalId, string? ItemId, string RowJson);
internal sealed record ArchivedRedirect(string FromId, string ToId);
internal sealed record RepairedWork(Guid Id, string Name, int Chapters, string? HighestChapter, int Sources);
internal sealed record RepairResult(string OutputPath, Guid PreviousItemId, long Revision,
    List<RepairedWork> Works, int BindingsReassigned, int AmbiguousRedirectsArchived,
    bool PrivateDocumentsUnchanged, string PrivateDocumentsSha256, string InputSnapshotSha256,
    string OutputSha256, string Warning);

/// <summary>Offline, transactional separation; all private documents are opaque and unchanged.</summary>
internal static class ServerRepair
{
    private static readonly RepairJsonContext Compact = new(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    internal static RepairResult Apply(string planPath, string outputPath)
    {
        planPath = Path.GetFullPath(planPath);
        outputPath = Path.GetFullPath(outputPath);
        var plan = JsonSerializer.Deserialize(File.ReadAllText(planPath), RepairJsonContext.Default.RepairPlan)
            ?? throw new InvalidDataException("Missing repair plan.");
        if (plan.DatabaseKind != "server" || !plan.FetchCompleted || plan.Groups.Count == 0 ||
            plan.Sources.Count == 0 || plan.Sources.Any(s => s.FreshItem is null || s.Failure is not null))
            throw new InvalidDataException("Every bound server source must have a successful fresh response; partial plans cannot be applied.");
        var backupPath = Path.Combine(Path.GetDirectoryName(planPath)!, "backup.sqlite");
        if (HashFile(backupPath) != plan.BackupSha256) throw new InvalidDataException("The input backup changed since planning.");
        if (File.Exists(outputPath) || Directory.Exists(outputPath)) throw new IOException("Output exists. Choose a new file.");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var candidatePath = outputPath + ".candidate";
        using (var reserve = new FileStream(candidatePath, FileMode.CreateNew, FileAccess.Write)) { }
        using (var source = Open(backupPath, readOnly: true))
        using (var target = Open(candidatePath, readOnly: false)) source.BackupDatabase(target);

        RepairResult result;
        using (var database = Open(candidatePath, readOnly: false))
        {
            CheckIntegrity(database);
            Execute(database, null, "PRAGMA journal_mode=DELETE; PRAGMA foreign_keys=ON;");
            var privateHash = TableHash(database, "SELECT key,parent,payload FROM server_documents ORDER BY key;");
            using var transaction = database.BeginTransaction(deferred: false);
            var original = ReadOriginal(database, transaction, plan.ItemId);
            if (Hash(original.Payload) != plan.ExpectedServerPayloadSha256)
                throw new InvalidDataException("The affected item changed since planning.");
            var old = JsonSerializer.Deserialize(original.Payload, Compact.CatalogTransferItem)!;
            var bindings = ReadBindings(database, transaction, original.Id);
            var expected = plan.Sources.Select(s => s.ProviderId + "\n" + s.ExternalId).ToHashSet(StringComparer.Ordinal);
            if (expected.Count != plan.Sources.Count || !expected.SetEquals(bindings.Select(b => b.ProviderId + "\n" + b.ExternalId)))
                throw new InvalidDataException("Source bindings changed or the plan omits a source.");
            var groups = ValidateGroups(plan, old);
            var anchor = groups.Select((g, index) => (g, index)).Where(pair =>
                CatalogTransferIdentity.GetNameKeys(pair.g.Item).Contains(NameNormalizer.Normalize(old.Name))).ToArray();
            if (anchor.Length != 1) throw new InvalidDataException("The original primary title must identify exactly one fresh group.");
            var redirects = ReadRedirects(database, transaction, original.Id);
            var revision = checked(Convert.ToInt64(Scalar(database, transaction, "SELECT revision FROM catalog_state WHERE singleton=1;"), CultureInfo.InvariantCulture) + 1);
            var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            var works = new List<RepairedWork>();
            var ids = new List<Guid>();
            foreach (var group in groups)
            {
                // Existing public rows are not silently merged or overwritten.
                // If another component exists it needs its own source evidence.
                RejectOtherMatchingRows(database, transaction, original.Id, group.Item);
                var id = ids.Count == anchor[0].index ? plan.ItemId : Guid.CreateVersion7();
                ids.Add(id);
                var item = Sanitize(group.Item) with { Id = id, Deleted = false, SyncVersion = null };
                CatalogTransferValidator.Validate(new CatalogTransferDocument { Items = [item] });
                WriteItem(database, transaction, item, revision, now);
                foreach (var index in group.SourceIndexes)
                {
                    var source = plan.Sources[index];
                    ReassignBinding(database, transaction, source, original.Id, id.ToString("N"), bindings);
                }
                works.Add(new(id, item.Name, item.Units.Count, item.Units.OrderByDescending(u =>
                    decimal.TryParse(u.DisplayNumber, NumberStyles.Number, CultureInfo.InvariantCulture, out var n) ? n : -1).FirstOrDefault()?.DisplayNumber,
                    group.SourceIndexes.Count));
            }
            using (var delete = Command(database, transaction, "DELETE FROM catalog_redirects WHERE to_id=$id;"))
            {
                delete.Parameters.AddWithValue("$id", original.Id);
                delete.ExecuteNonQuery();
            }
            // Archive, rather than guess which split work a historic alias ID
            // or chapter number represented. User snapshots are never rewritten.
            Execute(database, transaction, """
                CREATE TABLE IF NOT EXISTS catalog_identity_repair_archive(
                    repair_id TEXT PRIMARY KEY,created_at TEXT NOT NULL,item_id TEXT NOT NULL,
                    original_row TEXT NOT NULL,bindings TEXT NOT NULL,redirects TEXT NOT NULL);
                """);
            using (var archive = Command(database, transaction, "INSERT INTO catalog_identity_repair_archive VALUES($repair,$now,$id,$row,$bindings,$redirects);"))
            {
                archive.Parameters.AddWithValue("$repair", Guid.CreateVersion7().ToString("N"));
                archive.Parameters.AddWithValue("$now", now);
                archive.Parameters.AddWithValue("$id", original.Id);
                archive.Parameters.AddWithValue("$row", JsonSerializer.Serialize(original, Compact.ArchivedRow));
                archive.Parameters.AddWithValue("$bindings", JsonSerializer.Serialize(bindings, Compact.ListArchivedBinding));
                archive.Parameters.AddWithValue("$redirects", JsonSerializer.Serialize(redirects, Compact.ListArchivedRedirect));
                archive.ExecuteNonQuery();
            }
            using (var state = Command(database, transaction, "UPDATE catalog_state SET revision=$revision,updated_at=$now WHERE singleton=1;"))
            {
                state.Parameters.AddWithValue("$revision", revision);
                state.Parameters.AddWithValue("$now", now);
                if (state.ExecuteNonQuery() != 1) throw new InvalidDataException("Missing catalog state.");
            }
            transaction.Commit();
            if (privateHash != TableHash(database, "SELECT key,parent,payload FROM server_documents ORDER BY key;"))
                throw new InvalidDataException("Private documents changed; refusing to deliver the candidate.");
            CheckIntegrity(database);
            result = new(outputPath, plan.ItemId, revision, works, plan.Sources.Count, redirects.Count, true,
                privateHash, plan.BackupSha256, "", "Offline copy only. Deploy the identity fix first. Do not overwrite a newer live database: this file is a snapshot. Client catalogs/read history are not automatically repaired. Historic ambiguous redirects are archived, not reassigned by chapter number.");
        }
        File.Move(candidatePath, outputPath, overwrite: false);
        result = result with { OutputSha256 = HashFile(outputPath) };
        WriteCatalogPatch(backupPath, outputPath, plan, result);
        using var report = new FileStream(outputPath + ".report.json", FileMode.CreateNew, FileAccess.Write);
        JsonSerializer.Serialize(report, result, RepairJsonContext.Default.RepairResult);
        return result;
    }

    private static void WriteCatalogPatch(string backupPath, string repairedPath, RepairPlan plan, RepairResult result)
    {
        using var before = Open(backupPath, readOnly: true);
        using var after = Open(repairedPath, readOnly: true);
        using var sql = new StreamWriter(new FileStream(repairedPath + ".catalog.sql", FileMode.CreateNew, FileAccess.Write), new UTF8Encoding(false));
        static string Q(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
        void Guard(string condition) => sql.WriteLine($"INSERT INTO umd_repair_guard VALUES(CASE WHEN {condition} THEN 1 ELSE 0 END);");
        sql.WriteLine("-- Public-catalog repair only. Generated from verified fresh source evidence; never edits server_documents.");
        sql.WriteLine("PRAGMA foreign_keys=ON;\nBEGIN IMMEDIATE;\nDROP TABLE IF EXISTS temp.umd_repair_guard;\nCREATE TEMP TABLE umd_repair_guard(ok INTEGER CHECK(ok=1));");
        using var transaction = before.BeginTransaction(deferred: true);
        var original = ReadOriginal(before, transaction, plan.ItemId);
        var bindings = ReadBindings(before, transaction, original.Id);
        var redirects = ReadRedirects(before, transaction, original.Id);
        // Related rows are compared exactly, but unrelated live catalog changes
        // and newer account/progress/session documents are intentionally allowed.
        Guard($"(SELECT payload FROM catalog_items WHERE id={Q(original.Id)})={Q(original.Payload)}");
        Guard($"(SELECT COUNT(*) FROM crawler_titles WHERE item_id={Q(original.Id)})={plan.Sources.Count}");
        Guard($"(SELECT COUNT(*) FROM catalog_redirects WHERE to_id={Q(original.Id)})={redirects.Count}");
        Guard("NOT EXISTS(SELECT 1 FROM crawler_leases WHERE name='catalog' AND julianday(expires_at)>julianday('now'))");
        foreach (var binding in bindings)
            Guard($"(SELECT {BindingJson} FROM crawler_titles WHERE provider_id={Q(binding.ProviderId)} AND external_id={Q(binding.ExternalId)})={Q(binding.RowJson)}");
        foreach (var source in plan.Sources.Where(s => s.ResolvedExternalId is not null && s.ResolvedExternalId != s.ExternalId))
        {
            using var query = Command(before, transaction, "SELECT " + BindingJson + " FROM crawler_titles WHERE provider_id=$provider AND external_id=$external;");
            query.Parameters.AddWithValue("$provider", source.ProviderId);
            query.Parameters.AddWithValue("$external", source.ResolvedExternalId!);
            var row = query.ExecuteScalar() as string;
            Guard(row is null ? $"NOT EXISTS(SELECT 1 FROM crawler_titles WHERE provider_id={Q(source.ProviderId)} AND external_id={Q(source.ResolvedExternalId!)})" :
                $"(SELECT {BindingJson} FROM crawler_titles WHERE provider_id={Q(source.ProviderId)} AND external_id={Q(source.ResolvedExternalId!)})={Q(row)}");
        }
        foreach (var redirect in redirects)
            Guard($"EXISTS(SELECT 1 FROM catalog_redirects WHERE from_id={Q(redirect.FromId)} AND to_id={Q(redirect.ToId)})");
        foreach (var work in result.Works.Where(w => w.Id != plan.ItemId))
            Guard($"NOT EXISTS(SELECT 1 FROM catalog_items WHERE id={Q(work.Id.ToString("N"))})");

        foreach (var work in result.Works)
        {
            using var query = Command(after, null, "SELECT id,media_type,subtype,name,search_text,payload,content_hash FROM catalog_items WHERE id=$id;");
            query.Parameters.AddWithValue("$id", work.Id.ToString("N"));
            using var reader = query.ExecuteReader();
            reader.Read();
            var values = Enumerable.Range(0, 7).Select(i => Q(reader.GetString(i)));
            sql.WriteLine("INSERT INTO catalog_items(id,media_type,subtype,name,search_text,payload,content_hash,revision,updated_at) VALUES(" +
                string.Join(',', values) + ",(SELECT revision+1 FROM catalog_state WHERE singleton=1),strftime('%Y-%m-%dT%H:%M:%f+00:00','now')) " +
                "ON CONFLICT(id) DO UPDATE SET name=excluded.name,media_type=excluded.media_type,subtype=excluded.subtype,search_text=excluded.search_text,payload=excluded.payload,content_hash=excluded.content_hash,revision=excluded.revision,updated_at=excluded.updated_at;");
            sql.WriteLine($"DELETE FROM catalog_names WHERE item_id={Q(work.Id.ToString("N"))};");
            using var names = Command(after, null, "SELECT name_key FROM catalog_names WHERE item_id=$id ORDER BY name_key;");
            names.Parameters.AddWithValue("$id", work.Id.ToString("N"));
            using var nameReader = names.ExecuteReader();
            while (nameReader.Read()) sql.WriteLine($"INSERT INTO catalog_names VALUES({Q(nameReader.GetString(0))},{Q(work.Id.ToString("N"))});");
        }
        var keys = plan.Sources.SelectMany(s => new[] { (s.ProviderId, External: s.ExternalId), (s.ProviderId, External: s.ResolvedExternalId ?? s.ExternalId) }).Distinct().ToArray();
        foreach (var key in keys) sql.WriteLine($"DELETE FROM crawler_titles WHERE provider_id={Q(key.ProviderId)} AND external_id={Q(key.External)};");
        foreach (var key in keys)
        {
            using var query = Command(after, null, "SELECT " + BindingJson + " FROM crawler_titles WHERE provider_id=$provider AND external_id=$external;");
            query.Parameters.AddWithValue("$provider", key.ProviderId);
            query.Parameters.AddWithValue("$external", key.External);
            if (query.ExecuteScalar() is not string row) continue;
            using var document = JsonDocument.Parse(row);
            var columns = document.RootElement.EnumerateObject().ToArray();
            sql.WriteLine("INSERT INTO crawler_titles(" + string.Join(',', columns.Select(p => p.Name)) + ") VALUES(" +
                string.Join(',', columns.Select(p => p.Value.ValueKind is JsonValueKind.Null ? "NULL" :
                    p.Value.ValueKind is JsonValueKind.Number ? p.Value.GetRawText() : Q(p.Value.GetString()!))) + ");");
        }
        sql.WriteLine($"DELETE FROM catalog_redirects WHERE to_id={Q(original.Id)};");
        sql.WriteLine("CREATE TABLE IF NOT EXISTS catalog_identity_repair_archive(repair_id TEXT PRIMARY KEY,created_at TEXT NOT NULL,item_id TEXT NOT NULL,original_row TEXT NOT NULL,bindings TEXT NOT NULL,redirects TEXT NOT NULL);");
        using (var archive = Command(after, null, "SELECT repair_id,created_at,item_id,original_row,bindings,redirects FROM catalog_identity_repair_archive WHERE item_id=$id;"))
        {
            archive.Parameters.AddWithValue("$id", original.Id);
            using var reader = archive.ExecuteReader();
            while (reader.Read()) sql.WriteLine("INSERT INTO catalog_identity_repair_archive VALUES(" + string.Join(',', Enumerable.Range(0, 6).Select(i => Q(reader.GetString(i)))) + ");");
        }
        sql.WriteLine("UPDATE catalog_state SET revision=revision+1,updated_at=strftime('%Y-%m-%dT%H:%M:%f+00:00','now') WHERE singleton=1;\nCOMMIT;");
    }

    private static List<RepairGroup> ValidateGroups(RepairPlan plan, CatalogTransferItem old)
    {
        var covered = new HashSet<int>();
        var groups = new List<RepairGroup>();
        foreach (var group in plan.Groups)
        {
            if (group.SourceIndexes.Count == 0) throw new InvalidDataException("Empty group.");
            var members = new List<CatalogTransferItem>();
            foreach (var index in group.SourceIndexes)
            {
                if (index < 0 || index >= plan.Sources.Count || !covered.Add(index)) throw new InvalidDataException("Invalid/duplicate source index.");
                var source = plan.Sources[index];
                var item = source.FreshItem!;
                var preview = new CatalogTransferItem { Name = source.PreviewName, MediaType = item.MediaType, Subtype = item.Subtype };
                if (!CatalogTransferIdentity.AreCompatible(old, item) || !RepairEvidence.Matches(item, preview))
                    throw new InvalidDataException($"Fresh response does not verify the source identity: {source.ProviderId}/{source.ExternalId}.");
                if (members.Any(member => !RepairEvidence.Matches(member, item)))
                    throw new InvalidDataException("Group contains unrelated fresh responses.");
                members.Add(item);
            }
            var merged = members[0];
            foreach (var member in members.Skip(1))
                merged = CatalogTransferIdentity.CombineConverged(merged, CatalogTransferIdentity.ConvergeIdentity(merged, member));
            groups.Add(new() { Item = merged, SourceIndexes = group.SourceIndexes });
        }
        if (covered.Count != plan.Sources.Count) throw new InvalidDataException("Not all sources are represented.");
        for (var i = 0; i < groups.Count; i++)
            for (var j = i + 1; j < groups.Count; j++)
                if (RepairEvidence.Matches(groups[i].Item, groups[j].Item))
                    throw new InvalidDataException("Two proposed groups still identify the same work; review grouping.");
        return groups;
    }

    private static void RejectOtherMatchingRows(SqliteConnection db, SqliteTransaction tx, string originalId, CatalogTransferItem item)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in CatalogTransferIdentity.GetNameKeys(item))
        {
            using var query = Command(db, tx, "SELECT c.id,c.payload FROM catalog_items c JOIN catalog_names n ON n.item_id=c.id WHERE n.name_key=$name AND c.id<>$id;");
            query.Parameters.AddWithValue("$name", key);
            query.Parameters.AddWithValue("$id", originalId);
            using var reader = query.ExecuteReader();
            while (reader.Read())
                if (seen.Add(reader.GetString(0)) && RepairEvidence.Matches(
                    JsonSerializer.Deserialize(reader.GetString(1), Compact.CatalogTransferItem)!, item))
                    throw new InvalidDataException($"Another existing catalog component matches '{item.Name}'; gather its sources before applying.");
        }
    }

    private static CatalogTransferItem Sanitize(CatalogTransferItem item) => item with
    {
        ThumbnailRef = null,
        BackdropRef = null,
        Sources = [],
        DistributionRights = [],
        Aliases = null,
        AlternativeNames = CatalogTransferMapper.GetAlternativeNames(item).Where(a => CatalogTransferIdentity.IsUsableName(a.Name)).ToArray(),
        Contributors = item.Contributors.Select(c => c with { Url = null, ThumbnailRef = null }).ToArray(),
        Units = item.Units.Select(u => u with { ThumbnailRef = null, Sources = [] }).ToArray(),
        Seasons = item.Seasons.Select(s => s with { ThumbnailRef = null, Units = s.Units.Select(u => u with { ThumbnailRef = null, Sources = [] }).ToArray() }).ToArray()
    };

    private static void WriteItem(SqliteConnection db, SqliteTransaction tx, CatalogTransferItem item, long revision, string now)
    {
        var id = item.Id!.Value.ToString("N");
        var payload = JsonSerializer.Serialize(item, Compact.CatalogTransferItem);
        using var insert = Command(db, tx, """
            INSERT INTO catalog_items(id,media_type,subtype,name,search_text,payload,content_hash,revision,updated_at)
            VALUES($id,$media,$subtype,$name,$search,$payload,$hash,$revision,$now)
            ON CONFLICT(id) DO UPDATE SET media_type=excluded.media_type,subtype=excluded.subtype,name=excluded.name,
            search_text=excluded.search_text,payload=excluded.payload,content_hash=excluded.content_hash,revision=excluded.revision,updated_at=excluded.updated_at;
            DELETE FROM catalog_names WHERE item_id=$id;
            """);
        insert.Parameters.AddWithValue("$id", id);
        insert.Parameters.AddWithValue("$media", item.MediaType);
        insert.Parameters.AddWithValue("$subtype", item.Subtype);
        insert.Parameters.AddWithValue("$name", item.Name);
        insert.Parameters.AddWithValue("$search", SearchText(string.Join('\n', CatalogTransferIdentity.GetNameKeys(item).Concat(item.Genres)
            .Concat(item.Categories).Concat(item.Contributors.Select(c => c.Name)).Append(item.Description ?? ""))));
        insert.Parameters.AddWithValue("$payload", payload);
        insert.Parameters.AddWithValue("$hash", Hash(payload));
        insert.Parameters.AddWithValue("$revision", revision);
        insert.Parameters.AddWithValue("$now", now);
        insert.ExecuteNonQuery();
        foreach (var key in CatalogTransferIdentity.GetNameKeys(item))
        {
            using var alias = Command(db, tx, "INSERT INTO catalog_names(name_key,item_id) VALUES($key,$id);");
            alias.Parameters.AddWithValue("$key", key);
            alias.Parameters.AddWithValue("$id", id);
            alias.ExecuteNonQuery();
        }
    }

    private static ArchivedRow ReadOriginal(SqliteConnection db, SqliteTransaction tx, Guid id)
    {
        using var command = Command(db, tx, "SELECT id,media_type,subtype,name,search_text,payload,content_hash,revision,updated_at FROM catalog_items WHERE id=$id;");
        command.Parameters.AddWithValue("$id", id.ToString("N"));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidDataException("Target item is missing.");
        return new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
            reader.GetString(5), reader.GetString(6), reader.GetInt64(7), reader.GetString(8));
    }
    private static List<ArchivedBinding> ReadBindings(SqliteConnection db, SqliteTransaction tx, string id)
    {
        using var command = Command(db, tx, "SELECT provider_id,external_id,item_id," + BindingJson + " FROM crawler_titles WHERE item_id=$id ORDER BY provider_id,external_id;");
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        var rows = new List<ArchivedBinding>();
        while (reader.Read()) rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        return rows;
    }
    private const string BindingJson = "json_object('provider_id',provider_id,'external_id',external_id,'preview',preview,'item_id',item_id,'discovered_at',discovered_at,'last_seen',last_seen,'last_success',last_success,'due_at',due_at,'failures',failures,'failure_kind',failure_kind)";
    private static void ReassignBinding(SqliteConnection db, SqliteTransaction tx, RepairSource source, string oldId, string newId, List<ArchivedBinding> archive)
    {
        var resolved = source.ResolvedExternalId ?? source.ExternalId;
        if (resolved != source.ExternalId)
        {
            using var canonical = Command(db, tx, "SELECT item_id," + BindingJson + " FROM crawler_titles WHERE provider_id=$provider AND external_id=$external;");
            canonical.Parameters.AddWithValue("$provider", source.ProviderId);
            canonical.Parameters.AddWithValue("$external", resolved);
            string? canonicalRow = null;
            string? canonicalItem = null;
            using (var reader = canonical.ExecuteReader())
                if (reader.Read()) { canonicalItem = reader.IsDBNull(0) ? null : reader.GetString(0); canonicalRow = reader.GetString(1); }
            if (canonicalRow is not null)
            {
                if (canonicalItem is not null && canonicalItem != oldId && canonicalItem != newId)
                    throw new InvalidDataException("Canonical binding belongs to another catalog component.");
                if (!archive.Any(b => b.ProviderId == source.ProviderId && b.ExternalId == resolved))
                    archive.Add(new(source.ProviderId, resolved, canonicalItem, canonicalRow));
                using var removeOld = Command(db, tx, "DELETE FROM crawler_titles WHERE provider_id=$provider AND external_id=$external AND item_id=$old;");
                removeOld.Parameters.AddWithValue("$provider", source.ProviderId);
                removeOld.Parameters.AddWithValue("$external", source.ExternalId);
                removeOld.Parameters.AddWithValue("$old", oldId);
                if (removeOld.ExecuteNonQuery() != 1) throw new InvalidDataException("Missing retired binding.");
            }
            else
            {
                using var rename = Command(db, tx, "UPDATE crawler_titles SET external_id=$resolved WHERE provider_id=$provider AND external_id=$external AND item_id=$old;");
                rename.Parameters.AddWithValue("$provider", source.ProviderId);
                rename.Parameters.AddWithValue("$external", source.ExternalId);
                rename.Parameters.AddWithValue("$resolved", resolved);
                rename.Parameters.AddWithValue("$old", oldId);
                if (rename.ExecuteNonQuery() != 1) throw new InvalidDataException("Missing canonicalization source.");
            }
        }
        using var update = Command(db, tx, "UPDATE crawler_titles SET item_id=$new WHERE provider_id=$provider AND external_id=$external AND (item_id=$old OR item_id=$new OR item_id IS NULL);");
        update.Parameters.AddWithValue("$new", newId);
        update.Parameters.AddWithValue("$provider", source.ProviderId);
        update.Parameters.AddWithValue("$external", resolved);
        update.Parameters.AddWithValue("$old", oldId);
        if (update.ExecuteNonQuery() != 1) throw new InvalidDataException("A binding could not be reassigned.");
    }
    private static List<ArchivedRedirect> ReadRedirects(SqliteConnection db, SqliteTransaction tx, string id)
    {
        using var command = Command(db, tx, "SELECT from_id,to_id FROM catalog_redirects WHERE to_id=$id ORDER BY from_id;");
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        var rows = new List<ArchivedRedirect>();
        while (reader.Read()) rows.Add(new(reader.GetString(0), reader.GetString(1)));
        return rows;
    }
    private static void CheckIntegrity(SqliteConnection db)
    {
        if (Scalar(db, null, "PRAGMA integrity_check;") as string != "ok" || Scalar(db, null, "PRAGMA foreign_key_check;") is not null)
            throw new InvalidDataException("Database integrity/foreign-key check failed.");
    }
    private static string TableHash(SqliteConnection db, string query)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var command = Command(db, null, query);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var bytes = Encoding.UTF8.GetBytes(reader.GetString(i));
                hash.AppendData(BitConverter.GetBytes(bytes.Length));
                hash.AppendData(bytes);
            }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    private static string SearchText(string value) => string.Concat(value.Normalize(NormalizationForm.FormD)
        .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)).ToUpperInvariant().Normalize(NormalizationForm.FormC);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string HashFile(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static SqliteConnection Open(string path, bool readOnly)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite
        }.ToString());
        db.Open();
        return db;
    }
    private static SqliteCommand Command(SqliteConnection db, SqliteTransaction? tx, string sql)
    { var command = db.CreateCommand(); command.Transaction = tx; command.CommandText = sql; return command; }
    private static object? Scalar(SqliteConnection db, SqliteTransaction? tx, string sql)
    { using var command = Command(db, tx, sql); return command.ExecuteScalar(); }
    private static void Execute(SqliteConnection db, SqliteTransaction? tx, string sql)
    { using var command = Command(db, tx, sql); command.ExecuteNonQuery(); }
}
