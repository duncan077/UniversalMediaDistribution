using UniversalMediaDistribution.Catalog.Sync;

namespace UniversalMediaDistribution.Catalog.Sqlite.Tests;

/// <summary>
/// Each profile signs into its own server account, so the address, the account
/// name and above all the token belong to a profile and not to the device.
/// </summary>
public sealed class ProfileSignInIsolationTests
{
    [Fact]
    public async Task ASessionIsInvisibleToAnotherProfile()
    {
        await using var scope = TestDatabaseScope.Create();
        var profiles = new SqliteUserProfileStore(scope.Database);
        var store = new SqliteRemoteSessionStore(scope.Database);
        await store.SaveAsync(SessionFor("alice", 'a'));

        var second = await profiles.CreateAsync("Bob");
        scope.Database.ActiveProfileId = second.Id;

        Assert.Null(await store.LoadAsync());
    }

    [Fact]
    public async Task EachProfileKeepsItsOwnSession()
    {
        await using var scope = TestDatabaseScope.Create();
        var profiles = new SqliteUserProfileStore(scope.Database);
        var store = new SqliteRemoteSessionStore(scope.Database);
        var first = (await profiles.ListAsync())[0];
        await store.SaveAsync(SessionFor("alice", 'a'));

        var second = await profiles.CreateAsync("Bob");
        scope.Database.ActiveProfileId = second.Id;
        await store.SaveAsync(SessionFor("bob", 'b'));

        var bob = await store.LoadAsync();
        Assert.NotNull(bob);
        Assert.Equal("bob", bob.Username);
        Assert.Equal(new string('b', 43), bob.AccessToken);

        // Switching back finds the first account exactly as it was left, which
        // is the point: coming back must not mean signing in again.
        scope.Database.ActiveProfileId = first.Id;
        var alice = await store.LoadAsync();
        Assert.NotNull(alice);
        Assert.Equal("alice", alice.Username);
        Assert.Equal(new string('a', 43), alice.AccessToken);
    }

    [Fact]
    public async Task SigningOutLeavesTheOtherProfileSignedIn()
    {
        await using var scope = TestDatabaseScope.Create();
        var profiles = new SqliteUserProfileStore(scope.Database);
        var store = new SqliteRemoteSessionStore(scope.Database);
        var first = (await profiles.ListAsync())[0];
        await store.SaveAsync(SessionFor("alice", 'a'));

        var second = await profiles.CreateAsync("Bob");
        scope.Database.ActiveProfileId = second.Id;
        await store.SaveAsync(SessionFor("bob", 'b'));
        await store.SaveAsync(null);

        Assert.Null(await store.LoadAsync());
        scope.Database.ActiveProfileId = first.Id;
        Assert.NotNull(await store.LoadAsync());
    }

    [Fact]
    public async Task DeletingAProfileTakesItsAccountWithIt()
    {
        await using var scope = TestDatabaseScope.Create();
        var profiles = new SqliteUserProfileStore(scope.Database);
        var store = new SqliteRemoteSessionStore(scope.Database);
        var second = await profiles.CreateAsync("Bob");
        scope.Database.ActiveProfileId = second.Id;
        await store.SaveAsync(SessionFor("bob", 'b'));

        await profiles.DeleteAsync(second.Id);

        // The row is gone with the profile rather than waiting for whoever is
        // given that identifier next.
        scope.Database.ActiveProfileId = second.Id;
        Assert.Null(await store.LoadAsync());
    }

    [Fact]
    public async Task ServerSettingsAreNotSharedBetweenProfiles()
    {
        await using var scope = TestDatabaseScope.Create();
        var profiles = new SqliteUserProfileStore(scope.Database);
        var store = new SqliteRemoteServerSettingsStore(scope.Database);
        var first = (await profiles.ListAsync())[0];
        await store.SaveAsync(new RemoteServerSettings
        {
            ServiceUrl = "https://alice.example.com/",
            Username = "alice",
            DeviceId = new string('a', 32),
            DeviceName = "Desk",
            OnlineCatalogEnabled = true
        });

        var second = await profiles.CreateAsync("Bob");
        scope.Database.ActiveProfileId = second.Id;
        var loaded = await store.LoadAsync();

        Assert.Equal(string.Empty, loaded.Username);
        Assert.NotEqual("https://alice.example.com/", loaded.ServiceUrl);

        scope.Database.ActiveProfileId = first.Id;
        var alice = await store.LoadAsync();
        Assert.Equal("alice", alice.Username);
        Assert.Equal("https://alice.example.com/", alice.ServiceUrl);
    }

    private static RemoteSessionState SessionFor(string username, char token) =>
        new()
        {
            ServiceUrl = $"https://{username}.example.com/",
            AccessToken = new string(token, 43),
            AccountId = Guid.CreateVersion7(),
            Username = username,
            DisplayName = username,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30)
        };
}
