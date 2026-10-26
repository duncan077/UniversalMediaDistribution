using Avalonia.Controls;
using Android.Content;
using UniversalMediaDistribution.Mobile;
using UniversalMediaDistribution.UI.Playback;

namespace UniversalMediaDistribution.Android;

internal sealed class AndroidPlatformMediaPlayer(Context applicationContext) :
    IPlatformMediaPlayer,
    IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Context _applicationContext =
        applicationContext.ApplicationContext ?? applicationContext;
    private PlaybackLaunch? _activeLaunch;
    private WeakReference<NativePlaybackActivity>? _activity;
    private PlatformMediaState _state = new(0, null, false, false, true);
    private bool _disposed;

    public bool IsAvailable => !_disposed;

    public Control? EmbeddedView => null;

    public bool SupportsExternalTracks => false;

    public bool SupportsInPlayerServerSelection => true;

    public ValueTask OpenAsync(
        PlatformMediaRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        var source = MobilePlaybackSourcePolicy.RequirePlaybackUri(request);
        var variants = new List<PlaybackVariant>
        {
            CreatePlaybackVariant(request.InitialVariant, source)
        };
        foreach (var alternate in request.AlternateAudioVariants)
        {
            if (alternate.Source.PlaybackUri is not { } alternateSource ||
                variants.Any(candidate =>
                    candidate.Source == alternateSource))
            {
                continue;
            }

            variants.Add(CreatePlaybackVariant(alternate, alternateSource));
        }

        var launch = new PlaybackLaunch(
            Guid.CreateVersion7(),
            variants,
            request.Title,
            Math.Max(0, request.StartPositionSeconds),
            request.IsLive,
            request.Subtitles);

        NativePlaybackActivity? previousActivity = null;
        lock (_gate)
        {
            if (_activity?.TryGetTarget(out previousActivity) is not true)
            {
                previousActivity = null;
            }

            _activeLaunch = launch;
            _activity = null;
            // Until the native activity attaches and prepares, the session is
            // loading; reporting it as a plain non-playing state would look
            // like a viewer pause to watch-together sync.
            _state = new PlatformMediaState(
                launch.StartPositionSeconds,
                null,
                false,
                false,
                false,
                IsBuffering: true);
        }

        previousActivity?.RunOnUiThread(previousActivity.Finish);

        var intent = new Intent(_applicationContext, typeof(NativePlaybackActivity));
        intent.PutExtra(NativePlaybackActivity.SessionTokenExtra, launch.Token.ToString("N"));
        intent.AddFlags(ActivityFlags.NewTask);
        _applicationContext.StartActivity(intent);
        return ValueTask.CompletedTask;
    }

    public async ValueTask<PlatformMediaState> GetStateAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        NativePlaybackActivity? activity = null;
        lock (_gate)
        {
            if (_activity?.TryGetTarget(out activity) is not true)
            {
                activity = null;
            }
        }

        if (activity is not null)
        {
            try
            {
                var snapshot = await activity.CaptureStateAsync(cancellationToken);
                lock (_gate)
                {
                    if (_activeLaunch?.Token == snapshot.Token)
                    {
                        _state = snapshot.State;
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                // The native activity may disappear between the weak-reference check and capture.
            }
        }

        lock (_gate)
        {
            return _state;
        }
    }

    public bool SupportsTransportControl => true;

    public ValueTask SetPlayingAsync(
        bool playing,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        // Watch-together commands may race the viewer closing the native
        // activity; with nothing attached there is nothing to command.
        TryGetActivity()?.RequestSetPlaying(playing);
        return ValueTask.CompletedTask;
    }

    public ValueTask SeekAsync(
        double positionSeconds,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        TryGetActivity()?.RequestSeek(positionSeconds);
        return ValueTask.CompletedTask;
    }

    public ValueTask CloseAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        NativePlaybackActivity? activity = null;
        lock (_gate)
        {
            if (_activity?.TryGetTarget(out activity) is not true)
            {
                activity = null;
            }

            _activeLaunch = null;
            _activity = null;
            _state = _state with
            {
                IsPlaying = false,
                IsBuffering = false,
                IsClosed = true
            };
        }

        activity?.RunOnUiThread(activity.Finish);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await CloseAsync();
        _disposed = true;
    }

    internal bool TryAttach(
        NativePlaybackActivity activity,
        Guid token,
        out PlaybackLaunch launch)
    {
        ArgumentNullException.ThrowIfNull(activity);
        lock (_gate)
        {
            if (_disposed ||
                _activeLaunch is not { } activeLaunch ||
                activeLaunch.Token != token)
            {
                launch = null!;
                return false;
            }

            _activity = new WeakReference<NativePlaybackActivity>(activity);
            launch = activeLaunch;
            return true;
        }
    }

    internal void ReportState(Guid token, PlatformMediaState state)
    {
        lock (_gate)
        {
            if (_activeLaunch?.Token == token)
            {
                _state = state;
            }
        }
    }

    internal void Detach(Guid token, PlatformMediaState finalState)
    {
        lock (_gate)
        {
            if (_activeLaunch?.Token != token)
            {
                return;
            }

            _activeLaunch = null;
            _activity = null;
            _state = finalState with
            {
                IsPlaying = false,
                IsBuffering = false,
                IsClosed = true
            };
        }
    }

    private NativePlaybackActivity? TryGetActivity()
    {
        lock (_gate)
        {
            return _activity?.TryGetTarget(out var activity) is true
                ? activity
                : null;
        }
    }

    private static PlaybackVariant CreatePlaybackVariant(
        PlatformMediaVariant variant,
        Uri source) =>
        new(
            source,
            string.IsNullOrWhiteSpace(variant.Label)
                ? source.Host
                : variant.Label.Trim(),
            variant.IsAudio,
            variant.Source.Referrer,
            variant.Source.UserAgent,
            variant.Source.HttpOrigin,
            variant.Source.DecryptionKeys,
            variant.Subtitles);

    internal sealed record PlaybackLaunch(
        Guid Token,
        IReadOnlyList<PlaybackVariant> Variants,
        string Title,
        double StartPositionSeconds,
        bool IsLive,
        IReadOnlyList<PlatformSubtitleTrack>? Subtitles = null)
    {
        public PlaybackVariant InitialVariant => Variants[0];

        public bool IsAudio => InitialVariant.IsAudio;
    }

    internal sealed record PlaybackVariant(
        Uri Source,
        string Label,
        bool IsAudio,
        Uri? Referrer,
        string? UserAgent,
        Uri? HttpOrigin,
        IReadOnlyList<PlatformMediaDecryptionKey> DecryptionKeys,
        IReadOnlyList<PlatformSubtitleTrack>? Subtitles = null)
    {
        public PlatformMediaSource MediaSource => new(
            Source, null, Referrer, UserAgent, DecryptionKeys, HttpOrigin);
    }
}
