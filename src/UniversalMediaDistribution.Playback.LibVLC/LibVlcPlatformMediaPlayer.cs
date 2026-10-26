using Avalonia.Controls;
using LibVLCSharp.Shared;
using UniversalMediaDistribution.Torrenting;
using UniversalMediaDistribution.UI.Diagnostics;
using UniversalMediaDistribution.UI.Localization;
using UniversalMediaDistribution.UI.Playback;
using VlcEngine = LibVLCSharp.Shared.LibVLC;

namespace UniversalMediaDistribution.Playback.LibVLC;

public sealed partial class LibVlcPlatformMediaPlayer : IPlatformMediaPlayer, IAsyncDisposable
{
    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
        "AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/138.0.0.0 Safari/537.36";
    private static readonly TimeSpan PlaybackStartTimeout =
        TimeSpan.FromSeconds(20);
    private static readonly TimeSpan PlaybackStatePollInterval =
        TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// How long to wait for the native video window before giving up on this
    /// attempt.
    /// <para>
    /// Two seconds was tight for a cold first layout, and the previous
    /// behaviour of starting anyway is what produced a separate VLC window on
    /// the desktop.
    /// </para>
    /// </summary>
    private static readonly TimeSpan VideoSurfaceTimeout =
        TimeSpan.FromSeconds(6);

    private readonly object _initializationLock = new();
    private readonly SemaphoreSlim _playbackGate = new(1, 1);
    private VlcEngine? _libVlc;
    private MediaPlayer? _mediaPlayer;
    private LibVlcPlayerView? _view;
    private Media? _media;
    private StreamMediaInput? _mediaInput;
    private IMediaReadHandle? _readHandle;
    private HlsLoopbackProxySession? _hlsProxySession;
    private PlatformMediaRequest? _request;
    private IReadOnlyList<PlatformMediaVariant> _variants = [];
    private Exception? _initializationError;
    private string? _playbackError;
    private int _selectedVariantIndex = -1;
    private bool _isClosed = true;
    private bool _disposed;

    public bool IsAvailable
    {
        get
        {
            EnsureInitialized();
            return _initializationError is null;
        }
    }

    public Control? EmbeddedView
    {
        get
        {
            EnsureInitialized();
            return _view;
        }
    }

    public async ValueTask OpenAsync(
        PlatformMediaRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();
        EnsureInitialized();
        if (_initializationError is not null)
        {
            throw new NotSupportedException(
                UiTextCatalog.Get("LibVlcInitializeFailed"),
                _initializationError);
        }

        await _playbackGate.WaitAsync(cancellationToken);
        try
        {
            await CloseCurrentMediaAsync();
            var variants = request.AlternateAudioVariants
                .Prepend(request.InitialVariant)
                .ToArray();
            if (variants.Length > 16 ||
                variants.Any(static variant => string.IsNullOrWhiteSpace(variant.Label)) ||
                variants.Any(variant =>
                    variant.IsAudio != request.InitialVariant.IsAudio))
            {
                throw new ArgumentException(
                    UiTextCatalog.Get("NativeRenditionListInvalid"),
                    nameof(request));
            }

            _request = request;
            _variants = variants;
            _selectedVariantIndex = 0;
            try
            {
                await OpenVariantCoreAsync(
                    request,
                    variants[0],
                    selectedVariantIndex: 0,
                    request.StartPositionSeconds,
                    resumePlayback: true,
                    cancellationToken);
            }
            catch
            {
                await CloseCurrentMediaAsync();
                ClearVariantState();
                throw;
            }
        }
        finally
        {
            _playbackGate.Release();
        }
    }

    public async ValueTask<PlatformMediaState> GetStateAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureInitialized();
        if (_mediaPlayer is null)
        {
            return new PlatformMediaState(
                0,
                null,
                false,
                false,
                true,
                _initializationError?.Message);
        }

        await _playbackGate.WaitAsync(cancellationToken);
        try
        {
            if (_castRecovery is { } recovery)
                return new PlatformMediaState(recovery.Position, recovery.Duration, false, false, false);
            var nativeState = _mediaPlayer.State;
            if (nativeState is VLCState.Error)
            {
                _playbackError ??= UiTextCatalog.Get("LibVlcDecodeFailed");
            }

            var position = Math.Max(0, _mediaPlayer.Time) / 1000d;
            var duration = _mediaPlayer.Length > 0
                ? _mediaPlayer.Length / 1000d
                : (double?)null;
            return new PlatformMediaState(
                position,
                duration,
                nativeState is VLCState.Playing || _mediaPlayer.IsPlaying,
                nativeState is VLCState.Ended,
                _isClosed,
                _playbackError,
                // Opening and Buffering both mean the stream is not yet
                // delivering; neither is the viewer's doing.
                nativeState is VLCState.Buffering or VLCState.Opening);
        }
        finally
        {
            _playbackGate.Release();
        }
    }

    public bool SupportsTransportControl => true;

    public async ValueTask SetPlayingAsync(
        bool playing,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _playbackGate.WaitAsync(cancellationToken);
        try
        {
            if (_mediaPlayer is null || _isClosed)
            {
                return;
            }

            // SetPause is idempotent both ways, so no state check is needed —
            // and reading State here could race the poll-based monitors.
            await Task.Run(() => _mediaPlayer.SetPause(!playing),
                cancellationToken);
        }
        finally
        {
            _playbackGate.Release();
        }
    }

    public async ValueTask SeekAsync(
        double positionSeconds,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _playbackGate.WaitAsync(cancellationToken);
        try
        {
            if (_mediaPlayer is null || _isClosed)
            {
                return;
            }

            var milliseconds =
                (long)Math.Max(0, positionSeconds * 1_000);
            await Task.Run(() => _mediaPlayer.Time = milliseconds,
                cancellationToken);
        }
        finally
        {
            _playbackGate.Release();
        }
    }

    public async ValueTask CloseAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return;
        }

        await _playbackGate.WaitAsync(cancellationToken);
        try
        {
            await CloseCurrentMediaAsync();
            ClearVariantState();
            await EndCastingAsync();
        }
        finally
        {
            _playbackGate.Release();
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
        if (_view is not null)
        {
            _view.VariantSelectionRequested -= OnVariantSelectionRequested;
            _view.CastDiscoveryRequested -= OnCastDiscoveryRequested;
            _view.CastSelectionRequested -= OnCastSelectionRequested;
        }

        _view?.Dispose();
        _mediaPlayer?.Dispose();
        _libVlc?.Dispose();
        _playbackGate.Dispose();
    }

    /// <summary>
    /// Records what the extractor handed to libVLC. When a hoster plays in the
    /// embedded browser but stalls here, the difference is almost always what
    /// the request carries: the browser sends the page's referrer and cookies
    /// on its own, while libVLC sends only what is passed as media options.
    /// This says which of those were actually present.
    /// </summary>
    /// <remarks>
    /// The query string is deliberately omitted. Resolved hoster URLs carry
    /// signed tokens there, and a diagnostic log is not the place for them; the
    /// host and path identify the stream well enough, and the token's presence
    /// and length are reported without its value.
    /// </remarks>
    private static void LogNativeMediaHandoff(
        PlatformMediaVariant variant)
    {
        try
        {
            var source = variant.Source;
            var uri = source.PlaybackUri;
            var target = uri is null
                ? "-"
                : $"{uri.Scheme}://{uri.Host}{uri.AbsolutePath}";
            var query = uri?.Query ?? string.Empty;
            ApplicationDiagnosticLog.Information(
                "playback.native-handoff",
                $"variant='{variant.Label}'; url={target}; " +
                $"queryChars={query.Length}; " +
                $"referrer={source.Referrer?.AbsoluteUri ?? "NONE"}; " +
                $"origin={source.HttpOrigin?.GetLeftPart(UriPartial.Authority) ?? "NONE"}; " +
                $"userAgent={(source.UserAgent is null ? "default" : "custom")}; " +
                $"clearKeys={source.DecryptionKeys.Count}");
        }
        catch (Exception exception)
        {
            // Never let a diagnostic stop playback.
            ApplicationDiagnosticLog.Warning(
                "playback.native-handoff",
                $"Could not record the media handoff: {exception.Message}");
        }
    }

    private void EnsureInitialized()
    {
        if (_mediaPlayer is not null || _initializationError is not null || _disposed)
        {
            return;
        }

        lock (_initializationLock)
        {
            if (_mediaPlayer is not null || _initializationError is not null || _disposed)
            {
                return;
            }

            try
            {
                BundledLibVlcRuntime.Configure();
                Core.Initialize();
                _libVlc = CreateEngine();
                _libVlc.SetUserAgent(
                    "UniversalMediaDistribution",
                    BrowserUserAgent);
                _mediaPlayer = new MediaPlayer(_libVlc)
                {
                    EnableHardwareDecoding = true,
                    NetworkCaching = 1_500,
                    Volume = 100
                };
                // VLC would otherwise grab keyboard and mouse input for its
                // own shortcuts, which is why Escape never reached the app
                // while the video had focus.
                _mediaPlayer.EnableKeyInput = false;
                _mediaPlayer.EnableMouseInput = false;
                _view = new LibVlcPlayerView(_mediaPlayer);
                _view.VariantSelectionRequested += OnVariantSelectionRequested;
                ConfigureCasting();
                ApplicationDiagnosticLog.Information(
                    "playback.libvlc-audio",
                    $"processId={Environment.ProcessId}; " +
                    (OperatingSystem.IsWindows()
                        ? "output=wasapi; session=process-default; passthrough=disabled"
                        : "output=platform-default"));
            }
            catch (Exception exception)
            {
                _view?.Dispose();
                _mediaPlayer?.Dispose();
                _libVlc?.Dispose();
                _view = null;
                _mediaPlayer = null;
                _libVlc = null;
                _initializationError = exception;
                ApplicationDiagnosticLog.Error(
                    "playback.libvlc-initialization", "Could not initialize the native player.", exception);
            }
        }
    }

    private static VlcEngine CreateEngine()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new VlcEngine("--no-video-title-show", "--no-plugins-cache");
        }

        // LibVLC runs inside UMD. Its Windows volume-save option nevertheless
        // assigns audio to VLC's own session GUID. Use the process's default
        // session so app audio capture can associate playback with UMD.
        // Decode to PCM through shared WASAPI: HDMI/SPDIF passthrough uses
        // exclusive mode, which bypasses the mix captured by sharing apps.
        return new VlcEngine(
            "--no-video-title-show",
            "--aout=mmdevice",
            "--mmdevice-backend=wasapi",
            "--no-volume-save",
            "--mmdevice-passthrough=0");
    }

    private async ValueTask OpenVariantCoreAsync(
        PlatformMediaRequest request,
        PlatformMediaVariant variant,
        int selectedVariantIndex,
        double startPositionSeconds,
        bool resumePlayback,
        CancellationToken cancellationToken)
    {
        var mediaPlayer = _mediaPlayer!;
        Media? media = null;
        StreamMediaInput? mediaInput = null;
        IMediaReadHandle? readHandle = null;
        HlsLoopbackProxySession? hlsProxySession = null;
        try
        {
            if (variant.Source.OpenReadAsync is not null)
            {
                readHandle = await variant.Source.OpenReadAsync(cancellationToken);
                var stream = readHandle.Stream ??
                    throw new InvalidOperationException(
                        UiTextCatalog.Get("NativeMediaStreamMissing"));
                if (!stream.CanRead)
                {
                    throw new InvalidOperationException(
                        UiTextCatalog.Get("NativeMediaStreamUnreadable"));
                }

                mediaInput = new StreamMediaInput(stream);
                media = new Media(_libVlc!, mediaInput);
            }
            else if (variant.Source.PlaybackUri is not null)
            {
                var mediaUri = variant.Source.PlaybackUri;
                if (RequiresHlsProxy(variant))
                {
                    hlsProxySession =
                        await HlsLoopbackProxySession.StartAsync(
                            variant.Source,
                            BrowserUserAgent,
                            cancellationToken);
                    mediaUri = hlsProxySession.PlaybackUri;
                }

                media = new Media(_libVlc!, mediaUri);
                media.AddOption(
                    $":http-user-agent={variant.Source.UserAgent ?? BrowserUserAgent}");
                media.AddOption(":http-reconnect");
                if (hlsProxySession is null &&
                    variant.Source.Referrer is not null)
                {
                    media.AddOption(
                        $":http-referrer={variant.Source.Referrer.AbsoluteUri}");
                }

                // libVLC 3.0.21+ consumes ClearKey pairs through
                // `--dash-clearkey=<kid>:<key>` (hex, no separators).
                // Each pair must be added as its own option, in the
                // same form the upstream series-scraper recovers from
                // the player page, so the native player can decrypt
                // the DASH stream the WebView would otherwise render.
                foreach (var pair in variant.Source.DecryptionKeys)
                {
                    media.AddOption(
                        $":dash-clearkey={pair.KeyId}:{pair.Key}");
                }

                LogNativeMediaHandoff(variant);
            }
            else
            {
                throw new InvalidOperationException(
                    UiTextCatalog.Get("NativePlaybackSourceMissing"));
            }

            foreach (var subtitle in variant.Subtitles ?? request.Subtitles)
            {
                media.AddSlave(
                    MediaSlaveType.Subtitle,
                    subtitle.IsDefault ? 4u : 2u,
                    subtitle.Source);
            }

            _readHandle = readHandle;
            _mediaInput = mediaInput;
            _media = media;
            _hlsProxySession = hlsProxySession;
            hlsProxySession = null;
            _playbackError = null;
            _isClosed = false;
            _view!.SetMedia(
                request.Title,
                variant.IsAudio,
                _variants.Select(static item => item.Label).ToArray(),
                selectedVariantIndex);

            // SetMedia above may have just re-shown the video surface, and
            // showing it does not create the native child window — an arrange
            // pass does. Starting playback before that window exists is what
            // makes LibVLC render into a top-level window of its own instead
            // of inside the application.
            // Starting without that window is not a degraded success: LibVLC
            // opens a top-level window of its own, outside the application,
            // with none of its controls and no way to get back. Failing here
            // instead lets the caller move on to the next server, which is the
            // behaviour a viewer can actually recover from.
            if (_rendererTarget is null && !await _view.WaitForVideoSurfaceAsync(
                    VideoSurfaceTimeout,
                    cancellationToken))
            {
                throw new InvalidOperationException(
                    UiTextCatalog.Get("LibVlcVideoSurfaceUnavailable"));
            }

            mediaPlayer.Media = media;
            if (!mediaPlayer.Play())
            {
                throw new InvalidOperationException(
                    UiTextCatalog.Get("LibVlcStreamRejected"));
            }

            VLCState startState;
            try
            {
                startState =
                    await LibVlcPlaybackStateMonitor.WaitForStartAsync(
                        () => mediaPlayer.State,
                        PlaybackStatePollInterval,
                        PlaybackStartTimeout,
                        cancellationToken);
            }
            catch (TimeoutException exception)
            {
                ApplicationDiagnosticLog.Warning(
                    "playback.libvlc-start-timeout",
                    exception.Message);
                throw new InvalidOperationException(
                    UiTextCatalog.Get("LibVlcPlaybackStartTimedOut"),
                    exception);
            }

            if (startState is VLCState.Error)
            {
                _playbackError = UiTextCatalog.Get("LibVlcDecodeFailed");
                throw new InvalidOperationException(_playbackError);
            }

            if (startState is not VLCState.Ended)
            {
                ApplyInitialPlaybackState(
                    mediaPlayer,
                    startPositionSeconds,
                    resumePlayback);
            }

            ApplicationDiagnosticLog.Information(
                "playback.libvlc-started",
                $"state={startState}; " +
                $"nativeAot={!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported}");
            _castRecovery = null;
        }
        catch
        {
            if (_media is null && _mediaInput is null && _readHandle is null)
            {
                media?.Dispose();
                mediaInput?.Dispose();
                if (readHandle is not null)
                {
                    await readHandle.DisposeAsync();
                }

                if (hlsProxySession is not null)
                {
                    await hlsProxySession.DisposeAsync();
                }
            }
            else
            {
                await CloseCurrentMediaAsync();
            }

            throw;
        }
    }

    private async void OnVariantSelectionRequested(object? sender, int requestedIndex)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            await SwitchVariantAsync(requestedIndex);
        }
        catch (Exception exception)
        {
            _playbackError = UiTextCatalog.Format(
                "RenditionOpenFailed",
                exception.Message);
            _view?.SetSelectedVariant(_selectedVariantIndex);
        }
    }

    private async ValueTask SwitchVariantAsync(int requestedIndex)
    {
        await _playbackGate.WaitAsync();
        try
        {
            if (_request is null ||
                requestedIndex < 0 ||
                requestedIndex >= _variants.Count ||
                requestedIndex == _selectedVariantIndex)
            {
                return;
            }

            var previousIndex = _selectedVariantIndex;
            var previousPosition = Math.Max(0, _mediaPlayer?.Time ?? 0) / 1000d;
            var resumePlayback = _mediaPlayer?.IsPlaying is true;
            await CloseCurrentMediaAsync();
            _selectedVariantIndex = requestedIndex;
            try
            {
                await OpenVariantCoreAsync(
                    _request,
                    _variants[requestedIndex],
                    requestedIndex,
                    previousPosition,
                    resumePlayback,
                    CancellationToken.None);
            }
            catch (Exception switchException)
            {
                await CloseCurrentMediaAsync();
                _selectedVariantIndex = previousIndex;
                try
                {
                    await OpenVariantCoreAsync(
                        _request,
                        _variants[previousIndex],
                        previousIndex,
                        previousPosition,
                        resumePlayback,
                        CancellationToken.None);
                    _playbackError = UiTextCatalog.Format(
                        "RenditionOpenFailed",
                        switchException.Message);
                }
                catch (Exception recoveryException)
                {
                    ClearVariantState();
                    throw new InvalidOperationException(
                        UiTextCatalog.Get("RenditionRestoreFailed"),
                        new AggregateException(switchException, recoveryException));
                }
            }
        }
        finally
        {
            _playbackGate.Release();
        }
    }

    private async ValueTask CloseCurrentMediaAsync()
    {
        try
        {
            if (_mediaPlayer is not null && !_isClosed)
            {
                // Stop holds the libVLC player lock until the input dies,
                // which on a stalled network stream is a long time. The view's
                // refresh timer (and any control the viewer clicks) would walk
                // into that lock from the UI thread and freeze the whole
                // application for the duration, so the view stops talking to
                // the player first and resumes on the next SetMedia.
                _view?.SuspendPlayerAccess();
                await Task.Run(_mediaPlayer.Stop);
            }
        }
        finally
        {
            if (_mediaPlayer is not null)
            {
                _mediaPlayer.Media = null;
            }

            _media?.Dispose();
            _media = null;
            _mediaInput?.Dispose();
            _mediaInput = null;
            if (_readHandle is not null)
            {
                await _readHandle.DisposeAsync();
                _readHandle = null;
            }

            if (_hlsProxySession is not null)
            {
                await _hlsProxySession.DisposeAsync();
                _hlsProxySession = null;
            }

            _isClosed = true;
            _playbackError = null;
            _view?.Reset();
        }
    }

    /// <summary>
    /// Applies resume and pause state after direct state polling has observed
    /// playback start. This deliberately runs outside libVLC's native event
    /// thread: re-entering libVLC from an event callback can deadlock against a
    /// concurrent Stop or state query.
    /// </summary>
    private static void ApplyInitialPlaybackState(
        MediaPlayer player,
        double startPositionSeconds,
        bool resumePlayback)
    {
        var startMilliseconds =
            (long)Math.Max(0, startPositionSeconds * 1_000);
        if (startMilliseconds > 0)
        {
            player.Time = startMilliseconds;
        }

        if (!resumePlayback)
        {
            player.SetPause(true);
        }
        else if (player.State is VLCState.Paused)
        {
            player.SetPause(false);
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);

    internal static bool RequiresHlsProxy(PlatformMediaVariant variant)
    {
        if (variant.Source.HttpOrigin is null ||
            variant.Source.PlaybackUri is null)
        {
            return false;
        }

        return variant.Source.PlaybackUri.AbsolutePath.EndsWith(
                   ".m3u8",
                   StringComparison.OrdinalIgnoreCase) ||
               variant.MimeType?.Contains(
                   "mpegurl",
                   StringComparison.OrdinalIgnoreCase) is true;
    }

    private void ClearVariantState()
    {
        _castRecovery = null;
        _request = null;
        _variants = [];
        _selectedVariantIndex = -1;
    }
}
