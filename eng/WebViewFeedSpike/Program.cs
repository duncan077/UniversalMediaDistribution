using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using UniversalMediaDistribution.Catalog.ContentSources;

// Deliberately isolated from production. No cookie export, saved signed URLs,
// automatic CAPTCHA solving, browser security overrides or native playback.
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var endpoint = new Uri(args.FirstOrDefault() ?? "https://pelisplus.autos/pelicula/spider-man-un-nuevo-dia/");
        if (endpoint.Scheme != "https" || endpoint.IsLoopback) throw new ArgumentException("Public HTTPS URL required.");
        Application.Run(new SpikeWindow(endpoint));
    }
}

internal sealed class SpikeWindow : Form
{
    private readonly WebView2 _browser = new() { Dock = DockStyle.Fill };
    private readonly Label _status = new() { Dock = DockStyle.Top, Height = 55, Text = "Choose a server and press Play. Captured feeds are checked without cookies. No CAPTCHA automation." };
    private readonly Button _probe = new() { Dock = DockStyle.Bottom, Text = "Verify captured native feed", Enabled = false, Height = 38 };
    private readonly CancellationTokenSource _closing = new();
    private Uri _endpoint;
    private readonly string _runDirectory;
    private readonly HashSet<string> _events = new(StringComparer.Ordinal);
    private ApiContentVideoSource? _candidate;
    private bool _probing;
    private int _lines;

    public SpikeWindow(Uri endpoint)
    {
        _endpoint = endpoint;
        _runDirectory = Path.Combine(Path.GetTempPath(), "UMD-WebViewFeedSpike", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_runDirectory);
        Text = "UMD WebView feed spike";
        Width = 1150; Height = 850;
        Controls.Add(_browser); Controls.Add(_status); Controls.Add(_probe);
        var address = new TextBox { Text = endpoint.AbsoluteUri, Dock = DockStyle.Fill };
        var load = new Button { Text = "Load test URL", Dock = DockStyle.Right, Width = 120 };
        var navigation = new Panel { Dock = DockStyle.Top, Height = 32 };
        navigation.Controls.Add(address); navigation.Controls.Add(load); Controls.Add(navigation);
        load.Click += (_, _) =>
        {
            if (Uri.TryCreate(address.Text, UriKind.Absolute, out var target) && target.Scheme == "https" && !target.IsLoopback)
            { _endpoint = target; _browser.CoreWebView2?.Navigate(target.AbsoluteUri); }
        };
        _probe.Click += async (_, _) => await ProbeAsync();
        Shown += async (_, _) => await InitializeAsync();
        FormClosed += (_, _) => { _closing.Cancel(); _browser.Dispose(); _closing.Dispose(); };
    }

    private void Log(string text)
    {
        if (_lines++ >= 5000) return;
        File.AppendAllText(Path.Combine(_runDirectory, "events.log"), $"{DateTimeOffset.UtcNow:O} {text}{Environment.NewLine}");
    }

    private async Task InitializeAsync()
    {
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(_runDirectory, "profile"));
            await _browser.EnsureCoreWebView2Async(environment);
            var core = _browser.CoreWebView2;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, e) =>
            {
                if (Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri) && IsObservedAdHost(uri.IdnHost))
                    e.Response = environment.CreateWebResourceResponse(Stream.Null, 204, "No Content", "");
            };
            core.NewWindowRequested += (_, e) => { e.Handled = true; Log($"popup blocked host={(Uri.TryCreate(e.Uri, UriKind.Absolute, out var popup) ? popup.IdnHost : "unknown")}"); };
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.NavigationStarting += (_, e) =>
            {
                if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var target) ||
                    !VideoChallengeCapture.AllowsNavigation(_endpoint, target))
                { e.Cancel = true; Log("top navigation blocked"); return; }
                _candidate = null; _probe.Enabled = false;
                Log($"navigate host={target.IdnHost}");
            };
            core.NavigationCompleted += async (_, e) =>
            {
                Log($"navigation success={e.IsSuccess} error={e.WebErrorStatus}");
                if (!e.IsSuccess) return;
                try
                {
                    Log("observer installed=" + await core.ExecuteScriptAsync("!!window.__umdVideoChallengeInstalled"));
                    await core.ExecuteScriptAsync(VideoChallengeCapture.Script);
                    await core.ExecuteScriptAsync(Diagnostics);
                    await core.ExecuteScriptAsync(OverlayFilter);
                }
                catch (Exception failure) { Log("observer failure=" + failure.GetType().Name); }
            };
            core.FrameCreated += (_, e) => e.Frame.NavigationStarting += (_, _) =>
            {
                // A newly selected server must never verify the previous server's URL.
                _candidate = null; _probe.Enabled = false; _status.Text = "Server loading. Press Play to capture a fresh feed.";
            };
            core.ProcessFailed += (_, e) => Log($"browser failure={e.ProcessFailedKind}");
            core.WebResourceResponseReceived += (_, e) =>
            {
                if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri)) return;
                var mime = e.Response.Headers.Contains("Content-Type") ? e.Response.Headers.GetHeader("Content-Type").Split(';')[0] : "";
                if (mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase) || mime.Contains("mpegurl", StringComparison.OrdinalIgnoreCase) ||
                    mime.Contains("dash+xml", StringComparison.OrdinalIgnoreCase) || e.Response.StatusCode >= 400 || mime == "text/html")
                {
                    var record = $"response host={uri.IdnHost} status={e.Response.StatusCode} mime={mime}";
                    if (_events.Add(record)) Log(record);
                }
            };
            core.WebMessageReceived += (_, e) =>
            {
                string message;
                try { message = e.TryGetWebMessageAsString(); } catch { return; }
                if (message.Length > 16384) return;
                var original = new ApiContentVideoSource("WebView spike", _endpoint, kind: ApiContentVideoSourceKind.EmbeddedWeb);
                if (VideoChallengeCapture.Parse(message, original) is { } captured)
                {
                    if (_candidate?.PlaybackUri != captured.PlaybackUri)
                    {
                        Log($"capture player={captured.Referrer?.IdnHost} media={captured.PlaybackUri.IdnHost} mime={captured.MimeType}");
                        _status.Text = $"Captured {captured.MimeType} from {captured.Referrer?.IdnHost}. Verify to test outside the browser.";
                    }
                    _candidate = captured; _probe.Enabled = !_probing;
                    return;
                }
                try
                {
                    using var json = JsonDocument.Parse(message);
                    if (json.RootElement.TryGetProperty("spike", out var diagnostic) && diagnostic.ValueKind == JsonValueKind.String)
                    {
                        var safe = diagnostic.GetString() ?? "";
                        if (safe.Length < 500 && !safe.Contains('\n') && !safe.Contains('\r') && _events.Add(safe)) Log(safe);
                    }
                }
                catch (JsonException) { }
            };
            await core.AddScriptToExecuteOnDocumentCreatedAsync(VideoChallengeCapture.Script);
            await core.AddScriptToExecuteOnDocumentCreatedAsync(Diagnostics);
            await core.AddScriptToExecuteOnDocumentCreatedAsync(OverlayFilter);
            Log($"engine={environment.BrowserVersionString} starting={_endpoint.IdnHost}");
            core.Navigate(_endpoint.AbsoluteUri);
        }
        catch (Exception e) { Log($"startup failure={e.GetType().Name}"); _status.Text = e.GetType().Name; }
    }

    private const string OverlayFilter = """
                (() => {
                  window.open = () => null;
                  // Spike-only: observed full-page ad anchors consume every click
                  // when their popunder is blocked. Leave player/CAPTCHA nodes alone.
                  function clean() {
                    for (const node of document.querySelectorAll('a[href],iframe')) {
                      if (node.tagName === 'IFRAME' && (!node.getAttribute('src') || node.src === 'about:blank')) {
                        const frameRect = node.getBoundingClientRect();
                        if (frameRect.width >= innerWidth * .95 && frameRect.height >= innerHeight * .95) { node.remove(); continue; }
                        let overlay = node;
                        for (let depth = 0; overlay && depth < 4; depth++, overlay = overlay.parentElement) {
                          const rect = overlay.getBoundingClientRect(), style = getComputedStyle(overlay);
                          if (style.position === 'fixed' && rect.width >= innerWidth * .95 && rect.height >= innerHeight * .95) {
                            overlay.remove(); break;
                          }
                        }
                      }
                      try { const host = new URL(node.href || node.src, location.href).hostname;
                        if (node.tagName === 'A' && host !== location.hostname) {
                          const r = node.getBoundingClientRect(), s = getComputedStyle(node);
                          if (['fixed','absolute'].includes(s.position) && r.width >= innerWidth * .8 && r.height >= innerHeight * .8) {
                            node.remove(); continue;
                          }
                        }
                        if (['protrafficinspector.com','workdeadlinededicate.com'].some(d => host === d || host.endsWith('.' + d))) node.remove();
                      } catch (_) {}
                    }
                  }
                  new MutationObserver(clean).observe(document, { childList:true, subtree:true, attributes:true, attributeFilter:['style','src','href'] });
                  // Stylesheets and delayed CSS sizing need not mutate attributes.
                  setInterval(clean, 500);
                })();
                """;

    private static bool IsObservedAdHost(string host) =>
        host is "protrafficinspector.com" or "workdeadlinededicate.com" ||
        host.EndsWith(".protrafficinspector.com", StringComparison.Ordinal) ||
        host.EndsWith(".workdeadlinededicate.com", StringComparison.Ordinal);

    private async Task ProbeAsync()
    {
        if (_candidate is not { } candidate || _probing) return;
        _probing = true; _probe.Enabled = false;
        try
        {
            var valid = await VideoChallengeMediaProbe.VerifyAsync(candidate, _closing.Token);
            Log($"probe player={candidate.Referrer?.IdnHost} media={candidate.PlaybackUri.IdnHost} success={valid}");
            _status.Text = valid ? "PASS: cookie-free native feed probe succeeded (not a VLC playback test)." : "FAIL: browser capture succeeded, but the cookie-free native probe failed.";
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Log($"probe failure={e.GetType().Name}"); }
        finally { _probing = false; if (!IsDisposed) _probe.Enabled = _candidate is not null; }
    }

    private const string Diagnostics = """
        (() => {
          function report(value) {
            try { if (window.chrome?.webview) window.chrome.webview.postMessage(JSON.stringify({spike:value})); } catch (_) {}
            if (parent !== window) parent.postMessage({spikeFrame:value}, '*');
          }
          window.addEventListener('message', e => {
            if (Array.from(document.querySelectorAll('iframe')).some(f => f.contentWindow === e.source) && typeof e.data?.spikeFrame === 'string') report(e.data.spikeFrame);
          });
          report('frame host=' + location.hostname);
          if (!location.hostname) {
            try {
              let node = frameElement;
              for (let i = 0; node && i < 4; i++, node = node.parentElement) {
                const r = node.getBoundingClientRect(), s = parent.getComputedStyle(node);
                report('blank-frame ancestor=' + i + ' tag=' + node.tagName + ' position=' + s.position + ' width=' + Math.round(r.width) + ' height=' + Math.round(r.height));
              }
            } catch (_) {}
          }
          window.addEventListener('click', e => {
            const target = e.target;
            if (target instanceof Element) report('click host=' + location.hostname + ' target=' + target.tagName + ' id=' + target.id.slice(0,80) + ' class=' + String(target.className).slice(0,120));
          }, true);
          setInterval(() => {
            for (const v of document.querySelectorAll('video')) report('video host=' + location.hostname + ' playing=' + (!v.paused && v.currentTime > 0) + ' ready=' + v.readyState + ' source=' + (v.currentSrc.startsWith('blob:') ? 'blob' : v.currentSrc.startsWith('http') ? 'http' : 'none') + ' encrypted=' + !!v.mediaKeys + ' error=' + (v.error?.code || 0));
          }, 5000);
        })();
        """;
}
