using Avalonia.Controls;
using Avalonia.Platform;
using UniversalMediaDistribution.ContentFiltering;

namespace UniversalMediaDistribution.UI.Playback;

/// <summary>
/// What a platform hook needs from the filtering engine, without knowing
/// anything about how the engine works.
/// </summary>
public interface IContentFilterSession
{
    /// <summary>
    /// False when no embedded page is open. A hook stays attached for the life
    /// of the web view, so it has to know when to stand down.
    /// </summary>
    bool IsActive { get; }

    /// <summary>The page the requests belong to, when one is open.</summary>
    Uri? DocumentUri { get; }

    /// <summary>
    /// Raised when a different page is opened. Cosmetic payloads are per host,
    /// so a hook that registered one when it attached would keep serving the
    /// first host's rules to every server the viewer picks afterwards.
    /// </summary>
    event Action<Uri>? DocumentOpened;

    FilterDecision Match(ReadOnlySpan<char> url, ResourceType type);

    /// <summary>
    /// The script to run before the page's own scripts: hiding rules,
    /// scriptlets, and the built-in popup and click-trap defences.
    /// </summary>
    string BuildInjectionPayload(string host);

    /// <summary>
    /// The body to serve in place of a redirected resource, or null when the
    /// surrogate is unknown and the request should simply be refused.
    /// </summary>
    string? Surrogate(string resourceName);
}

/// <summary>
/// Stops requests at the network layer, where the platform allows it.
///
/// The Avalonia wrapper cannot cancel a resource request, but the native
/// object underneath it can: WebView2 can answer a request with a response of
/// our choosing, and Android's <c>WebViewClient</c> can intercept one outright.
/// A hook reaches through <c>TryGetPlatformHandle()</c> to do that. Where no
/// hook attaches, the injected policy remains the enforcement mechanism.
/// </summary>
public interface INativeWebViewFilterHook
{
    /// <summary>
    /// Raised when the native web engine observes a likely media playlist.
    ///
    /// WebView2 reports the committed response, including its status and
    /// content type. Android exposes the request but not the response, so its
    /// observation leaves <see cref="EmbeddedWebMediaResponse.StatusCode"/>
    /// null and the playback layer validates the endpoint before handing it to
    /// the native player.
    /// </summary>
    event Action<EmbeddedWebMediaResponse>? MediaResponseReceived;

    /// <summary>
    /// Attaches to the platform object behind <paramref name="webView"/>.
    /// Returns false when the handle is not available yet — the caller retries
    /// — or when this platform has no hook to offer, in which case it is never
    /// asked again.
    /// </summary>
    bool TryAttach(
        NativeWebView webView,
        IContentFilterSession session,
        IPlatformHandle? platformHandle = null);

    /// <summary>True once requests are being refused at the network layer.</summary>
    bool IsAttached { get; }

    /// <summary>
    /// Why the hook is not attached, when it is not. Surfaced to the viewer,
    /// because "filtering is weaker than you think" is not something that
    /// should only be visible in a debugger.
    /// </summary>
    string? AttachFailure { get; }
}

/// <summary>
/// The hook used where no platform implementation is supplied.
/// </summary>
public sealed class UnavailableWebViewFilterHook : INativeWebViewFilterHook
{
    public static UnavailableWebViewFilterHook Instance { get; } = new();

    private UnavailableWebViewFilterHook()
    {
    }

    public event Action<EmbeddedWebMediaResponse>? MediaResponseReceived
    {
        add
        {
        }
        remove
        {
        }
    }

    public bool TryAttach(
        NativeWebView webView,
        IContentFilterSession session,
        IPlatformHandle? platformHandle = null) =>
        false;

    public bool IsAttached => false;

    public string? AttachFailure => "this platform has no network-level hook";
}

/// <summary>
/// A media request observed inside an embedded player.
///
/// Header values remain strings here because they came from an untrusted
/// document. The handoff policy parses and bounds them before constructing a
/// <see cref="PlatformMediaSource"/>.
/// </summary>
public sealed record EmbeddedWebMediaResponse(
    Uri DocumentUri,
    Uri RequestUri,
    int? StatusCode,
    string? ContentType,
    string? Referrer,
    string? Origin,
    string? UserAgent);

public sealed class EmbeddedWebFullscreenChangedEventArgs(bool isFullscreen) :
    EventArgs
{
    public bool IsFullscreen { get; } = isFullscreen;
}

/// <summary>
/// Optional notifications emitted when a web fallback becomes native
/// playback without a new selection from the viewer, and when an embedded
/// document enters or leaves HTML fullscreen.
/// </summary>
public interface IPlatformMediaPlayerTransitions
{
    /// <summary>
    /// Raised when <see cref="IPlatformMediaPlayer.EmbeddedView"/> changes
    /// during a browser-to-native handoff or when the browser is restored
    /// after a failed native start.
    /// </summary>
    event EventHandler? PlaybackSurfaceChanged;

    event EventHandler? NativePlaybackStarted;

    event EventHandler<EmbeddedWebFullscreenChangedEventArgs>?
        EmbeddedFullscreenChanged;

    /// <summary>
    /// Raised when Escape is pressed inside the native WebView. That key may
    /// never reach Avalonia's routed input tree.
    /// </summary>
    event EventHandler? EmbeddedFullscreenExitRequested;

    /// <summary>
    /// Leaves HTML fullscreen when the application shell exits fullscreen by
    /// its own toolbar, F11, remote Back, or another native action.
    /// </summary>
    void RequestEmbeddedFullscreenExit();
}
