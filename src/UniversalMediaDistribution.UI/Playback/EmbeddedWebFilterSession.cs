using UniversalMediaDistribution.ContentFiltering;
using UniversalMediaDistribution.ContentFiltering.Injection;
using UniversalMediaDistribution.UI.Diagnostics;

namespace UniversalMediaDistribution.UI.Playback;

/// <summary>
/// What filtering did to the embedded page that is open, or was open last.
/// </summary>
/// <param name="IsNetworkLevel">False when only the injected policy is
/// filtering, which a page can work around and the network layer cannot.</param>
public readonly record struct EmbeddedWebFilterReport(
    bool IsNetworkLevel,
    string? Limitation,
    int RequestsSeen,
    int RequestsBlocked,
    int RequestsAllowedAsSameSite,
    int RequestsAllowedAsMedia,
    IReadOnlyCollection<string> UnmatchedHosts);

/// <summary>
/// The filtering engine as the embedded player's platform hooks see it.
///
/// The document is written from the UI thread when a server is opened and read
/// from whichever thread the platform raises its request callback on — Android
/// uses its network thread — so it is published through a volatile field
/// rather than a lock. A hook that reads a document one navigation out of date
/// costs one request its first-party allowance; a lock on that path costs
/// every page load.
/// </summary>
public sealed class EmbeddedWebFilterSession : IContentFilterSession
{
    private readonly ContentFilterEngine _engine;
    private readonly Func<InjectionOptions> _injectionOptions;
    private volatile Uri? _documentUri;
    private volatile bool _isPassthrough;
    private volatile ContentFilterEngine? _compatibilityEngine;

    public EmbeddedWebFilterSession(
        ContentFilterEngine engine,
        Func<InjectionOptions>? injectionOptions = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
        _injectionOptions = injectionOptions ??
            (() => InjectionOptions.Standard);
    }

    public bool IsActive => _documentUri is not null;

    public Uri? DocumentUri => _documentUri;

    /// <summary>
    /// True when the current hoster page must run without request filtering or
    /// injected defenses. Platform hooks remain attached so media requests can
    /// still be observed for an optional native-player handoff.
    /// </summary>
    public bool IsPassthrough => _isPassthrough;

    /// <summary>
    /// True when the current player gets a deliberately narrow network policy
    /// and no page modifications. This is distinct from pass-through: known
    /// advertisement endpoints are still refused by the native hook.
    /// </summary>
    public bool IsCompatibilityLimited => _compatibilityEngine is not null;

    public bool IsPageInjectionSuppressed =>
        _isPassthrough || _compatibilityEngine is not null;

    public event Action<Uri>? DocumentOpened;

    public void Open(Uri documentUri) =>
        OpenCore(
            documentUri,
            isPassthrough: false,
            compatibilityEngine: null);

    public void OpenPassthrough(Uri documentUri) =>
        OpenCore(
            documentUri,
            isPassthrough: true,
            compatibilityEngine: null);

    public void OpenCompatibilityLimited(
        Uri documentUri,
        ContentFilterEngine compatibilityEngine)
    {
        ArgumentNullException.ThrowIfNull(compatibilityEngine);
        OpenCore(
            documentUri,
            isPassthrough: false,
            compatibilityEngine);
    }

    /// <summary>
    /// Moves an active session to a top-level redirect without changing its
    /// filtering mode or erasing the evidence collected so far. Player hosts
    /// such as VOE deliberately redirect from their stable public name to a
    /// rotating serving name. Requests from that accepted document must be
    /// compared with the serving name, otherwise its own player scripts look
    /// third-party and are incorrectly blocked.
    /// </summary>
    public void Navigate(Uri documentUri)
    {
        ArgumentNullException.ThrowIfNull(documentUri);
        if (_documentUri is null)
        {
            throw new InvalidOperationException(
                "A filtering session must be open before it can navigate.");
        }

        _documentUri = documentUri;
        DocumentOpened?.Invoke(documentUri);
    }

    private void OpenCore(
        Uri documentUri,
        bool isPassthrough,
        ContentFilterEngine? compatibilityEngine)
    {
        ArgumentNullException.ThrowIfNull(documentUri);
        _isPassthrough = isPassthrough;
        _compatibilityEngine = compatibilityEngine;
        _documentUri = documentUri;
        Interlocked.Exchange(ref _seen, 0);
        Interlocked.Exchange(ref _blocked, 0);
        Interlocked.Exchange(ref _sameSite, 0);
        Interlocked.Exchange(ref _media, 0);
        lock (_unmatchedHosts)
        {
            _unmatchedHosts.Clear();
        }

        DocumentOpened?.Invoke(documentUri);
    }

    public void Close()
    {
        _documentUri = null;
        _isPassthrough = false;
        _compatibilityEngine = null;
    }

    /// <summary>
    /// Requests examined and refused since the current page was opened.
    /// Written from the platform's request callback and read from the UI
    /// thread, so both are interlocked: they are a diagnostic, and a
    /// diagnostic that tears is worse than none.
    /// </summary>
    public int RequestsSeen => Volatile.Read(ref _seen);

    public int RequestsBlocked => Volatile.Read(ref _blocked);

    /// <summary>
    /// Requests to the page's own site that were allowed after filtering,
    /// plus the selected document itself.
    /// </summary>
    public int RequestsAllowedAsSameSite => Volatile.Read(ref _sameSite);

    /// <summary>Requests allowed because they are part of playing the video.</summary>
    public int RequestsAllowedAsMedia => Volatile.Read(ref _media);

    private const int MaxReportedHosts = 8;

    private static readonly System.Buffers.SearchValues<char> HostTerminators =
        System.Buffers.SearchValues.Create("/?#:");

    private readonly HashSet<string> _unmatchedHosts =
        new(StringComparer.OrdinalIgnoreCase);
    private int _seen;
    private int _blocked;
    private int _sameSite;
    private int _media;

    /// <summary>
    /// Hosts that were loaded because no rule mentioned them.
    ///
    /// This is the one thing a viewer cannot work out for themselves and the
    /// one thing needed to fix a surviving advertisement: the lists are large
    /// but not complete, and a small or regional advertisement network simply
    /// is not in them. Naming the host turns "there are still advertisements"
    /// into a rule someone can write.
    /// </summary>
    public IReadOnlyCollection<string> UnmatchedHosts
    {
        get
        {
            lock (_unmatchedHosts)
            {
                return [.. _unmatchedHosts];
            }
        }
    }

    private void RecordUnmatched(ReadOnlySpan<char> url)
    {
        var start = url.IndexOf("://", StringComparison.Ordinal);
        if (start < 0)
        {
            return;
        }

        var host = url[(start + 3)..];
        var end = host.IndexOfAny(HostTerminators);
        if (end >= 0)
        {
            host = host[..end];
        }

        if (host.IsEmpty)
        {
            return;
        }

        lock (_unmatchedHosts)
        {
            if (_unmatchedHosts.Count < MaxReportedHosts &&
                !_unmatchedHosts.GetAlternateLookup<ReadOnlySpan<char>>()
                    .Contains(host))
            {
                _unmatchedHosts.Add(host.ToString());
            }
        }
    }

    public FilterDecision Match(ReadOnlySpan<char> url, ResourceType type)
    {
        var isPassthrough = _isPassthrough;
        var compatibilityEngine = _compatibilityEngine;
        var document = _documentUri;
        // An empty primary engine means the viewer selected Off. The limited
        // engine must respect that preference instead of becoming an
        // unadvertised way to turn filtering back on for one hoster.
        var filteringDisabled = _engine.Ruleset.NetworkRuleCount == 0;
        var decision = isPassthrough ||
            compatibilityEngine is not null && filteringDisabled
            ? FilterDecision.Allow
            : (compatibilityEngine ?? _engine).Match(new FilterRequest(
                url,
                document is null ? default : document.AbsoluteUri.AsSpan(),
                type));
        var seen = Interlocked.Increment(ref _seen);
        if (decision.IsBlocking)
        {
            Interlocked.Increment(ref _blocked);
        }
        else if (decision.Reason == FilterReason.SameSite)
        {
            Interlocked.Increment(ref _sameSite);
        }
        else if (decision.Reason == FilterReason.Media)
        {
            Interlocked.Increment(ref _media);
        }
        else if (!isPassthrough && decision.Reason == FilterReason.NoRule)
        {
            RecordUnmatched(url);
        }

        if (seen == 1)
        {
            ApplicationDiagnosticLog.Information(
                "playback.webview-filter-active",
                $"host={GetDiagnosticHost(url)}; type={type}; " +
                $"mode={GetDiagnosticMode(isPassthrough, compatibilityEngine)}");
        }

        if (decision.IsBlocking)
        {
            ApplicationDiagnosticLog.Information(
                "playback.webview-request-blocked",
                $"host={GetDiagnosticHost(url)}; type={type}; " +
                $"reason={decision.Reason}; " +
                $"surrogate={decision.RedirectResource ?? "-"}");
        }

        EmbeddedWebDiagnostics.LogRequest(
            decision.Action.ToString(),
            isPassthrough
                ? "Passthrough"
                : decision.Reason.ToString(),
            url);
        return decision;
    }

    private static string GetDiagnosticMode(
        bool isPassthrough,
        ContentFilterEngine? compatibilityEngine) =>
        isPassthrough
            ? "passthrough"
            : compatibilityEngine is null
                ? "full"
                : "limited-network";

    private static string GetDiagnosticHost(ReadOnlySpan<char> url)
    {
        const int MaximumDiagnosticUrlLength = 4096;
        if (url.Length > MaximumDiagnosticUrlLength ||
            !Uri.TryCreate(url.ToString(), UriKind.Absolute, out var uri) ||
            uri.IdnHost.Length == 0)
        {
            return "-";
        }

        return uri.IdnHost;
    }

    private readonly Lock _payloadGate = new();
    private string? _payloadHost;
    private CompiledRuleset? _payloadRuleset;
    private string? _payload;

    /// <summary>
    /// Builds the document-start script for a host, and remembers it.
    ///
    /// The generic hiding set alone runs to a quarter of a megabyte, and this
    /// is called on every navigation and re-registration. Rebuilding it each
    /// time was doing that work repeatedly on the thread the page is loading
    /// on. The cache is keyed on the ruleset too, so a refresh that swaps in
    /// new rules is picked up rather than served stale.
    /// </summary>
    public string BuildInjectionPayload(string host)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (IsPageInjectionSuppressed)
        {
            // Hooks register this at document start. A no-op keeps their
            // request blocking/observation path alive without modifying a
            // compatibility-sensitive page.
            return ";";
        }

        var ruleset = _engine.Ruleset;
        if (ruleset.NetworkRuleCount == 0 && ruleset.CosmeticRuleCount == 0)
        {
            return ";";
        }

        lock (_payloadGate)
        {
            if (_payload is not null &&
                ReferenceEquals(_payloadRuleset, ruleset) &&
                string.Equals(_payloadHost, host, StringComparison.Ordinal))
            {
                return _payload;
            }
        }

        var payload = InjectionPayloadBuilder.Build(
            _engine.GetCosmetics(host),
            _injectionOptions());

        lock (_payloadGate)
        {
            _payloadHost = host;
            _payloadRuleset = ruleset;
            _payload = payload;
        }

        return payload;
    }

    /// <summary>
    /// Best-effort seed request blocking when no native hook is available,
    /// followed by the normal cosmetic payload. Page API wrappers cannot
    /// intercept parser-created requests or implement downloaded network rules.
    /// </summary>
    public string BuildFallbackInjectionPayload(string host)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (IsPageInjectionSuppressed ||
            _engine.Ruleset is { NetworkRuleCount: 0, CosmeticRuleCount: 0 })
        {
            return ";";
        }

        var requestPolicy = _engine.Ruleset.NetworkRuleCount > 0
            ? EmbeddedWebAdBlocker.RequestBlockingScript
            : string.Empty;
        return requestPolicy + "\n" + BuildInjectionPayload(host);
    }

    /// <summary>
    /// The stand-ins a <c>$redirect=</c> rule can name. A page that waits on
    /// the script it asked for makes progress with one of these; a page that
    /// gets nothing sometimes waits forever.
    /// </summary>
    public string? Surrogate(string resourceName) =>
        resourceName switch
        {
            "noopjs" or "noop.js" => ";",
            "noop.txt" or "nooptext" => string.Empty,
            "noopframe" or "noop.html" =>
                "<!doctype html><html><head></head><body></body></html>",
            "noopcss" or "noop.css" => string.Empty,
            "noopjson" or "noop.json" => "{}",
            _ => null
        };
}
