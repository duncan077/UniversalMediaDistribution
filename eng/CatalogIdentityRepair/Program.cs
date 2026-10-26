using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.Catalog.Transfer;

// Applying is offline only: a new database is produced, never an in-place
// write to a supplied database or to a live server/user snapshot.
if (args.Length == 3 && args[0] == "--apply-plan")
{
    var result = ServerRepair.Apply(args[1], args[2]);
    Console.WriteLine($"Verified repaired copy: {result.OutputPath}; works={result.Works.Count}; private documents unchanged={result.PrivateDocumentsUnchanged}");
    return;
}
if (args.Length == 2 && args[0] == "--refresh-failures")
{
    await RepairEvidence.RefreshFailuresAsync(args[1]);
    return;
}
if (args.Length != 3 || !Guid.TryParse(args[1], out var itemId))
    throw new ArgumentException("Usage: CatalogIdentityRepair <database> <item-id> <new-output-directory>");
var database = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[2]);
if (!File.Exists(database)) throw new FileNotFoundException("Database not found.");
if (Directory.Exists(output) || File.Exists(output)) throw new IOException("Output already exists; use a new directory.");
Directory.CreateDirectory(output);
if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(output, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
var backupPath = Path.Combine(output, "backup.sqlite");
using (var source = Open(database))
using (var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backupPath, Pooling = false }.ToString()))
{
    backup.Open();
    source.BackupDatabase(backup);
    using var check = backup.CreateCommand();
    check.CommandText = "PRAGMA quick_check;";
    if (check.ExecuteScalar() as string != "ok") throw new InvalidDataException("Backup integrity check failed.");
}
Console.WriteLine("Consistent database backup verified. Keep this directory private; it can contain account/session data.");
using var connection = Open(backupPath);
var server = Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='crawler_titles';") is long count && count == 1;
var id = itemId.ToString(server ? "N" : "D");
var originalPayload = server ? (string?)Scalar("SELECT payload FROM catalog_items WHERE id=$id;", id) : null;
var name = server
    ? JsonSerializer.Deserialize(originalPayload ?? throw new InvalidDataException("Item not found."), RepairJsonContext.Default.CatalogTransferItem)!.Name
    : (string?)Scalar("SELECT name FROM catalog_items WHERE id=$id;", id) ?? throw new InvalidDataException("Item not found.");
var mediaType = server ? JsonSerializer.Deserialize(originalPayload!, RepairJsonContext.Default.CatalogTransferItem)!.MediaType
    : (string)Scalar("SELECT media_type FROM catalog_items WHERE id=$id;", id)!;
var subtype = server ? JsonSerializer.Deserialize(originalPayload!, RepairJsonContext.Default.CatalogTransferItem)!.Subtype
    : ((string)Scalar("SELECT subtype_code FROM catalog_items WHERE id=$id;", id)!).Split('.')[^1];
var sources = new List<RepairSource>();
var oldNumbers = new List<string>();
if (server)
{
    var original = JsonSerializer.Deserialize(originalPayload!, RepairJsonContext.Default.CatalogTransferItem)!;
    oldNumbers.AddRange(original.Units.Select(unit => unit.DisplayNumber));
    using var command = Query("SELECT provider_id,external_id,preview FROM crawler_titles WHERE item_id=$id ORDER BY provider_id,external_id;", id);
    using var reader = command.ExecuteReader();
    while (reader.Read())
    {
        if (sources.Count >= 200) throw new InvalidDataException("More than 200 source bindings; manual investigation required.");
        var preview = JsonSerializer.Deserialize(reader.GetString(2), RepairJsonContext.Default.CatalogTransferItem)!;
        sources.Add(new(reader.GetString(0), reader.GetString(1), preview.Name));
    }
}
else
{
    using (var command = Query("SELECT display_number FROM content_units WHERE catalog_item_id=$id;", id))
    using (var reader = command.ExecuteReader())
        while (reader.Read()) oldNumbers.Add(reader.GetString(0));
    var bindings = Scalar("SELECT value FROM application_settings WHERE key=$id;", "api_title_binding:" + itemId.ToString("N")) as string;
    if (bindings is not null)
    {
        using var document = JsonDocument.Parse(bindings);
        foreach (var property in document.RootElement.EnumerateObject())
            sources.Add(new(property.Name, property.Value.GetProperty("externalId").GetString()!, property.Value.GetProperty("title").GetString()!));
    }
}
var plan = new RepairPlan
{
    DatabaseKind = server ? "server" : "app",
    ItemId = itemId,
    Name = name,
    OldChapterCount = oldNumbers.Count,
    BackupSha256 = HashFile(backupPath),
    ExpectedServerPayloadSha256 = originalPayload is null ? null : Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(originalPayload))),
    Sources = sources,
    FavoriteRows = server ? 0 : Convert.ToInt32(Scalar("SELECT COUNT(*) FROM library_entries WHERE catalog_item_id=$id AND is_favorite=1;", id), CultureInfo.InvariantCulture),
    ProgressRows = server ? 0 : Convert.ToInt32(Scalar("SELECT COUNT(*) FROM playback_progress WHERE catalog_item_id=$id OR content_unit_id IN (SELECT id FROM content_units WHERE catalog_item_id=$id);", id), CultureInfo.InvariantCulture)
};
await SavePlan(); // Retain a useful incomplete report if interrupted/offline.
using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(30));
using var http = CatalogCrawlerHttpClient.Create();
var registry = BuiltInApiContentProviders.Create(http).ToDictionary(p => p.Descriptor.Id);
foreach (var source in sources)
{
    if (!registry.TryGetValue(source.ProviderId, out var registration) || !registration.Descriptor.ProvidesUnitContent)
    {
        source.Failure = "Metadata-only or unavailable provider; not used as independent evidence.";
        continue;
    }
    try
    {
        var overrideUrl = Environment.GetEnvironmentVariable("UMD_CRAWLER_BASE_" + source.ProviderId.Replace('-', '_').ToUpperInvariant());
        var root = new Uri(string.IsNullOrWhiteSpace(overrideUrl) ? registration.Descriptor.DefaultBaseUrl : overrideUrl);
        if (!CatalogCrawlerHttpClient.IsPublicUri(root)) throw new InvalidDataException("Nonpublic base URL.");
        var interval = server ? Scalar("SELECT interval_seconds FROM crawler_provider_settings WHERE provider_id=$id;", source.ProviderId) : null;
        using var pacing = CatalogCrawlerHttpClient.UseProviderInterval(TimeSpan.FromSeconds(interval is long seconds ? seconds : 2));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var fresh = await registration.GetProvider().LoadItemAsync(root, new ApiContentSearchItem(source.ExternalId,
            new CatalogTransferItem { MediaType = mediaType, Subtype = subtype, Name = source.PreviewName }, RequireProviderTitle: true), timeout.Token);
        source.FreshItem = fresh.CatalogItem with { Id = null, SyncVersion = null };
        CatalogTransferValidator.Validate(new CatalogTransferDocument { Items = [source.FreshItem] });
        Console.WriteLine($"{source.ProviderId}: {source.FreshItem.Name}; chapters={source.FreshItem.Units.Count}");
    }
    catch (Exception exception) when (!cancellation.IsCancellationRequested)
    {
        source.FreshItem = null;
        source.Failure = exception.GetType().Name; // Do not copy URLs/tokens from arbitrary provider errors.
        Console.WriteLine($"{source.ProviderId}: failed ({source.Failure}); no replacement proposed for this source.");
    }
    await SavePlan();
}
// Cluster only mutually compatible fresh responses, not transitive chains of
// accumulated aliases from the contaminated item.
RepairEvidence.Regroup(plan);
foreach (var group in plan.Groups)
{
    var numbers = group.Item.Units.Select(u => NumberKey(u.DisplayNumber)).ToHashSet(StringComparer.Ordinal);
    group.OldNumbersNotInFreshGroup = oldNumbers.Where(n => !numbers.Contains(NumberKey(n))).ToArray();
}
plan.FetchCompleted = true;
await SavePlan();
Console.WriteLine($"Plan only: {plan.Groups.Count} proposed works. No catalog, favorites, progress, bindings or server user documents were changed.");

async Task SavePlan()
{
    await using var stream = File.Create(Path.Combine(output, "plan.json"));
    await JsonSerializer.SerializeAsync(stream, plan, RepairJsonContext.Default.RepairPlan);
}
SqliteCommand Query(string sql, string? value = null)
{
    var command = connection.CreateCommand();
    command.CommandText = sql;
    if (value is not null) command.Parameters.AddWithValue("$id", value);
    return command;
}
object? Scalar(string sql, string? value = null) { using var command = Query(sql, value); return command.ExecuteScalar(); }
static SqliteConnection Open(string path)
{
    var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
    connection.Open();
    return connection;
}
static string HashFile(string path)
{
    using var stream = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(stream));
}
static string NumberKey(string value) => decimal.TryParse(value.Replace(',', '.'), NumberStyles.Number,
    CultureInfo.InvariantCulture, out var number) ? number.ToString("G29", CultureInfo.InvariantCulture) : value.Trim();

internal sealed record RepairSource(string ProviderId, string ExternalId, string PreviewName)
{
    public string? ResolvedExternalId { get; set; }
    public CatalogTransferItem? FreshItem { get; set; }
    public string? Failure { get; set; }
}
internal sealed class RepairGroup
{
    public required CatalogTransferItem Item { get; set; }
    public List<int> SourceIndexes { get; set; } = [];
    public string[] OldNumbersNotInFreshGroup { get; set; } = [];
}
internal sealed class RepairPlan
{
    public required string DatabaseKind { get; init; }
    public Guid ItemId { get; init; }
    public required string Name { get; init; }
    public required string BackupSha256 { get; init; }
    public string? ExpectedServerPayloadSha256 { get; init; }
    public int OldChapterCount { get; init; }
    public int FavoriteRows { get; init; }
    public int ProgressRows { get; init; }
    public required List<RepairSource> Sources { get; init; }
    public List<RepairGroup> Groups { get; set; } = [];
    public bool FetchCompleted { get; set; }
    public bool Applied => false;
    public string Warning => "Preparation only. Keep item/unit IDs and favorites. Preserve ambiguous read history in quarantine; do not assign it to another work by chapter number. Stop crawler and client sync before a separately reviewed transactional repair; compare snapshot hashes first. Never import this plan as an ordinary catalog update.";
}
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(RepairPlan))]
[JsonSerializable(typeof(RepairResult))]
[JsonSerializable(typeof(ArchivedRow))]
[JsonSerializable(typeof(List<ArchivedBinding>))]
[JsonSerializable(typeof(List<ArchivedRedirect>))]
internal partial class RepairJsonContext : JsonSerializerContext;
