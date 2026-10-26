using UniversalMediaDistribution.Application.ContentSources;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.Catalog.Sqlite.Tests;

public sealed class ApiTitleBindingPersistenceTests
{
    [Fact]
    public async Task ABoundTitleSurvivesReopeningTheDatabase()
    {
        await using var scope = TestDatabaseScope.Create();
        var itemId = Guid.CreateVersion7();
        await new SqliteApiTitleBindingStore(scope.Database).SaveAsync(
            new ApiTitleBinding(
                itemId,
                "example-provider",
                "/anime/kami-no-shizuku",
                "Kami no Shizuku"));

        var restored = await new SqliteApiTitleBindingStore(
            new SqliteCatalogDatabase(scope.DatabasePath)).LoadAsync(itemId);

        var binding = Assert.Single(restored).Value;
        Assert.Equal("example-provider", binding.ProviderId);
        Assert.Equal("/anime/kami-no-shizuku", binding.ExternalId);
        Assert.Equal("Kami no Shizuku", binding.Title);
    }

    [Fact]
    public async Task EachProviderKeepsItsOwnTitleForTheSameItem()
    {
        await using var scope = TestDatabaseScope.Create();
        var store = new SqliteApiTitleBindingStore(scope.Database);
        var itemId = Guid.CreateVersion7();
        await store.SaveAsync(
            new ApiTitleBinding(itemId, "provider-a", "a/1", "Title A"));
        await store.SaveAsync(
            new ApiTitleBinding(itemId, "provider-b", "b/2", "Title B"));

        var bindings = await store.LoadAsync(itemId);

        Assert.Equal(2, bindings.Count);
        Assert.Equal("a/1", bindings["provider-a"].ExternalId);
        Assert.Equal("b/2", bindings["provider-b"].ExternalId);
    }

    [Fact]
    public async Task ABindingKeepsItsKnownUnitRoutesAndLastUseTime()
    {
        await using var scope = TestDatabaseScope.Create();
        var store = new SqliteApiTitleBindingStore(scope.Database);
        var itemId = Guid.CreateVersion7();
        var lastUsedAt = new DateTimeOffset(
            2026,
            7,
            28,
            12,
            34,
            56,
            TimeSpan.Zero);
        await store.SaveAsync(
            new ApiTitleBinding(
                itemId,
                "seriesflix",
                "serie/rick-y-morty-zeek",
                "Rick y Morty",
                [
                    new ApiProviderUnitRoute(
                        ContentUnitKind.Episode,
                        displayNumber: null,
                        seasonNumber: 1,
                        "/temporada/rick-y-morty-zeek-1/")
                ],
                lastUsedAt));

        var binding = Assert.Single(
            await new SqliteApiTitleBindingStore(
                    new SqliteCatalogDatabase(scope.DatabasePath))
                .LoadAsync(itemId)).Value;

        Assert.Equal(lastUsedAt, binding.LastUsedAt);
        var route = Assert.Single(binding.UnitRoutes);
        Assert.Equal(ContentUnitKind.Episode, route.UnitKind);
        Assert.Null(route.DisplayNumber);
        Assert.Equal(1, route.SeasonNumber);
        Assert.Equal(
            "/temporada/rick-y-morty-zeek-1/",
            route.Route);
    }

    [Fact]
    public async Task PersistedUnitRoutesNeverRetainTheProviderDomain()
    {
        await using var scope = TestDatabaseScope.Create();
        var store = new SqliteApiTitleBindingStore(scope.Database);
        var itemId = Guid.CreateVersion7();
        var route = new ApiProviderUnitRoute(
            ContentUnitKind.Episode,
            displayNumber: null,
            seasonNumber: 2,
            "https://old.series.example/temporada/show-2/?lang=es#player");
        Assert.Equal("/temporada/show-2/?lang=es", route.Route);

        await store.SaveAsync(
            new ApiTitleBinding(
                itemId,
                "seriesflix",
                "serie/show",
                "Show",
                [route]));

        var restored = Assert.Single(
            await new SqliteApiTitleBindingStore(
                    new SqliteCatalogDatabase(scope.DatabasePath))
                .LoadAsync(itemId)).Value;

        Assert.Equal(
            "/temporada/show-2/?lang=es",
            Assert.Single(restored.UnitRoutes).Route);
    }

    [Fact]
    public async Task RebindingAProviderReplacesTheEarlierTitle()
    {
        await using var scope = TestDatabaseScope.Create();
        var store = new SqliteApiTitleBindingStore(scope.Database);
        var itemId = Guid.CreateVersion7();
        await store.SaveAsync(
            new ApiTitleBinding(itemId, "provider-a", "a/wrong", "Wrong"));

        await store.SaveAsync(
            new ApiTitleBinding(itemId, "provider-a", "a/right", "Right"));

        Assert.Equal(
            "a/right",
            Assert.Single(await store.LoadAsync(itemId)).Value.ExternalId);
    }

    [Fact]
    public async Task AStaleBindingCanBeForgotten()
    {
        await using var scope = TestDatabaseScope.Create();
        var store = new SqliteApiTitleBindingStore(scope.Database);
        var itemId = Guid.CreateVersion7();
        await store.SaveAsync(
            new ApiTitleBinding(itemId, "provider-a", "a/1", "Title A"));
        await store.SaveAsync(
            new ApiTitleBinding(itemId, "provider-b", "b/2", "Title B"));

        await store.RemoveAsync(itemId, "provider-a");

        var remaining = await store.LoadAsync(itemId);
        Assert.Equal("provider-b", Assert.Single(remaining).Key);
    }

    [Fact]
    public async Task BindingsAreScopedToTheirCatalogItem()
    {
        await using var scope = TestDatabaseScope.Create();
        var store = new SqliteApiTitleBindingStore(scope.Database);
        await store.SaveAsync(
            new ApiTitleBinding(
                Guid.CreateVersion7(),
                "provider-a",
                "a/1",
                "Title A"));

        Assert.Empty(await store.LoadAsync(Guid.CreateVersion7()));
    }

    [Fact]
    public async Task AnItemWithoutBindingsReportsNone()
    {
        await using var scope = TestDatabaseScope.Create();

        Assert.Empty(
            await new SqliteApiTitleBindingStore(scope.Database)
                .LoadAsync(Guid.CreateVersion7()));
    }
}
