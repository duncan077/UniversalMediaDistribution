using Android.App;
using Android.Content.PM;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Views;
using Android.Widget;
using UniversalMediaDistribution.Mobile;
using UniversalMediaDistribution.UI.Diagnostics;
using UniversalMediaDistribution.UI.Localization;
using UniversalMediaDistribution.UI.Playback;

namespace UniversalMediaDistribution.Android;

[Activity(
    Label = "@string/now_playing",
    Theme = "@android:style/Theme.Material.NoActionBar",
    Exported = false,
    ConfigurationChanges =
        ConfigChanges.Orientation |
        ConfigChanges.ScreenSize |
        ConfigChanges.UiMode)]
internal sealed partial class NativePlaybackActivity :
    Activity,
    global::Android.Media.MediaPlayer.IOnInfoListener
{
    internal const string SessionTokenExtra =
        "org.universalmediadistribution.client.PLAYBACK_SESSION";

    private AndroidPlatformMediaPlayer? _platformPlayer;
    private AndroidPlatformMediaPlayer.PlaybackLaunch? _launch;
    private AndroidPlaybackView? _videoView;
    private Button? _engineButton;
    private readonly HashSet<AndroidPlaybackEngine> _attemptedEngines = [];
    private int _playbackAttemptGeneration;
    private CancellationTokenSource? _preparationTimeout;
    private bool _isActivityResumed;
    private LinearLayout? _toolbar;
    private Button? _backButton;
    private Spinner? _variantSpinner;
    private ProgressBar? _loadingIndicator;
    private TextView? _playbackStatus;
    private Button? _fullscreenButton;
    private Button? _exitFullscreenButton;
    private LinearLayout? _playbackControls;
    private Button? _rewindButton;
    private Button? _playPauseButton;
    private Button? _forwardButton;
    private SeekBar? _seekBar;
    private TextView? _elapsedTime;
    private TextView? _durationTime;
    private bool _isScrubbing;
    private Handler? _controlsHandler;
    private Java.Lang.Runnable? _progressTick;
    private Java.Lang.Runnable? _hideControls;

    /// <summary>
    /// How long the playback controls stay up after the last input.
    /// </summary>
    private static readonly TimeSpan ControlsHideDelay =
        TimeSpan.FromSeconds(5);

    private readonly HashSet<int> _attemptedVariantIndexes = [];
    private int _selectedVariantIndex;
    private bool _ignoreVariantSelection;
    private bool _isPrepared;
    private bool _isEnded;
    private bool _isBuffering;
    private bool _resumeAfterPause;
    private bool _isFullscreen;
    private bool _isTelevision;
    private bool _playbackControlsVisible;
    private readonly NativePlayerKeyPressGuard _focusChangingPress = new();
    private string? _errorMessage;
    private int _statusBarInset;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        if (!TryGetLaunch(out var launch))
        {
            Finish();
            return;
        }

        _launch = launch;
        _controlsHandler = new Handler(Looper.MainLooper!);
        _progressTick = new Java.Lang.Runnable(UpdatePlaybackProgress);
        _hideControls = new Java.Lang.Runnable(HidePlaybackControls);
        _isTelevision =
            Application is UmdAndroidApplication { IsTelevision: true };
        _statusBarInset = ResolveStatusBarInset();
        if (_isTelevision)
        {
            RequestedOrientation = ScreenOrientation.Landscape;
        }

        Window?.AddFlags(WindowManagerFlags.KeepScreenOn);

        var root = new FrameLayout(this)
        {
            Background = new ColorDrawable(Color.Black)
        };
        var playerLayout = new LinearLayout(this)
        {
            Orientation = Orientation.Vertical,
            Background = new ColorDrawable(Color.Black)
        };
        _toolbar = new LinearLayout(this)
        {
            Orientation = Orientation.Horizontal,
            Background = new ColorDrawable(Color.Argb(235, 14, 18, 27))
        };
        _toolbar.SetGravity(GravityFlags.CenterVertical);
        _toolbar.SetPadding(Dp(16), Dp(10), Dp(16), Dp(10));

        _backButton = CreatePlaybackControlButton(
            UiTextCatalog.Get("PlayerBack"), BackButton_OnClick, 72);
        _backButton.ContentDescription =
            UiTextCatalog.Get("PlayerBackDescription");
        _toolbar.AddView(
            _backButton,
            new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WrapContent,
                ViewGroup.LayoutParams.WrapContent));

        var title = new TextView(this)
        {
            Text = launch.Title,
            TextSize = _isTelevision ? 22 : 18,
            Gravity = GravityFlags.CenterVertical
        };
        title.SetSingleLine(true);
        title.Ellipsize = global::Android.Text.TextUtils.TruncateAt.End;
        title.SetTextColor(Color.White);
        title.SetPadding(Dp(16), 0, Dp(12), 0);
        _toolbar.AddView(
            title,
            new LinearLayout.LayoutParams(
                width: 0,
                 ViewGroup.LayoutParams.WrapContent,
                 weight: 1));

        if (launch.Variants.Count > 1)
        {
            _variantSpinner = new Spinner(this)
            {
                Id = View.GenerateViewId(),
                ContentDescription = UiTextCatalog.Get("PlayerServerQuality")
            };
            var variantLabels = launch.Variants
                .Select(static variant => variant.Label)
                .ToArray();
            var adapter = new ArrayAdapter<string>(
                this,
                global::Android.Resource.Layout.SimpleSpinnerItem,
                variantLabels);
            adapter.SetDropDownViewResource(
                global::Android.Resource.Layout.SimpleSpinnerDropDownItem);
            _variantSpinner.Adapter = adapter;
            _variantSpinner.ItemSelected += VariantSpinner_OnItemSelected;
        }

        _engineButton = CreatePlaybackControlButton("VLC", EngineButton_OnClick, 72);

        _fullscreenButton = new Button(this)
        {
            Id = View.GenerateViewId(),
            Text = "⛶",
            ContentDescription =
                UiTextCatalog.Get("PlayerEnterFullscreen"),
            Visibility = launch.IsAudio || _isTelevision
                ? ViewStates.Gone
                : ViewStates.Visible
        };
        _fullscreenButton.SetTextColor(Color.White);
        _fullscreenButton.SetMinimumWidth(Dp(48));
        _fullscreenButton.Background = CreatePlaybackButtonBackground();
        _fullscreenButton.Click += FullscreenButton_OnClick;
        _toolbar.AddView(
            _fullscreenButton,
            new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WrapContent,
                ViewGroup.LayoutParams.WrapContent));
        if (!_isTelevision)
        {
            playerLayout.AddView(
                _toolbar,
                new LinearLayout.LayoutParams(
                    ViewGroup.LayoutParams.MatchParent,
                    ViewGroup.LayoutParams.WrapContent)
                {
                    // Android 15 enforces edge-to-edge. Without this inset the
                    // toolbar is painted underneath the status bar: visible in
                    // screenshots, but Android consumes every touch before the
                    // Back, server and fullscreen controls receive it.
                    TopMargin = _statusBarInset
                });
        }

        var playbackSurface = new FrameLayout(this)
        {
            Background = new ColorDrawable(Color.Black)
        };
        _videoView = new AndroidPlaybackView(this)
        {
            Id = View.GenerateViewId(),
            Focusable = true,
            FocusableInTouchMode = true,
            ContentDescription = launch.IsAudio
                ? UiTextCatalog.Format("AudioPlayerFor", launch.Title)
                : UiTextCatalog.Format("VideoPlayerFor", launch.Title)
        };
        // VideoView is backed by a hardware SurfaceView. Painting an opaque
        // background on the view can cover the decoded hardware plane on
        // Android TV devices even while MediaPlayer is rendering frames.
        // The parent playback surface already supplies the black background.
        _videoView.Click += VideoSurface_OnClick;
        _videoView.Prepared += VideoView_OnPrepared;
        _videoView.PlaybackStateChanged += VideoView_OnPlaybackStateChanged;
        _videoView.Completion += VideoView_OnCompletion;
        _videoView.Error += VideoView_OnError;
        // Stall notifications only arrive through the info listener. Without
        // them a rebuffering member reads as "viewer pressed pause" and
        // watch-together sync would pause the whole party.
        _videoView.SetOnInfoListener(this);
        playbackSurface.AddView(
            _videoView,
            new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent,
                ViewGroup.LayoutParams.MatchParent,
                GravityFlags.Center));

        _loadingIndicator = new ProgressBar(this)
        {
            Indeterminate = true
        };
        playbackSurface.AddView(
            _loadingIndicator,
            new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.WrapContent,
                ViewGroup.LayoutParams.WrapContent,
                GravityFlags.Center));

        _playbackStatus = new TextView(this)
        {
            Gravity = GravityFlags.Center,
            TextSize = 17
        };
        _playbackStatus.SetTextColor(Color.White);
        _playbackStatus.SetPadding(32, 96, 32, 0);
        playbackSurface.AddView(
            _playbackStatus,
            new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent,
                ViewGroup.LayoutParams.WrapContent,
                GravityFlags.Center));

        if (_isTelevision)
        {
            root.AddView(
                playbackSurface,
                new FrameLayout.LayoutParams(
                    ViewGroup.LayoutParams.MatchParent,
                    ViewGroup.LayoutParams.MatchParent));
            root.AddView(
                _toolbar,
                new FrameLayout.LayoutParams(
                    ViewGroup.LayoutParams.MatchParent,
                    ViewGroup.LayoutParams.WrapContent,
                    GravityFlags.Top));

            _playbackControls = CreatePlaybackControls();
            root.AddView(
                _playbackControls,
                new FrameLayout.LayoutParams(
                    ViewGroup.LayoutParams.MatchParent,
                    ViewGroup.LayoutParams.WrapContent,
                    GravityFlags.Bottom));
        }
        else
        {
            _playbackControls = CreatePlaybackControls();
            playbackSurface.AddView(_playbackControls, new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Bottom));
            playerLayout.AddView(
                playbackSurface,
                new LinearLayout.LayoutParams(
                    ViewGroup.LayoutParams.MatchParent,
                    height: 0,
                    weight: 1));
            root.AddView(
                playerLayout,
                new FrameLayout.LayoutParams(
                    ViewGroup.LayoutParams.MatchParent,
                    ViewGroup.LayoutParams.MatchParent));
        }

        _exitFullscreenButton = new Button(this)
        {
            Id = View.GenerateViewId(),
            Text = UiTextCatalog.Get("PlayerExitFullscreen"),
            ContentDescription =
                UiTextCatalog.Get("PlayerExitFullscreen"),
            Visibility = ViewStates.Gone
        };
        _exitFullscreenButton.SetAllCaps(false);
        _exitFullscreenButton.SetTextColor(Color.White);
        _exitFullscreenButton.Background = CreatePlaybackButtonBackground();
        _exitFullscreenButton.Click += FullscreenButton_OnClick;
        var exitFullscreenLayout = new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent,
            ViewGroup.LayoutParams.WrapContent,
            GravityFlags.Top | GravityFlags.Right);
        exitFullscreenLayout.SetMargins(12, 12, 12, 12);
        root.AddView(_exitFullscreenButton, exitFullscreenLayout);

        SetContentView(root);
        ApplicationDiagnosticLog.Information(
            "playback.android-native",
            $"stage=opened; variants={launch.Variants.Count}; " +
            $"statusBarInset={_statusBarInset}; television={_isTelevision}");
        if (_isTelevision)
        {
            ApplyFullscreenSystemUi();
        }
        ShowPlaybackControls(_isTelevision ? _playPauseButton : null, scheduleHide: false);

        ApplyVariant(0);
    }

    public override bool DispatchKeyEvent(KeyEvent? eventArgs)
    {
        // Consume the complete OK press used only to reveal the TV controls.
        // Its key-up must not click the Play button that just received focus.
        if (eventArgs is not null && _focusChangingPress.Consume(
                (int)eventArgs.KeyCode, eventArgs.Action == KeyEventActions.Up))
        {
            return true;
        }
        if (eventArgs is null ||
            eventArgs.Action is not KeyEventActions.Down)
        {
            return base.DispatchKeyEvent(eventArgs);
        }

        if (_isTelevision)
        {
            return DispatchTelevisionKeyEvent(eventArgs) ||
                base.DispatchKeyEvent(eventArgs);
        }

        switch (eventArgs.KeyCode)
        {
            case Keycode.Back:
                OnBackPressed();
                return true;

            case Keycode.DpadCenter:
            case Keycode.Enter:
                if (CurrentFocus is Button or Spinner or SeekBar)
                {
                    return base.DispatchKeyEvent(eventArgs);
                }

                if (eventArgs.RepeatCount == 0)
                {
                    TogglePlayback();
                }

                return true;

            case Keycode.MediaPlayPause:
                if (eventArgs.RepeatCount == 0)
                {
                    TogglePlayback();
                }

                return true;

            case Keycode.MediaPlay:
                StartPlayback();
                return true;

            case Keycode.MediaPause:
                PausePlayback();
                return true;

            case Keycode.DpadLeft:
                if (CurrentFocus is Button or Spinner or SeekBar)
                {
                    return base.DispatchKeyEvent(eventArgs);
                }

                SeekBy(-10_000);
                return true;

            case Keycode.DpadRight:
                if (CurrentFocus is Button or Spinner or SeekBar)
                {
                    return base.DispatchKeyEvent(eventArgs);
                }

                SeekBy(30_000);
                return true;

            case Keycode.DpadUp:
                if (_isFullscreen)
                {
                    _exitFullscreenButton?.RequestFocus();
                }
                else
                {
                    (_variantSpinner as View ?? _fullscreenButton)?.RequestFocus();
                }

                return true;

            case Keycode.DpadDown:
                _videoView?.RequestFocus();
                ShowPlaybackControls();
                return true;

            default:
                return base.DispatchKeyEvent(eventArgs);
        }
    }

    private bool DispatchTelevisionKeyEvent(KeyEvent eventArgs)
    {
        switch (eventArgs.KeyCode)
        {
            case Keycode.DpadCenter:
            case Keycode.Enter:
                if (!_playbackControlsVisible)
                {
                    _focusChangingPress.SuppressUntilRelease((int)eventArgs.KeyCode);
                    ShowPlaybackControls(_playPauseButton);
                    return true;
                }

                if (CurrentFocus is Button or Spinner or SeekBar)
                {
                    if (CurrentFocus is Spinner)
                    {
                        CancelPlaybackControlsHide();
                    }

                    return false;
                }

                if (eventArgs.RepeatCount == 0)
                {
                    TogglePlayback();
                    _focusChangingPress.SuppressUntilRelease((int)eventArgs.KeyCode);
                    ShowPlaybackControls(_playPauseButton);
                }

                return true;

            case Keycode.MediaPlayPause:
                if (eventArgs.RepeatCount == 0)
                {
                    TogglePlayback();
                    ShowPlaybackControls(_playPauseButton);
                }

                return true;

            case Keycode.MediaPlay:
                StartPlayback();
                ShowPlaybackControls(_playPauseButton);
                return true;

            case Keycode.MediaPause:
                PausePlayback();
                ShowPlaybackControls(_playPauseButton, scheduleHide: false);
                return true;

            case Keycode.DpadLeft:
                if (!_playbackControlsVisible ||
                    CurrentFocus == _videoView)
                {
                    SeekBy(-10_000);
                    ShowPlaybackControls();
                    return true;
                }

                SchedulePlaybackControlsHide();
                return false;

            case Keycode.DpadRight:
                if (!_playbackControlsVisible ||
                    CurrentFocus == _videoView)
                {
                    SeekBy(30_000);
                    ShowPlaybackControls();
                    return true;
                }

                SchedulePlaybackControlsHide();
                return false;

            case Keycode.DpadUp:
                if (!_playbackControlsVisible)
                {
                    ShowPlaybackControls(
                        _variantSpinner as View ?? _backButton);
                    return true;
                }

                if (CurrentFocus == _videoView ||
                    CurrentFocus == _rewindButton ||
                    CurrentFocus == _playPauseButton ||
                    CurrentFocus == _forwardButton)
                {
                    ShowPlaybackControls(_seekBar is { Enabled: true } ? _seekBar : _backButton);
                    return true;
                }

                SchedulePlaybackControlsHide();
                return false;

            case Keycode.DpadDown:
                if (CurrentFocus == _seekBar)
                {
                    ShowPlaybackControls(_playPauseButton);
                    return true;
                }
                if (CurrentFocus == _variantSpinner || CurrentFocus == _engineButton)
                {
                    ShowPlaybackControls(_partyButton);
                    return true;
                }
                if (!_playbackControlsVisible ||
                    CurrentFocus?.Parent == _toolbar)
                {
                    ShowPlaybackControls(_playPauseButton);
                    return true;
                }

                if (CurrentFocus == _rewindButton ||
                    CurrentFocus == _playPauseButton ||
                    CurrentFocus == _forwardButton)
                {
                    ShowPlaybackControls(_variantSpinner as View ??
                        (_engineButton is { Enabled: true } ? _engineButton : _partyButton));
                    return true;
                }

                SchedulePlaybackControlsHide();
                return false;

            case Keycode.Menu:
            case Keycode.Info:
                if (eventArgs.RepeatCount == 0) ShowOptionsPanel();
                return true;

            default:
                return false;
        }
    }

    protected override void OnPause()
    {
        ClosePlayerPanel(restoreFocus: false);
        _focusChangingPress.Reset();
        _isActivityResumed = false;
        if (_progressTick is not null) _controlsHandler?.RemoveCallbacks(_progressTick);
        CancelPreparationTimeout();
        CancelPlaybackControlsHide();
        if (_videoView is { } videoView && _isPrepared)
        {
            try
            {
                _resumeAfterPause = videoView.IsPlaying;
                if (_resumeAfterPause)
                {
                    videoView.Pause();
                }

                ReportCurrentState();
            }
            catch (Java.Lang.IllegalStateException)
            {
                _resumeAfterPause = false;
                _isPrepared = false;
            }
        }

        base.OnPause();
    }

    protected override void OnResume()
    {
        base.OnResume();
        _isActivityResumed = true;
        UpdatePlaybackProgress();
        if (!_isPrepared && _launch is not null)
            _ = CheckPreparationTimeoutAsync(_playbackAttemptGeneration);
        if (_resumeAfterPause && _videoView is { } videoView && _isPrepared)
        {
            _resumeAfterPause = false;
            try
            {
                videoView.Start();
                UpdatePlaybackPlayPauseButton();
                SchedulePlaybackControlsHide();
                ReportCurrentState();
            }
            catch (Java.Lang.IllegalStateException)
            {
                _isPrepared = false;
            }
        }
    }

    public override void OnWindowFocusChanged(bool hasFocus)
    {
        base.OnWindowFocusChanged(hasFocus);
        if (!hasFocus) CancelPlaybackControlsHide();
        else SchedulePlaybackControlsHide();
        if (hasFocus && (_isFullscreen || _isTelevision))
        {
            ApplyFullscreenSystemUi();
        }
    }

#pragma warning disable CS0672
    public override void OnBackPressed()
#pragma warning restore CS0672
    {
        if (_playerDialog is not null)
        {
            ClosePlayerPanel();
            return;
        }
        if (_isTelevision && _playbackControlsVisible && IsVideoPlaying())
        {
            HidePlaybackControls();
            return;
        }
        if (_isFullscreen)
        {
            SetFullscreen(false);
            return;
        }

        Finish();
    }

    protected override void OnDestroy()
    {
        DisposePlayerPanels();
        CancelPreparationTimeout();
        _playbackAttemptGeneration++;
        CancelPlaybackControlsHide();
        // Capture state before StopPlayback releases MediaPlayer. Reading
        // CurrentPosition or Duration after that release throws on Android TV
        // and used to turn a normal Back action into an activity crash.
        var finalState = CreateState(isClosed: true);
        _isPrepared = false;
        if (_videoView is { } videoView)
        {
            videoView.Click -= VideoSurface_OnClick;
            videoView.Prepared -= VideoView_OnPrepared;
            videoView.PlaybackStateChanged -= VideoView_OnPlaybackStateChanged;
            videoView.Completion -= VideoView_OnCompletion;
            videoView.Error -= VideoView_OnError;
            videoView.SetOnInfoListener(null);
            try
            {
                videoView.ReleasePlayback();
            }
            catch
            {
                // Android may already have released the underlying MediaPlayer.
            }
        }

        if (_variantSpinner is not null)
        {
            _variantSpinner.ItemSelected -= VariantSpinner_OnItemSelected;
        }

        Window?.ClearFlags(WindowManagerFlags.KeepScreenOn);
        if (_launch is { } launch)
        {
            _platformPlayer?.Detach(launch.Token, finalState);
        }

        if (_seekBar is not null)
        {
            _seekBar.ProgressChanged -= SeekBar_OnProgressChanged;
            _seekBar.StartTrackingTouch -= SeekBar_OnStartTrackingTouch;
            _seekBar.StopTrackingTouch -= SeekBar_OnStopTrackingTouch;
        }
        _controlsHandler?.RemoveCallbacksAndMessages(null);
        _progressTick?.Dispose();
        _hideControls?.Dispose();
        _controlsHandler?.Dispose();
        _progressTick = null;
        _hideControls = null;
        _controlsHandler = null;
        _seekBar = null;
        _elapsedTime = null;
        _durationTime = null;
        _videoView = null;
        _variantSpinner = null;
        if (_engineButton is not null) _engineButton.Click -= EngineButton_OnClick;
        _engineButton = null;
        _loadingIndicator = null;
        _playbackStatus = null;
        if (_fullscreenButton is not null)
        {
            _fullscreenButton.Click -= FullscreenButton_OnClick;
        }

        if (_exitFullscreenButton is not null)
        {
            _exitFullscreenButton.Click -= FullscreenButton_OnClick;
        }
        if (_rewindButton is not null)
        {
            _rewindButton.Click -= RewindButton_OnClick;
        }
        if (_playPauseButton is not null)
        {
            _playPauseButton.Click -= PlayPauseButton_OnClick;
        }
        if (_forwardButton is not null)
        {
            _forwardButton.Click -= ForwardButton_OnClick;
        }
        if (_backButton is not null)
        {
            _backButton.Click -= BackButton_OnClick;
        }

        _toolbar = null;
        _backButton = null;
        _playbackControls = null;
        _rewindButton = null;
        _playPauseButton = null;
        _forwardButton = null;
        _fullscreenButton = null;
        _exitFullscreenButton = null;
        _launch = null;
        _platformPlayer = null;
        base.OnDestroy();
    }

    internal Task<AndroidPlaybackSnapshot> CaptureStateAsync(
        CancellationToken cancellationToken)
    {
        var launch = _launch ?? throw new ObjectDisposedException(nameof(NativePlaybackActivity));
        var completion = new TaskCompletionSource<AndroidPlaybackSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        RunOnUiThread(() =>
        {
            if (_launch is null || IsFinishing || IsDestroyed)
            {
                completion.TrySetException(
                    new ObjectDisposedException(nameof(NativePlaybackActivity)));
                return;
            }

            var state = CreateState(isClosed: false);
            _platformPlayer?.ReportState(launch.Token, state);
            completion.TrySetResult(new AndroidPlaybackSnapshot(launch.Token, state));
        });

        return completion.Task.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Pauses or resumes on behalf of a watch-together party. Shares the
    /// remote-control key paths so programmatic commands and viewer input
    /// keep the on-screen controls in the same state.
    /// </summary>
    internal void RequestSetPlaying(bool playing)
    {
        try
        {
            RunOnUiThread(() =>
            {
                if (_launch is null || IsFinishing || IsDestroyed)
                {
                    return;
                }

                if (playing)
                {
                    StartPlayback();
                }
                else
                {
                    PausePlayback();
                }
            });
        }
        catch (ObjectDisposedException)
        {
            // The activity closed between the weak-reference check and dispatch.
        }
    }

    /// <summary>
    /// Seeks to an absolute position on behalf of a watch-together party.
    /// </summary>
    internal void RequestSeek(double positionSeconds)
    {
        try
        {
            RunOnUiThread(() =>
            {
                if (_launch is null or { IsLive: true } ||
                    IsFinishing ||
                    IsDestroyed ||
                    _videoView is not { } videoView ||
                    !_isPrepared)
                {
                    return;
                }

                try
                {
                    videoView.SeekTo((int)Math.Min(
                        int.MaxValue,
                        Math.Max(0, positionSeconds) * 1000));
                }
                catch (Java.Lang.IllegalStateException)
                {
                    _isPrepared = false;
                    return;
                }

                SchedulePlaybackControlsHide();
                ReportCurrentState();
            });
        }
        catch (ObjectDisposedException)
        {
            // The activity closed between the weak-reference check and dispatch.
        }
    }

    private bool TryGetLaunch(
        out AndroidPlatformMediaPlayer.PlaybackLaunch launch)
    {
        launch = null!;
        var tokenText = Intent?.GetStringExtra(SessionTokenExtra);
        if (!Guid.TryParseExact(tokenText, "N", out var token) ||
            Application is not UmdAndroidApplication application ||
            application.PlatformMediaPlayer is not { } platformPlayer ||
            !platformPlayer.TryAttach(this, token, out launch))
        {
            return false;
        }

        _platformPlayer = platformPlayer;
        return true;
    }

    private void VideoView_OnPrepared(
        object? sender,
        EventArgs eventArgs)
    {
        CancelPreparationTimeout();
        _isPrepared = true;
        _isBuffering = false;
        if (_loadingIndicator is not null)
        {
            _loadingIndicator.Visibility = ViewStates.Gone;
        }
        if (_playbackStatus is not null)
        {
            _playbackStatus.Visibility = ViewStates.Gone;
        }

        if (_pendingSeekPositionSeconds > 0 && _launch?.IsLive is not true &&
            _videoView is { } videoView)
        {
            var milliseconds = (int)Math.Min(
                int.MaxValue,
                _pendingSeekPositionSeconds * 1000);
            videoView.SeekTo(milliseconds);
        }

        _pendingSeekPositionSeconds = 0;
        if (_isActivityResumed) _videoView?.Start();
        else
        {
            _resumeAfterPause = true;
            _videoView?.Pause();
        }
        UpdatePlaybackPlayPauseButton();
        if (_isTelevision && _playbackControlsVisible && CurrentFocus == _videoView)
            _playPauseButton?.RequestFocus();
        SchedulePlaybackControlsHide();
        ReportCurrentState();
    }

    private void VideoView_OnPlaybackStateChanged(object? sender, EventArgs eventArgs)
    {
        if (!_isPrepared || _videoView is null) return;

        UpdatePlaybackPlayPauseButton();
        if (_isActivityResumed && IsVideoPlaying())
            SchedulePlaybackControlsHide();
        else
            CancelPlaybackControlsHide();
        ReportCurrentState();
    }

    private void VideoView_OnCompletion(
        object? sender,
        EventArgs eventArgs)
    {
        _isEnded = true;
        _isBuffering = false;
        UpdatePlaybackPlayPauseButton();
        ShowPlaybackControls(_playPauseButton, scheduleHide: false);
        ReportCurrentState();
    }

    private void VideoView_OnError(
        object? sender,
        PlaybackErrorEventArgs eventArgs)
    {
        CancelPreparationTimeout();
        ApplicationDiagnosticLog.Warning("playback.android-error",
            $"engine={_videoView?.Engine}; variant={_selectedVariantIndex}; error={eventArgs.What}");
        if (TryFallbackEngine() || TryPlayNextVariant())
        {
            eventArgs.Handled = true;
            return;
        }

        _isBuffering = false;
        _errorMessage =
            UiTextCatalog.Format(
                "AndroidPlaybackFailed",
                eventArgs.What);
        if (_loadingIndicator is not null)
        {
            _loadingIndicator.Visibility = ViewStates.Gone;
        }
        if (_playbackStatus is not null)
        {
            _playbackStatus.Text = _errorMessage;
            _playbackStatus.Visibility = ViewStates.Visible;
        }

        eventArgs.Handled = true;
        UpdatePlaybackPlayPauseButton();
        ShowPlaybackControls(_playPauseButton, scheduleHide: false);
        ReportCurrentState();
    }

    public bool OnInfo(
        global::Android.Media.MediaPlayer? mediaPlayer,
        global::Android.Media.MediaInfo what,
        int extra)
    {
        switch (what)
        {
            case global::Android.Media.MediaInfo.BufferingStart:
                _isBuffering = true;
                if (_loadingIndicator is not null) _loadingIndicator.Visibility = ViewStates.Visible;
                CancelPlaybackControlsHide();
                ReportCurrentState();
                return true;

            case global::Android.Media.MediaInfo.BufferingEnd:
                _isBuffering = false;
                if (_loadingIndicator is not null) _loadingIndicator.Visibility = ViewStates.Gone;
                UpdatePlaybackProgress();
                SchedulePlaybackControlsHide();
                ReportCurrentState();
                return true;

            default:
                return false;
        }
    }

    private double _pendingSeekPositionSeconds;

    private void VariantSpinner_OnItemSelected(
        object? sender,
        AdapterView.ItemSelectedEventArgs eventArgs)
    {
        var selectedIndex = (int)eventArgs.Position;
        if (_ignoreVariantSelection ||
            selectedIndex == _selectedVariantIndex)
        {
            return;
        }

        _attemptedVariantIndexes.Clear();
        ApplyVariant(selectedIndex);
        SchedulePlaybackControlsHide();
    }

    private void ApplyVariant(int index, AndroidPlaybackEngine? requestedEngine = null)
    {
        if (_launch is not { } launch ||
            _videoView is not { } videoView ||
            index < 0 ||
            index >= launch.Variants.Count)
        {
            return;
        }

        if (_isPrepared)
        {
            try
            {
                _pendingSeekPositionSeconds =
                    Math.Max(0, videoView.CurrentPosition) / 1000d;
            }
            catch (Java.Lang.IllegalStateException)
            {
                _pendingSeekPositionSeconds = 0;
            }
        }
        else if (_attemptedVariantIndexes.Count == 0)
        {
            _pendingSeekPositionSeconds = launch.StartPositionSeconds;
        }

        if (requestedEngine is null) _attemptedEngines.Clear();
        CancelPreparationTimeout();
        var source = launch.Variants[index].MediaSource;
        var engine = requestedEngine ?? AndroidPlaybackEnginePolicy.Initial(_isTelevision, launch.IsLive, source);
        _attemptedEngines.Add(engine);
        var attempt = ++_playbackAttemptGeneration;
        _selectedVariantIndex = index;
        _attemptedVariantIndexes.Add(index);
        _isPrepared = false;
        _isScrubbing = false;
        _isEnded = false;
        // A freshly set source spends time prebuffering before Prepared
        // fires; sync peers must wait that out rather than read it as a
        // deliberate pause.
        _isBuffering = true;
        _errorMessage = null;

        if (_variantSpinner is not null &&
            _variantSpinner.SelectedItemPosition != index)
        {
            _ignoreVariantSelection = true;
            _variantSpinner.SetSelection(index);
            _ignoreVariantSelection = false;
        }

        var variant = launch.Variants[index];
        if (_loadingIndicator is not null)
        {
            _loadingIndicator.Visibility = ViewStates.Visible;
        }
        if (_playbackStatus is not null)
        {
            _playbackStatus.Text =
                UiTextCatalog.Format("AndroidPlaybackLoading", $"{variant.Label} ({EngineName(engine)})");
            _playbackStatus.Visibility = ViewStates.Visible;
        }
        ShowPlaybackControls(_playPauseButton, scheduleHide: false);

        try
        {
            videoView.SetEngine(engine);
            if (_engineButton is not null)
            {
                _engineButton.Text = EngineName(engine);
                _engineButton.ContentDescription = UiTextCatalog.Format("AndroidPlaybackEngine", EngineName(engine));
                _engineButton.Enabled = AndroidPlaybackEnginePolicy.Fallback(true, source, engine,
                    new HashSet<AndroidPlaybackEngine>()) is not null;
            }
            ApplicationDiagnosticLog.Information("playback.android-open",
                $"engine={engine}; live={launch.IsLive}; host={variant.Source.IdnHost}; clearKeys={source.DecryptionKeys.Count}");
            videoView.SetSource(source, launch.IsLive, variant.Subtitles ?? launch.Subtitles);

            if (_isTelevision)
            {
                _playPauseButton?.RequestFocus();
            }
            else
            {
                videoView.RequestFocus();
            }
            videoView.Start();
            _ = CheckPreparationTimeoutAsync(attempt);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ApplicationDiagnosticLog.Warning("playback.android-start-failed",
                $"engine={engine}; exception={exception.GetType().Name}");
            if (!TryFallbackEngine() && !TryPlayNextVariant())
            {
                _isBuffering = false;
                _errorMessage = source.RequiresClearKeyDecryption
                    ? UiTextCatalog.Get("AndroidPlaybackEncryptionUnsupported")
                    : UiTextCatalog.Format("AndroidPlaybackFailed", $"{variant.Label} / {EngineName(engine)}");
                if (_loadingIndicator is not null)
                {
                    _loadingIndicator.Visibility = ViewStates.Gone;
                }
                if (_playbackStatus is not null)
                {
                    _playbackStatus.Text = _errorMessage;
                    _playbackStatus.Visibility = ViewStates.Visible;
                }

                ReportCurrentState();
            }
        }
    }

    private static string EngineName(AndroidPlaybackEngine engine) =>
        engine == AndroidPlaybackEngine.LibVlc ? "VLC" : "Android";

    private bool TryFallbackEngine()
    {
        if (_launch is not { } launch || _videoView is not { } view) return false;
        var next = AndroidPlaybackEnginePolicy.Fallback(_isTelevision,
            launch.Variants[_selectedVariantIndex].MediaSource, view.Engine, _attemptedEngines);
        if (next is null) return false;
        ApplyVariant(_selectedVariantIndex, next);
        return true;
    }

    private void EngineButton_OnClick(object? sender, EventArgs args)
    {
        _attemptedEngines.Clear();
        _attemptedVariantIndexes.Clear();
        TryFallbackEngine();
    }

    private async Task CheckPreparationTimeoutAsync(int attempt)
    {
        // Start can fail synchronously and open the fallback before returning.
        // Its stale caller must not cancel the replacement player's timer.
        if (attempt != _playbackAttemptGeneration || _isPrepared) return;
        CancelPreparationTimeout();
        var cancellation = new CancellationTokenSource();
        _preparationTimeout = cancellation;
        var token = cancellation.Token;
        try { await Task.Delay(TimeSpan.FromSeconds(20), token); }
        catch (System.OperationCanceledException) { return; }
        RunOnUiThread(() =>
        {
            if (token.IsCancellationRequested || !_isActivityResumed || IsFinishing || IsDestroyed ||
                _launch is null || _isPrepared || attempt != _playbackAttemptGeneration) return;
            VideoView_OnError(this, new PlaybackErrorEventArgs("preparation timed out"));
        });
    }

    private void CancelPreparationTimeout()
    {
        _preparationTimeout?.Cancel();
        _preparationTimeout?.Dispose();
        _preparationTimeout = null;
    }

    private bool TryPlayNextVariant()
    {
        if (_launch is not { } launch)
        {
            return false;
        }

        for (var index = 0; index < launch.Variants.Count; index++)
        {
            if (_attemptedVariantIndexes.Contains(index))
            {
                continue;
            }

            ApplyVariant(index);
            return true;
        }

        return false;
    }

    private void RewindButton_OnClick(object? sender, EventArgs eventArgs)
    {
        SeekBy(-10_000);
        ShowPlaybackControls(_rewindButton);
    }

    private void PlayPauseButton_OnClick(object? sender, EventArgs eventArgs)
    {
        TogglePlayback();
        ShowPlaybackControls(
            _playPauseButton,
            scheduleHide: IsVideoPlaying());
    }

    private void ForwardButton_OnClick(object? sender, EventArgs eventArgs)
    {
        SeekBy(30_000);
        ShowPlaybackControls(_forwardButton);
    }

    private void BackButton_OnClick(object? sender, EventArgs eventArgs)
    {
        ApplicationDiagnosticLog.Information(
            "playback.android-native",
            "control=back; action=clicked");
        Finish();
    }

    private void FullscreenButton_OnClick(
        object? sender,
        EventArgs eventArgs) =>
        SetFullscreen(!_isFullscreen);

    private void SetFullscreen(bool isFullscreen)
    {
        if (_launch?.IsAudio is not false ||
            _toolbar is null ||
            _exitFullscreenButton is null)
        {
            return;
        }

        _isFullscreen = isFullscreen;
        if (_toolbar.LayoutParameters is LinearLayout.LayoutParams toolbarLayout)
        {
            toolbarLayout.TopMargin = isFullscreen ? 0 : _statusBarInset;
            _toolbar.LayoutParameters = toolbarLayout;
        }
        ApplicationDiagnosticLog.Information(
            "playback.android-native",
            $"control=fullscreen; value={isFullscreen}");
        _toolbar.Visibility =
            isFullscreen ? ViewStates.Gone : ViewStates.Visible;
        _exitFullscreenButton.Visibility =
            isFullscreen ? ViewStates.Visible : ViewStates.Gone;
        if (isFullscreen)
        {
            ApplyFullscreenSystemUi();
            _exitFullscreenButton.RequestFocus();
        }
        else if (Window is { } window)
        {
            window.ClearFlags(WindowManagerFlags.Fullscreen);
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                window.InsetsController?.Show(
                    WindowInsets.Type.SystemBars());
            }
            else
            {
                window.DecorView.SystemUiFlags = SystemUiFlags.Visible;
            }

            _fullscreenButton?.RequestFocus();
        }
        ShowPlaybackControls();
    }

    private void ApplyFullscreenSystemUi()
    {
        if (Window is not { } window)
        {
            return;
        }

        window.AddFlags(WindowManagerFlags.Fullscreen);
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            if (window.InsetsController is { } controller)
            {
                controller.SystemBarsBehavior =
                    (int)WindowInsetsControllerBehavior
                        .ShowTransientBarsBySwipe;
                controller.Hide(WindowInsets.Type.SystemBars());
            }
        }
        else
        {
            window.DecorView.SystemUiFlags =
                SystemUiFlags.ImmersiveSticky |
                SystemUiFlags.Fullscreen |
                SystemUiFlags.HideNavigation |
                SystemUiFlags.LayoutFullscreen |
                SystemUiFlags.LayoutHideNavigation |
                SystemUiFlags.LayoutStable;
        }
    }

    private int ResolveStatusBarInset()
    {
        if (_isTelevision || Resources is not { } resources)
        {
            return 0;
        }

        var resourceId = resources.GetIdentifier(
            "status_bar_height",
            "dimen",
            "android");
        return resourceId > 0
            ? resources.GetDimensionPixelSize(resourceId)
            : 0;
    }

    private void TogglePlayback()
    {
        if (IsVideoPlaying())
        {
            PausePlayback();
        }
        else
        {
            StartPlayback();
        }
    }

    private void StartPlayback()
    {
        if (!_isPrepared || _videoView is not { } videoView)
        {
            return;
        }

        if (_isEnded)
        {
            // Reopen the same rendition at zero and clear the ended snapshot.
            // VLC can stay in Ended until its media is opened again.
            _isPrepared = false;
            _pendingSeekPositionSeconds = 0;
            _attemptedEngines.Clear();
            ApplyVariant(_selectedVariantIndex, videoView.Engine);
            return;
        }

        try
        {
            videoView.Start();
        }
        catch (Java.Lang.IllegalStateException)
        {
            _isPrepared = false;
            return;
        }
        ShowPlaybackControls();
        UpdatePlaybackPlayPauseButton();
        SchedulePlaybackControlsHide();
        ReportCurrentState();
    }

    private void PausePlayback()
    {
        if (!_isPrepared || _videoView is not { } videoView)
        {
            return;
        }

        try
        {
            videoView.Pause();
        }
        catch (Java.Lang.IllegalStateException)
        {
            _isPrepared = false;
            return;
        }
        ShowPlaybackControls();
        UpdatePlaybackPlayPauseButton();
        ShowPlaybackControls(_playPauseButton, scheduleHide: false);
        ReportCurrentState();
    }

    private void SeekBy(int milliseconds)
    {
        if (_launch?.IsLive is true ||
            _videoView is not { } videoView ||
            !_isPrepared)
        {
            return;
        }

        try
        {
            var maximum = videoView.Duration > 0
                ? videoView.Duration
                : int.MaxValue;
            videoView.SeekTo((int)Math.Clamp(
                (long)videoView.CurrentPosition + milliseconds,
                0,
                maximum));
        }
        catch (Java.Lang.IllegalStateException)
        {
            _isPrepared = false;
            return;
        }
        ShowPlaybackControls();
        SchedulePlaybackControlsHide();
        ReportCurrentState();
    }

    private void ReportCurrentState()
    {
        if (_launch is { } launch)
        {
            _platformPlayer?.ReportState(launch.Token, CreateState(isClosed: false));
        }
    }

    private PlatformMediaState CreateState(bool isClosed)
    {
        var position = _launch?.StartPositionSeconds ?? 0;
        double? duration = null;
        var isPlaying = false;
        if (_videoView is { } videoView && _isPrepared)
        {
            try
            {
                position = Math.Max(0, videoView.CurrentPosition) / 1000d;
                duration = videoView.Duration > 0
                    ? videoView.Duration / 1000d
                    : null;
                isPlaying = videoView.IsPlaying;
            }
            catch (Java.Lang.IllegalStateException)
            {
                // MediaPlayer may transition to released/error between two
                // lifecycle callbacks. Return the last safe launch position.
            }
        }

        return new PlatformMediaState(
            position,
            duration,
            isPlaying,
            _isEnded,
            isClosed,
            ErrorMessage: _errorMessage,
            IsBuffering: !isClosed && _isBuffering);
    }

    private bool IsVideoPlaying()
    {
        if (_videoView is not { } videoView || !_isPrepared)
        {
            return false;
        }

        try
        {
            return videoView.IsPlaying;
        }
        catch (Java.Lang.IllegalStateException)
        {
            _isPrepared = false;
            return false;
        }
    }

    internal sealed record AndroidPlaybackSnapshot(
        Guid Token,
        PlatformMediaState State);
}
