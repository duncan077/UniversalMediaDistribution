using Microsoft.Data.Sqlite;

namespace UniversalMediaDistribution.Catalog.Sqlite.Tests;

/// <summary>
/// Guards the schema-version-12 indexes by asserting the plans SQLite actually
/// chooses, rather than merely that the indexes exist.
/// <para>
/// An index that exists but cannot be used is the failure mode these replaced:
/// content_units carried two indexes leading with catalog_item_id, but both are
/// partial, and a query that does not constrain season_id may not use either.
/// Asserting on the plan is the only check that would have caught that.
/// </para>
/// </summary>
public sealed class CatalogIndexPlanTests
{
    [Theory]
    // The per-title unit read, issued by the importer and by both readers.
    [InlineData(
        "SELECT id FROM content_units WHERE catalog_item_id = 'x';",
        "ix_content_units_catalog_item")]
    // The last-unit lookup in SqliteUserMediaStateStore, which also orders by
    // sort_order - the reason that column is in the index.
    [InlineData(
        "SELECT id FROM content_units WHERE catalog_item_id = 'x' " +
        "ORDER BY sort_order DESC;",
        "ix_content_units_catalog_item")]
    // The export reader's join onto the torrent package.
    [InlineData(
        "SELECT id FROM media_sources WHERE torrent_package_id = 'x';",
        "ix_media_sources_torrent_package")]
    [InlineData(
        "SELECT id FROM media_sources WHERE catalog_item_id = 'x';",
        "ix_media_sources_catalog_item")]
    [InlineData(
        "SELECT id FROM media_sources WHERE content_unit_id = 'x';",
        "ix_media_sources_content_unit")]
    // The resume lookup, which runs on a two-second timer during playback.
    [InlineData(
        "SELECT position FROM playback_progress " +
        "WHERE profile_id = 'x' AND content_unit_id = 'y';",
        "ix_playback_progress_profile_unit")]
    public async Task IndexedQueriesDoNotScan(string sql, string expectedIndex)
    {
        await using var scope = TestDatabaseScope.Create();
        await scope.Database.InitializeAsync();

        var plan = await ExplainAsync(scope.DatabasePath, sql);

        Assert.True(
            plan.Contains(expectedIndex, StringComparison.Ordinal) &&
            !plan.Contains("SCAN", StringComparison.Ordinal),
            $"Expected a search using {expectedIndex}.{Environment.NewLine}" +
            $"Query: {sql}{Environment.NewLine}Plan:{Environment.NewLine}{plan}");
    }

    /// <summary>
    /// The whole-catalog ordering behind the "recently added" shelf. It cannot
    /// avoid visiting every row, but it must not also sort them into temporary
    /// storage on every catalog refresh.
    /// </summary>
    [Fact]
    public async Task PerProfileProgressHistoryDoesNotSortIntoTemporaryStorage()
    {
        await using var scope = TestDatabaseScope.Create();
        await scope.Database.InitializeAsync();

        var plan = await ExplainAsync(
            scope.DatabasePath,
            "SELECT content_unit_id FROM playback_progress " +
            "WHERE profile_id = 'x' ORDER BY updated_at DESC;");

        Assert.True(
            plan.Contains(
                "ix_playback_progress_profile_updated",
                StringComparison.Ordinal) &&
            !plan.Contains("TEMP B-TREE", StringComparison.Ordinal),
            $"Expected an ordered index scan.{Environment.NewLine}" +
            $"Plan:{Environment.NewLine}{plan}");
    }

    private static async Task<string> ExplainAsync(
        string databasePath,
        string sql)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
        await using var reader = await command.ExecuteReaderAsync();

        var rows = new List<string>();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetString(reader.GetOrdinal("detail")));
        }

        return string.Join(Environment.NewLine, rows);
    }
}
