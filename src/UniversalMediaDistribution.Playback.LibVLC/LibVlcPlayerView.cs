using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Automation;
using Avalonia.Data;
using Avalonia.Layout;
using System.Windows.Input;
using Avalonia.Animation;
using Avalonia.Media;
using Avalonia.Threading;
using LibVLCSharp.Avalonia;
using LibVLCSharp.Shared;
using UniversalMediaDistribution.UI.Diagnostics;
using UniversalMediaDistribution.UI.Localization;
using UniversalMediaDistribution.UI.ViewModels;

namespace UniversalMediaDistribution.Playback.LibVLC;

internal sealed partial class LibVlcPlayerView : UserControl, IDisposable
{
    /// <summary>
    /// Roughly one display frame, so a surface that needs a second layout
    /// cycle is picked up on the next one rather than spun on.
    /// </summary>
    private static readonly TimeSpan SurfacePollInterval =
        TimeSpan.FromMilliseconds(16);

    private readonly MediaPlayer _mediaPlayer;
    private readonly VideoView _videoView;
    private readonly Grid _mediaSurface;
    private readonly Border _audioPlaceholder;
    private readonly TextBlock _title;
    private readonly TextBlock _position;
    private readonly Button _playPause;
    private readonly Slider _seek;
    private readonly Slider _volume;
    private readonly StackPanel _variantPanel;
    private readonly TextBlock _variantLabel;
    private readonly ComboBox _variantPicker;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _controlsHideTimer;
    private readonly PlayerOverlayLifecycle _overlayLifecycle = new();
    private Border? _controls;
    private Popup? _controlsPopup;
    private bool _pointerOverControls;
    private bool _controlsRevealed = true;
    private bool _updatingControls;
    private bool _updatingVariant;
    private bool _disposed;

    /// <summary>
    /// True while a Stop is tearing the current input down on a worker
    /// thread. Every libVLC call is serialized on the player lock, and Stop
    /// holds it for as long as the input takes to die — on a stalled network
    /// stream that is tens of seconds. Any call made from the UI thread in
    /// that window (the refresh timer reading Time was the one caught in the
    /// hang dump) blocks the UI thread for the whole teardown, which is the
    /// freeze reported when switching away from a slow server. While this is
    /// set, everything UI-driven that would enter libVLC simply declines.
    /// </summary>
    private volatile bool _playerAccessSuspended;

    public event EventHandler<int>? VariantSelectionRequested;

    public LibVlcPlayerView(MediaPlayer mediaPlayer)
    {
        _mediaPlayer = mediaPlayer;
        Background = Brush.Parse("#080A0F");
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        ClipToBounds = true;

        _videoView = new LibVlcVideoView
        {
            MediaPlayer = mediaPlayer,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            ClipToBounds = true
        };
        _audioPlaceholder = new Border
        {
            Background = Brush.Parse("#0D1119"),
            IsVisible = false,
            Child = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = UiTextCatalog.Get("PlayerAudio"),
                        Foreground = Brush.Parse("#65D6AD"),
                        FontSize = 13,
                        FontWeight = FontWeight.Bold,
                        HorizontalAlignment = HorizontalAlignment.Center
                    },
                    new TextBlock
                    {
                        Text = UiTextCatalog.Get("StreamingMonoTorrent"),
                        Foreground = Brush.Parse("#8992A8"),
                        FontSize = 12,
                        HorizontalAlignment = HorizontalAlignment.Center
                    }
                }
            }
        };

        _title = new TextBlock
        {
            FontSize = 15,
            Foreground = Brushes.White,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        _position = new TextBlock
        {
            Text = "0:00 / --:--",
            Foreground = Brush.Parse("#A1A9BC"),
            FontSize = 12,
            MinWidth = 92,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        _playPause = CreateButton(UiTextCatalog.Get("PlayerPlay"), 84);
        var rewind = CreateButton("-10s", 54);
        var forward = CreateButton("+30s", 54);
        _seek = new Slider
        {
            Minimum = 0,
            Maximum = 1,
            VerticalAlignment = VerticalAlignment.Center
        };
        _volume = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            Value = mediaPlayer.Volume,
            Width = 90,
            VerticalAlignment = VerticalAlignment.Center
        };
        _variantPicker = new ComboBox
        {
            MinWidth = 180,
            MaxWidth = 300,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        _variantLabel = new TextBlock
        {
            Text = UiTextCatalog.Get("PlayerAudioRelease"),
            Foreground = Brush.Parse("#8992A8"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center
        };
        _variantPanel = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 5,
            Margin = new Thickness(0, 0, 0, 8),
            IsVisible = false,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                _variantLabel,
                _variantPicker
            }
        };

        _playPause.Click += (_, _) => TogglePlayback();
        rewind.Click += (_, _) => SeekBy(-10_000);
        forward.Click += (_, _) => SeekBy(30_000);
        _seek.ValueChanged += (_, _) => ApplySeek();
        _seek.AddHandler(PointerPressedEvent, (_, _) => _isSeeking = true,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        _seek.AddHandler(PointerReleasedEvent, (_, _) => CompleteSeek(),
            RoutingStrategies.Bubble, handledEventsToo: true);
        _seek.PointerCaptureLost += (_, _) => CompleteSeek();
        _volume.ValueChanged += (_, _) => ApplyVolume();
        _variantPicker.SelectionChanged += OnVariantSelectionChanged;
        var fullscreen = CreateButton("⛶", 44);
        fullscreen.Margin = new Thickness(8, 0, 0, 0);
        // Read the command off the view model rather than binding to a string
        // path. A Binding resolves its path by reflection and needs runtime code
        // generation, which a Native AOT build cannot provide (IL3050), so the
        // fullscreen button quietly did nothing there. This project already
        // references the UI, so the type is available and no reflection is
        // involved. DataContext is inherited, so it arrives after construction.
        fullscreen.DataContextChanged += (sender, _) =>
        {
            if (sender is Button button)
            {
                button.Command = (button.DataContext as MainViewModel)
                    ?.TogglePlayerFullscreenCommand;
            }
        };
        ToolTip.SetTip(
            fullscreen,
            UiTextCatalog.Get("PlayerEnterFullscreen"));

        _mediaSurface = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            ClipToBounds = true
        };
        _mediaSurface.SizeChanged += OnMediaSurfaceSizeChanged;
        _mediaSurface.Children.Add(_videoView);
        _mediaSurface.Children.Add(_audioPlaceholder);

        CreateCastingControls();
        CreateAdditionalControls();

        _controls = CreateControls(rewind, forward, fullscreen);
        // The video is a native child window, and on Windows such a window is
        // always painted above Avalonia content: controls drawn over it would
        // be invisible. So the bar keeps its own row and, when it hides, that
        // row shrinks to a slim strip the pointer can find to bring it back.
        // The video is a native child window and always paints above ordinary
        // Avalonia content, so the bar cannot simply be drawn on top of it. A
        // popup is its own top-level window, which does sit above the video —
        // that is what makes an overlay possible here at all.
        _controlsPopup = new Popup
        {
            PlacementTarget = _mediaSurface,
            Placement = PlacementMode.AnchorAndGravity,
            PlacementAnchor = PopupAnchor.Bottom,
            PlacementGravity = PopupGravity.Top,
            // Without this the positioner is free to flip the bar to the top
            // edge when it decides the bottom does not fit, which is exactly
            // what happened in fullscreen. Sliding keeps it at the bottom.
            PlacementConstraintAdjustment =
                PopupPositionerConstraintAdjustment.SlideX |
                PopupPositionerConstraintAdjustment.SlideY,
            IsLightDismissEnabled = false,
            // Clicking a control must not pull focus away from the app, or the
            // keyboard shortcuts stop working.
            Focusable = false,
            Child = _controls
        };
        var root = new Grid
        {
            Background = Brush.Parse("#080A0F")
        };
        root.Children.Add(_mediaSurface);
        root.Children.Add(_controlsPopup);
        Content = root;

        // The popup only covers the bottom band, so hovering there reveals the
        // bar the way a video player is expected to behave. It stays open and
        // fades instead of closing, because a closed popup would stop
        // receiving the pointer that is meant to bring it back.
        _mediaSurface.SizeChanged += (_, eventArgs) =>
        {
            if (_controls is not null)
            {
                _controls.Width = Math.Max(0, eventArgs.NewSize.Width - 32);
            }

            // Entering or leaving fullscreen changes the surface; the popup is
            // a separate window and has to be told to place itself again.
            RepositionControls();
        };

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(400)
        };
        _timer.Tick += OnTimerTick;
        _controlsHideTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _controlsHideTimer.Tick += (_, _) =>
        {
            _controlsHideTimer.Stop();
            HideControls();
        };

        // Any sign of the viewer brings the controls back; pointer movement is
        // the usual one, and a key press covers a remote or keyboard.
        PointerMoved += (_, _) => RevealControls();
        PointerEntered += (_, _) => RevealControls();
        PointerExited += (_, _) =>
        {
            _pointerOverControls = false;
            HideControls();
        };
        // Escape has to work while the video has the screen. The native video
        // surface does not route keys to Avalonia, so the player view takes
        // focus itself and handles the key here rather than relying on the
        // shell seeing it.
        Focusable = true;
        _videoView.Focusable = false;
        KeyDown += OnPlayerKeyDown;
        PointerPressed += (_, e) =>
        {
            RevealControls();
            if (!IsInteractiveSource(e.Source)) Focus();
        };
        AttachedToVisualTree += (_, _) =>
        {
            OnPlayerDataContextChanged(this, EventArgs.Empty);
            _overlayLifecycle.Attach();
            ApplyOverlayVisibility(focusWhenOpening: true);
        };
        DetachedFromVisualTree += (_, _) =>
        {
            if (_playerViewModel is not null) _playerViewModel.PropertyChanged -= OnPlayerModelChanged;
            _isSeeking = false;
            ClosePlayerPanels();
            _overlayLifecycle.Detach();
            ApplyOverlayVisibility();
        };
    }

    /// <summary>
    /// Stops the UI from calling into libVLC until the next media opens.
    /// Safe to call from any thread; the flag is volatile and the guarded
    /// calls all run on the UI thread.
    /// </summary>
    internal void SuspendPlayerAccess() => _playerAccessSuspended = true;

    public void SetMedia(
        string title,
        bool isAudio,
        IReadOnlyList<string> variantLabels,
        int selectedVariantIndex)
    {
        _playerAccessSuspended = false;
        _isSeeking = false;
        _trackMessage.Text = string.Empty;
        _overlayLifecycle.ActivateMedia();
        _title.Text = title;
        _audioPlaceholder.IsVisible = isAudio;
        _videoView.IsVisible = !isAudio;
        _updatingVariant = true;
        _variantPicker.ItemsSource = variantLabels;
        _variantPicker.SelectedIndex = selectedVariantIndex;
        _variantLabel.Text = UiTextCatalog.Get(
            isAudio
                ? "PlayerAudioRelease"
                : "PlayerServerQuality");
        _variantPanel.IsVisible = variantLabels.Count > 1;
        _updatingVariant = false;
        ApplyOverlayVisibility(focusWhenOpening: true);
        RefreshControls();
    }

    public void SetSelectedVariant(int selectedVariantIndex)
    {
        _updatingVariant = true;
        _variantPicker.SelectedIndex = selectedVariantIndex;
        _updatingVariant = false;
    }

    /// <summary>
    /// True once there is somewhere for the video to go. Audio needs no
    /// surface at all, so it is ready immediately; on Windows the only answer
    /// that matters is whether the media player has been handed the child
    /// window, which is exactly what LibVLC checks before deciding to open a
    /// top-level window of its own.
    /// </summary>
    private bool IsVideoSurfaceReady =>
        !_videoView.IsVisible ||
        (OperatingSystem.IsWindows()
            ? _mediaPlayer.Hwnd != IntPtr.Zero
            : _videoView.IsEffectivelyVisible &&
              _videoView.Bounds is { Width: > 0, Height: > 0 });

    /// <summary>
    /// Waits for that surface before playback starts.
    ///
    /// <see cref="VideoView"/> is a native control host: hiding it or
    /// detaching its parent destroys the child window, and the replacement is
    /// created from a later arrange pass. Showing it and calling Play in the
    /// same block leaves Avalonia no opportunity to run that pass, so the
    /// handle is still absent and the video opens in its own desktop window.
    /// Returns false on timeout; the caller must not start a detached player.
    /// </summary>
    internal async ValueTask<bool> WaitForVideoSurfaceAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (IsVideoSurfaceReady)
        {
            return true;
        }

        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Yield below layout and render so the pass that creates the
            // child window actually gets to run before the next check. Asking
            // for that pass explicitly matters when nothing else has
            // invalidated the host: without it the loop can spin out its whole
            // deadline waiting for a layout that was never scheduled, and the
            // video then opens in a window of its own.
            await Dispatcher.UIThread.InvokeAsync(
                () =>
                {
                    _videoView.InvalidateMeasure();
                    _videoView.InvalidateArrange();
                },
                DispatcherPriority.Background);
            if (IsVideoSurfaceReady)
            {
                return true;
            }

            await Task.Delay(SurfacePollInterval, cancellationToken);
        }

        var ready = IsVideoSurfaceReady;
        if (!ready)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
                ApplicationDiagnosticLog.Warning(
                    "playback.libvlc-surface-timeout",
                    $"attached={TopLevel.GetTopLevel(_videoView) is not null}; " +
                    $"visible={_videoView.IsVisible}; " +
                    $"effectiveVisible={_videoView.IsEffectivelyVisible}; " +
                    $"width={_videoView.Bounds.Width}; height={_videoView.Bounds.Height}; " +
                    $"hasHandle={_mediaPlayer.Hwnd != IntPtr.Zero}"));
        }

        return ready;
    }

    public void Reset()
    {
        _isSeeking = false;
        ClosePlayerPanels();
        _overlayLifecycle.DeactivateMedia();
        ApplyOverlayVisibility();
        _title.Text = string.Empty;
        _playPause.Content = _playIcon;
        _position.Text = "0:00 / --:--";
        _updatingControls = true;
        _seek.Maximum = 1;
        _seek.Value = 0;
        _updatingControls = false;
        _updatingVariant = true;
        _variantPicker.ItemsSource = null;
        _variantPicker.SelectedIndex = -1;
        _variantPanel.IsVisible = false;
        _updatingVariant = false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DataContextChanged -= OnPlayerDataContextChanged;
        if (_playerViewModel is not null) _playerViewModel.PropertyChanged -= OnPlayerModelChanged;
        _partyView.DataContext = null;
        _overlayLifecycle.DeactivateMedia();
        _overlayLifecycle.Detach();
        ApplyOverlayVisibility();
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
        _variantPicker.SelectionChanged -= OnVariantSelectionChanged;
        _mediaSurface.SizeChanged -= OnMediaSurfaceSizeChanged;
        _videoView.MediaPlayer = null;
    }

    /// <summary>
    /// Forces the popup to work out its position again after the video surface
    /// moved or resized.
    /// </summary>
    private void RepositionControls()
    {
        if (!_overlayLifecycle.ShouldShowOverlay ||
            _controlsPopup is not { IsOpen: true })
        {
            return;
        }

        Dispatcher.UIThread.Post(
            () =>
            {
                if (_controlsPopup is not { IsOpen: true } popup ||
                    !_overlayLifecycle.ShouldShowOverlay ||
                    _disposed)
                {
                    return;
                }

                popup.IsOpen = false;
                popup.IsOpen = true;
                RevealControls();
            },
            DispatcherPriority.Background);
    }

    private void OnPlayerKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        RevealControls();
        if (eventArgs.Handled || eventArgs.KeyModifiers != KeyModifiers.None)
        {
            return;
        }

        if (eventArgs.Key == Key.Escape && ClosePlayerPanels())
        {
            eventArgs.Handled = true;
            Focus();
            return;
        }

        // Typing a party code or using a slider must keep its normal keys.
        if (eventArgs.Key is not (Key.Escape or Key.F11) && IsInteractiveSource(eventArgs.Source)) return;
        switch (eventArgs.Key)
        {
            case Key.Space:
                TogglePlayback();
                break;
            case Key.Left:
                SeekBy(-10_000);
                break;
            case Key.Right:
                SeekBy(30_000);
                break;
            case Key.M:
                ToggleMute();
                break;
            case Key.Escape when _playerViewModel?.IsPlayerFullscreen is not true:
                return;
            case Key.Escape:
            case Key.F11:
            case Key.F:
                ToggleFullscreen();
                break;
            default:
                return;
        }
        eventArgs.Handled = true;
    }

    private void ToggleFullscreen()
    {
        if ((DataContext as MainViewModel)?.TogglePlayerFullscreenCommand
                is { } command &&
            command.CanExecute(null))
        {
            command.Execute(null);
        }
    }

    /// <summary>
    /// Shows the controls and restarts the countdown that hides them again.
    /// </summary>
    private void RevealControls()
    {
        if (!_overlayLifecycle.ShouldShowOverlay ||
            _controls is null)
        {
            return;
        }

        if (!_controlsRevealed)
        {
            _controlsRevealed = true;
            _controls.Opacity = 1;
            if (_controls.Child is not null)
            {
                _controls.Child.IsHitTestVisible = true;
            }
        }

        _controlsHideTimer.Stop();
        _controlsHideTimer.Start();
    }

    /// <summary>
    /// Hides the controls unless the pointer is resting on them or playback is
    /// paused, where leaving the viewer without a play button would be unkind.
    /// </summary>
    private void HideControls()
    {
        if (!_overlayLifecycle.ShouldShowOverlay ||
            _controls is null ||
            !_controlsRevealed ||
            _pointerOverControls ||
            _isSeeking ||
            _controls.IsKeyboardFocusWithin ||
            _variantPicker.IsDropDownOpen ||
            _partyPanel.IsVisible ||
            _settingsPanel.IsVisible ||
            _castingPanel.IsVisible ||
            _playerAccessSuspended ||
            !_mediaPlayer.IsPlaying)
        {
            return;
        }

        _controlsRevealed = false;
        // Faded out but still present and still hit-testable, so the pointer
        // reaching the bottom of the picture brings it back. Only the buttons
        // inside stop responding, so an invisible bar cannot be clicked by
        // accident.
        _controls.Opacity = 0;
        if (_controls.Child is not null)
        {
            _controls.Child.IsHitTestVisible = false;
        }
    }

    private Border CreateControls(
        Button rewind,
        Button forward,
        Button fullscreen)
    {
        var titlePanel = new StackPanel { Spacing = 3, Children = { _title, _partyStatus } };
        var topRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 12
        };
        topRow.Children.Add(titlePanel);
        Grid.SetColumn(_position, 1);
        topRow.Children.Add(_position);

        // A full-width timeline stays usable when the action rows wrap.
        var transport = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { rewind, _playPause, forward }
        };
        var sound = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { _muteButton, _volume }
        };
        var bottomRow = new WrapPanel
        {
            Children = { transport, sound, _partyButton, _settingsButton, _castButton, fullscreen }
        };
        var controls = new Border
        {
            // Translucent so the picture still reads underneath the bar.
            Background = Brush.Parse("#C010131A"),
            BorderBrush = Brush.Parse("#40242938"),
            // Flush with the bottom edge: any bottom margin lives inside the
            // popup window and would lift the bar off the picture. Only the
            // top corners are rounded, since the bottom ones would show the
            // video through the gap.
            BorderThickness = new Thickness(1, 1, 1, 0),
            CornerRadius = new CornerRadius(14, 14, 0, 0),
            Padding = new Thickness(18, 12),
            Margin = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Bottom,
            Transitions =
            [
                new DoubleTransition
                {
                    Property = OpacityProperty,
                    Duration = TimeSpan.FromMilliseconds(160)
                }
            ],
            Child = new StackPanel
            {
                Spacing = 9,
                Children = { topRow, _seek, bottomRow, _partyPanel, _settingsPanel, _castingPanel }
            }
        };
        controls.KeyDown += OnPlayerKeyDown;
        controls.PointerEntered += (_, _) =>
        {
            _pointerOverControls = true;
            RevealControls();
        };
        // Entering fires once; if the pointer is already inside the band when
        // the bar fades, only movement will bring it back.
        controls.PointerMoved += (_, _) =>
        {
            _pointerOverControls = true;
            RevealControls();
        };
        controls.PointerExited += (_, _) =>
        {
            _pointerOverControls = false;
            RevealControls();
        };
        return controls;
    }

    private static Button CreateButton(string text, double width) => new()
    {
        Content = text,
        MinWidth = width,
        Padding = new Thickness(10, 6),
        Margin = new Thickness(0, 0, 7, 0)
    };

    private void ApplyOverlayVisibility(bool focusWhenOpening = false)
    {
        var shouldShow =
            !_disposed && _overlayLifecycle.ShouldShowOverlay;
        if (!shouldShow)
        {
            _timer.Stop();
            _controlsHideTimer.Stop();
            _pointerOverControls = false;
            _controlsRevealed = false;
            ClosePlayerPanels();
            if (_controlsPopup is not null)
            {
                _controlsPopup.IsOpen = false;
            }

            return;
        }

        _timer.Start();
        if (_controlsPopup is not null)
        {
            _controlsPopup.IsOpen = true;
        }

        RevealControls();
        if (focusWhenOpening)
        {
            Focus();
        }
    }

    private void OnTimerTick(object? sender, EventArgs eventArgs) => RefreshControls();

    private void OnMediaSurfaceSizeChanged(
        object? sender,
        SizeChangedEventArgs eventArgs)
    {
        if (_disposed ||
            eventArgs.NewSize.Width <= 0 ||
            eventArgs.NewSize.Height <= 0)
        {
            return;
        }

        // VideoView hosts a native child window. Giving that host the current
        // media-row dimensions explicitly keeps it synchronized with Avalonia
        // while the application window is resized.
        _videoView.Width = eventArgs.NewSize.Width;
        _videoView.Height = eventArgs.NewSize.Height;
        _partyPanel.MaxHeight = Math.Max(100, Math.Min(320, eventArgs.NewSize.Height * 0.45));
        _videoView.InvalidateMeasure();
        _videoView.InvalidateArrange();
        if (!_playerAccessSuspended)
        {
            _mediaPlayer.Scale = 0;
            _mediaPlayer.AspectRatio = null;
        }
    }

    private void RefreshControls()
    {
        if (_disposed || _playerAccessSuspended)
        {
            return;
        }

        var current = Math.Max(0, _mediaPlayer.Time);
        var duration = Math.Max(0, _mediaPlayer.Length);
        _updatingControls = true;
        _seek.Maximum = Math.Max(1, duration);
        _seek.IsEnabled = duration > 0 && _mediaPlayer.IsSeekable;
        if (!_isSeeking) _seek.Value = Math.Min(current, _seek.Maximum);
        _position.Text = $"{FormatTime(_isSeeking ? (long)_seek.Value : current)} / {(duration > 0 ? FormatTime(duration) : "--:--")}";
        _volume.Value = Math.Clamp(_mediaPlayer.Volume, 0, 100);
        var muteLabel = UiTextCatalog.Get(_mediaPlayer.Mute ? "PlayerUnmuteAction" : "PlayerMuteAction");
        ToolTip.SetTip(_muteButton, muteLabel);
        AutomationProperties.SetName(_muteButton, muteLabel);
        _muteButton.Opacity = _mediaPlayer.Mute ? 0.5 : 1;
        if (!_mediaPlayer.IsPlaying)
        {
            RevealControls();
        }

        _playPause.Content = _mediaPlayer.IsPlaying ? _pauseIcon : _playIcon;
        var playLabel = UiTextCatalog.Get(_mediaPlayer.IsPlaying ? "PlayerPause" : "PlayerPlay");
        ToolTip.SetTip(_playPause, playLabel);
        AutomationProperties.SetName(_playPause, playLabel);
        _updatingControls = false;
        if (_settingsPanel.IsVisible && !_audioTracks.IsDropDownOpen && !_subtitleTracks.IsDropDownOpen)
            RefreshTrackControls();
    }

    private void TogglePlayback()
    {
        if (_playerAccessSuspended)
        {
            return;
        }

        if (_mediaPlayer.IsPlaying)
        {
            _mediaPlayer.Pause();
        }
        else
        {
            _mediaPlayer.Play();
        }

        RefreshControls();
    }

    private void SeekBy(long milliseconds)
    {
        if (_playerAccessSuspended || !_mediaPlayer.IsSeekable || _mediaPlayer.Length <= 0)
        {
            return;
        }

        _mediaPlayer.Time = Math.Clamp(
            _mediaPlayer.Time + milliseconds,
            0,
            _mediaPlayer.Length);
        RefreshControls();
    }

    private void ApplySeek()
    {
        if (!_updatingControls &&
            !_playerAccessSuspended &&
            !_isSeeking &&
            _mediaPlayer.IsSeekable &&
            _mediaPlayer.Length > 0)
        {
            _mediaPlayer.Time = (long)_seek.Value;
        }
    }

    private void ApplyVolume()
    {
        if (!_updatingControls && !_playerAccessSuspended)
        {
            _mediaPlayer.Volume = (int)_volume.Value;
        }
    }

    private void OnVariantSelectionChanged(object? sender, SelectionChangedEventArgs eventArgs)
    {
        if (!_updatingVariant && _variantPicker.SelectedIndex >= 0)
        {
            VariantSelectionRequested?.Invoke(this, _variantPicker.SelectedIndex);
        }
    }

    private static string FormatTime(long milliseconds)
    {
        var time = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Minutes}:{time.Seconds:00}";
    }
}
