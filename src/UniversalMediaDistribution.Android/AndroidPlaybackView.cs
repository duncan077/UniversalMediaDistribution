using Android.Content;
using Android.Views;
using Android.Widget;
using LibVLCSharp.Shared;
using UniversalMediaDistribution.Mobile;
using UniversalMediaDistribution.UI.Playback;
using VlcVideoView = LibVLCSharp.Platforms.Android.VideoView;
using VlcPlayer = LibVLCSharp.Shared.MediaPlayer;
using SystemPlayer = Android.Media.MediaPlayer;

namespace UniversalMediaDistribution.Android;

/// <summary>
/// Keeps the existing TV controls while switching the decoding surface. VLC
/// is initialized only when selected, and its callbacks are marshalled to UI.
/// </summary>
internal sealed class AndroidPlaybackView : FrameLayout, SystemPlayer.IOnPreparedListener
{
    private readonly VideoView _systemView;
    private SystemPlayer? _systemPlayer;
    public bool IsMuted { get; private set; }
    private VlcVideoView? _vlcView;
    private LibVLC? _libVlc;
    private VlcPlayer? _vlcPlayer;
    private Media? _media;
    private SystemPlayer.IOnInfoListener? _infoListener;
    private int _generation;
    private bool _vlcPrepared;
    private bool _released;

    public AndroidPlaybackView(Context context) : base(context)
    {
        _systemView = new VideoView(context);
        AddView(_systemView, SurfaceLayout());
        _systemView.SetOnPreparedListener(this);
        _systemView.Completion += (_, _) => { if (Engine == AndroidPlaybackEngine.System) Completion?.Invoke(this, EventArgs.Empty); };
        _systemView.Error += (_, args) =>
        {
            args.Handled = true;
            if (Engine == AndroidPlaybackEngine.System)
                Error?.Invoke(this, new PlaybackErrorEventArgs($"{args.What}/{args.Extra}"));
        };
    }

    public event EventHandler? Prepared;

    public void OnPrepared(SystemPlayer? player)
    {
        if (_released || Engine != AndroidPlaybackEngine.System) return;
        _systemPlayer = player;
        SetMuted(IsMuted);
        Prepared?.Invoke(this, EventArgs.Empty);
    }
    // Route video-surface taps to the shared overlay, including when the VLC
    // child surface would otherwise consume them. Controls are sibling views.
    public override bool OnInterceptTouchEvent(MotionEvent? ev) => true;

    public event EventHandler? PlaybackStateChanged;
    public event EventHandler? Completion;
    public event EventHandler<PlaybackErrorEventArgs>? Error;
    public AndroidPlaybackEngine Engine { get; private set; }
    public int CurrentPosition => Engine == AndroidPlaybackEngine.System ? _systemView.CurrentPosition : (int)Math.Clamp(_vlcPlayer?.Time ?? 0, 0, int.MaxValue);
    public int Duration => Engine == AndroidPlaybackEngine.System ? _systemView.Duration : (int)Math.Clamp(_vlcPlayer?.Length ?? 0, 0, int.MaxValue);
    public bool CanSeek => Engine == AndroidPlaybackEngine.System
        ? Duration > 0 : _vlcPlayer?.IsSeekable is true;
    public bool IsPlaying => Engine == AndroidPlaybackEngine.System ? _systemView.IsPlaying : _vlcPlayer?.IsPlaying is true;

    public void SetEngine(AndroidPlaybackEngine engine)
    {
        StopPlayback();
        Engine = engine;
        _systemView.Visibility = engine == AndroidPlaybackEngine.System ? ViewStates.Visible : ViewStates.Gone;
        if (_vlcView is not null) _vlcView.Visibility = engine == AndroidPlaybackEngine.LibVlc ? ViewStates.Visible : ViewStates.Gone;
    }

    public void SetSource(PlatformMediaSource source, bool live, IReadOnlyList<PlatformSubtitleTrack>? subtitles = null)
    {
        var uri = source.PlaybackUri ?? throw new NotSupportedException("A streaming URI is required.");
        if (Engine == AndroidPlaybackEngine.System)
        {
            if (source.RequiresClearKeyDecryption)
                throw new NotSupportedException("The Android system player has no ClearKey session configured.");
            var headers = new Dictionary<string, string>();
            if (source.Referrer is { } referrer) headers["Referer"] = referrer.AbsoluteUri;
            if (source.UserAgent is { } userAgent) headers["User-Agent"] = userAgent;
            if (source.HttpOrigin is { } origin) headers["Origin"] = origin.GetLeftPart(UriPartial.Authority);
            _systemView.SetVideoURI(global::Android.Net.Uri.Parse(uri.AbsoluteUri), headers);
            return;
        }

        var options = AndroidPlaybackEnginePolicy.LibVlcOptions(source, live);
        if (_libVlc is null)
        {
            Core.Initialize();
            _libVlc = new LibVLC("--no-video-title-show");
        }
        if (_vlcView is null)
        {
            _vlcView = new VlcVideoView(Context!) { Focusable = false };
            AddView(_vlcView, SurfaceLayout());
        }
        _vlcView.Visibility = ViewStates.Visible;
        var player = new VlcPlayer(_libVlc);
        player.Mute = IsMuted;
        _vlcPlayer = player;
        var generation = _generation;
        void OnUi(Action action) => Post(() =>
        {
            if (!_released && generation == _generation && ReferenceEquals(player, _vlcPlayer)) action();
        });

        var lastBufferingState = -1;
        player.Playing += (_, _) =>
        {
            Interlocked.Exchange(ref lastBufferingState, 0);
            OnUi(() =>
            {
                _infoListener?.OnInfo(null, global::Android.Media.MediaInfo.BufferingEnd, 0);
                if (!_vlcPrepared)
                {
                    _vlcPrepared = true;
                    Prepared?.Invoke(this, EventArgs.Empty);
                }
                // Play() returns before IsPlaying changes. Notify on every resume,
                // not just the initial preparation, so controls reflect real state.
                PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
            });
        };
        player.Paused += (_, _) => OnUi(() => PlaybackStateChanged?.Invoke(this, EventArgs.Empty));
        player.Buffering += (_, args) =>
        {
            var buffering = args.Cache < 100 ? 1 : 0;
            if (Interlocked.Exchange(ref lastBufferingState, buffering) == buffering) return;
            OnUi(() => _infoListener?.OnInfo(null,
                buffering == 1 ? global::Android.Media.MediaInfo.BufferingStart : global::Android.Media.MediaInfo.BufferingEnd, 0));
        };
        player.EndReached += (_, _) => OnUi(() => Completion?.Invoke(this, EventArgs.Empty));
        player.EncounteredError += (_, _) => OnUi(() => Error?.Invoke(this, new PlaybackErrorEventArgs("LibVLC")));
        _media = new Media(_libVlc, uri);
        foreach (var option in options) _media.AddOption(option);
        foreach (var subtitle in subtitles ?? [])
            _media.AddSlave(MediaSlaveType.Subtitle, subtitle.IsDefault ? 4u : 2u, subtitle.Source);
        player.Media = _media;
        _vlcView.MediaPlayer = player;
    }

    public void Start()
    {
        if (Engine == AndroidPlaybackEngine.System) _systemView.Start();
        else if (_vlcPlayer is { IsPlaying: false } player && !player.Play())
            Error?.Invoke(this, new PlaybackErrorEventArgs("LibVLC refused playback"));
    }

    public void Pause()
    {
        if (Engine == AndroidPlaybackEngine.System) _systemView.Pause();
        else _vlcPlayer?.SetPause(true);
    }

    public void SetMuted(bool muted)
    {
        IsMuted = muted;
        if (_vlcPlayer is not null) _vlcPlayer.Mute = muted;
        try { _systemPlayer?.SetVolume(muted ? 0 : 1, muted ? 0 : 1); }
        catch (Java.Lang.IllegalStateException) { }
    }

    public IReadOnlyList<NativePlaybackTrack> GetTracks(bool subtitles)
    {
        if (Engine == AndroidPlaybackEngine.LibVlc && _vlcPlayer is { } vlc)
        {
            var selected = subtitles ? vlc.Spu : vlc.AudioTrack;
            var descriptions = subtitles ? vlc.SpuDescription : vlc.AudioTrackDescription;
            return descriptions.Select(t => new NativePlaybackTrack(t.Id,
                t.Id == -1 ? UI.Localization.UiTextCatalog.Get("PlayerTrackNone") : t.Name,
                t.Id == selected)).ToArray();
        }
        if (_systemPlayer is not { } player) return [];
        var tracks = player.GetTrackInfo() ?? [];
        var result = new List<NativePlaybackTrack>();
        var hasSelected = false;
        for (var index = 0; index < tracks.Length; index++)
        {
            var track = tracks[index];
            if (track is null) continue;
            var isSubtitle = track.TrackType is global::Android.Media.MediaTrackType.Subtitle or global::Android.Media.MediaTrackType.Timedtext;
            if (subtitles ? !isSubtitle : track.TrackType != global::Android.Media.MediaTrackType.Audio) continue;
            var selected = player.GetSelectedTrack(track.TrackType) == index;
            hasSelected |= selected;
            result.Add(new NativePlaybackTrack(index,
                UI.Localization.UiTextCatalog.Format("PlayerTrackNumber", result.Count + 1, track.Language ?? "und"), selected));
        }
        if (subtitles && result.Count > 0)
            result.Insert(0, new NativePlaybackTrack(-1, UI.Localization.UiTextCatalog.Get("PlayerTrackNone"), !hasSelected));
        return result;
    }

    public bool SelectTrack(int id, bool subtitles)
    {
        if (Engine == AndroidPlaybackEngine.LibVlc && _vlcPlayer is { } vlc)
            return subtitles ? vlc.SetSpu(id) : vlc.SetAudioTrack(id);
        if (_systemPlayer is not { } player) return false;
        if (id >= 0) player.SelectTrack(id);
        else if (subtitles)
        {
            foreach (var type in new[] { global::Android.Media.MediaTrackType.Subtitle, global::Android.Media.MediaTrackType.Timedtext })
            {
                var selected = player.GetSelectedTrack(type);
                if (selected >= 0) player.DeselectTrack(selected);
            }
        }
        return true;
    }

    public void SeekTo(int milliseconds)
    {
        if (Engine == AndroidPlaybackEngine.System) _systemView.SeekTo(milliseconds);
        else if (_vlcPlayer is { IsSeekable: true } player) player.Time = milliseconds;
    }

    public void SetMediaController(MediaController? controller) => _systemView.SetMediaController(controller);
    public void SetOnInfoListener(SystemPlayer.IOnInfoListener? listener)
    {
        _infoListener = listener;
        _systemView.SetOnInfoListener(listener);
    }

    public void StopPlayback()
    {
        _generation++;
        _vlcPrepared = false;
        _systemPlayer = null;
        _systemView.StopPlayback();
        var player = _vlcPlayer;
        _vlcPlayer = null;
        if (player is not null)
        {
            // Never called from LibVLC's decoder/event thread: stopping there
            // can deadlock. Invalidate posted callbacks before releasing it.
            player.Stop();
            if (_vlcView is not null) _vlcView.MediaPlayer = null;
            player.Dispose();
        }
        _media?.Dispose();
        _media = null;
    }

    public void ReleasePlayback()
    {
        if (_released) return;
        _released = true;
        StopPlayback();
        if (_vlcView is not null)
        {
            RemoveView(_vlcView);
            _vlcView.Dispose();
            _vlcView = null;
        }
        _libVlc?.Dispose();
        _libVlc = null;
    }

    private static FrameLayout.LayoutParams SurfaceLayout() => new(
        ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent, GravityFlags.Center);
}

internal sealed record NativePlaybackTrack(int Id, string Label, bool IsSelected);

internal sealed class PlaybackErrorEventArgs(string what) : EventArgs
{
    public string What { get; } = what;
    public bool Handled { get; set; }
}
