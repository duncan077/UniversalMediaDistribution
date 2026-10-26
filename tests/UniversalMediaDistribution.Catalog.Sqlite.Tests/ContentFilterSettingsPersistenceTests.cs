using UniversalMediaDistribution.Catalog.Sqlite;
using UniversalMediaDistribution.ContentFiltering.Subscriptions;

namespace UniversalMediaDistribution.Catalog.Sqlite.Tests;

public sealed class ContentFilterSettingsPersistenceTests
{
    [Fact]
    public async Task TheFilteringPreferenceSurvivesARestart()
    {
        await using var scope = TestDatabaseScope.Create();
        var refreshed = new DateTimeOffset(2026, 7, 27, 10, 30, 0, TimeSpan.Zero);

        await new SqliteContentFilterSettingsStore(
            new SqliteCatalogDatabase(scope.DatabasePath)).SaveAsync(
            new ContentFilterSettings
            {
                Mode = FilteringMode.Complete,
                HideAdultContent = true,
                LastRefreshUtc = refreshed
            });

        // A second store over a fresh connection stands in for the next run.
        var restored = await new SqliteContentFilterSettingsStore(
            new SqliteCatalogDatabase(scope.DatabasePath)).LoadAsync();

        Assert.Equal(FilteringMode.Complete, restored.Mode);
        Assert.True(restored.HideAdultContent);
        Assert.Equal(refreshed, restored.LastRefreshUtc);
    }

    [Fact]
    public async Task AnUnsetPreferenceDefaultsToStandardFiltering()
    {
        await using var scope = TestDatabaseScope.Create();

        var settings = await new SqliteContentFilterSettingsStore(
            new SqliteCatalogDatabase(scope.DatabasePath)).LoadAsync();

        Assert.Equal(FilteringMode.Standard, settings.Mode);
        Assert.True(settings.HideAdultContent);
        Assert.Null(settings.LastRefreshUtc);
    }

    [Fact]
    public async Task TurningFilteringOffIsRemembered()
    {
        await using var scope = TestDatabaseScope.Create();
        var store = new SqliteContentFilterSettingsStore(
            new SqliteCatalogDatabase(scope.DatabasePath));

        await store.SaveAsync(new ContentFilterSettings
        {
            Mode = FilteringMode.Off
        });

        Assert.Equal(
            FilteringMode.Off,
            (await new SqliteContentFilterSettingsStore(
                new SqliteCatalogDatabase(scope.DatabasePath))
                .LoadAsync()).Mode);
        Assert.True((await new SqliteContentFilterSettingsStore(
            new SqliteCatalogDatabase(scope.DatabasePath)).LoadAsync())
            .HideAdultContent);
    }

    [Fact]
    public async Task TurningAdultContentFilteringOffIsRemembered()
    {
        await using var scope = TestDatabaseScope.Create();
        var store = new SqliteContentFilterSettingsStore(
            new SqliteCatalogDatabase(scope.DatabasePath));

        await store.SaveAsync(new ContentFilterSettings
        {
            HideAdultContent = false
        });

        Assert.False((await new SqliteContentFilterSettingsStore(
            new SqliteCatalogDatabase(scope.DatabasePath)).LoadAsync())
            .HideAdultContent);
    }

    [Fact]
    public async Task AnUnreadableModeFallsBackToTheDefaultRatherThanFailing()
    {
        await using var scope = TestDatabaseScope.Create();
        var store = new SqliteContentFilterSettingsStore(
            new SqliteCatalogDatabase(scope.DatabasePath));
        await store.SaveAsync(new ContentFilterSettings
        {
            Mode = (FilteringMode)99
        });

        Assert.Equal(FilteringMode.Standard, (await store.LoadAsync()).Mode);
    }
}
