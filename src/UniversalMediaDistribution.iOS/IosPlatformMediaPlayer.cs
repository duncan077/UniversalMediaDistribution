using AVFoundation;
using AVKit;
using Avalonia.Controls;
using CoreMedia;
using Foundation;
using UIKit;
using UniversalMediaDistribution.Mobile;
using UniversalMediaDistribution.UI.Localization;
using UniversalMediaDistribution.UI.Playback;

namespace UniversalMediaDistribution.iOS;

internal sealed class IosPlatformMediaPlayer :
    IPlatformMediaPlayer,
    IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AVPlayer? _player;
    private DismissAwarePlayerViewController? _controller;
    private NSObject? _endedObserver;
    private bool _isEnded;
    private bool _isClosed = true;
    private string? _errorMessage;
    private bool _disposed;

    public bool IsAvailable => !_disposed;

    public Control? EmbeddedView => null;

    public bool SupportsExternalTracks => false;

    public async ValueTask OpenAsync(
        PlatformMediaRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var source = MobilePlaybackSourcePolicy.RequirePlaybackUri(request);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await InvokeOnMainThreadAsync(
                async () =>
                {
                    await CloseCoreAsync(dismissController: true);

                    var nsUrl = NSUrl.FromString(source.AbsoluteUri) ??
                        throw new InvalidOperationException(
                            UiTextCatalog.Get("IosPlaybackAddressRejected"));
                    _player = AVPlayer.FromUrl(nsUrl) ??
                        throw new InvalidOperationException(
                            UiTextCatalog.Get("IosPlayerCreateFailed"));
                    _controller = new DismissAwarePlayerViewController(
                        OnPlayerViewDismissed)
                    {
                        Player = _player,
                        Title = request.Title
                    };
                    _isEnded = false;
                    _isClosed = false;
                    _errorMessage = null;
                    _endedObserver = AVPlayerItem.Notifications.ObserveDidPlayToEndTime(
                        _player.CurrentItem!,
                        (_, _) => _isEnded = true);

                    var presenter = FindTopViewController() ??
                        throw new InvalidOperationException(
                            UiTextCatalog.Get("IosWindowUnavailable"));
                    await presenter.PresentViewControllerAsync(
                        _controller,
                        animated: true);

                    if (request.StartPositionSeconds > 0)
                    {
                        await _player.SeekAsync(
                            CMTime.FromSeconds(
                                request.StartPositionSeconds,
                                preferredTimeScale: 600));
                    }

                    _player.Play();
                },
                cancellationToken);
        }
        catch
        {
            await InvokeOnMainThreadAsync(
                () => CloseCoreAsync(dismissController: true),
                CancellationToken.None);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<PlatformMediaState> GetStateAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await InvokeOnMainThreadAsync(
                CaptureState,
                cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public bool SupportsTransportControl => true;

    public async ValueTask SetPlayingAsync(
        bool playing,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await InvokeOnMainThreadAsync(
                () =>
                {
                    // A watch-together command can arrive after the viewer has
                    // dismissed the player; that must stay a quiet no-op rather
                    // than fault the whole party session.
                    if (_player is not { } player || _isClosed)
                    {
                        return Task.CompletedTask;
                    }

                    if (playing)
                    {
                        player.Play();
                    }
                    else
                    {
                        player.Pause();
                    }

                    return Task.CompletedTask;
                },
                cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask SeekAsync(
        double positionSeconds,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await InvokeOnMainThreadAsync(
                async () =>
                {
                    // Same quiet no-op as SetPlayingAsync: a party seek after
                    // dismissal has nothing to reposition.
                    if (_player is not { } player || _isClosed)
                    {
                        return;
                    }

                    // Same 600-tick timescale as the resume seek in OpenAsync,
                    // so party seeks land where local resume seeks would.
                    await player.SeekAsync(
                        CMTime.FromSeconds(
                            Math.Max(0, positionSeconds),
                            preferredTimeScale: 600));
                },
                cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask CloseAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await InvokeOnMainThreadAsync(
                () => CloseCoreAsync(dismissController: true),
                cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await CloseAsync();
        _disposed = true;
        _gate.Dispose();
    }

    private PlatformMediaState CaptureState()
    {
        var player = _player;
        if (player is null)
        {
            return new PlatformMediaState(
                0,
                null,
                false,
                _isEnded,
                true,
                _errorMessage);
        }

        var position = NormalizeSeconds(player.CurrentTime.Seconds) ?? 0;
        var duration = NormalizeSeconds(player.CurrentItem?.Duration.Seconds);
        var timeControlStatus = player.TimeControlStatus;
        var itemError = player.CurrentItem?.Error?.LocalizedDescription;
        if (!string.IsNullOrWhiteSpace(itemError))
        {
            _errorMessage = itemError;
        }

        return new PlatformMediaState(
            position,
            duration,
            timeControlStatus is AVPlayerTimeControlStatus.Playing,
            _isEnded,
            _isClosed,
            _errorMessage,
            // Waiting-to-play means the player stalled on data rather than the
            // viewer pausing; watch-together needs that distinction so one
            // member's rebuffer is not mistaken for a deliberate pause.
            IsBuffering: timeControlStatus is
                AVPlayerTimeControlStatus.WaitingToPlayAtSpecifiedRate);
    }

    private async Task CloseCoreAsync(bool dismissController)
    {
        var player = _player;
        var controller = _controller;
        _player = null;
        _controller = null;
        _endedObserver?.Dispose();
        _endedObserver = null;
        _isClosed = true;

        player?.Pause();
        if (controller is not null)
        {
            controller.Player = null;
            if (dismissController && controller.PresentingViewController is not null)
            {
                await controller.DismissViewControllerAsync(animated: true);
            }

            controller.Dispose();
        }

        player?.Dispose();
    }

    private void OnPlayerViewDismissed()
    {
        if (_isClosed)
        {
            return;
        }

        var state = CaptureState();
        _isEnded = state.IsEnded;
        _errorMessage = state.ErrorMessage;
        _isClosed = true;
        _player?.Pause();
    }

    private static UIViewController? FindTopViewController()
    {
        var window = UIApplication.SharedApplication.ConnectedScenes
            .OfType<UIWindowScene>()
            .Where(static scene =>
                scene.ActivationState is UISceneActivationState.ForegroundActive)
            .SelectMany(static scene => scene.Windows)
            .FirstOrDefault(static candidate => candidate.IsKeyWindow) ??
            UIApplication.SharedApplication.ConnectedScenes
                .OfType<UIWindowScene>()
                .SelectMany(static scene => scene.Windows)
                .FirstOrDefault(static candidate => !candidate.Hidden);
        var controller = window?.RootViewController;
        while (controller?.PresentedViewController is { } presented)
        {
            controller = presented;
        }

        return controller;
    }

    private static double? NormalizeSeconds(double? value) =>
        value is { } seconds &&
        !double.IsNaN(seconds) &&
        !double.IsInfinity(seconds) &&
        seconds >= 0
            ? seconds
            : null;

    private static Task InvokeOnMainThreadAsync(
        Func<Task> action,
        CancellationToken cancellationToken)
    {
        if (NSThread.IsMain)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return action();
        }

        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        UIApplication.SharedApplication.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await action();
                completion.TrySetResult();
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        });
        return completion.Task.WaitAsync(cancellationToken);
    }

    private static Task<T> InvokeOnMainThreadAsync<T>(
        Func<T> action,
        CancellationToken cancellationToken)
    {
        if (NSThread.IsMain)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(action());
        }

        var completion = new TaskCompletionSource<T>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        UIApplication.SharedApplication.BeginInvokeOnMainThread(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                completion.TrySetResult(action());
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        });
        return completion.Task.WaitAsync(cancellationToken);
    }

    private sealed class DismissAwarePlayerViewController(Action dismissed) :
        AVPlayerViewController
    {
        private bool _reported;

        public override void ViewDidDisappear(bool animated)
        {
            base.ViewDidDisappear(animated);
            if (!_reported && IsBeingDismissed)
            {
                _reported = true;
                dismissed();
            }
        }
    }
}
