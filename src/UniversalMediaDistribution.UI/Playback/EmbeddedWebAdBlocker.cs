using System.Text;
using System.Text.Json;
using UniversalMediaDistribution.ContentFiltering;
using UniversalMediaDistribution.ContentFiltering.Compilation;
using UniversalMediaDistribution.ContentFiltering.Parsing;

namespace UniversalMediaDistribution.UI.Playback;

/// <summary>
/// Filtering policy for the embedded player surface.
///
/// Decisions come from <see cref="ContentFilterEngine"/>, compiled from the
/// seed rules below. The seed is what protects a first run and an offline
/// install; downloaded filter lists replace it once they arrive.
///
/// Native platform hooks enforce the engine's downloaded rules. Without a
/// native hook, the injected policy below wraps page request APIs using the
/// seed rules only; it cannot intercept every request made by the browser.
/// Downloaded cosmetic rules are applied by the separate generated runtime.
///
/// Playback resources remain protected. Same-site subresources are filtered
/// too; being served by the player is not evidence that a request is safe.
/// </summary>
internal static class EmbeddedWebAdBlocker
{
    /// <summary>
    /// Advertising, popunder, and analytics hosts, matched on the host itself
    /// or any subdomain of it.
    /// </summary>
    private static readonly string[] BlockedHostSuffixes =
    [
        "1dmp.io",
        "2mdn.net",
        "acscdn.com",
        "adcash.com",
        "adform.net",
        "adnxs.com",
        "adroll.com",
        "ads-twitter.com",
        "adsafeprotected.com",
        "adskeeper.co.uk",
        "adsrvr.org",
        "adsterra.com",
        "adthrive.com",
        "advertising.com",
        "amazon-adsystem.com",
        "amvntixh.in",
        "amplitude.com",
        "app-measurement.com",
        "bidswitch.net",
        "branch.io",
        "bttrack.com",
        "casalemedia.com",
        "chartbeat.com",
        "clarity.ms",
        "clickadu.com",
        "clicksor.com",
        "cloudflareinsights.com",
        "coosync.com",
        "criteo.com",
        "criteo.net",
        "crwdcntrl.net",
        "demdex.net",
        "demandingoverdriveunthread.com",
        "doubleclick.net",
        "doubleverify.com",
        "everesttech.net",
        "exoclick.com",
        "exosrv.com",
        "facebook.net",
        "fullstory.com",
        "google-analytics.com",
        "googleadservices.com",
        "googlesyndication.com",
        "googletagmanager.com",
        "googletagservices.com",
        "histats.com",
        "hotjar.com",
        "hs-analytics.net",
        "ibclick.stream",
        "imasdk.googleapis.com",
        "improvedigital.com",
        "indexww.com",
        "infolinks.com",
        "juicyads.com",
        "logrocket.com",
        "media.net",
        "mgid.com",
        "mixpanel.com",
        "moatads.com",
        "mookie1.com",
        "newrelic.com",
        "onclickalgo.com",
        "onclicka.com",
        "onesignal.com",
        "openx.net",
        "outbrain.com",
        "phncdn.com",
        // The image CDN behind the "Are you 18+?" in-page push cards. Its
        // companion tracker domains rotate on every page load and cannot be
        // listed, but this one is stable — and refusing the picture is what
        // lets the injected policy recognize the card around it.
        "pncloudfl.com",
        "popads.net",
        "popcash.net",
        "poptm.com",
        "propellerads.com",
        "pubmatic.com",
        "pushnami.com",
        "quantserve.com",
        "quantcast.com",
        "revcontent.com",
        "rlcdn.com",
        "rubiconproject.com",
        "scorecardresearch.com",
        "segment.com",
        "segment.io",
        "sekindo.com",
        "sharethrough.com",
        "smartadserver.com",
        "snigelweb.com",
        "statcounter.com",
        "taboola.com",
        "tapad.com",
        "teads.tv",
        "trafficfactory.biz",
        "trafficjunky.net",
        "trafficstars.com",
        "turn.com",
        "wpadmngr.com",
        "yieldmo.com",
        "zedo.com",
        "zergnet.com"
    ];

    /// <summary>
    /// Whole host labels that only appear on advertising or measurement
    /// endpoints. Matched label by label, never as a substring, so hosts such
    /// as <c>downloads.example</c> or <c>uploads.example</c> are unaffected.
    /// </summary>
    private static readonly string[] BlockedHostLabels =
    [
        "ad",
        "adengine",
        "adhost",
        "ads",
        "adserv",
        "adserver",
        "adservice",
        "adsystem",
        "adtech",
        "advert",
        "advertising",
        "analytics",
        "banners",
        "beacon",
        "metrics",
        "pixel",
        "popunder",
        "prebid",
        "telemetry",
        "tracker",
        "tracking"
    ];

    /// <summary>
    /// Path and query fragments that identify an advertising endpoint even on
    /// hosts that also serve legitimate content, including the player's own
    /// host. Media requests are protected before these rules are evaluated.
    /// </summary>
    private static readonly string[] BlockedPathFragments =
    [
        "/adframe",
        "/adhandler",
        "/adimage",
        "/adserver",
        "/adsync",
        "/advertisement",
        "/adview",
        "/pagead/",
        "/popunder",
        "/prebid",
        "/pubads",
        "/tag/js/gpt",
        "adsbygoogle",
        "aff_id=",
        // The impression call behind the in-page push cards. Its host is a
        // throwaway domain that changes daily and cannot be listed, but the
        // endpoint name and its zone parameter do not change.
        "/chicken.gif?z=",
        "zoneid="
    ];

    /// <summary>
    /// Media extensions and complete endpoint names for the injected policy.
    /// They match the URL path only, and never override a known non-media type.
    /// </summary>
    private static readonly string[] MediaPathFragments =
    [
        ".aac",
        ".flac",
        ".key",
        ".m3u",
        ".m3u8",
        ".m4a",
        ".m4s",
        ".mkv",
        ".mp3",
        ".mp4",
        ".mpd",
        ".ogg",
        ".opus",
        ".srt",
        ".ts",
        ".vtt",
        ".webm",
        "/dash/",
        "/hls/",
        "/manifest",
        "/playlist",
        "/segment",
        "/segments",
        "/subtitle",
        "/subtitles"
    ];

    public static string InjectionScript { get; } = BuildInjectionScript();

    // The generated cosmetic runtime already owns popup and overlay handling.
    // Reuse only the seed request wrappers when native interception is absent.
    public static string RequestBlockingScript { get; } =
        BuildInjectionScript(requestOnly: true);

    /// <summary>
    /// The curated rules above, written as filter syntax.
    ///
    /// A suffix becomes <c>||suffix^</c>, which matches the host itself and any
    /// subdomain of it but stops at a label boundary. A label needs two rules:
    /// <c>||label.</c> reaches it anywhere but last, and <c>||label^</c> reaches
    /// it when it is the final label.
    /// </summary>
    internal static string SeedRuleset { get; } = BuildSeedRuleset();

    /// <summary>
    /// The deliberately small policy used by Nupload's signed watch page.
    ///
    /// Its player breaks when a downloaded list or the cosmetic runtime
    /// changes the document, but allowing every request also restores its
    /// pop-ad loaders. These three hosts were isolated from a working player
    /// capture: refusing them leaves JW Player and the ibra.lat stream intact.
    /// Keep this list narrow; it is a compatibility boundary, not a second
    /// general-purpose ruleset.
    /// </summary>
    private static readonly string[] NuploadBlockedHostSuffixes =
    [
        "acscdn.com",
        "cloudflareinsights.com",
        "wpadmngr.com"
    ];

    internal static ContentFilterEngine NuploadCompatibilityEngine { get; } =
        CreateEngine(BuildHostRuleset(
            "Universal Media Distribution Nupload compatibility rules",
            NuploadBlockedHostSuffixes));

    /// <summary>
    /// The engine every decision goes through. The seed is compiled once; a
    /// subscription refresh swaps in a wider ruleset without touching callers.
    /// </summary>
    public static ContentFilterEngine Engine { get; } =
        CreateEngine(SeedRuleset);

    /// <summary>
    /// True when the embedded player must not load the request at all.
    /// </summary>
    public static bool ShouldBlock(
        Uri requestUri,
        Uri? documentUri = null)
    {
        ArgumentNullException.ThrowIfNull(requestUri);
        return Engine.ShouldBlock(requestUri, documentUri);
    }

    private static string BuildSeedRuleset()
    {
        var builder = new StringBuilder();
        builder.AppendLine("! Title: Universal Media Distribution seed rules");
        foreach (var suffix in BlockedHostSuffixes)
        {
            builder.Append("||").Append(suffix).AppendLine("^");
        }

        foreach (var label in BlockedHostLabels)
        {
            builder.Append("||").Append(label).AppendLine(".");
            builder.Append("||").Append(label).AppendLine("^");
        }

        foreach (var fragment in BlockedPathFragments)
        {
            builder.AppendLine(fragment);
        }

        return builder.ToString();
    }

    private static string BuildHostRuleset(
        string title,
        IReadOnlyList<string> suffixes)
    {
        var builder = new StringBuilder();
        builder.Append("! Title: ").AppendLine(title);
        foreach (var suffix in suffixes)
        {
            builder.Append("||").Append(suffix).AppendLine("^");
        }

        return builder.ToString();
    }

    private static ContentFilterEngine CreateEngine(string ruleset) =>
        new(CompiledRuleset.Load(
            RulesetCompiler.Compile(
                FilterListParser.Parse(ruleset),
                out _)));

    private static string BuildInjectionScript(bool requestOnly = false)
    {
        var blockedHosts = Serialize(BlockedHostSuffixes);
        var blockedLabels = Serialize(BlockedHostLabels);
        var blockedPaths = Serialize(BlockedPathFragments);
        var mediaPaths = Serialize(MediaPathFragments);
        return $$"""
            (() => {
              const requestOnly = {{(requestOnly ? "true" : "false")}};
              const marker = requestOnly
                ? "__umdEmbeddedRequestBlockerV1"
                : "__umdEmbeddedAdBlockerV3";
              if (requestOnly && globalThis[marker]) { return "active"; }
              const blockedHosts = new Set({{blockedHosts}});
              const blockedLabels = new Set({{blockedLabels}});
              const blockedPaths = {{blockedPaths}};
              const mediaPaths = {{mediaPaths}};
              const documentHost =
                location.hostname.toLowerCase().replace(/\.$/, "");

              const sameSite = host =>
                host === documentHost || host.endsWith("." + documentHost);

              const isBlockedHost = host => {
                let candidate = host;
                while (candidate) {
                  if (blockedHosts.has(candidate)) {
                    return true;
                  }

                  const separator = candidate.indexOf(".");
                  if (separator < 0) {
                    return false;
                  }

                  candidate = candidate.slice(separator + 1);
                }

                return false;
              };

              const requestUrl = value => {
                if (typeof value === "string" || value instanceof URL) {
                  return value;
                }

                return value && typeof value.url === "string"
                  ? value.url
                  : "";
              };

              const resolve = value => {
                try {
                  return new URL(requestUrl(value), document.baseURI);
                } catch {
                  return null;
                }
              };

              // Seed protection for platforms without native interception.
              // Downloaded network rules still require the native engine.
              const isBlocked = (value, type = "other") => {
                const url = resolve(value);
                if (!url ||
                    !["http:", "https:", "ws:", "wss:"].includes(url.protocol)) {
                  return false;
                }

                const host = url.hostname.toLowerCase().replace(/\.$/, "");
                if (!host) {
                  return false;
                }

                const target = (url.pathname + url.search).toLowerCase();
                const path = url.pathname.toLowerCase();
                const filename = path.slice(path.lastIndexOf("/") + 1);
                const isMedia = type === "media" || type === "other" && (
                  mediaPaths.some(item => item[0] === "." && path.endsWith(item)) ||
                  !filename.includes(".") && mediaPaths.some(item =>
                    item[0] === "/" && path.split("/").includes(item.replaceAll("/", ""))));
                if (isMedia) {
                  return false;
                }

                const labels = host.split(".");
                return isBlockedHost(host) ||
                  labels.some(label => blockedLabels.has(label)) ||
                  blockedPaths.some(item => target.includes(item));
              };

              const blockedError = () =>
                new TypeError("Blocked by the embedded player");

              const adSelectors = [
                "ins.adsbygoogle",
                "[data-ad-client]",
                "[data-ad-slot]",
                "[data-adbridg-ad-client]",
                "[id^='google_ads_']",
                "[id^='aswift_']",
                "iframe[name^='google_ads_']",
                "iframe[src*='doubleclick']",
                "iframe[src*='googlesyndication']",
                ".ad-banner",
                ".ad-container",
                ".ad-overlay",
                ".ad-wrapper",
                ".ads-container",
                ".adsbox",
                ".advertisement",
                ".banner-ad",
                ".popup-ad",
                ".popunder",
                ".sponsored",
                "[aria-label='Advertisement']",
                "[aria-label='Anuncio']",
                "[class*='adsbygoogle']"
              ];

              const removableResourceTags = new Set([
                "IFRAME",
                "IMG",
                "LINK",
                "OBJECT",
                "SCRIPT",
                "SOURCE",
                "TRACK",
                "VIDEO"
              ]);
              const resourceAttributes = new Set([
                "data",
                "href",
                "src"
              ]);

              const resourceType = element => {
                switch (element.tagName) {
                  case "SCRIPT": return "script";
                  case "IMG": return "image";
                  case "IFRAME": return "document";
                  case "LINK": return "stylesheet";
                  case "VIDEO": case "AUDIO": case "TRACK": return "media";
                  case "SOURCE": return /^(VIDEO|AUDIO)$/.test(element.parentElement?.tagName)
                    ? "media" : "other";
                  default: return "other";
                }
              };

              // Full-viewport transparent layers are how these pages steal the
              // first click. A layer that covers the page and holds no media is
              // never legitimate player chrome.
              const isClickTrap = element => {
                if (!(element instanceof HTMLElement) ||
                    element.querySelector("video, audio, canvas")) {
                  return false;
                }

                const style = getComputedStyle(element);
                if (style.position !== "fixed" &&
                    style.position !== "absolute") {
                  return false;
                }

                const rect = element.getBoundingClientRect();
                const coversViewport =
                  rect.width >= innerWidth * 0.9 &&
                  rect.height >= innerHeight * 0.9;
                const transparent =
                  style.opacity !== "" && Number(style.opacity) <= 0.05;
                const zIndex = Number(style.zIndex) || 0;
                return coversViewport && (transparent || zIndex >= 2147483000);
              };

              const clean = root => {
                if (!root || typeof root.querySelectorAll !== "function") {
                  return;
                }

                for (const selector of adSelectors) {
                  if (typeof root.matches === "function" &&
                      root.matches(selector)) {
                    root.remove();
                    return;
                  }

                  for (const element of root.querySelectorAll(selector)) {
                    element.remove();
                  }
                }

                if (root instanceof Element &&
                    removableResourceTags.has(root.tagName)) {
                  const rootAddress =
                    root.getAttribute("src") ||
                    root.getAttribute("href") ||
                    root.getAttribute("data");
                  if (rootAddress && isBlocked(rootAddress, resourceType(root))) {
                    root.remove();
                    return;
                  }
                }

                for (const element of root.querySelectorAll(
                    "[src], [href], object[data]")) {
                  if (!removableResourceTags.has(element.tagName)) {
                    continue;
                  }

                  const address =
                    element.getAttribute("src") ||
                    element.getAttribute("href") ||
                    element.getAttribute("data");
                  if (address && isBlocked(address, resourceType(element))) {
                    element.remove();
                  }
                }

                // Popups need a target to land in; without one the click stays
                // on this page.
                for (const anchor of root.querySelectorAll("a[target]")) {
                  if (anchor.target && anchor.target !== "_self") {
                    anchor.removeAttribute("target");
                  }
                }

                for (const form of root.querySelectorAll("form[target]")) {
                  form.removeAttribute("target");
                }

                if (isClickTrap(root)) {
                  root.remove();
                  return;
                }

                for (const element of root.querySelectorAll(
                    "div, span, a, section")) {
                  if (isClickTrap(element)) {
                    element.remove();
                  }
                }
              };

              if (!globalThis[marker]) {
                if (!requestOnly) {
                  const style = document.createElement("style");
                  style.id = "umd-embedded-adblock-style";
                  style.textContent =
                    adSelectors.join(",") +
                    "{display:none!important;visibility:hidden!important;" +
                    "width:0!important;height:0!important;" +
                    "min-width:0!important;min-height:0!important;" +
                    "margin:0!important;padding:0!important;}";
                  (document.head || document.documentElement)?.appendChild(style);
                }

                const originalFetch =
                  typeof globalThis.fetch === "function"
                    ? globalThis.fetch.bind(globalThis)
                    : null;
                if (originalFetch) {
                  globalThis.fetch = (input, init) =>
                    isBlocked(input)
                      ? Promise.reject(blockedError())
                      : originalFetch(input, init);
                }

                if (typeof XMLHttpRequest !== "undefined") {
                  const originalOpen = XMLHttpRequest.prototype.open;
                  const originalSend = XMLHttpRequest.prototype.send;
                  XMLHttpRequest.prototype.open =
                    function(method, url, ...rest) {
                      this.__umdBlockedRequest = isBlocked(url);
                      return originalOpen.call(this, method, url, ...rest);
                    };
                  XMLHttpRequest.prototype.send = function(body) {
                    if (this.__umdBlockedRequest) {
                      this.abort();
                      return;
                    }

                    return originalSend.call(this, body);
                  };
                }

                if (typeof navigator.sendBeacon === "function") {
                  const originalSendBeacon =
                    navigator.sendBeacon.bind(navigator);
                  navigator.sendBeacon = (url, data) =>
                    isBlocked(url, "ping") ? true : originalSendBeacon(url, data);
                }

                // Prevent dynamically created resources before assigning the
                // URL. MutationObserver runs after insertion, which can be too
                // late because browsers may start the download immediately.
                const originalSetAttribute = Element.prototype.setAttribute;
                Element.prototype.setAttribute =
                  function(name, value) {
                    const normalized = String(name).toLowerCase();
                    if (removableResourceTags.has(this.tagName) &&
                        resourceAttributes.has(normalized) &&
                        isBlocked(value, resourceType(this))) {
                      return;
                    }

                    return originalSetAttribute.call(this, name, value);
                  };

                if (typeof Element.prototype.setAttributeNS === "function") {
                  const originalSetAttributeNS =
                    Element.prototype.setAttributeNS;
                  Element.prototype.setAttributeNS =
                    function(namespace, name, value) {
                      const normalized = String(name).toLowerCase();
                      if (removableResourceTags.has(this.tagName) &&
                          resourceAttributes.has(normalized) &&
                          isBlocked(value, resourceType(this))) {
                        return;
                      }

                      return originalSetAttributeNS.call(
                        this,
                        namespace,
                        name,
                        value);
                    };
                }

                const protectUrlProperty = (type, property) => {
                  if (typeof type !== "function") {
                    return;
                  }

                  const descriptor = Object.getOwnPropertyDescriptor(
                    type.prototype,
                    property);
                  if (!descriptor?.get ||
                      !descriptor?.set ||
                      descriptor.configurable === false) {
                    return;
                  }

                  try {
                    Object.defineProperty(type.prototype, property, {
                      configurable: descriptor.configurable,
                      enumerable: descriptor.enumerable,
                      get: descriptor.get,
                      set(value) {
                        if (!isBlocked(value, resourceType(this))) {
                          descriptor.set.call(this, value);
                        }
                      }
                    });
                  } catch {
                    // Some WebViews expose non-patchable DOM prototypes;
                    // setAttribute and the request API wrappers still apply.
                  }
                };

                protectUrlProperty(globalThis.HTMLIFrameElement, "src");
                protectUrlProperty(globalThis.HTMLImageElement, "src");
                protectUrlProperty(globalThis.HTMLLinkElement, "href");
                protectUrlProperty(globalThis.HTMLObjectElement, "data");
                protectUrlProperty(globalThis.HTMLScriptElement, "src");
                protectUrlProperty(globalThis.HTMLSourceElement, "src");
                protectUrlProperty(globalThis.HTMLTrackElement, "src");

                // Image, EventSource, WebSocket and Worker are all used to
                // fetch or phone home once fetch and XHR are wrapped. Image()
                // uses the patched HTMLImageElement.src setter above.

                if (typeof EventSource === "function") {
                  const NativeEventSource = EventSource;
                  globalThis.EventSource = function(url, config) {
                    if (isBlocked(url)) {
                      throw blockedError();
                    }

                    return new NativeEventSource(url, config);
                  };
                  globalThis.EventSource.prototype =
                    NativeEventSource.prototype;
                }

                if (typeof WebSocket === "function") {
                  const NativeWebSocket = WebSocket;
                  globalThis.WebSocket = function(url, protocols) {
                    if (isBlocked(url, "websocket")) {
                      throw blockedError();
                    }

                    return new NativeWebSocket(url, protocols);
                  };
                  globalThis.WebSocket.prototype = NativeWebSocket.prototype;
                }

                if (typeof Worker === "function") {
                  const NativeWorker = Worker;
                  globalThis.Worker = function(url, config) {
                    if (isBlocked(url, "script")) {
                      throw blockedError();
                    }

                    return new NativeWorker(url, config);
                  };
                  globalThis.Worker.prototype = NativeWorker.prototype;
                }

                if (requestOnly) {
                  globalThis[marker] = { isBlocked };
                  return "active";
                }

                // Popups and forced redirects.
                window.open = () => null;
                globalThis.addEventListener(
                  "click",
                  event => {
                    const anchor =
                      event.target &&
                      typeof event.target.closest === "function"
                        ? event.target.closest("a[href]")
                        : null;
                    if (!anchor) {
                      return;
                    }

                    const url = resolve(anchor.href);
                    if (!url) {
                      return;
                    }

                    const host = url.hostname.toLowerCase().replace(/\.$/, "");
                    // Player pages navigate within their own site; a click that
                    // jumps to another site is the advertisement.
                    if (isBlocked(anchor.href) ||
                        (url.protocol.startsWith("http") && !sameSite(host))) {
                      event.preventDefault();
                      event.stopImmediatePropagation();
                    }
                  },
                  true);

                const pendingRoots = new Set();
                let cleanScheduled = false;
                const scheduleClean = root => {
                  if (root?.nodeType === Node.ELEMENT_NODE) {
                    pendingRoots.add(root);
                  }

                  if (cleanScheduled) {
                    return;
                  }

                  cleanScheduled = true;
                  queueMicrotask(() => {
                    cleanScheduled = false;
                    const roots = Array.from(pendingRoots);
                    pendingRoots.clear();
                    for (const item of roots) {
                      if (item.isConnected) {
                        clean(item);
                      }
                    }
                  });
                };

                const observer = new MutationObserver(records => {
                  for (const record of records) {
                    if (record.type === "attributes") {
                      scheduleClean(record.target);
                      continue;
                    }

                    for (const node of record.addedNodes) {
                      scheduleClean(node);
                    }
                  }
                });
                observer.observe(
                  document.documentElement,
                  {
                    attributes: true,
                    attributeFilter: ["data", "href", "src", "target"],
                    childList: true,
                    subtree: true
                  });

                globalThis[marker] = {
                  clean,
                  isBlocked,
                  observer,
                  scheduleClean
                };
              }

              globalThis[marker].clean(document);
              return "active";
            })();
            """;
    }

    private static string Serialize(string[] values) =>
        JsonSerializer.Serialize(
            values
                .Select(value => value.ToLowerInvariant())
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray(),
            EmbeddedWebAdBlockerJsonContext.Default.StringArray);
}
