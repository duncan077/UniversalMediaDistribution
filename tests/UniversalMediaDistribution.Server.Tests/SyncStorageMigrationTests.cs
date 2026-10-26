using System.Globalization;
using System.Text.Json;
using UniversalMediaDistribution.Catalog.Sync;
using UniversalMediaDistribution.Catalog.Transfer;

namespace UniversalMediaDistribution.Server.Tests;

public sealed class SyncStorageMigrationTests
{
    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(4, true)]
    public async Task LegacySnapshotsKeepReadingContextAndRevisions(int version, bool indexed)
    {
        using var scope = TestDirectory.Create();
        var accountId = Guid.CreateVersion7();
        var document = CreateDocument(version);
        var revisions = indexed
            ? UserSyncDocumentDelta.EnumerateKeys(document).ToDictionary(key => key, _ => 37L)
            : null;
        var original = new UserSyncSnapshotState(37, document.ExportedAt, document.DeviceId, document, revisions);
        var path = SnapshotPath(scope.Path, accountId);
        await WriteLegacyAsync(path, original);
        var originalJson = await File.ReadAllTextAsync(path);

        var store = new SqliteUserSyncStore(scope.Path);
        var restored = Assert.IsType<UserSyncSnapshotState>(await store.GetAsync(accountId, CancellationToken.None));
        Assert.Equal(37, restored.Revision);
        Assert.Equal(original.UpdatedAt, restored.UpdatedAt);
        Assert.Equal(original.UpdatedByDeviceId, restored.UpdatedByDeviceId);
        Assert.Equal(version == 1 ? 2 : version, restored.Document.Version);
        var expected = UserSyncDocumentMerger.Normalize(document);
        Assert.Equal(Serialize(expected), Serialize(restored.Document));
        Assert.Equal(1000, Assert.Single(restored.Document.Catalog.Items).Units.Count);
        Assert.Equal(42, Assert.Single(restored.Document.Progress, entry => !entry.IsCompleted).Position);
        Assert.True(restored.Document.Progress.Single(entry => entry.Content.DisplayNumber == "1").IsCompleted);
        Assert.Equal("continuous", Assert.Single(restored.Document.ReaderPreferences).DisplayMode);
        Assert.Single(restored.Document.Lists);

        // The public crawler's no-art/no-provider-URL policy must never be
        // applied to a private snapshot or its existing reading context.
        Assert.Equal(document.Catalog.Items[0].ThumbnailRef, restored.Document.Catalog.Items[0].ThumbnailRef);
        Assert.Equal(originalJson, await File.ReadAllTextAsync(path));
        using (var connection = new ServerDatabase(scope.Path).Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT payload FROM server_documents WHERE key=$key;";
            command.Parameters.AddWithValue("$key", $"user-sync/{accountId:N}/latest.json");
            Assert.Equal(originalJson, command.ExecuteScalar());
        }

        var reopened = new SqliteUserSyncStore(scope.Path);
        Assert.Equal(Serialize(restored.Document), Serialize((await reopened.GetAsync(accountId, CancellationToken.None))!.Document));
        Assert.Null(await reopened.GetAsync(Guid.CreateVersion7(), CancellationToken.None));
        var changes = Assert.IsType<UserSyncChangesResponse>(await reopened.GetChangesAsync(accountId, 36, CancellationToken.None));
        Assert.Equal(!indexed, changes.Full);
        Assert.Equal(expected.Progress, changes.Document.Progress);
        Assert.Equal(expected.ReaderPreferences, changes.Document.ReaderPreferences);
    }

    [Fact]
    public async Task AnOldNameBasedIndexFallsBackToTheFullReadingContext()
    {
        using var scope = TestDirectory.Create();
        var accountId = Guid.CreateVersion7();
        var document = CreateDocument(3);
        // Old releases indexed book:name. Normalization now qualifies manga
        // versus novel, so looking up those old keys in the new document
        // silently leaves positions, preferences and list members out.
        var revisions = UserSyncDocumentDelta.EnumerateKeys(document).ToDictionary(key => key, _ => 37L);
        await WriteLegacyAsync(SnapshotPath(scope.Path, accountId),
            new UserSyncSnapshotState(37, document.ExportedAt, document.DeviceId, document, revisions));

        var store = new SqliteUserSyncStore(scope.Path);
        var changes = Assert.IsType<UserSyncChangesResponse>(await store.GetChangesAsync(accountId, 36, CancellationToken.None));
        Assert.True(changes.Full);
        Assert.Equal(2, changes.Document.Progress.Count);
        Assert.Single(changes.Document.ReaderPreferences);
        Assert.Single(Assert.Single(changes.Document.Lists).ItemStates);
        Assert.All(changes.Document.Progress, entry => Assert.Equal("manga", entry.Content.Item.Subtype));

        // The next normal write seeds a current index without resetting the
        // snapshot revision, changing clocks, or losing the untouched chapter.
        var previous = changes.Document.Progress[0];
        var changed = previous with
        {
            Position = 99,
            SyncVersion = previous.SyncVersion! with
            {
                Clock = previous.SyncVersion.Clock + 1,
                OperationId = Guid.CreateVersion7()
            }
        };
        await store.ApplyProgressPatchAsync(accountId, new UserSyncProgressPatchRequest(changes.Document with
        {
            Progress = [changed],
            ReaderPreferences = [],
            Lists = [],
            FavoriteStates = [],
            FavoriteContributorKeys = []
        }), CancellationToken.None);
        var next = (await store.GetChangesAsync(accountId, 37, CancellationToken.None))!;
        Assert.False(next.Full);
        Assert.Equal(38, next.ToRevision);
        Assert.Equal(99, Assert.Single(next.Document.Progress).Position);
        Assert.Equal(2, (await store.GetAsync(accountId, CancellationToken.None))!.Document.Progress.Count);
    }

    [Fact]
    public async Task UpdatingOrDeletingAMigratedSnapshotDoesNotReplayTheRetainedFile()
    {
        using var scope = TestDirectory.Create();
        var accountId = Guid.CreateVersion7();
        var document = CreateDocument(4);
        var path = SnapshotPath(scope.Path, accountId);
        await WriteLegacyAsync(path, new UserSyncSnapshotState(37, document.ExportedAt, document.DeviceId, document));
        var store = new SqliteUserSyncStore(scope.Path);
        await store.SaveAsync(accountId, new UserSyncUploadRequest(37, document), CancellationToken.None);
        Assert.Equal(38, (await new SqliteUserSyncStore(scope.Path).GetAsync(accountId, CancellationToken.None))!.Revision);
        await store.DeleteAsync(accountId, 38, CancellationToken.None);
        Assert.True(File.Exists(path));
        Assert.Null(await new SqliteUserSyncStore(scope.Path).GetAsync(accountId, CancellationToken.None));
    }

    private static string SnapshotPath(string root, Guid accountId) =>
        Path.Combine(root, "user-sync", accountId.ToString("N"), "latest.json");

    private static ValueTask WriteLegacyAsync(string path, UserSyncSnapshotState state) =>
        ServerFileStorage.WriteJsonAtomicAsync(path, state,
            ServerJsonSerializerContext.Default.UserSyncSnapshotState, CancellationToken.None);

    private static string Serialize(UserSyncDocument document) =>
        JsonSerializer.Serialize(document, ServerJsonSerializerContext.Default.UserSyncDocument);

    private static UserSyncDocument CreateDocument(int version)
    {
        var timestamp = DateTimeOffset.UnixEpoch.AddDays(20_000);
        var recordVersion = UserSyncDocumentMerger.CreateLegacyVersion("legacy-device", timestamp, "reading");
        var itemId = version >= 4 ? Guid.CreateVersion7() : (Guid?)null;
        var identity = new UserSyncCatalogItemIdentity
        {
            MediaType = "book",
            Name = "Existing reading history",
            ItemId = itemId,
            Subtype = version >= 4 ? "manga" : null
        };
        return new UserSyncDocument
        {
            Version = version,
            ExportedAt = timestamp,
            DeviceId = "legacy-device",
            DeviceName = "Previous desktop",
            Catalog = new CatalogTransferDocument
            {
                ExportedAt = timestamp,
                Items = [new CatalogTransferItem
                {
                    Id = itemId, MediaType = "book", Subtype = "manga", Name = identity.Name,
                    ThumbnailRef = "https://example.com/existing-cover.jpg",
                    Units = Enumerable.Range(1, 1000).Select(number => new CatalogTransferUnit
                    {
                        Kind = "chapter", DisplayNumber = number.ToString(CultureInfo.InvariantCulture), SortOrder = number
                    }).ToArray()
                }]
            },
            Progress = new[] { "1", "500" }.Select(number => new UserSyncProgressEntry
            {
                Content = new UserSyncContentIdentity { Item = identity, UnitKind = "chapter", DisplayNumber = number },
                Position = 42,
                PositionKind = "page",
                IsCompleted = number == "1",
                UpdatedAt = timestamp,
                SyncVersion = version == 1 ? null : recordVersion
            }).ToArray(),
            ReaderPreferences = [new UserSyncReaderPreference
            {
                Item = identity, DisplayMode = "continuous", UpdatedAt = timestamp, SyncVersion = version == 1 ? null : recordVersion
            }],
            FavoriteContributorKeys = ["organization:existing provider"],
            FavoriteStates = version == 1 ? [] : [new UserSyncFavoriteState
            {
                ContributorKey = "organization:existing provider", IsFavorite = true, SyncVersion = recordVersion
            }],
            Lists = [new UserSyncMediaList
            {
                Id = Guid.CreateVersion7(), Name = "Reading", CreatedAt = timestamp, UpdatedAt = timestamp,
                Items = [identity], SyncVersion = version == 1 ? null : recordVersion,
                ItemStates = version == 1 ? [] : [new UserSyncListItemState { Item = identity, IsMember = true, SyncVersion = recordVersion }]
            }]
        };
    }
}
