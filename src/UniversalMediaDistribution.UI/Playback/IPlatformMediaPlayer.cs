using Avalonia.Controls;
using UniversalMediaDistribution.Torrenting;
using UniversalMediaDistribution.UI.Localization;

namespace UniversalMediaDistribution.UI.Playback;

public sealed record PlatformSubtitleTrack(
    Uri Source,
    string Label,
    string Language,
    bool IsDefault = false);

public sealed record PlatformMediaSource
{
    public PlatformMediaSource(
        Uri? playbackUri,
        Func<CancellationToken, ValueTask<IMediaReadHandle>>? openReadAsync,
        Uri? referrer = null,
        string? userAgent = null,
        IReadOnlyList<PlatformMediaDecryptionKey>? decryptionKeys = null,
        Uri? httpOrigin = null)
    {
        if (playbackUri is null && openReadAsync is null)
        {
            throw new ArgumentException(
                "A platform media source requires a playback URI, a stream opener, or both.");
        }

        if (referrer is not null &&
            (!referrer.IsAbsoluteUri ||
             referrer.Scheme is not ("http" or "https") ||
             referrer.UserInfo.Length > 0))
        {
            throw new ArgumentException(
                "A media referrer must be an absolute HTTP or HTTPS URL without embedded credentials.",
                nameof(referrer));
        }

        if (userAgent is not null &&
            (string.IsNullOrWhiteSpace(userAgent) ||
             userAgent.Length > 512 ||
             userAgent.Contains('\r') ||
             userAgent.Contains('\n')))
        {
            throw new ArgumentException(
                "A media user agent must be a single non-empty header value no longer than 512 characters.",
                nameof(userAgent));
        }

        if (httpOrigin is not null &&
            (!httpOrigin.IsAbsoluteUri ||
             httpOrigin.Scheme is not ("http" or "https") ||
             httpOrigin.UserInfo.Length > 0 ||
             httpOrigin.AbsolutePath != "/" ||
             httpOrigin.Query.Length > 0 ||
             httpOrigin.Fragment.Length > 0))
        {
            throw new ArgumentException(
                "A media origin must be an absolute HTTP or HTTPS origin without credentials, a path, query, or fragment.",
                nameof(httpOrigin));
        }

        var keys = decryptionKeys?.ToArray() ?? [];
        if (keys.Length > 32)
        {
            throw new ArgumentException(
                "A platform media source cannot carry more than 32 ClearKey pairs.",
                nameof(decryptionKeys));
        }

        PlaybackUri = playbackUri;
        OpenReadAsync = openReadAsync;
        Referrer = referrer;
        UserAgent = userAgent;
        HttpOrigin = httpOrigin;
        DecryptionKeys = keys;
    }

    public Uri? PlaybackUri { get; }

    public Func<CancellationToken, ValueTask<IMediaReadHandle>>? OpenReadAsync { get; }

    public Uri? Referrer { get; }

    public string? UserAgent { get; }

    public Uri? HttpOrigin { get; }

    public IReadOnlyList<PlatformMediaDecryptionKey> DecryptionKeys { get; }

    public bool RequiresClearKeyDecryption => DecryptionKeys.Count > 0;
}

/// <summary>
/// A ClearKey decryption pair the platform player should apply to a DASH
/// stream. Mirrors the catalog-level
/// <see cref="UniversalMediaDistribution.Catalog.ContentSources.ApiContentVideoDecryptionKey"/>
/// but lives in the playback layer so the player can depend on the keys
/// without taking a dependency on the catalog project.
/// </summary>
public sealed record PlatformMediaDecryptionKey(
    string KeyId,
    string Key)
{
    public string KeyId { get; } =
        string.IsNullOrWhiteSpace(KeyId) ? throw new ArgumentException(
            "A platform decryption key ID must be non-empty.",
            nameof(KeyId)) : KeyId.Trim();
    public string Key { get; } =
        string.IsNullOrWhiteSpace(Key) ? throw new ArgumentException(
            "A platform decryption key must be non-empty.",
            nameof(Key)) : Key.Trim();
}

public sealed record PlatformMediaVariant(
    PlatformMediaSource Source,
    string Label,
    bool IsAudio,
    string? MimeType,
    IReadOnlyList<PlatformSubtitleTrack>? Subtitles = null);

public sealed record PlatformMediaRequest(
    PlatformMediaVariant InitialVariant,
    string Title,
    double StartPositionSeconds,
    IReadOnlyList<PlatformSubtitleTrack> Subtitles,
    IReadOnlyList<PlatformMediaVariant> AlternateAudioVariants,
    bool IsLive = false);

/// <summary>
/// What the player is doing right now.
/// <para>
/// <see cref="IsBuffering"/> separates a stalled player from a deliberately
/// paused one. Watch-together sync needs that distinction: a member whose
/// stream is refilling would otherwise read as "the viewer pressed pause" and
/// stop the whole party.
/// </para>
/// </summary>
public sealed record PlatformMediaState(
    double PositionSeconds,
    double? DurationSeconds,
    bool IsPlaying,
    bool IsEnded,
    bool IsClosed,
    string? ErrorMessage = null,
    bool IsBuffering = false);

public interface IPlatformMediaPlayer
{
    bool IsAvailable { get; }

    Control? EmbeddedView { get; }

    bool SupportsExternalTracks => true;

    /// <summary>
    /// Whether alternate direct video servers can be selected inside the
    /// platform player's own surface. Android opens a separate native
    /// activity, so the Avalonia server bar underneath it is not reachable.
    /// </summary>
    bool SupportsInPlayerServerSelection => false;

    bool SupportsEmbeddedWeb => false;

    ValueTask OpenAsync(
        PlatformMediaRequest request,
        CancellationToken cancellationToken = default);

    ValueTask OpenEmbeddedWebAsync(
        Uri source,
        string title,
        Uri? referrer = null,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException(new NotSupportedException(
            UiTextCatalog.Get("EmbeddedWebPlayerNotConfigured")));

    ValueTask<PlatformMediaState> GetStateAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether this player can be paused, resumed, and repositioned by code
    /// rather than only by the viewer. Watch-together sync needs this; a
    /// player without it can still publish what its viewer does but cannot
    /// follow the party's commands.
    /// </summary>
    bool SupportsTransportControl => false;

    /// <summary>
    /// Pauses or resumes the open media without tearing it down. A no-op when
    /// nothing is open or <see cref="SupportsTransportControl"/> is false.
    /// </summary>
    ValueTask SetPlayingAsync(
        bool playing,
        CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    /// <summary>
    /// Moves the open media to that position without reopening it. A no-op
    /// when nothing is open or <see cref="SupportsTransportControl"/> is
    /// false.
    /// </summary>
    ValueTask SeekAsync(
        double positionSeconds,
        CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    ValueTask CloseAsync(CancellationToken cancellationToken = default);
}

public sealed class UnavailablePlatformMediaPlayer : IPlatformMediaPlayer
{
    public bool IsAvailable => false;

    public Control? EmbeddedView => null;

    public ValueTask OpenAsync(
        PlatformMediaRequest request,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException(new NotSupportedException(
            UiTextCatalog.Get("InAppPlayerNotConfigured")));

    public ValueTask<PlatformMediaState> GetStateAsync(
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new PlatformMediaState(0, null, false, false, true));

    public ValueTask CloseAsync(CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;
}
