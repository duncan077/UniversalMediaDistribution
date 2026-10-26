using ColorStateList = Android.Content.Res.ColorStateList;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;
using UniversalMediaDistribution.Mobile;
using UniversalMediaDistribution.UI.Localization;

namespace UniversalMediaDistribution.Android;

internal sealed partial class NativePlaybackActivity
{
    private int Dp(int value) => (int)Math.Round(value * (Resources?.DisplayMetrics?.Density ?? 1));

    private LinearLayout CreatePlaybackControls()
    {
        // Native shapes only: no large artwork, blur layers or animation buffers.
        var controls = new LinearLayout(this)
        {
            Orientation = Orientation.Vertical,
            Background = new GradientDrawable(GradientDrawable.Orientation.TopBottom,
                [Color.Argb(170, 10, 14, 22).ToArgb(), Color.Argb(248, 10, 14, 22).ToArgb()])
        };
        controls.SetPadding(Dp(_isTelevision ? 28 : 16), Dp(12), Dp(_isTelevision ? 28 : 16),
            Dp(_isTelevision ? 20 : 24));

        var timeline = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        timeline.SetGravity(GravityFlags.CenterVertical);
        _elapsedTime = CreateTimeLabel(GravityFlags.Left);
        _durationTime = CreateTimeLabel(GravityFlags.Right);
        _seekBar = new SeekBar(this)
        {
            Id = View.GenerateViewId(),
            Max = NativePlaybackTimeline.MaximumProgress,
            ContentDescription = UiTextCatalog.Get("PlayerSeekPosition"),
            ProgressTintList = ColorStateList.ValueOf(Color.Rgb(137, 171, 255)),
            ThumbTintList = ColorStateList.ValueOf(Color.White),
            Focusable = true,
            Enabled = false,
            Visibility = _launch?.IsLive is true ? ViewStates.Gone : ViewStates.Visible
        };
        _seekBar.KeyProgressIncrement = 10;
        _seekBar.ProgressChanged += SeekBar_OnProgressChanged;
        _seekBar.StartTrackingTouch += SeekBar_OnStartTrackingTouch;
        _seekBar.StopTrackingTouch += SeekBar_OnStopTrackingTouch;
        timeline.AddView(_elapsedTime, new LinearLayout.LayoutParams(Dp(72), Dp(48)));
        timeline.AddView(_seekBar, new LinearLayout.LayoutParams(0, Dp(48), 1));
        timeline.AddView(_durationTime, new LinearLayout.LayoutParams(
            _launch?.IsLive is true ? 0 : Dp(72), Dp(48), _launch?.IsLive is true ? 1 : 0));
        controls.AddView(timeline);

        var transport = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        transport.SetGravity(GravityFlags.Center);
        _rewindButton = CreatePlaybackControlButton("−10 s", RewindButton_OnClick, _isTelevision ? 112 : 72);
        _rewindButton.ContentDescription = UiTextCatalog.Get("PlayerRewind10");
        _playPauseButton = CreatePlaybackControlButton(UiTextCatalog.Get("PlayerPlay"),
            PlayPauseButton_OnClick, _isTelevision ? 136 : 104);
        _playPauseButton.Background = CreatePlaybackButtonBackground(primary: true);
        _forwardButton = CreatePlaybackControlButton("+30 s", ForwardButton_OnClick, _isTelevision ? 112 : 72);
        _forwardButton.ContentDescription = UiTextCatalog.Get("PlayerForward30");
        var live = _launch?.IsLive is true;
        _rewindButton.Visibility = _forwardButton.Visibility = live ? ViewStates.Gone : ViewStates.Visible;
        foreach (var button in new[] { _rewindButton, _playPauseButton, _forwardButton })
        {
            var layout = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, Dp(52));
            layout.SetMargins(Dp(4), 0, Dp(4), 0);
            transport.AddView(button, layout);
        }
        controls.AddView(transport);

        var sources = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        sources.SetGravity(GravityFlags.CenterVertical);
        sources.SetPadding(0, Dp(10), 0, 0);
        if (_variantSpinner is { } spinner)
        {
            sources.AddView(spinner, new LinearLayout.LayoutParams(0, Dp(48), 1));
            spinner.NextFocusUpId = _playPauseButton.Id;
            spinner.NextFocusRightId = _engineButton!.Id;
        }
        else
        {
            var source = CreateTimeLabel(GravityFlags.Left);
            source.Text = _launch?.Variants.FirstOrDefault()?.Label;
            source.Ellipsize = global::Android.Text.TextUtils.TruncateAt.End;
            sources.AddView(source, new LinearLayout.LayoutParams(0, Dp(48), 1));
        }
        if (_engineButton is { } engine)
        {
            sources.AddView(engine, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, Dp(48)));
            engine.NextFocusUpId = _playPauseButton.Id;
            engine.NextFocusLeftId = _variantSpinner?.Id ?? _playPauseButton.Id;
        }
        controls.AddView(sources);
        controls.AddView(CreatePlayerActions());
        _partyStatusLabel = CreateTimeLabel(GravityFlags.Center);
        _partyStatusLabel.SetSingleLine(false);
        _partyStatusLabel.SetMaxLines(2);
        controls.AddView(_partyStatusLabel);
        RefreshPlayerActions();

        if (_isTelevision)
        {
            var hint = CreateTimeLabel(GravityFlags.Center);
            hint.Text = UiTextCatalog.Get(live ? "PlayerTvLiveRemoteHint" : "PlayerTvRemoteHint");
            hint.TextSize = 12;
            hint.SetPadding(0, Dp(6), 0, 0);
            controls.AddView(hint);
        }

        _rewindButton.NextFocusLeftId = _rewindButton.Id;
        _rewindButton.NextFocusRightId = _playPauseButton.Id;
        _playPauseButton.NextFocusLeftId = live ? _playPauseButton.Id : _rewindButton.Id;
        _playPauseButton.NextFocusRightId = live ? _playPauseButton.Id : _forwardButton.Id;
        _forwardButton.NextFocusLeftId = _playPauseButton.Id;
        _forwardButton.NextFocusRightId = _forwardButton.Id;
        foreach (var button in new[] { _rewindButton, _playPauseButton, _forwardButton })
        {
            button.NextFocusUpId = live ? _backButton!.Id : _seekBar.Id;
            button.NextFocusDownId = _variantSpinner?.Id ?? _engineButton!.Id;
        }
        _seekBar.NextFocusUpId = _backButton!.Id;
        _seekBar.NextFocusDownId = _playPauseButton.Id;
        _backButton.NextFocusDownId = live ? _playPauseButton.Id : _seekBar.Id;
        _videoView!.NextFocusUpId = _backButton.Id;
        _videoView.NextFocusDownId = _playPauseButton.Id;
        return controls;
    }

    private TextView CreateTimeLabel(GravityFlags gravity)
    {
        var label = new TextView(this)
        {
            TextSize = _isTelevision ? 16 : 13,
            Gravity = gravity | GravityFlags.CenterVertical
        };
        label.SetSingleLine(true);
        label.SetTextColor(Color.Rgb(211, 221, 240));
        return label;
    }

    private Button CreatePlaybackControlButton(string text, EventHandler clickHandler, int minimumWidth = 112)
    {
        var button = new Button(this)
        {
            Id = View.GenerateViewId(),
            Text = text,
            ContentDescription = text,
            TextSize = _isTelevision ? 16 : 14,
            Focusable = true
        };
        button.SetMinimumWidth(Dp(minimumWidth));
        button.SetMinimumHeight(Dp(48));
        button.SetAllCaps(false);
        button.SetTextColor(new ColorStateList(
            [[-global::Android.Resource.Attribute.StateEnabled], []],
            [Color.Rgb(125, 134, 152).ToArgb(), Color.White.ToArgb()]));
        button.SetPadding(Dp(12), 0, Dp(12), 0);
        button.Background = CreatePlaybackButtonBackground();
        button.Click += clickHandler;
        return button;
    }

    private StateListDrawable CreatePlaybackButtonBackground(bool primary = false)
    {
        var backgrounds = new StateListDrawable();
        backgrounds.AddState([-global::Android.Resource.Attribute.StateEnabled],
            CreatePlaybackButtonShape(Color.Rgb(28, 34, 46), Color.Transparent, 0));
        backgrounds.AddState([global::Android.Resource.Attribute.StatePressed],
            CreatePlaybackButtonShape(Color.Rgb(84, 121, 214), Color.White, 2));
        backgrounds.AddState([global::Android.Resource.Attribute.StateFocused],
            CreatePlaybackButtonShape(Color.Rgb(52, 81, 149), Color.White, 3));
        backgrounds.AddState([], CreatePlaybackButtonShape(
            primary ? Color.Rgb(48, 81, 168) : Color.Rgb(32, 41, 59), Color.Transparent, 0));
        return backgrounds;
    }

    private GradientDrawable CreatePlaybackButtonShape(Color fill, Color stroke, int strokeWidth)
    {
        var shape = new GradientDrawable();
        shape.SetColor(fill);
        shape.SetCornerRadius(Dp(12));
        if (strokeWidth > 0) shape.SetStroke(Dp(strokeWidth), stroke);
        return shape;
    }

    private void VideoSurface_OnClick(object? sender, EventArgs args)
    {
        if (_playbackControlsVisible && IsVideoPlaying()) HidePlaybackControls();
        else ShowPlaybackControls();
    }

    private void ShowPlaybackControls(View? preferredFocus = null, bool scheduleHide = true)
    {
        if (_toolbar is null || _playbackControls is null) return;
        CancelPlaybackControlsHide();
        _toolbar.Visibility = _isFullscreen ? ViewStates.Gone : ViewStates.Visible;
        _playbackControls.Visibility = ViewStates.Visible;
        _playbackControlsVisible = true;
        if (_exitFullscreenButton is not null)
            _exitFullscreenButton.Visibility = _isFullscreen ? ViewStates.Visible : ViewStates.Gone;
        UpdatePlaybackPlayPauseButton();
        if (_isTelevision && preferredFocus is not null)
            (preferredFocus.Enabled && preferredFocus.Visibility == ViewStates.Visible
                ? preferredFocus : _videoView)?.RequestFocus();
        if (scheduleHide) SchedulePlaybackControlsHide();
    }

    private void SchedulePlaybackControlsHide()
    {
        CancelPlaybackControlsHide();
        if (_launch?.IsAudio is true || !_isActivityResumed || !_playbackControlsVisible || !_isPrepared || _isScrubbing ||
            _playerDialog is not null ||
            !IsVideoPlaying() || _hideControls is null) return;
        _controlsHandler?.PostDelayed(_hideControls, (long)ControlsHideDelay.TotalMilliseconds);
    }

    private void CancelPlaybackControlsHide()
    {
        if (_hideControls is not null) _controlsHandler?.RemoveCallbacks(_hideControls);
    }

    private void HidePlaybackControls()
    {
        if (_launch?.IsAudio is true || _toolbar is null || _playbackControls is null ||
            _playerDialog is not null ||
            _isScrubbing || !IsVideoPlaying()) return;
        CancelPlaybackControlsHide();
        if (_isTelevision || _isFullscreen) _toolbar.Visibility = ViewStates.Gone;
        _playbackControls.Visibility = ViewStates.Gone;
        _playbackControlsVisible = false;
        if (_exitFullscreenButton is not null) _exitFullscreenButton.Visibility = ViewStates.Gone;
        if (_progressTick is not null) _controlsHandler?.RemoveCallbacks(_progressTick);
        if (_isTelevision) _videoView?.RequestFocus();
        if (_isTelevision || _isFullscreen) ApplyFullscreenSystemUi();
    }

    private void UpdatePlaybackPlayPauseButton()
    {
        if (_playPauseButton is null) return;
        _playPauseButton.Text = UiTextCatalog.Get(_isEnded ? "PlayerReplay" :
            IsVideoPlaying() ? "PlayerPause" : "PlayerPlay");
        _playPauseButton.ContentDescription = _playPauseButton.Text;
        _playPauseButton.Enabled = _isPrepared;
        UpdatePlaybackProgress();
    }

    private void UpdatePlaybackProgress()
    {
        if (_progressTick is not null) _controlsHandler?.RemoveCallbacks(_progressTick);
        if (IsFinishing || IsDestroyed || !_playbackControlsVisible || _seekBar is null) return;
        var live = _launch?.IsLive is true;
        var duration = 0;
        var position = 0;
        if (_isPrepared && _videoView is { } view)
        {
            try { duration = view.Duration; position = view.CurrentPosition; }
            catch (Java.Lang.IllegalStateException) { /* Surface is changing engines. */ }
        }
        var seekable = _isPrepared && !live && duration > 0 && _videoView?.CanSeek is true;
        _seekBar.Enabled = seekable;
        if (_rewindButton is not null) _rewindButton.Enabled = seekable;
        if (_forwardButton is not null) _forwardButton.Enabled = seekable;
        // Never send the remote to a disabled timeline while a source loads,
        // or to the hidden Back button in phone fullscreen mode.
        var upperTarget = _isFullscreen ? _exitFullscreenButton?.Id : _backButton?.Id;
        _seekBar.NextFocusUpId = upperTarget ?? _seekBar.Id;
        if (_exitFullscreenButton is not null)
            _exitFullscreenButton.NextFocusDownId = seekable ? _seekBar.Id : _playPauseButton!.Id;
        foreach (var button in new[] { _rewindButton, _playPauseButton, _forwardButton })
            if (button is not null) button.NextFocusUpId = seekable ? _seekBar.Id : upperTarget ?? button.Id;
        if (_backButton is not null) _backButton.NextFocusDownId = seekable ? _seekBar.Id : _playPauseButton!.Id;
        if (_playPauseButton is not null)
        {
            _playPauseButton.NextFocusLeftId = seekable ? _rewindButton!.Id : _playPauseButton.Id;
            _playPauseButton.NextFocusRightId = seekable ? _forwardButton!.Id : _playPauseButton.Id;
        }
        var sourceTarget = _variantSpinner is { Enabled: true } ? _variantSpinner.Id :
            _engineButton is { Enabled: true } ? _engineButton.Id :
            _playPauseButton is { Enabled: true } ? _playPauseButton.Id : upperTarget;
        foreach (var button in new[] { _partyButton, _optionsButton, _muteButton })
            if (button is not null) button.NextFocusUpId = sourceTarget ?? button.Id;
        if (!_isScrubbing)
        {
            _seekBar.Progress = NativePlaybackTimeline.ToProgress(position, duration);
            if (_elapsedTime is not null) _elapsedTime.Text = live ? "" : NativePlaybackTimeline.FormatTime(position);
        }
        if (_durationTime is not null)
            _durationTime.Text = live ? "● " + UiTextCatalog.Get("PlayerLive") :
                duration > 0 ? NativePlaybackTimeline.FormatTime(duration) : "—:—";
        if (_isActivityResumed && (!_isPrepared || IsVideoPlaying() || _isBuffering) && _progressTick is not null)
            _controlsHandler?.PostDelayed(_progressTick, 1000);
    }

    private void SeekBar_OnStartTrackingTouch(object? sender, SeekBar.StartTrackingTouchEventArgs args)
    {
        _isScrubbing = true;
        CancelPlaybackControlsHide();
    }

    private void SeekBar_OnProgressChanged(object? sender, SeekBar.ProgressChangedEventArgs args)
    {
        if (!args.FromUser || _videoView is not { } view || !_isPrepared) return;
        try
        {
            var position = NativePlaybackTimeline.ToMilliseconds(args.Progress, view.Duration);
            if (_elapsedTime is not null) _elapsedTime.Text = NativePlaybackTimeline.FormatTime(position);
            // Touch previews until release. Remote arrows commit each step.
            if (!_isScrubbing) CommitSeek(position);
        }
        catch (Java.Lang.IllegalStateException) { }
        if (!_isScrubbing) SchedulePlaybackControlsHide();
    }

    private void SeekBar_OnStopTrackingTouch(object? sender, SeekBar.StopTrackingTouchEventArgs args)
    {
        _isScrubbing = false;
        if (_videoView is { } view && _isPrepared && _seekBar is { Enabled: true } seekBar)
        {
            try { CommitSeek(NativePlaybackTimeline.ToMilliseconds(seekBar.Progress, view.Duration)); }
            catch (Java.Lang.IllegalStateException) { }
        }
        SchedulePlaybackControlsHide();
    }

    private void CommitSeek(int milliseconds)
    {
        if (_launch?.IsLive is true || _videoView?.CanSeek is not true || !_isPrepared) return;
        _videoView.SeekTo(milliseconds);
        ReportCurrentState();
    }
}
