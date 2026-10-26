using Avalonia.Headless;
using Avalonia.Threading;
using UniversalMediaDistribution.Application.Catalog;
using UniversalMediaDistribution.Application.UserState;
using UniversalMediaDistribution.Domain.Reading;
using UniversalMediaDistribution.UI.Remote;

namespace UniversalMediaDistribution.UI.Tests;

[Collection("Player surface transitions")]
public sealed class ProgressWriteThreadTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteRunsAwayFromUiAndOnlySuccessfulSavesNotifyOnUi(bool fail)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(PlayerFullscreenLayoutTests.TestApp));
        await session.Dispatch(async () =>
        {
            var inner = new RecordingStore { Fail = fail };
            var notified = false;
            var store = new ProgressSyncingUserMediaStateStore(inner, _ =>
            {
                Assert.True(Dispatcher.UIThread.CheckAccess());
                notified = true;
            });
            var identity = new CatalogContentIdentity(Guid.NewGuid(), "movie:test");
            var progress = new MediaProgressState(20, MediaPositionKind.Seconds, false, DateTimeOffset.UtcNow);
            if (fail)
                await Assert.ThrowsAsync<IOException>(() => store.SaveProgressAsync(identity, progress).AsTask());
            else
                await store.SaveProgressAsync(identity, progress);
            Assert.False(inner.RanOnUi);
            Assert.Equal(!fail, notified);
        }, CancellationToken.None);
    }

    private sealed class RecordingStore : IUserMediaStateStore
    {
        public bool Fail { get; init; }
        public bool RanOnUi { get; private set; } = true;
        public ValueTask SaveProgressAsync(CatalogContentIdentity content, MediaProgressState progress, CancellationToken cancellationToken = default)
        {
            RanOnUi = Dispatcher.UIThread.CheckAccess();
            if (Fail) throw new IOException("Test write failure");
            return ValueTask.CompletedTask;
        }
        public ValueTask<MediaProgressState?> LoadProgressAsync(CatalogContentIdentity content, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<CatalogProgressSummary>> LoadCatalogProgressAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<ReaderDisplayMode> LoadReaderDisplayModeAsync(CatalogContentIdentity content, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask SaveReaderDisplayModeAsync(CatalogContentIdentity content, ReaderDisplayMode mode, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<IReadOnlySet<string>> LoadFavoriteContributorKeysAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask SetFavoriteContributorAsync(string key, bool favorite, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<IReadOnlySet<Guid>> LoadFavoriteCatalogItemIdsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask SetFavoriteCatalogItemAsync(Guid id, bool favorite, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
