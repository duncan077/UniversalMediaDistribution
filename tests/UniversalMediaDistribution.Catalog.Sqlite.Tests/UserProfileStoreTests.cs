using UniversalMediaDistribution.Application.UserState;

namespace UniversalMediaDistribution.Catalog.Sqlite.Tests;

/// <summary>
/// Managing who uses this installation. The rules that matter are the ones
/// that stop somebody losing what is theirs: a profile cannot be removed while
/// it is the only one, and removing one takes its own state and nothing else.
/// </summary>
public sealed class UserProfileStoreTests
{
    [Fact]
    public async Task AnUpgradedLibraryStartsWithTheProfileItInherited()
    {
        await using var scope = TestDatabaseScope.Create();
        var store = new SqliteUserProfileStore(scope.Database);

        var profile = Assert.Single(await store.ListAsync());

        Assert.Equal("Me", profile.Name);
        Assert.Null(profile.AccentKey);
    }

    [Fact]
    public async Task ProfilesAreListedInTheOrderTheyWereAdded()
    {
        await using var scope = TestDatabaseScope.Create();
        var store = new SqliteUserProfileStore(scope.Database);

        await store.CreateAsync("Ana");
        await store.CreateAsync("Bruno");

        Assert.Equal(
            ["Me", "Ana", "Bruno"],
            (await store.ListAsync()).Select(static p => p.Name));
    }

    [Fact]
    public async Task TwoProfilesCannotShareAName()
    {
        await using var scope = TestDatabaseScope.Create();
        var store = new SqliteUserProfileStore(scope.Database);
        await store.CreateAsync("Ana");

        // Same name, differently typed: the check is on the normalized form,
        // so it catches what a person would consider the same name.
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await store.CreateAsync("  ana  "));
    }

    [Fact]
    public async Task RenamingKeepsTheProfileAndItsPlace()
    {
        await using var scope = TestDatabaseScope.Create();
        var store = new SqliteUserProfileStore(scope.Database);
        var created = await store.CreateAsync("Ana");

        var renamed = await store.RenameAsync(created.Id, "Ana Maria");

        Assert.Equal(created.Id, renamed.Id);
        Assert.Equal("Ana Maria", renamed.Name);
        Assert.Equal(2, (await store.ListAsync()).Count);
    }

    /// <summary>
    /// Somebody has to be using the application. Without this the picker could
    /// be emptied and the library left owned by nobody.
    /// </summary>
    [Fact]
    public async Task TheLastProfileCannotBeRemoved()
    {
        await using var scope = TestDatabaseScope.Create();
        var store = new SqliteUserProfileStore(scope.Database);
        var only = Assert.Single(await store.ListAsync());

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await store.DeleteAsync(only.Id));

        Assert.Single(await store.ListAsync());
    }

    /// <summary>
    /// Removing a profile takes what it owned with it and leaves everyone
    /// else's alone — including progress, whose profile column carries no
    /// foreign key and so is not swept up by the cascade.
    /// </summary>
    [Fact]
    public async Task RemovingAProfileTakesOnlyItsOwnState()
    {
        await using var scope = TestDatabaseScope.Create();
        await scope.Repository.MergeCatalogAsync(
            TestCatalogDocuments.SingleMovie("Shared title"));
        var item = Assert.Single(await scope.Repository.ListAsync());
        var details = await scope.Repository.GetDetailsAsync(item.Id);

        var profiles = new SqliteUserProfileStore(scope.Database);
        var state = new SqliteUserMediaStateStore(scope.Database);
        var mine = Assert.Single(await profiles.ListAsync());
        var theirs = await profiles.CreateAsync("Ana");

        await state.SetFavoriteCatalogItemAsync(item.Id, true);
        await state.CreateUserListAsync("Mine");
        await state.SaveProgressAsync(
            details!.Identity,
            new MediaProgressState(
                12,
                MediaPositionKind.Seconds,
                isCompleted: false,
                DateTimeOffset.UtcNow));

        scope.Database.ActiveProfileId = theirs.Id;
        await state.SetFavoriteCatalogItemAsync(item.Id, true);
        await state.CreateUserListAsync("Theirs");
        await state.SaveProgressAsync(
            details.Identity,
            new MediaProgressState(
                99,
                MediaPositionKind.Seconds,
                isCompleted: false,
                DateTimeOffset.UtcNow));

        await profiles.DeleteAsync(theirs.Id);

        scope.Database.ActiveProfileId = mine.Id;
        Assert.Contains(item.Id, await state.LoadFavoriteCatalogItemIdsAsync());
        Assert.Equal(
            "Mine",
            Assert.Single(await state.LoadUserListsAsync()).Name);
        Assert.Equal(
            12,
            (await state.LoadProgressAsync(details.Identity))!.Position);

        // And nothing of theirs is left behind for a future profile to
        // inherit by reusing an identifier.
        scope.Database.ActiveProfileId = theirs.Id;
        Assert.Null(await state.LoadProgressAsync(details.Identity));
        Assert.Empty(await state.LoadUserListsAsync());
    }

    [Fact]
    public async Task TheProfileInUseIsRemembered()
    {
        await using var scope = TestDatabaseScope.Create();
        var store = new SqliteUserProfileStore(scope.Database);
        var created = await store.CreateAsync("Ana");

        Assert.Null(created.LastUsedAt);
        await store.MarkUsedAsync(created.Id);

        var reloaded = (await store.ListAsync())
            .Single(profile => profile.Id == created.Id);
        Assert.NotNull(reloaded.LastUsedAt);
    }
}
