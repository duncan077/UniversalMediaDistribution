using UniversalMediaDistribution.Catalog.ContentSources;

namespace UniversalMediaDistribution.Server.Tests;

public sealed class CatalogCrawlerSettingsTests
{
    [Fact]
    public void DefaultsToTwoSecondsAndSavedValuesSurviveRestartAndAreReadPerRun()
    {
        using var scope = TestDirectory.Create();
        var database = new ServerDatabase(scope.Path);
        using var http = new HttpClient();
        var providers = BuiltInApiContentProviders.Create(http);
        var initial = CatalogCrawlerConfiguration.Read(providers, database);
        Assert.Empty(initial.ProviderIntervals);
        Assert.Equal(TimeSpan.FromSeconds(2), initial.MinimumTitleInterval);
        var diagnostics = new ServerDiagnostics(database);
        diagnostics.SetProviderInterval("olympusxyz", 1);
        diagnostics.SetProviderInterval("leercapitulo", 60);

        var restarted = new ServerDatabase(scope.Path);
        var options = CatalogCrawlerConfiguration.Read(providers, restarted);
        Assert.Equal(TimeSpan.FromSeconds(1), options.ProviderIntervals["olympusxyz"]);
        Assert.Equal(TimeSpan.FromSeconds(60), options.ProviderIntervals["leercapitulo"]);
        Assert.Empty(initial.ProviderIntervals);
        var snapshot = new ServerDiagnosticsStore(restarted).Snapshot(providers.Select(provider => provider.Descriptor).ToArray(),
            new HashSet<string>(), new(DateTimeOffset.UtcNow, 0, 0, 0, 0));
        Assert.Equal(1, snapshot.Providers.Single(provider => provider.Id == "olympusxyz").IntervalSeconds);
        Assert.Equal(60, snapshot.Providers.Single(provider => provider.Id == "leercapitulo").IntervalSeconds);
        Assert.Equal(2, snapshot.Providers.Single(provider => provider.Id == "jkanime").IntervalSeconds);

        diagnostics.SetProviderInterval("olympusxyz", 5);
        Assert.Equal(TimeSpan.FromSeconds(1), options.ProviderIntervals["olympusxyz"]);
        Assert.Equal(TimeSpan.FromSeconds(5), CatalogCrawlerConfiguration.Read(providers, restarted).ProviderIntervals["olympusxyz"]);
        Assert.Empty(diagnostics.Store.ReadLogs(null, null, null, 10).Items);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(61)]
    public void InvalidIntervalDoesNotOverwriteExistingValue(int seconds)
    {
        using var scope = TestDirectory.Create();
        var store = new CatalogCrawlerSettingsStore(new(scope.Path));
        store.SetInterval("olympusxyz", 2);
        Assert.Throws<ArgumentException>(() => store.SetInterval("olympusxyz", seconds));
        Assert.Equal(2, new CatalogCrawlerSettingsStore(new(scope.Path)).ReadIntervals()["olympusxyz"]);
    }
}
