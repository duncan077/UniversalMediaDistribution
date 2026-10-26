using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.Catalog.Transfer;

namespace UniversalMediaDistribution.Server.Tests;

public sealed class CatalogIdentityRepairTests
{
    [Fact]
    public void OfflineRepairSeparatesPublicChaptersAndPreservesPrivateDocumentsAndOriginalBackup()
    {
        using var scope = TestDirectory.Create();
        var fixture = Prepare(scope.Path);
        var originalHash = HashFile(fixture.Backup);
        var output = Path.Combine(scope.Path, "repaired.db");
        var result = ServerRepair.Apply(fixture.Plan, output);
        Assert.True(result.PrivateDocumentsUnchanged);
        Assert.Equal(2, result.Works.Count);
        Assert.Equal(2, result.BindingsReassigned);
        Assert.Equal(1, result.AmbiguousRedirectsArchived);
        Assert.Equal(originalHash, HashFile(fixture.Backup));
        using var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={output};Pooling=False");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT payload FROM server_documents WHERE key='user-sync/test.json';";
        Assert.Equal("{\"favorites\":[\"unchanged\"],\"position\":12}", command.ExecuteScalar());
        command.CommandText = "SELECT COUNT(*) FROM catalog_items;";
        Assert.Equal(2L, command.ExecuteScalar());
        command.CommandText = "SELECT json_array_length(payload,'$.units') FROM catalog_items WHERE name='Work A';";
        Assert.Equal(1L, command.ExecuteScalar());
        command.CommandText = "SELECT COUNT(*) FROM catalog_redirects;";
        Assert.Equal(0L, command.ExecuteScalar());
        command.CommandText = "SELECT COUNT(*) FROM catalog_identity_repair_archive;";
        Assert.Equal(1L, command.ExecuteScalar());
        Assert.Throws<IOException>(() => ServerRepair.Apply(fixture.Plan, output));
    }

    [Theory]
    [InlineData("failed-source")]
    [InlineData("mixed-group")]
    [InlineData("stale-hash")]
    public void IncompleteAmbiguousOrChangedEvidenceCannotBeApplied(string problem)
    {
        using var scope = TestDirectory.Create();
        var fixture = Prepare(scope.Path);
        var plan = JsonSerializer.Deserialize(File.ReadAllText(fixture.Plan), RepairJsonContext.Default.RepairPlan)!;
        if (problem == "failed-source") plan.Sources[0].Failure = "HttpRequestException";
        if (problem == "mixed-group") plan.Groups = [new() { Item = plan.Sources[0].FreshItem!, SourceIndexes = [0, 1] }];
        if (problem == "stale-hash") plan = new RepairPlan
        {
            DatabaseKind = plan.DatabaseKind,
            ItemId = plan.ItemId,
            Name = plan.Name,
            BackupSha256 = "changed",
            Sources = plan.Sources,
            Groups = plan.Groups,
            FetchCompleted = true
        };
        File.WriteAllText(fixture.Plan, JsonSerializer.Serialize(plan, RepairJsonContext.Default.RepairPlan));
        var originalHash = HashFile(fixture.Backup);
        var output = Path.Combine(scope.Path, "rejected.db");
        Assert.Throws<InvalidDataException>(() => ServerRepair.Apply(fixture.Plan, output));
        Assert.False(File.Exists(output));
        Assert.Equal(originalHash, HashFile(fixture.Backup));
    }

    [Fact]
    public void CatalogOnlyPatchPreservesNewerPrivateProgressAndRejectsReapplication()
    {
        using var scope = TestDirectory.Create();
        var fixture = Prepare(scope.Path);
        var repaired = Path.Combine(scope.Path, "repaired.db");
        ServerRepair.Apply(fixture.Plan, repaired);
        var live = Path.Combine(scope.Path, "live.db");
        File.Copy(fixture.Backup, live);
        using var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={live};Pooling=False");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "UPDATE server_documents SET payload='{\"position\":99}' WHERE key='user-sync/test.json'; UPDATE catalog_state SET revision=100;";
        command.ExecuteNonQuery();
        command.CommandText = File.ReadAllText(repaired + ".catalog.sql");
        command.ExecuteNonQuery();
        command.CommandText = "SELECT payload FROM server_documents WHERE key='user-sync/test.json';";
        Assert.Equal("{\"position\":99}", command.ExecuteScalar());
        command.CommandText = "SELECT revision FROM catalog_state;";
        Assert.Equal(101L, command.ExecuteScalar());
        command.CommandText = File.ReadAllText(repaired + ".catalog.sql");
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => command.ExecuteNonQuery());
        command.CommandText = "ROLLBACK;";
        command.ExecuteNonQuery();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RetiredProviderIdsAreCanonicalizedWithoutLosingTheirArchive(bool canonicalAlreadyQueued)
    {
        using var scope = TestDirectory.Create();
        var fixture = Prepare(scope.Path);
        var plan = JsonSerializer.Deserialize(File.ReadAllText(fixture.Plan), RepairJsonContext.Default.RepairPlan)!;
        plan.Sources[1].ResolvedExternalId = "canonical-2";
        if (canonicalAlreadyQueued)
        {
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={fixture.Backup};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO crawler_titles(provider_id,external_id,preview,discovered_at,last_seen,due_at) SELECT provider_id,'canonical-2',preview,discovered_at,last_seen,due_at FROM crawler_titles WHERE provider_id='b';";
            command.ExecuteNonQuery();
        }
        plan = new RepairPlan
        {
            DatabaseKind = plan.DatabaseKind,
            ItemId = plan.ItemId,
            Name = plan.Name,
            BackupSha256 = HashFile(fixture.Backup),
            ExpectedServerPayloadSha256 = plan.ExpectedServerPayloadSha256,
            Sources = plan.Sources,
            Groups = plan.Groups,
            FetchCompleted = true
        };
        File.WriteAllText(fixture.Plan, JsonSerializer.Serialize(plan, RepairJsonContext.Default.RepairPlan));
        var output = Path.Combine(scope.Path, "canonical.db");
        ServerRepair.Apply(fixture.Plan, output);
        using var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={output};Pooling=False");
        db.Open();
        using var check = db.CreateCommand();
        check.CommandText = "SELECT external_id FROM crawler_titles WHERE provider_id='b';";
        Assert.Equal("canonical-2", check.ExecuteScalar());
        check.CommandText = "SELECT bindings FROM catalog_identity_repair_archive;";
        var archived = (string)check.ExecuteScalar()!;
        Assert.Contains("\"externalId\":\"2\"", archived);
        if (canonicalAlreadyQueued) Assert.Contains("canonical-2", archived);
    }

    private static (string Plan, string Backup) Prepare(string root)
    {
        var db = new ServerDatabase(root);
        var catalog = new SqliteOnlineCatalogStore(root);
        var first = Book("Work A", "1");
        var second = Book("Work B", "924");
        var dirty = first with { AlternativeNames = [new() { Name = "Work B" }], Units = first.Units.Concat(second.Units).ToArray() };
        var id = catalog.MergeScraped("a", "1", dirty, default).ItemId;
        var queue = new CatalogCrawlerStore(db);
        queue.State("a");
        queue.State("b");
        queue.Enqueue("a", [new("1", first)], DateTimeOffset.UtcNow, updates: true);
        queue.Enqueue("b", [new("2", second)], DateTimeOffset.UtcNow, updates: true);
        queue.TitleSucceeded("a", "1", id, DateTimeOffset.UtcNow);
        queue.TitleSucceeded("b", "2", id, DateTimeOffset.UtcNow);
        string payload;
        using (var connection = db.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO server_documents VALUES('user-sync/test.json','user-sync','{\"favorites\":[\"unchanged\"],\"position\":12}'); INSERT INTO catalog_redirects VALUES($from,$to);";
            command.Parameters.AddWithValue("$from", Guid.CreateVersion7().ToString("N"));
            command.Parameters.AddWithValue("$to", id.ToString("N"));
            command.ExecuteNonQuery();
            command.CommandText = "SELECT payload FROM catalog_items WHERE id=$to;";
            payload = (string)command.ExecuteScalar()!;
        }
        var backup = Path.Combine(root, "backup.sqlite");
        db.Backup(backup);
        var plan = new RepairPlan
        {
            DatabaseKind = "server",
            ItemId = id,
            Name = first.Name,
            BackupSha256 = HashFile(backup),
            ExpectedServerPayloadSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))),
            Sources = [new("a", "1", first.Name) { FreshItem = first }, new("b", "2", second.Name) { FreshItem = second }],
            Groups = [new() { Item = first, SourceIndexes = [0] }, new() { Item = second, SourceIndexes = [1] }],
            FetchCompleted = true
        };
        var path = Path.Combine(root, "plan.json");
        File.WriteAllText(path, JsonSerializer.Serialize(plan, RepairJsonContext.Default.RepairPlan));
        return (path, backup);
    }
    private static CatalogTransferItem Book(string name, string number) => new()
    {
        Name = name,
        MediaType = "book",
        Subtype = "manga",
        Units = [new() { Kind = "chapter", DisplayNumber = number }]
    };
    private static string HashFile(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
}
