using Microsoft.Data.Sqlite;
using UniversalMediaDistribution.Application.Catalog;
using UniversalMediaDistribution.Application.UserState;
using UniversalMediaDistribution.Domain.Catalog;
using UniversalMediaDistribution.Domain.Reading;

namespace UniversalMediaDistribution.Catalog.Sqlite.Tests;

/// <summary>
/// Two people sharing a device share the library and nothing else. These are
/// the checks that the sharing line falls where it should: the catalog on one
/// side, everything anyone does with it on the other.
/// </summary>
public sealed class ProfileIsolationTests
{
    private static readonly Guid Second =
        new("00000000-0000-0000-0000-0000000000ff");

    [Fact]
    public async Task FavouritesBelongToOneProfile()
    {
        await using var scope = await CreateWithSecondProfileAsync();
        var item = Assert.Single(await scope.Repository.ListAsync());
        var state = new SqliteUserMediaStateStore(scope.Database);

        await state.SetFavoriteCatalogItemAsync(item.Id, true);
        Assert.Contains(item.Id, await state.LoadFavoriteCatalogItemIdsAsync());

        scope.Database.ActiveProfileId = Second;
        Assert.Empty(await state.LoadFavoriteCatalogItemIdsAsync());
    }

    [Fact]
    public async Task ProgressBelongsToOneProfile()
    {
        await using var scope = await CreateWithSecondProfileAsync();
        var item = Assert.Single(await scope.Repository.ListAsync());
        var details = await scope.Repository.GetDetailsAsync(item.Id);
        var content = details!.Identity;
        var state = new SqliteUserMediaStateStore(scope.Database);

        await state.SaveProgressAsync(
            content,
            new MediaProgressState(
                42,
                MediaPositionKind.Seconds,
                isCompleted: false,
                DateTimeOffset.UtcNow));
        Assert.Equal(
            42,
            (await state.LoadProgressAsync(content))!.Position);

        scope.Database.ActiveProfileId = Second;
        Assert.Null(await state.LoadProgressAsync(content));
    }

    [Fact]
    public async Task ListsBelongToOneProfile()
    {
        await using var scope = await CreateWithSecondProfileAsync();
        var state = new SqliteUserMediaStateStore(scope.Database);
        await state.CreateUserListAsync("Later");

        Assert.Single(await state.LoadUserListsAsync());

        scope.Database.ActiveProfileId = Second;
        Assert.Empty(await state.LoadUserListsAsync());
    }

    /// <summary>
    /// The same list name on two profiles has to be allowed — it was unique
    /// across the whole device before, which would have made the second person
    /// unable to keep a list called what the first one called theirs.
    /// </summary>
    [Fact]
    public async Task TwoProfilesMayNameAListTheSameThing()
    {
        await using var scope = await CreateWithSecondProfileAsync();
        var state = new SqliteUserMediaStateStore(scope.Database);
        await state.CreateUserListAsync("Later");

        scope.Database.ActiveProfileId = Second;
        var second = await state.CreateUserListAsync("Later");

        Assert.Equal("Later", second.Name);
        Assert.Single(await state.LoadUserListsAsync());
    }

    [Fact]
    public async Task FavouriteProvidersBelongToOneProfile()
    {
        await using var scope = await CreateWithSecondProfileAsync();
        var state = new SqliteUserMediaStateStore(scope.Database);
        await state.SetFavoriteContributorAsync("contributor:one", true);

        Assert.Contains(
            "contributor:one",
            await state.LoadFavoriteContributorKeysAsync());

        scope.Database.ActiveProfileId = Second;
        Assert.Empty(await state.LoadFavoriteContributorKeysAsync());
    }

    /// <summary>
    /// The catalog is the shared half, so it must look identical from either
    /// side. This is the check that would fail if profile scoping were ever
    /// applied one table too far.
    /// </summary>
    [Fact]
    public async Task TheCatalogLooksTheSameFromBothProfiles()
    {
        await using var scope = await CreateWithSecondProfileAsync();
        var fromFirst = await scope.Repository.ListAsync();

        scope.Database.ActiveProfileId = Second;
        var fromSecond = await scope.Repository.ListAsync();

        Assert.Equal(
            fromFirst.Select(static item => item.Id),
            fromSecond.Select(static item => item.Id));
    }

    private static async Task<TestDatabaseScope> CreateWithSecondProfileAsync()
    {
        var scope = TestDatabaseScope.Create();
        await scope.Repository.MergeCatalogAsync(
            TestCatalogDocuments.SingleMovie("Shared title"));

        await using var connection = new SqliteConnection(
            $"Data Source={scope.DatabasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO profiles (
                id, name, name_key, accent_key, sort_order, created_at, last_used_at)
            VALUES ($id, 'Someone else', 'SOMEONE ELSE', NULL, 1,
                    datetime('now'), NULL);
            """;
        command.Parameters.AddWithValue("$id", Second.ToString("D"));
        await command.ExecuteNonQueryAsync();
        await connection.CloseAsync();
        SqliteConnection.ClearPool(connection);
        return scope;
    }
}
