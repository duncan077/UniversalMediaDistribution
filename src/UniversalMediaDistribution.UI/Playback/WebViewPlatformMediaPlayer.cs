using System.Net;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.ContentFiltering.Injection;
using UniversalMediaDistribution.ContentFiltering.Subscriptions;
using UniversalMediaDistribution.UI.Diagnostics;
using UniversalMediaDistribution.UI.Localization;

namespace UniversalMediaDistribution.UI.Playback;

/// <summary>
/// Adds a native web-view playback path while preserving the platform's
/// existing direct-media player.
/// </summary>
public sealed class WebViewPlatformMediaPlayer :
    IPlatformMediaPlayer,
    IPlatformMediaPlayerTransitions,
    IAsyncDisposable
{
    private static readonly Uri BlankPage = new("about:blank");
    private static readonly TimeSpan DirectSurfaceTimeout =
        TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DirectSurfacePollInterval =
        TimeSpan.FromMilliseconds(16);
    private static readonly TimeSpan DeferredNavigationTimeout =
        TimeSpan.FromSeconds(2);
    private static readonly TimeSpan NativePlaybackStartTimeout =
        TimeSpan.FromSeconds(20);
    private static readonly TimeSpan NativePlaybackPollInterval =
        TimeSpan.FromMilliseconds(100);
    private readonly IPlatformMediaPlayer _directMediaPlayer;
    private readonly INativeWebViewFilterHook _filterHook;
    private readonly INativeWebViewRemoteInput? _remoteInput;
    private readonly INativeWebViewAudioRelay? _audioRelay;
    private bool _centerClickPending;
    private readonly EmbeddedWebMediaHandoffResolver _handoffResolver;
    private readonly EmbeddedWebFullscreenBridge _fullscreenBridge = new();
    private readonly EmbeddedWebPlaybackBridge _playbackBridge = new();
    private readonly EmbeddedWebFilterSession _filterSession;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private NativeWebView? _webView;
    private Uri? _embeddedSource;
    private Uri? _embeddedReferrer;
    private string? _embeddedTitle;
    private HandoffAttempt? _activeHandoff;
    private long _embeddedSessionId;
    private long _handoffSuppressedSessionId = -1;
    private volatile bool _isEmbeddedWebOpen;
    private volatile bool _isNativeHandoffPreparing;
    private bool _isFilterHookAttached;

    // Written on whichever thread disposes and read from callers on others.
    private volatile bool _disposed;

    /// <summary>
    /// Set from the dispatcher each time it answers the watchdog's ping. The
    /// watchdog compares it against the clock rather than blocking on the
    /// dispatcher, so a stalled thread is observed instead of joined.
    /// </summary>
    private long _lastUiPingTicks = Environment.TickCount64;
    private bool _watchdogStarted;

    /// <param name="deviceTier">
    /// How much injected filtering this hardware can carry. The session used to
    /// take the default, which is <see cref="InjectionOptions.Standard"/> — so
    /// the low-memory televisions were handed the full fifty-thousand-selector
    /// payload and the procedural engine on every navigation, despite the
    /// Android head already having worked out that they are
    /// <see cref="DeviceTier.Constrained"/> and the mapping to a smaller budget
    /// already existing. The tier was simply never carried this far.
    /// </param>
    public WebViewPlatformMediaPlayer(
        IPlatformMediaPlayer directMediaPlayer,
        INativeWebViewFilterHook? filterHook = null,
        HttpMessageHandler? mediaProbeHandler = null,
        DeviceTier deviceTier = DeviceTier.Standard,
        INativeWebViewRemoteInput? remoteInput = null,
        INativeWebViewAudioRelay? audioRelay = null)
    {
        _directMediaPlayer = directMediaPlayer ??
            throw new ArgumentNullException(nameof(directMediaPlayer));
        _filterHook =
            filterHook ?? UnavailableWebViewFilterHook.Instance;
        _remoteInput = remoteInput;
        _audioRelay = audioRelay;
        _handoffResolver =
            new EmbeddedWebMediaHandoffResolver(mediaProbeHandler);
        _filterSession = new EmbeddedWebFilterSession(
            EmbeddedWebAdBlocker.Engine,
            () => deviceTier.InjectionOptions());
        _filterHook.MediaResponseReceived += OnMediaResponseReceived;
    }

    public event EventHandler? PlaybackSurfaceChanged;

    public event EventHandler? NativePlaybackStarted;

    public event EventHandler<EmbeddedWebFullscreenChangedEventArgs>?
        EmbeddedFullscreenChanged;

    public event EventHandler? EmbeddedFullscreenExitRequested;

    private bool StartUiWatchdogOnce()
    {
        _watchdogStarted = true;
        EmbeddedWebDiagnostics.StartUiWatchdog(() =>
        {
            Dispatcher.UIThread.Post(
                () => Volatile.Write(
                    ref _lastUiPingTicks,
                    Environment.TickCount64),
                DispatcherPriority.Background);
            return Environment.TickCount64 -
                Volatile.Read(ref _lastUiPingTicks) < 3000;
        });
        return true;
    }

    public bool IsAvailable =>
        !_disposed &&
        _directMediaPlayer.IsAvailable;

    public Control? EmbeddedView
    {
        get
        {
            if (_disposed)
            {
                return null;
            }

            if (_isEmbeddedWebOpen && !_isNativeHandoffPreparing)
            {
                return GetOrCreateWebView();
            }

            return _directMediaPlayer.EmbeddedView;
        }
    }

    public bool SupportsExternalTracks =>
        _directMediaPlayer.SupportsExternalTracks;

    public bool SupportsInPlayerServerSelection =>
        _directMediaPlayer.SupportsInPlayerServerSelection;

    public bool SupportsEmbeddedWeb => !_disposed;

    public Func<bool>? RemoteActivationAllowed { get; set; }

    public bool CanActivateRemoteCenter =>
        !_disposed && _isEmbeddedWebOpen && !_isNativeHandoffPreparing &&
        _remoteInput is not null && RemoteActivationAllowed?.Invoke() is true &&
        _webView is { IsEffectivelyVisible: true, Bounds.Width: > 0, Bounds.Height: > 0 };

    /// <summary>One OK press clicks the current viewport center, never the last cursor position.</summary>
    public bool TryActivateRemoteCenter(bool isRepeatOrRelease = false)
    {
        if (!CanActivateRemoteCenter || _webView is not { } webView)
        {
            return false;
        }

        if (isRepeatOrRelease || _centerClickPending)
        {
            return true;
        }

        _centerClickPending = true;
        var session = Volatile.Read(ref _embeddedSessionId);
        // Native key callbacks must return before asking the browser for input.
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                if (CanActivateRemoteCenter && ReferenceEquals(webView, _webView) &&
                    session == Volatile.Read(ref _embeddedSessionId))
                {
                    await _remoteInput!.ClickCenterAsync(webView);
                    EmbeddedWebDiagnostics.LogNote("remote center click");
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                EmbeddedWebDiagnostics.LogNote($"remote center click failed {exception.GetType().Name}");
            }
            finally
            {
                _centerClickPending = false;
            }
        }, DispatcherPriority.Input);
        return true;
    }

    public void RequestEmbeddedFullscreenExit()
    {
        if (_disposed ||
            !_isEmbeddedWebOpen ||
            !_fullscreenBridge.IsDocumentFullscreen ||
            _webView is not { } webView)
        {
            return;
        }

        Dispatcher.UIThread.Post(
            async () =>
            {
                try
                {
                    _ = await webView.InvokeScript(
                        EmbeddedWebFullscreenBridge.ExitScript);
                }
                catch (Exception exception) when (
                    exception is not OutOfMemoryException)
                {
                    EmbeddedWebDiagnostics.LogNote(
                        $"embedded fullscreen exit failed " +
                        $"{exception.GetType().Name}");
                }
            },
            DispatcherPriority.Input);
    }

    /// <summary>
    /// The filtering session behind this player. Exposed so a host can tell
    /// whether an embedded page is open — compiling a filter ruleset while one
    /// is playing is exactly the wrong moment.
    /// </summary>
    public IContentFilterSession FilterSession => _filterSession;

    /// <summary>
    /// What the last embedded page actually got: whether requests were refused
    /// at the network layer or only from inside the page, and how many were
    /// refused. Without this, "filtering is not working" is a report nobody
    /// can act on.
    /// </summary>
    public EmbeddedWebFilterReport FilterReport => new(
        !_filterSession.IsPassthrough &&
        _isFilterHookAttached &&
        _filterHook.IsAttached,
        _filterSession.IsPassthrough
            ? "Filtering is paused for this player to preserve compatibility."
            : !_isFilterHookAttached || !_filterHook.IsAttached
                ? _filterHook.AttachFailure
                : _filterSession.IsCompatibilityLimited
                    ? "Page injection is disabled for this player; only " +
                      "playback-safe network rules are active."
                    : null,
        _filterSession.RequestsSeen,
        _filterSession.RequestsBlocked,
        _filterSession.RequestsAllowedAsSameSite,
        _filterSession.RequestsAllowedAsMedia,
        _filterSession.UnmatchedHosts);

    public async ValueTask OpenAsync(
        PlatformMediaRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);
        CancelPendingHandoff();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await CloseWebViewCoreAsync();
            // The caller may have shown EmbeddedView while it still referred
            // to the WebView. Closing that page changes the getter, but does
            // not update its binding or attach VLC's native child window.
            // Use the same surface transition as an automatically captured feed.
            await PrepareDirectSurfaceAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await _directMediaPlayer.OpenAsync(
                request,
                cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask OpenEmbeddedWebAsync(
        Uri source,
        string title,
        Uri? referrer = null,
        CancellationToken cancellationToken = default)
    {
        ValidateRemoteUri(source, nameof(source));
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        if (referrer is not null)
        {
            ValidateRemoteUri(referrer, nameof(referrer));
        }

        ObjectDisposedException.ThrowIf(_disposed, this);
        _ = _watchdogStarted || StartUiWatchdogOnce();
        CancelPendingHandoff();

        // A wrapper whose only content is an iframe of the real player puts
        // that player out of the filter's reach entirely, so open the inner
        // page directly and keep the wrapper as the referrer the host expects.
        var target = EmbeddedWebTarget.Unwrap(source, IsSafeRemoteUri);
        if (!ReferenceEquals(target, source))
        {
            referrer ??= source;
        }

        NativeWebView? webViewToNavigate = null;
        var sessionId = 0L;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await CloseWebViewCoreAsync();
            await _directMediaPlayer.CloseAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await Dispatcher.UIThread.InvokeAsync(
                () =>
                {
                    var webView = GetOrCreateWebView();
                    sessionId =
                        Interlocked.Increment(ref _embeddedSessionId);
                    Volatile.Write(
                        ref _handoffSuppressedSessionId,
                        -1);
                    _embeddedSource = target;
                    _embeddedReferrer = referrer;
                    _embeddedTitle = title;
                    _isEmbeddedWebOpen = true;
                    _isNativeHandoffPreparing = false;
                    webView.IsVisible = true;

                    webViewToNavigate = webView;
                });
        }
        finally
        {
            _gate.Release();
        }

        // Setting Source while NativeWebView is detached can create its
        // platform adapter too early. Return first so the view model can put
        // the WebView in the visual tree, then navigate.
        if (webViewToNavigate is not null)
        {
            _ = NavigateEmbeddedWebWhenReadyAsync(
                webViewToNavigate,
                target,
                source,
                sessionId);
        }
    }

    public async ValueTask<PlatformMediaState> GetStateAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_isEmbeddedWebOpen)
            {
                // What the page's own player reports, when it reports at all.
                // Otherwise the same placeholder as before: something is
                // playing in there, and nothing more can honestly be said.
                return _playbackBridge.TryRead() is { } reading
                    ? new PlatformMediaState(
                        reading.PositionSeconds,
                        reading.DurationSeconds,
                        reading.IsPlaying,
                        reading.IsEnded,
                        IsClosed: false,
                        ErrorMessage: null,
                        reading.IsBuffering)
                    : new PlatformMediaState(
                        0,
                        null,
                        IsPlaying: true,
                        IsEnded: false,
                        IsClosed: false);
            }

            return await _directMediaPlayer.GetStateAsync(
                cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Asked before an embedded page is started on the viewer's behalf, so
    /// something that knows more than the player — a watch party sitting
    /// paused — can hold it at its first frame. Unset means start playing,
    /// which is what picking a server has always meant.
    /// </summary>
    public Func<bool>? ShouldStartEmbeddedPlaybackPaused { get; set; }

    /// <summary>
    /// Transport control belongs to whichever player is actually presenting.
    /// A direct stream passes through to the native player underneath; an
    /// embedded page can be driven only while its own video element is
    /// reachable and reporting, which a cross-origin frame never is.
    /// </summary>
    public bool SupportsTransportControl =>
        _isEmbeddedWebOpen
            ? _playbackBridge.CanControl
            : _directMediaPlayer.SupportsTransportControl;

    public async ValueTask SetPlayingAsync(
        bool playing,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_isEmbeddedWebOpen)
            {
                await InvokeEmbeddedPlaybackScriptAsync(
                    EmbeddedWebPlaybackBridge.CreateSetPlayingScript(
                        playing));
                return;
            }

            await _directMediaPlayer.SetPlayingAsync(
                playing,
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
            if (_isEmbeddedWebOpen)
            {
                await InvokeEmbeddedPlaybackScriptAsync(
                    EmbeddedWebPlaybackBridge.CreateSeekScript(
                        positionSeconds));
                return;
            }

            await _directMediaPlayer.SeekAsync(
                positionSeconds,
                cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Runs one transport command inside the embedded document, on the UI
    /// thread the web view requires. A command that cannot be delivered is
    /// not an error the party should see: the next state report says whether
    /// the page moved, which is the only thing that decides anything.
    /// </summary>
    private async ValueTask InvokeEmbeddedPlaybackScriptAsync(string script)
    {
        var webView = _webView;
        if (webView is null || !_playbackBridge.CanControl)
        {
            return;
        }

        try
        {
            await Dispatcher.UIThread.InvokeAsync(
                async () => await webView.InvokeScript(script));
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException)
        {
            EmbeddedWebDiagnostics.LogNote(
                $"embedded playback command failed " +
                $"{exception.GetType().Name}");
        }
    }

    public async ValueTask CloseAsync(
        CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return;
        }

        CancelPendingHandoff();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await CloseWebViewCoreAsync();
            await _directMediaPlayer.CloseAsync(cancellationToken);
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

        CancelPendingHandoff();
        // Close under the gate and publish the disposed flag while still
        // holding it, so a caller queued behind us observes it and returns
        // instead of running against a torn-down player.
        // Restoring browser sound must not wait for a dispatcher that may
        // already have stopped during desktop shutdown.
        _audioRelay?.Detach();
        await _gate.WaitAsync();
        try
        {
            if (_disposed)
            {
                return;
            }

            await CloseWebViewCoreAsync();
            await _directMediaPlayer.CloseAsync();
            _disposed = true;
            if (_audioRelay is not null) await _audioRelay.DisposeAsync();
            RemoteActivationAllowed = null;
            if (_remoteInput is not null)
            {
                // Desktop disposal can run after the dispatcher has stopped.
                // AdapterDestroyed normally detaches first; never wait for a
                // stopped UI loop merely to remove an input subscription.
                if (Dispatcher.UIThread.CheckAccess()) _remoteInput.Detach();
                else Dispatcher.UIThread.Post(_remoteInput.Detach);
            }
            _filterHook.MediaResponseReceived -= OnMediaResponseReceived;
        }
        finally
        {
            _gate.Release();
        }

        if (_directMediaPlayer is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
        else if (_directMediaPlayer is IDisposable disposable)
        {
            disposable.Dispose();
        }

        _handoffResolver.Dispose();

        // The gate is deliberately not disposed. A caller already inside
        // WaitAsync would fault there and again in its finally Release, which
        // would mask the real path; a SemaphoreSlim whose wait handle was
        // never taken holds nothing that needs releasing.
    }

    private NativeWebView GetOrCreateWebView()
    {
        if (_webView is not null)
        {
            return _webView;
        }

        if (!Dispatcher.UIThread.CheckAccess())
        {
            throw new InvalidOperationException(
                UiTextCatalog.Get("EmbeddedWebPlayerUiThreadRequired"));
        }

        _webView = new NativeWebView
        {
            Source = BlankPage,
            Background = Brushes.Black,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch
        };
        _webView.EnvironmentRequested += OnEnvironmentRequested;
        _webView.AdapterCreated += OnWebViewAdapterCreated;
        _webView.AdapterDestroyed += OnWebViewAdapterDestroyed;
        _webView.NavigationStarted += OnNavigationStarted;
        _webView.NavigationCompleted += OnNavigationCompleted;
        _webView.NewWindowRequested += OnNewWindowRequested;
        _webView.WebMessageReceived += OnWebMessageReceived;
        _webView.WebResourceRequested += OnWebResourceRequested;

        return _webView;
    }

    private async Task NavigateEmbeddedWebWhenReadyAsync(
        NativeWebView webView,
        Uri target,
        Uri requestedSource,
        long sessionId)
    {
        try
        {
            var deadline = Environment.TickCount64 +
                (long)DeferredNavigationTimeout.TotalMilliseconds;
            while (true)
            {
                var state = await Dispatcher.UIThread.InvokeAsync(
                    () =>
                    {
                        if (_disposed ||
                            !_isEmbeddedWebOpen ||
                            _isNativeHandoffPreparing ||
                            sessionId !=
                                Volatile.Read(ref _embeddedSessionId) ||
                            !ReferenceEquals(webView, _webView))
                        {
                            return DeferredNavigationState.Stale;
                        }

                        return webView.IsEffectivelyVisible &&
                               webView.Bounds is
                               {
                                   Width: > 0,
                                   Height: > 0
                               }
                            ? DeferredNavigationState.Ready
                            : DeferredNavigationState.Waiting;
                    });
                if (state is DeferredNavigationState.Stale)
                {
                    return;
                }

                if (state is DeferredNavigationState.Ready ||
                    Environment.TickCount64 >= deadline)
                {
                    break;
                }

                await Task.Delay(DirectSurfacePollInterval)
                    .ConfigureAwait(false);
            }

            await Dispatcher.UIThread.InvokeAsync(
                () =>
                {
                    if (_disposed ||
                        !_isEmbeddedWebOpen ||
                        _isNativeHandoffPreparing ||
                        sessionId !=
                            Volatile.Read(ref _embeddedSessionId) ||
                        !ReferenceEquals(webView, _webView))
                    {
                        return;
                    }

                    // Attach after the native surface exists, then announce
                    // the document. This keeps both filtering and response
                    // capture bound to the adapter that will render the page.
                    TryAttachFilterHook(webView);
                    OpenFilterSession(target);
                    EmbeddedWebDiagnostics.LogNote(
                        ReferenceEquals(target, requestedSource)
                            ? $"open {target}"
                            : $"open {target} " +
                              $"(unwrapped from {requestedSource})");
                    webView.Source = target;
                });
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException)
        {
            // Deferring navigation is a rendering safeguard, never a reason
            // for the caller or process to fail. Fall back to the old direct
            // assignment so the safeguard itself can never leave a blank
            // browser.
            EmbeddedWebDiagnostics.LogNote(
                $"deferred web navigation failed " +
                $"{exception.GetType().Name}");
            try
            {
                await Dispatcher.UIThread.InvokeAsync(
                    () =>
                    {
                        if (_disposed ||
                            !_isEmbeddedWebOpen ||
                            _isNativeHandoffPreparing ||
                            sessionId !=
                                Volatile.Read(ref _embeddedSessionId) ||
                            !ReferenceEquals(webView, _webView))
                        {
                            return;
                        }

                        OpenFilterSession(target);
                        webView.Source = target;
                    });
            }
            catch
            {
                // The dispatcher may be shutting down. There is no active
                // browser surface left to recover in that case.
            }
        }
    }

    private void OpenFilterSession(Uri target)
    {
        string mode;
        if (EmbeddedWebMediaHandoffResolver.IsNuploadWatchPage(target))
        {
            // Nupload's signed player breaks when downloaded lists, cosmetic
            // defenses, or the generic autoplay shim modify its document.
            // Full pass-through also restores the pop-ad loaders. Keep the
            // document untouched while refusing only the endpoints verified
            // not to participate in JW Player or the ibra.lat media flow.
            _filterSession.OpenCompatibilityLimited(
                target,
                EmbeddedWebAdBlocker.NuploadCompatibilityEngine);
            mode = "limited-network";
        }
        else
        {
            _filterSession.Open(target);
            mode = "full";
        }

        ApplicationDiagnosticLog.Information(
            "playback.webview-open",
            $"host={target.IdnHost}; mode={mode}; " +
            $"networkAttached={_isFilterHookAttached}");
    }

    private async ValueTask CloseWebViewCoreAsync()
    {
        if (!_isEmbeddedWebOpen)
        {
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(
            () =>
            {
                LogEmbeddedWebFilterSummary("closed");
                _isEmbeddedWebOpen = false;
                _isNativeHandoffPreparing = false;
                Interlocked.Increment(ref _embeddedSessionId);
                _embeddedSource = null;
                _embeddedReferrer = null;
                _embeddedTitle = null;
                _filterSession.Close();
                _fullscreenBridge.Reset();
                _playbackBridge.Reset();
                _webView?.Stop();
                _audioRelay?.Detach();
                if (_webView is not null)
                {
                    _webView.Source = BlankPage;
                    _webView.IsVisible = false;
                }
            });
    }

    private void LogEmbeddedWebFilterSummary(string outcome)
    {
        var report = FilterReport;
        ApplicationDiagnosticLog.Information(
            "playback.webview-summary",
            $"outcome={outcome}; " +
            $"host={_filterSession.DocumentUri?.IdnHost ?? "-"}; " +
            $"network={report.IsNetworkLevel}; " +
            $"seen={report.RequestsSeen}; blocked={report.RequestsBlocked}; " +
            $"sameSite={report.RequestsAllowedAsSameSite}; " +
            $"media={report.RequestsAllowedAsMedia}; " +
            $"limitation={report.Limitation ?? "-"}");
    }

    private enum DeferredNavigationState
    {
        Waiting,
        Ready,
        Stale
    }

    private void OnMediaResponseReceived(
        EmbeddedWebMediaResponse observation)
    {
        if (_disposed ||
            !_isEmbeddedWebOpen ||
            _isNativeHandoffPreparing)
        {
            return;
        }

        var source = Volatile.Read(ref _embeddedSource);
        var title = Volatile.Read(ref _embeddedTitle);
        if (source is null ||
            string.IsNullOrWhiteSpace(title) ||
            !string.Equals(
                source.AbsoluteUri,
                observation.DocumentUri.AbsoluteUri,
                StringComparison.Ordinal) ||
            !EmbeddedWebMediaHandoffResolver.IsNuploadWatchPage(source) ||
            !EmbeddedWebMediaHandoffResolver.IsPotentialNuploadMediaRequest(
                observation.RequestUri,
                observation.ContentType))
        {
            return;
        }

        var sessionId = Volatile.Read(ref _embeddedSessionId);
        if (Volatile.Read(ref _handoffSuppressedSessionId) == sessionId)
        {
            return;
        }

        var attempt = new HandoffAttempt(
            sessionId);
        if (Interlocked.CompareExchange(
                ref _activeHandoff,
                attempt,
                comparand: null) is not null)
        {
            attempt.Dispose();
            return;
        }

        _ = TryNativeHandoffAsync(
            attempt,
            source,
            title,
            observation);
    }

    private async Task TryNativeHandoffAsync(
        HandoffAttempt attempt,
        Uri documentUri,
        string title,
        EmbeddedWebMediaResponse observation)
    {
        var gateEntered = false;
        var handoffCompleted = false;
        var surfaceSwitched = false;
        try
        {
            var request = await _handoffResolver.TryResolveAsync(
                    documentUri,
                    title,
                    observation,
                    attempt.Token)
                .ConfigureAwait(false);
            if (request is null)
            {
                EmbeddedWebDiagnostics.LogNote(
                    $"native handoff rejected " +
                    $"{SanitizeUri(observation.RequestUri)}");
                return;
            }

            await _gate.WaitAsync(attempt.Token).ConfigureAwait(false);
            gateEntered = true;
            if (_disposed ||
                !_isEmbeddedWebOpen ||
                attempt.SessionId !=
                    Volatile.Read(ref _embeddedSessionId) ||
                !ReferenceEquals(
                    Volatile.Read(ref _activeHandoff),
                    attempt))
            {
                return;
            }

            var hasEmbeddedNativeSurface =
                await PrepareDirectSurfaceAsync(attempt.Token)
                .ConfigureAwait(false);
            surfaceSwitched = true;
            await _directMediaPlayer.OpenAsync(
                    request,
                    attempt.Token)
                .ConfigureAwait(false);
            if (!hasEmbeddedNativeSurface)
            {
                await NativePlaybackStartGate.WaitAsync(
                        _directMediaPlayer.GetStateAsync,
                        NativePlaybackStartTimeout,
                        NativePlaybackPollInterval,
                        attempt.Token)
                    .ConfigureAwait(false);
            }
            attempt.Token.ThrowIfCancellationRequested();
            await CloseWebViewCoreAsync().ConfigureAwait(false);
            handoffCompleted = true;
            EmbeddedWebDiagnostics.LogNote(
                $"native handoff opened " +
                $"{SanitizeUri(request.InitialVariant.Source.PlaybackUri!)}");
        }
        catch (OperationCanceledException)
            when (attempt.Token.IsCancellationRequested)
        {
            // Closing the player or selecting another server superseded this
            // observation. The new action owns the surface.
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException)
        {
            EmbeddedWebDiagnostics.LogNote(
                $"native handoff failed " +
                $"{exception.GetType().Name}");
            if (!_disposed &&
                _isEmbeddedWebOpen &&
                attempt.SessionId ==
                    Volatile.Read(ref _embeddedSessionId))
            {
                // Do not repeatedly detach and reload the same working browser
                // page after this native player has already rejected it.
                Volatile.Write(
                    ref _handoffSuppressedSessionId,
                    attempt.SessionId);
            }

            try
            {
                await _directMediaPlayer.CloseAsync()
                    .ConfigureAwait(false);
            }
            catch
            {
                // Restoration below must still run when native cleanup fails.
            }

            if (surfaceSwitched &&
                !_disposed &&
                _isEmbeddedWebOpen &&
                attempt.SessionId ==
                    Volatile.Read(ref _embeddedSessionId))
            {
                try
                {
                    await RestoreEmbeddedWebSurfaceAsync(
                            documentUri,
                            attempt.SessionId)
                        .ConfigureAwait(false);
                }
                catch (Exception restoreException) when (
                    restoreException is not OutOfMemoryException)
                {
                    EmbeddedWebDiagnostics.LogNote(
                        $"web fallback restoration failed " +
                        $"{restoreException.GetType().Name}");
                }
            }
        }
        finally
        {
            if (gateEntered)
            {
                _gate.Release();
            }

            _ = Interlocked.CompareExchange(
                ref _activeHandoff,
                value: null,
                comparand: attempt);
            attempt.Dispose();
        }

        if (handoffCompleted)
        {
            try
            {
                NativePlaybackStarted?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception exception) when (
                exception is not OutOfMemoryException)
            {
                EmbeddedWebDiagnostics.LogNote(
                    $"native handoff notification failed " +
                    $"{exception.GetType().Name}");
            }
        }
    }

    private async ValueTask<bool> PrepareDirectSurfaceAsync(
        CancellationToken cancellationToken)
    {
        Control? directView = null;
        await Dispatcher.UIThread.InvokeAsync(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                _isNativeHandoffPreparing = _isEmbeddedWebOpen;
                directView = _directMediaPlayer.EmbeddedView;
                NotifyPlaybackSurfaceChanged();
            });
        if (directView is null)
        {
            return false;
        }

        var deadline = Environment.TickCount64 +
            (long)DirectSurfaceTimeout.TotalMilliseconds;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var isReady = await Dispatcher.UIThread.InvokeAsync(
                () =>
                    TopLevel.GetTopLevel(directView) is not null &&
                    directView.IsEffectivelyVisible &&
                    directView.Bounds is { Width: > 0, Height: > 0 });
            if (isReady || Environment.TickCount64 >= deadline)
            {
                ApplicationDiagnosticLog.Information(
                    "playback.native-surface",
                    $"stage=prepare; layoutReady={isReady}; " +
                    $"embeddedHandoff={_isNativeHandoffPreparing}");
                // This only checks the container. The native player still
                // verifies its actual child handle before calling Play.
                return true;
            }

            await Task.Delay(
                    DirectSurfacePollInterval,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async ValueTask RestoreEmbeddedWebSurfaceAsync(
        Uri documentUri,
        long sessionId)
    {
        NativeWebView? webView = null;
        await Dispatcher.UIThread.InvokeAsync(
            () =>
            {
                if (_disposed ||
                    !_isEmbeddedWebOpen ||
                    sessionId !=
                        Volatile.Read(ref _embeddedSessionId))
                {
                    return;
                }

                _isNativeHandoffPreparing = false;
                webView = _webView;
                if (webView is not null)
                {
                    webView.IsVisible = true;
                }

                NotifyPlaybackSurfaceChanged();
            });
        if (webView is not null)
        {
            _ = NavigateEmbeddedWebWhenReadyAsync(
                webView,
                documentUri,
                documentUri,
                sessionId);
        }
    }

    private void NotifyPlaybackSurfaceChanged()
    {
        try
        {
            PlaybackSurfaceChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException)
        {
            EmbeddedWebDiagnostics.LogNote(
                $"playback surface notification failed " +
                $"{exception.GetType().Name}");
        }
    }

    private void CancelPendingHandoff()
    {
        var attempt = Interlocked.Exchange(
            ref _activeHandoff,
            value: null);
        attempt?.Cancel();
    }

    private static string SanitizeUri(Uri uri) =>
        $"{uri.Scheme}://{uri.Host}{uri.AbsolutePath}";

    private sealed class HandoffAttempt(long sessionId) : IDisposable
    {
        private readonly CancellationTokenSource _cancellation = new();

        public long SessionId { get; } = sessionId;

        public CancellationToken Token => _cancellation.Token;

        public void Cancel()
        {
            try
            {
                _cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void Dispose() => _cancellation.Dispose();
    }

    /// <summary>
    /// Reaches through to the platform object so blocked requests are refused
    /// at the network layer rather than only inside the page. Retried on every
    /// navigation until it takes, because the adapter behind the control is
    /// not always created by the time a source is assigned.
    /// </summary>
    private void TryAttachFilterHook(
        NativeWebView webView,
        IPlatformHandle? platformHandle = null)
    {
        try
        {
            // Asked every time, never latched. The platform adapter behind
            // this control is destroyed and recreated when the control is
            // reparented — which is exactly what happens each time the viewer
            // returns to the details page and opens a server again — and the
            // request handler belongs to the adapter that went away. Latching
            // on the first success meant filtering worked for the first video
            // of a session and silently stopped for every one after it.
            var attached = _filterHook.TryAttach(
                webView,
                _filterSession,
                platformHandle);
            if (attached != _isFilterHookAttached)
            {
                EmbeddedWebDiagnostics.LogNote(
                    $"network hook attached={attached} " +
                    $"reason={_filterHook.AttachFailure ?? "-"}");
                ApplicationDiagnosticLog.Information(
                    "playback.webview-filter",
                    $"attached={attached}; " +
                    $"adapter=" +
                    $"{platformHandle?.HandleDescriptor ?? "deferred"}; " +
                    $"reason={_filterHook.AttachFailure ?? "-"}");
            }

            _isFilterHookAttached = attached;
        }
        catch
        {
            // A hook that cannot attach costs network-layer filtering, not
            // playback: the injected policy still runs.
            _isFilterHookAttached = false;
        }
    }

    private void OnWebViewAdapterCreated(
        object? sender,
        WebViewAdapterEventArgs eventArgs)
    {
        if (sender is not NativeWebView webView)
        {
            return;
        }

        var platformHandle = eventArgs.TryGetPlatformHandle();
        if (_isEmbeddedWebOpen) _audioRelay?.Attach(webView, platformHandle);
        ApplicationDiagnosticLog.Information(
            "playback.webview-adapter",
            $"created; adapter=" +
            $"{platformHandle?.HandleDescriptor ?? "unavailable"}");
        TryAttachFilterHook(webView, platformHandle);
        try
        {
            _remoteInput?.Attach(webView, platformHandle, TryActivateRemoteCenter);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            EmbeddedWebDiagnostics.LogNote($"remote input attach failed {exception.GetType().Name}");
        }
    }

    private void OnWebViewAdapterDestroyed(
        object? sender,
        WebViewAdapterEventArgs eventArgs)
    {
        _audioRelay?.Detach();
        _remoteInput?.Detach();
        _isFilterHookAttached = false;
        ApplicationDiagnosticLog.Information(
            "playback.webview-adapter",
            $"destroyed; adapter=" +
            $"{eventArgs.TryGetPlatformHandle()?.HandleDescriptor ?? "unavailable"}");
    }

    /// <summary>
    /// Opens the web engine's own developer tools for the embedded player,
    /// when <c>UMD_EMBEDDED_WEB_DEVTOOLS</c> asks for it.
    ///
    /// Off by default, and deliberately not a setting: it exists so that an
    /// advertisement no rule covers can be identified — its host and its
    /// markup — and turned into a rule. Guessing at what a page is doing from
    /// the outside is what makes that kind of report unanswerable.
    /// </summary>
    private static void OnEnvironmentRequested(
        object? sender,
        WebViewEnvironmentRequestedEventArgs eventArgs)
    {
        if (EmbeddedWebDiagnostics.IsDevToolsRequested)
        {
            eventArgs.EnableDevTools = true;
        }

        if (eventArgs is WindowsWebView2EnvironmentRequestedEventArgs webView2 &&
            EmbeddedWebDiagnostics.IsPrivateProfileEnabled)
        {
            // A private profile, for two reasons that point the same way.
            //
            // Filtering only sees requests that reach the network stack, so a
            // resource already in the profile cache is served without ever
            // being offered to it. A profile that accumulated advertisement
            // resources — which it will have done during any period when
            // filtering was not attached — keeps serving them afterwards, and
            // no amount of correct filtering changes that.
            //
            // And these are third-party pages from hosts whose business is
            // tracking. Nothing about an embedded player needs their cookies
            // or their storage to outlive the session.
            webView2.IsInPrivateModeEnabled = true;
        }
    }

    private void OnNavigationStarted(
        object? sender,
        WebViewNavigationStartingEventArgs eventArgs)
    {
        EmbeddedWebDiagnostics.LogNote(
            $"navigation started {eventArgs.Request}");
        if (sender is NativeWebView webView)
        {
            TryAttachFilterHook(webView);
        }

        if (!_isEmbeddedWebOpen || eventArgs.Request is not { } request)
        {
            return;
        }

        var currentDocument =
            _filterSession.DocumentUri ?? _embeddedSource;
        if (!IsSafeRemoteUri(request) ||
            (!_filterSession.IsPassthrough &&
             EmbeddedWebAdBlocker.ShouldBlock(
                 request,
                 currentDocument)))
        {
            eventArgs.Cancel = true;
            ApplicationDiagnosticLog.Information(
                "playback.webview-navigation-blocked",
                $"host={request.IdnHost}; " +
                $"from={currentDocument?.IdnHost ?? "-"}");
            return;
        }

        if (currentDocument is not null &&
            !HasSameOrigin(currentDocument, request))
        {
            ApplicationDiagnosticLog.Information(
                "playback.webview-document",
                $"redirect={currentDocument.IdnHost}->{request.IdnHost}");
            _filterSession.Navigate(request);
        }
    }

    private async void OnNavigationCompleted(
        object? sender,
        WebViewNavigationCompletedEventArgs eventArgs)
    {
        EmbeddedWebDiagnostics.LogNote(
            $"navigation completed success={eventArgs.IsSuccess} " +
            $"hookAttached={_isFilterHookAttached}");
        if (!_isEmbeddedWebOpen ||
            !eventArgs.IsSuccess ||
            sender is not NativeWebView webView)
        {
            return;
        }

        // Some adapters expose their platform handle only after the first
        // committed document. Retrying here makes response capture available
        // without changing or restarting that document.
        var filterWasAttachedForThisDocument =
            _isFilterHookAttached;
        TryAttachFilterHook(webView);
        if (_isEmbeddedWebOpen) _audioRelay?.Attach(webView, webView.TryGetPlatformHandle());
        ApplicationDiagnosticLog.Information(
            "playback.webview-navigation",
            $"success={eventArgs.IsSuccess}; " +
            $"host={eventArgs.Request?.IdnHost ?? "-"}; " +
            $"networkAttached={_isFilterHookAttached}; " +
            $"seen={_filterSession.RequestsSeen}; " +
            $"blocked={_filterSession.RequestsBlocked}");
        try
        {
            _ = await webView.InvokeScript(
                EmbeddedWebFullscreenBridge.ObserverScript);
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException)
        {
            // Fullscreen synchronization is independent from playback and
            // filtering. Failure leaves the existing toolbar and key routing
            // available instead of breaking the page.
            EmbeddedWebDiagnostics.LogNote(
                $"embedded fullscreen observer failed " +
                $"{exception.GetType().Name}");
        }

        // A fresh document means a fresh player: whatever the last one
        // reported is not this page's position.
        _playbackBridge.Reset();
        try
        {
            _ = await webView.InvokeScript(
                EmbeddedWebPlaybackBridge.ObserverScript);
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException)
        {
            // Without the reporter the player keeps its old contract: it
            // declares no transport control, and a watch party says so.
            EmbeddedWebDiagnostics.LogNote(
                $"embedded playback observer failed " +
                $"{exception.GetType().Name}");
        }

        if (_filterSession.IsPageInjectionSuppressed)
        {
            EmbeddedWebDiagnostics.LogNote(
                _filterSession.IsPassthrough
                    ? "embedded player compatibility pass-through active"
                    : "embedded player limited network filtering active");
            return;
        }

        if (!filterWasAttachedForThisDocument)
        {
            // Restore seed request wrappers as well as cosmetic defenses.
            // The request-only payload does not run the legacy overlay sweep.
            try
            {
                if ((_filterSession.DocumentUri ?? _embeddedSource)?.Host is
                    { Length: > 0 } host)
                {
                    _ = await webView.InvokeScript(
                        _filterSession.BuildFallbackInjectionPayload(host));
                }
            }
            catch
            {
                // Same best-effort contract: cosmetic filtering never blocks
                // playback.
            }
        }

        try
        {
            // Asked now rather than when the page was opened: a party can
            // pause while its document is still loading.
            var startPaused = ShouldStartEmbeddedPlaybackPaused?.Invoke()
                is true;
            _ = await webView.InvokeScript(
                BuildPlaybackStartScript(startPaused));
        }
        catch
        {
            // Same best-effort contract as the filter above.
        }
    }

    private void OnWebMessageReceived(
        object? sender,
        WebMessageReceivedEventArgs eventArgs)
    {
        if (_disposed ||
            !_isEmbeddedWebOpen ||
            !ReferenceEquals(sender, _webView))
        {
            return;
        }

        // Playback reports arrive far more often than fullscreen ones, so
        // they are claimed first and never fall through to it.
        if (_playbackBridge.Accept(eventArgs.Body))
        {
            return;
        }

        switch (_fullscreenBridge.Accept(eventArgs.Body))
        {
            case EmbeddedWebFullscreenTransition.Entered:
                EmbeddedWebDiagnostics.LogNote(
                    "embedded document entered fullscreen");
                NotifyEmbeddedFullscreenChanged(isFullscreen: true);
                break;

            case EmbeddedWebFullscreenTransition.Exited:
                EmbeddedWebDiagnostics.LogNote(
                    "embedded document exited fullscreen");
                NotifyEmbeddedFullscreenChanged(isFullscreen: false);
                break;

            case EmbeddedWebFullscreenTransition.ExitRequested:
                EmbeddedWebDiagnostics.LogNote(
                    "embedded document requested fullscreen exit");
                NotifyEmbeddedFullscreenExitRequested();
                break;
        }
    }

    private void NotifyEmbeddedFullscreenChanged(bool isFullscreen)
    {
        try
        {
            EmbeddedFullscreenChanged?.Invoke(
                this,
                new EmbeddedWebFullscreenChangedEventArgs(isFullscreen));
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException)
        {
            EmbeddedWebDiagnostics.LogNote(
                $"embedded fullscreen notification failed " +
                $"{exception.GetType().Name}");
        }
    }

    private void NotifyEmbeddedFullscreenExitRequested()
    {
        try
        {
            EmbeddedFullscreenExitRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException)
        {
            EmbeddedWebDiagnostics.LogNote(
                $"embedded fullscreen exit notification failed " +
                $"{exception.GetType().Name}");
        }
    }

    /// <summary>
    /// Starts the embedded page's own player. A television remote cannot click
    /// inside the hosted web surface, so a host that waits for a tap on its
    /// play overlay is otherwise unplayable there — the viewer picked the
    /// server, which is the intent to play.
    /// </summary>
    /// <param name="startPaused">
    /// Hold the page at its first frame instead of playing it. Joining a watch
    /// party that is paused should not play anything, and correcting that a
    /// tick later would be a second of video and sound nobody asked for.
    /// <para>
    /// A page whose player only exists after its overlay is clicked is still
    /// clicked: without a video element there is nothing for the party to
    /// read or drive, so it is materialized and then held, which costs a
    /// blink of playback rather than the ability to take part at all.
    /// </para>
    /// </param>
    internal static string BuildPlaybackStartScript(bool startPaused) =>
        $$"""
        (function () {
          const marker = "__umdEmbeddedPlaybackStart";
          if (window[marker]) { return; }
          window[marker] = true;
          const startPaused = {{(startPaused ? "true" : "false")}};
          const overlays = [
            ".vjs-big-play-button",
            ".jw-icon-display",
            ".plyr__control--overlaid",
            "button.play",
            ".play-button",
            "#play-button"
          ];
          let attempts = 0;
          let clicked = false;
          const start = function () {
            attempts++;
            let hasVideo = false;
            const videos = document.querySelectorAll("video");
            for (let index = 0; index < videos.length; index++) {
              const video = videos[index];
              hasVideo = true;
              if (startPaused) {
                if (!video.paused && !video.ended) { video.pause(); }
              } else if (video.paused && !video.ended) {
                const played = video.play();
                if (played && typeof played.catch === "function") {
                  played.catch(function () { });
                }
              }
            }
            if (!hasVideo && !clicked) {
              for (let index = 0; index < overlays.length; index++) {
                const candidate = document.querySelector(overlays[index]);
                if (candidate) { candidate.click(); clicked = true; break; }
              }
            }
            if (attempts < 20) { setTimeout(start, 500); }
          };
          start();
        })();
        """;

    private static void OnNewWindowRequested(
        object? sender,
        WebViewNewWindowRequestedEventArgs eventArgs)
    {
        // Embedded player hosts occasionally attempt advertising popups.
        // Keep all playback inside the application-owned surface.
        eventArgs.Handled = true;
    }

    /// <summary>
    /// Header hardening only. This event cannot filter: the wrapper exposes no
    /// cancel or response member and <c>Request.Uri</c> is init-only, so a
    /// request seen here cannot be stopped or redirected. Filtering happens in
    /// <see cref="OnNavigationStarted"/>, <see cref="OnNewWindowRequested"/>,
    /// and the policy injected by <see cref="OnNavigationCompleted"/>.
    /// </summary>
    private void OnWebResourceRequested(
        object? sender,
        WebResourceRequestedEventArgs eventArgs)
    {
        if (!_isEmbeddedWebOpen)
        {
            return;
        }

        if (!_filterSession.IsPassthrough && !_isFilterHookAttached)
        {
            // These privacy headers are only a fallback when no native request
            // hook is active. On Android, changing an allowed request here
            // makes Avalonia replay it through HttpURLConnection, bypassing the
            // WebView's normal cookie and redirect path and breaking signed
            // players such as Nupload.
            _ = eventArgs.Request.Headers.TrySet("DNT", "1");
            _ = eventArgs.Request.Headers.TrySet("Sec-GPC", "1");
            if (EmbeddedWebAdBlocker.ShouldBlock(
                    eventArgs.Request.Uri,
                    _embeddedSource))
            {
                // Avalonia exposes headers but no cancel/response surface. If
                // the platform has already scheduled an ad request, at least
                // avoid sending identifiers with it. Each mutation is best
                // effort: Android and some WebView2 request types expose
                // read-only headers.
                _ = eventArgs.Request.Headers.TryRemove("Authorization");
                _ = eventArgs.Request.Headers.TryRemove("Cookie");
                _ = eventArgs.Request.Headers.TryRemove("Referer");
            }
        }

        if (_embeddedSource == eventArgs.Request.Uri &&
            _embeddedReferrer is not null)
        {
            _ = eventArgs.Request.Headers.TrySet(
                "Referer",
                _embeddedReferrer.AbsoluteUri);
        }
    }

    private static void ValidateRemoteUri(Uri uri, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(uri, parameterName);
        if (!IsSafeRemoteUri(uri))
        {
            throw new ArgumentException(
                UiTextCatalog.Get("EmbeddedWebAddressInvalid"),
                parameterName);
        }
    }

    private static bool HasSameOrigin(Uri left, Uri right) =>
        string.Equals(
            left.Scheme,
            right.Scheme,
            StringComparison.OrdinalIgnoreCase) &&
        string.Equals(
            left.IdnHost,
            right.IdnHost,
            StringComparison.OrdinalIgnoreCase) &&
        left.Port == right.Port;

    private static bool IsSafeRemoteUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri ||
            uri.Scheme is not ("http" or "https") ||
            uri.UserInfo.Length > 0 ||
            string.Equals(
                uri.IdnHost,
                "localhost",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!IPAddress.TryParse(uri.IdnHost, out var address))
        {
            return true;
        }

        // "::ffff:192.168.1.1" is the same host as "192.168.1.1"; unwrapping it
        // first is what keeps the private-range table below from being skipped
        // just because the literal was written in its IPv6 form.
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) ||
            address.IsIPv6LinkLocal ||
            address.IsIPv6SiteLocal ||
            address.Equals(IPAddress.IPv6Any) ||
            address.Equals(IPAddress.IPv6None))
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily is not
            System.Net.Sockets.AddressFamily.InterNetwork)
        {
            // IsIPv6SiteLocal only covers the deprecated fec0::/10. Unique
            // local addresses — fc00::/7 — are the range home networks
            // actually use.
            return (bytes[0] & 0xFE) != 0xFC;
        }

        return bytes[0] switch
        {
            10 => false,
            127 => false,
            169 when bytes[1] == 254 => false,
            172 when bytes[1] is >= 16 and <= 31 => false,
            192 when bytes[1] == 168 => false,
            _ => true
        };
    }
}
