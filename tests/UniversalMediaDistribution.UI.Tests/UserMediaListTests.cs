using UniversalMediaDistribution.Application.Catalog;
using UniversalMediaDistribution.Application.UserState;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.UI.Tests;

public sealed class UserMediaListTests
{
    [Fact]
    public async Task InMemoryStoreCreatesRenamesAndUpdatesListMembership()
    {
        var store = new InMemoryUserMediaStateStore();
        var firstItemId = Guid.CreateVersion7();
        var secondItemId = Guid.CreateVersion7();

        var created = await store.CreateUserListAsync("  Read   later ");
        await store.SetUserListMembershipAsync(
            created.Id,
            firstItemId,
            isMember: true);
        await store.SetUserListMembershipAsync(
            created.Id,
            secondItemId,
            isMember: true);
        await store.SetUserListMembershipAsync(
            created.Id,
            firstItemId,
            isMember: false);
        var renamed = await store.RenameUserListAsync(
            created.Id,
            "Mixed media");

        var list = Assert.Single(await store.LoadUserListsAsync());
        Assert.Equal("Read later", created.Name);
        Assert.Equal("Mixed media", renamed.Name);
        Assert.Equal("Mixed media", list.Name);
        Assert.Equal([secondItemId], list.CatalogItemIds);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.CreateUserListAsync(" mixed   MEDIA ").AsTask());

        await store.DeleteUserListAsync(created.Id);
        Assert.Empty(await store.LoadUserListsAsync());
    }

    [Fact]
    public async Task InMemoryStoreKeepsSeenStateForEveryChapter()
    {
        var store = new InMemoryUserMediaStateStore();
        var itemId = Guid.CreateVersion7();
        var first = new CatalogContentIdentity(
            itemId,
            $"item:{itemId:N}/chapter:1",
            ContentUnitKind.Chapter,
            "1");
        var second = new CatalogContentIdentity(
            itemId,
            $"item:{itemId:N}/chapter:2",
            ContentUnitKind.Chapter,
            "2");

        await store.SaveProgressAsync(
            first,
            new MediaProgressState(
                0,
                MediaPositionKind.Page,
                isCompleted: true,
                DateTimeOffset.UtcNow.AddMinutes(-1)));
        await store.SaveProgressAsync(
            second,
            new MediaProgressState(
                0,
                MediaPositionKind.Page,
                isCompleted: true,
                DateTimeOffset.UtcNow));

        var progress = await store.LoadCatalogProgressAsync();

        Assert.Equal(2, progress.Count);
        Assert.All(
            progress,
            static summary =>
                Assert.True(summary.Progress.IsCompleted));
        Assert.Equal(
            ["1", "2"],
            progress
                .Select(static summary =>
                    summary.Content.DisplayNumber)
                .Order());
    }
}
