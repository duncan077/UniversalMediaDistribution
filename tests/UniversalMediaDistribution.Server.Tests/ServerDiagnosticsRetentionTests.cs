namespace UniversalMediaDistribution.Server.Tests;

public sealed class ServerDiagnosticsRetentionTests
{
    [Theory]
    [InlineData("info")]
    [InlineData("warning")]
    public void NonFailuresAreNotInserted(string level)
    {
        using var scope = TestDirectory.Create();
        var database = new ServerDatabase(scope.Path);
        new ServerDiagnosticsStore(database).Write(level, "crawler", "provider", "ok", "Not a failure");
        Assert.Equal(0, Count(database));
    }

    [Fact]
    public void ReadingDeletesExpiredFailuresAndLegacyNonFailuresButKeepsRecentFailures()
    {
        using var scope = TestDirectory.Create();
        var database = new ServerDatabase(scope.Path);
        Seed(database);
        var entries = new ServerDiagnosticsStore(database).ReadLogs(null, null, null, 10).Items;
        Assert.Equal("recent", Assert.Single(entries).Status);
        Assert.Equal(1, Count(database));
    }

    [Fact]
    public async Task CleanupRunsAtStartupAndWhileIdleWithoutChangingRunHistory()
    {
        using var scope = TestDirectory.Create();
        var database = new ServerDatabase(scope.Path);
        Seed(database);
        new CatalogCrawlerStore(database).StartRun("preserved", DateTimeOffset.UtcNow);
        using var service = new ServerDiagnosticsRetention(new(database), TimeSpan.FromMilliseconds(25));
        await service.StartAsync(CancellationToken.None);
        try
        {
            Assert.Equal(1, Count(database));
            // Expire the remaining row without writing a diagnostic or opening the admin.
            using var connection = database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE server_events SET occurred_at=$old;";
            command.Parameters.AddWithValue("$old", DateTimeOffset.UtcNow.AddHours(-25).ToString("O"));
            command.ExecuteNonQuery();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (Count(database) > 0) await Task.Delay(10, timeout.Token);
            command.CommandText = "SELECT COUNT(*) FROM crawler_runs WHERE id='preserved';";
            Assert.Equal(1L, command.ExecuteScalar());
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    private static void Seed(ServerDatabase database)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO server_events(occurred_at,level,source,stage,status,message) VALUES
                ($recent,'info','server','lifecycle','started','Legacy informational event'),
                ($recent,'warning','crawler','provider','partial','Legacy warning'),
                ($expired,'error','crawler','detail','expired','Expired error'),
                ($boundary,'error','crawler','detail','boundary','Exactly 24 hours old'),
                ($recent,'error','crawler','detail','recent','Recent error');
            """;
        var now = DateTimeOffset.UtcNow;
        command.Parameters.AddWithValue("$recent", now.AddHours(-23).ToString("O"));
        command.Parameters.AddWithValue("$expired", now.AddHours(-25).ToString("O"));
        command.Parameters.AddWithValue("$boundary", (now - ServerDiagnosticsStore.Retention).ToString("O"));
        command.ExecuteNonQuery();
    }

    private static long Count(ServerDatabase database)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM server_events;";
        return (long)command.ExecuteScalar()!;
    }
}
