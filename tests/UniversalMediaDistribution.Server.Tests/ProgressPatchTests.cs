using UniversalMediaDistribution.Catalog.Sync;
using UniversalMediaDistribution.Catalog.Transfer;

namespace UniversalMediaDistribution.Server.Tests;

/// <summary>
/// Merging a few changed positions into the stored snapshot.
/// <para>
/// This is the one place in the feature where a mistake costs someone their
/// library rather than a line of text, so what is pinned here is mostly what a
/// patch must <em>not</em> do: drop titles it did not mention, or overwrite a
/// position that is further along than the one it carries.
/// </para>
/// </summary>
public sealed class ProgressPatchTests
{
    private static readonly DateTimeOffset Monday =
        new(2026, 1, 5, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task APatchKeepsTitlesItDoesNotMention()
    {
        using var scope = TestDirectory.Create();
        var store = new SqliteUserSyncStore(scope.Path);
        var accountId = Guid.CreateVersion7();
        await store.SaveAsync(
            accountId,
            new UserSyncUploadRequest(
                0,
                CreateDocument(
                    Monday,
                    ("book", "Copper Skies", "10"),
                    ("book", "Iron Tide", "3"))),
            CancellationToken.None);

        await store.ApplyProgressPatchAsync(
            accountId,
            new UserSyncProgressPatchRequest(
                CreateDocument(
                    Monday.AddHours(1),
                    ("book", "Copper Skies", "11"))),
            CancellationToken.None);

        // Progress is kept per unit, so the patched chapter joins the two
        // already stored rather than replacing one of them.
        var stored = await store.GetAsync(accountId, CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Equal(3, stored!.Document.Progress.Count);
        Assert.Equal(["10", "11"], UnitsFor(stored, "Copper Skies"));
        Assert.Equal(["3"], UnitsFor(stored, "Iron Tide"));
    }

    [Fact]
    public async Task ALaterPositionReplacesAnEarlierOne()
    {
        using var scope = TestDirectory.Create();
        var store = new SqliteUserSyncStore(scope.Path);
        var accountId = Guid.CreateVersion7();
        await store.SaveAsync(
            accountId,
            new UserSyncUploadRequest(
                0,
                CreateDocument(Monday, ("book", "Copper Skies", "10"))),
            CancellationToken.None);

        await store.ApplyProgressPatchAsync(
            accountId,
            new UserSyncProgressPatchRequest(
                CreateDocument(
                    Monday.AddHours(2),
                    position: 42,
                    ("book", "Copper Skies", "10"))),
            CancellationToken.None);

        var stored = await store.GetAsync(accountId, CancellationToken.None);
        Assert.Equal(42, PositionFor(stored, "Copper Skies", "10"));
        Assert.Single(stored!.Document.Progress);
    }

    [Fact]
    public async Task APatchWithoutCatalogResolvesAgainstTheStoredSnapshot()
    {
        using var scope = TestDirectory.Create();
        var store = new SqliteUserSyncStore(scope.Path);
        var accountId = Guid.CreateVersion7();
        await store.SaveAsync(accountId,
            new UserSyncUploadRequest(0,
                CreateDocument(Monday, ("book", "Copper Skies", "10"))),
            CancellationToken.None);
        var patch = CreateDocument(Monday.AddHours(2), position: 42,
            ("book", "Copper Skies", "10")) with
        {
            Catalog = new CatalogTransferDocument()
        };

        await store.ApplyProgressPatchAsync(accountId,
            new UserSyncProgressPatchRequest(patch), CancellationToken.None);

        var stored = await store.GetAsync(accountId, CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Equal(42, PositionFor(stored, "Copper Skies", "10"));
        Assert.Single(stored.Document.Catalog.Items);
        UserSyncDocumentValidator.Validate(stored.Document);
    }

    /// <summary>
    /// A patch that arrives carrying an older position — a device that was
    /// offline, syncing late — must not walk the stored one backwards.
    /// </summary>
    [Fact]
    public async Task AnEarlierPositionDoesNotOverwriteALaterOne()
    {
        using var scope = TestDirectory.Create();
        var store = new SqliteUserSyncStore(scope.Path);
        var accountId = Guid.CreateVersion7();
        await store.SaveAsync(
            accountId,
            new UserSyncUploadRequest(
                0,
                CreateDocument(
                    Monday.AddHours(5),
                    position: 90,
                    ("book", "Copper Skies", "10"))),
            CancellationToken.None);

        await store.ApplyProgressPatchAsync(
            accountId,
            new UserSyncProgressPatchRequest(
                CreateDocument(
                    Monday,
                    position: 5,
                    ("book", "Copper Skies", "10"))),
            CancellationToken.None);

        Assert.Equal(
            90,
            PositionFor(
                await store.GetAsync(accountId, CancellationToken.None),
                "Copper Skies",
                "10"));
    }

    [Fact]
    public async Task APatchAddsATitleTheSnapshotHadNeverSeen()
    {
        using var scope = TestDirectory.Create();
        var store = new SqliteUserSyncStore(scope.Path);
        var accountId = Guid.CreateVersion7();
        await store.SaveAsync(
            accountId,
            new UserSyncUploadRequest(
                0,
                CreateDocument(Monday, ("book", "Copper Skies", "10"))),
            CancellationToken.None);

        await store.ApplyProgressPatchAsync(
            accountId,
            new UserSyncProgressPatchRequest(
                CreateDocument(
                    Monday.AddHours(1),
                    ("movie", "Silo", "6"))),
            CancellationToken.None);

        var stored = await store.GetAsync(accountId, CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Equal(2, stored!.Document.Catalog.Items.Count);
        Assert.Equal("6", FindUnit(stored, "Silo"));
    }

    /// <summary>
    /// A patch carries only what moved, so an account with nothing stored must
    /// not have one taken as its snapshot: a library of hundreds would be
    /// represented by the title just read, and the account would then offer to
    /// restore from it.
    /// </summary>
    [Fact]
    public async Task APatchIsRefusedWhenNothingIsStored()
    {
        using var scope = TestDirectory.Create();
        var store = new SqliteUserSyncStore(scope.Path);
        var accountId = Guid.CreateVersion7();

        await Assert.ThrowsAsync<SyncPatchWithoutSnapshotException>(
            () => store.ApplyProgressPatchAsync(
                accountId,
                new UserSyncProgressPatchRequest(
                    CreateDocument(Monday, ("book", "Copper Skies", "10"))),
                CancellationToken.None).AsTask());

        Assert.Null(await store.GetAsync(accountId, CancellationToken.None));
    }

    /// <summary>
    /// The refusal is meant to cost one upload, not the position: a full
    /// snapshot followed by the same patch has to leave both titles stored.
    /// </summary>
    [Fact]
    public async Task ARefusedPatchSucceedsOnceTheSnapshotIsUploaded()
    {
        using var scope = TestDirectory.Create();
        var store = new SqliteUserSyncStore(scope.Path);
        var accountId = Guid.CreateVersion7();
        var patch = new UserSyncProgressPatchRequest(
            CreateDocument(Monday.AddHours(1), ("book", "Iron Tide", "3")));

        await Assert.ThrowsAsync<SyncPatchWithoutSnapshotException>(
            () => store.ApplyProgressPatchAsync(
                accountId,
                patch,
                CancellationToken.None).AsTask());

        await store.SaveAsync(
            accountId,
            new UserSyncUploadRequest(
                0,
                CreateDocument(Monday, ("book", "Copper Skies", "10"))),
            CancellationToken.None);
        var state = await store.ApplyProgressPatchAsync(
            accountId,
            patch,
            CancellationToken.None);

        Assert.Equal(2, state.Revision);
        var stored = await store.GetAsync(accountId, CancellationToken.None);
        Assert.Equal(2, stored!.Document.Catalog.Items.Count);
        Assert.Equal(["10"], UnitsFor(stored, "Copper Skies"));
        Assert.Equal(["3"], UnitsFor(stored, "Iron Tide"));
    }

    /// <summary>
    /// Another device syncing between patches must not make the next one fail:
    /// a patch is merged, not swapped in, so it carries no expected revision.
    /// </summary>
    [Fact]
    public async Task APatchAppliesWhateverTheRevisionHasReached()
    {
        using var scope = TestDirectory.Create();
        var store = new SqliteUserSyncStore(scope.Path);
        var accountId = Guid.CreateVersion7();
        await store.SaveAsync(
            accountId,
            new UserSyncUploadRequest(
                0,
                CreateDocument(Monday, ("book", "Copper Skies", "10"))),
            CancellationToken.None);
        await store.SaveAsync(
            accountId,
            new UserSyncUploadRequest(
                1,
                CreateDocument(
                    Monday.AddHours(1),
                    ("book", "Copper Skies", "12"))),
            CancellationToken.None);

        var state = await store.ApplyProgressPatchAsync(
            accountId,
            new UserSyncProgressPatchRequest(
                CreateDocument(
                    Monday.AddHours(3),
                    position: 30,
                    ("book", "Copper Skies", "10"))),
            CancellationToken.None);

        Assert.Equal(3, state.Revision);
        Assert.Equal(
            30,
            PositionFor(
                await store.GetAsync(accountId, CancellationToken.None),
                "Copper Skies",
                "10"));
    }

    /// <summary>
    /// A patch the validator rejects must leave the stored snapshot exactly as
    /// it was, not half-applied.
    /// </summary>
    [Fact]
    public async Task ARejectedPatchLeavesTheSnapshotUntouched()
    {
        using var scope = TestDirectory.Create();
        var store = new SqliteUserSyncStore(scope.Path);
        var accountId = Guid.CreateVersion7();
        await store.SaveAsync(
            accountId,
            new UserSyncUploadRequest(
                0,
                CreateDocument(Monday, ("book", "Copper Skies", "10"))),
            CancellationToken.None);

        // Invalid progress remains an error even when its catalog is absent.
        var invalid = CreateDocument(
            Monday.AddHours(1),
            ("book", "Copper Skies", "11"));
        invalid = invalid with
        {
            Progress = [invalid.Progress[0] with { Position = -1 }],
            Catalog = new CatalogTransferDocument
            {
                SourceLabel = "Empty",
                Items = []
            }
        };

        await Assert.ThrowsAnyAsync<Exception>(
            () => store.ApplyProgressPatchAsync(
                accountId,
                new UserSyncProgressPatchRequest(invalid),
                CancellationToken.None).AsTask());

        var stored = await store.GetAsync(accountId, CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Equal(1, stored!.Revision);
        Assert.Equal("10", FindUnit(stored, "Copper Skies"));
    }

    private static string[] UnitsFor(
        UserSyncSnapshotState? state,
        string title) =>
        (state?.Document.Progress ?? [])
            .Where(entry => string.Equals(
                entry.Content.Item.Name,
                title,
                StringComparison.Ordinal))
            .Select(static entry => entry.Content.DisplayNumber ?? string.Empty)
            .OrderBy(static unit => unit, StringComparer.Ordinal)
            .ToArray();

    private static string? FindUnit(
        UserSyncSnapshotState? state,
        string title) =>
        UnitsFor(state, title).FirstOrDefault();

    private static double? PositionFor(
        UserSyncSnapshotState? state,
        string title,
        string unit) =>
        (state?.Document.Progress ?? [])
            .FirstOrDefault(entry =>
                string.Equals(
                    entry.Content.Item.Name,
                    title,
                    StringComparison.Ordinal) &&
                string.Equals(
                    entry.Content.DisplayNumber,
                    unit,
                    StringComparison.Ordinal))
            ?.Position;

    /// <summary>
    /// Version one so the upgrade path derives each record's version from its
    /// timestamp, which is how a real export orders them.
    /// </summary>
    private static UserSyncDocument CreateDocument(
        DateTimeOffset updatedAt,
        params (string MediaType, string Name, string Unit)[] entries) =>
        CreateDocument(updatedAt, 1, entries);

    private static UserSyncDocument CreateDocument(
        DateTimeOffset updatedAt,
        double position,
        params (string MediaType, string Name, string Unit)[] entries) =>
        new()
        {
            Version = 1,
            ExportedAt = updatedAt,
            DeviceId = "device-one",
            DeviceName = "Test device",
            Catalog = new CatalogTransferDocument
            {
                SourceLabel = "Patch tests",
                Items = entries
                    .GroupBy(entry => (entry.MediaType, entry.Name))
                    .Select(group => new CatalogTransferItem
                    {
                        MediaType = group.Key.MediaType,
                        Subtype = group.Key.MediaType == "book"
                            ? "manga"
                            : "series",
                        Name = group.Key.Name,
                        Description = "Test title",
                        Units = group
                            .Select(entry => entry.Unit)
                            .Distinct(StringComparer.Ordinal)
                            .Select((unit, index) => new CatalogTransferUnit
                            {
                                Kind = group.Key.MediaType == "book"
                                    ? "chapter"
                                    : "episode",
                                DisplayNumber = unit,
                                SortOrder = index
                            })
                            .ToArray()
                    })
                    .ToArray()
            },
            Progress = entries
                .Select(entry => new UserSyncProgressEntry
                {
                    Content = new UserSyncContentIdentity
                    {
                        Item = new UserSyncCatalogItemIdentity
                        {
                            MediaType = entry.MediaType,
                            Name = entry.Name
                        },
                        UnitKind = entry.MediaType == "book"
                            ? "chapter"
                            : "episode",
                        DisplayNumber = entry.Unit
                    },
                    Position = position,
                    PositionKind = entry.MediaType == "book"
                        ? "page"
                        : "seconds",
                    IsCompleted = false,
                    UpdatedAt = updatedAt
                })
                .ToArray()
        };
}
