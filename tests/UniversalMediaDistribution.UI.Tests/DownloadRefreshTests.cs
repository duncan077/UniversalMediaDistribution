using Avalonia.Headless;
using Avalonia.Threading;
using UniversalMediaDistribution.Torrenting.Ingestion;
using UniversalMediaDistribution.UI.Threading;
using UniversalMediaDistribution.UI.ViewModels;

namespace UniversalMediaDistribution.UI.Tests;

[Collection("Player surface transitions")]
public sealed class DownloadRefreshTests
{
    [Fact]
    public async Task BurstQueuesOneRefreshAndChangesDuringRefreshAreNotLost()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(PlayerFullscreenLayoutTests.TestApp));
        await session.Dispatch(() =>
        {
            var refresh = new CoalescedDispatcherAction();
            var calls = 0;
            var latest = 0;
            void Apply()
            {
                calls++;
                Assert.Equal(10000, latest);
                if (calls == 1) refresh.Post(Apply);
            }
            for (var i = 0; i < 10000; i++)
            {
                latest++;
                refresh.Post(Apply);
            }
            Assert.Equal(0, calls);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, calls);
        }, CancellationToken.None);
    }

    [Fact]
    public void UnchangedDownloadDoesNotNotifyEveryBoundProperty()
    {
        var now = DateTimeOffset.UtcNow;
        var record = new TorrentDownloadRecord(Guid.NewGuid(), null, null, "Title", null,
            TorrentDownloadState.Downloading, now, now);
        var viewModel = new TorrentDownloadItemViewModel(record, () => { }, _ => Task.CompletedTask,
            _ => Task.CompletedTask, _ => { });
        var notifications = 0;
        viewModel.PropertyChanged += (_, _) => notifications++;
        viewModel.Update(record);
        viewModel.Update(record with { });
        Assert.Equal(0, notifications);
        viewModel.Update(record with { DownloadedBytes = 1024 });
        Assert.True(notifications > 0);
        Assert.Equal(1024, viewModel.Record.DownloadedBytes);
    }
}
