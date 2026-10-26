using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Platform;
using Avalonia.Threading;
using UniversalMediaDistribution.UI.Playback;

namespace UniversalMediaDistribution.UI.Tests;

// These tests own an Avalonia dispatcher and must not change global services
// while the ordinary non-UI tests are constructing controls.
[CollectionDefinition("Player surface transitions", DisableParallelization = true)]
public sealed class PlayerSurfaceTransitionCollection;

[Collection("Player surface transitions")]
public sealed class WebViewPlayerSurfaceTransitionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectOpenAttachesSelectedSurfaceBeforeStarting(bool fromWeb)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(TestApp));
        await session.Dispatch(async () =>
        {
            var native = new RecordingPlayer(new Border());
            var audio = new RecordingAudioRelay();
            await using var player = new WebViewPlatformMediaPlayer(native, audioRelay: audio);
            var window = new Window { Width = 640, Height = 360 };
            window.Show();
            try
            {
                if (fromWeb)
                {
                    await player.OpenEmbeddedWebAsync(new Uri("https://example.invalid/embed"), "Test");
                    Assert.IsType<NativeWebView>(player.EmbeddedView);
                }

                // Represents the previously bound surface. Only a surface
                // notification may replace it; changing the getter is not enough.
                window.Content = new Border();
                var notifications = 0;
                player.PlaybackSurfaceChanged += (_, _) =>
                {
                    Assert.True(Dispatcher.UIThread.CheckAccess());
                    notifications++;
                    window.Content = player.EmbeddedView;
                };
                native.BeforeOpen = () =>
                {
                    if (fromWeb) Assert.True(audio.Detached);
                    Assert.True(notifications > 0);
                    Assert.Same(native.EmbeddedView, window.Content);
                    Assert.Same(window, TopLevel.GetTopLevel(native.EmbeddedView!));
                    Assert.True(native.EmbeddedView!.Bounds.Width > 0);
                    Assert.True(native.EmbeddedView.Bounds.Height > 0);
                };

                await player.OpenAsync(Request());
                await player.OpenAsync(Request());

                Assert.Equal(2, native.OpenCount);
                Assert.Equal(2, notifications);
            }
            finally
            {
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task CancellationDuringSurfaceSwitchDoesNotStartAndAllowsRetry()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(TestApp));
        await session.Dispatch(async () =>
        {
            var native = new RecordingPlayer(null);
            await using var player = new WebViewPlatformMediaPlayer(native);
            using var cancellation = new CancellationTokenSource();
            EventHandler cancel = (_, _) => cancellation.Cancel();
            player.PlaybackSurfaceChanged += cancel;

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                player.OpenAsync(Request(), cancellation.Token).AsTask());
            Assert.Equal(0, native.OpenCount);

            player.PlaybackSurfaceChanged -= cancel;
            await player.OpenAsync(Request());
            Assert.Equal(1, native.OpenCount);
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task PlayerWithoutEmbeddedSurfaceStillOpensAndCanRetryAfterFailure()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(TestApp));
        await session.Dispatch(async () =>
        {
            var native = new RecordingPlayer(null)
            {
                BeforeOpen = () => throw new InvalidOperationException("Test playback failure")
            };
            await using var player = new WebViewPlatformMediaPlayer(native);
            var notified = false;
            player.PlaybackSurfaceChanged += (_, _) => notified = true;

            await Assert.ThrowsAsync<InvalidOperationException>(() => player.OpenAsync(Request()).AsTask());
            Assert.True(notified);

            native.BeforeOpen = null;
            await player.OpenAsync(Request());
            Assert.Equal(1, native.OpenCount);
            Assert.Null(player.EmbeddedView);
            return true;
        }, CancellationToken.None);
    }

    private static PlatformMediaRequest Request() => new(
        new PlatformMediaVariant(
            new PlatformMediaSource(new Uri("https://example.invalid/video.m3u8"), null),
            "VOE", false, "application/vnd.apple.mpegurl"),
        "Test", 0, [], []);

    [Fact]
    public async Task ShutdownRestoresBrowserSoundBeforeWaitingForBusyPlayer()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(TestApp));
        await session.Dispatch(async () =>
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var native = new RecordingPlayer(null) { OpenCompletion = release.Task };
            var audio = new RecordingAudioRelay();
            var player = new WebViewPlatformMediaPlayer(native, audioRelay: audio);
            var opening = player.OpenAsync(Request()).AsTask();
            var closing = player.DisposeAsync().AsTask();
            try
            {
                Assert.True(audio.Detached);
                Assert.False(closing.IsCompleted);
            }
            finally
            {
                release.TrySetResult();
                await opening;
                await closing;
            }
            return true;
        }, CancellationToken.None);
    }

    public sealed class TestApp : Avalonia.Application
    {
        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<TestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
    }

    private sealed class RecordingPlayer(Control? view) : IPlatformMediaPlayer
    {
        public bool IsAvailable => true;
        public Control? EmbeddedView => view;
        public Action? BeforeOpen { get; set; }
        public Task? OpenCompletion { get; init; }
        public int OpenCount { get; private set; }

        public ValueTask OpenAsync(PlatformMediaRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BeforeOpen?.Invoke();
            OpenCount++;
            return OpenCompletion is { } pending ? new ValueTask(pending) : ValueTask.CompletedTask;
        }

        public ValueTask<PlatformMediaState> GetStateAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new PlatformMediaState(0, null, true, false, false));

        public ValueTask CloseAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class RecordingAudioRelay : INativeWebViewAudioRelay
    {
        public bool Detached { get; private set; }
        public void Attach(NativeWebView view, IPlatformHandle? handle) => Detached = false;
        public void Detach() => Detached = true;
        public ValueTask DisposeAsync() { Detach(); return ValueTask.CompletedTask; }
    }
}
