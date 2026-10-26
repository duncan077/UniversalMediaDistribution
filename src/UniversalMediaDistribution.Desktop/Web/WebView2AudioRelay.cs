using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Platform;
using UniversalMediaDistribution.UI.Diagnostics;
using UniversalMediaDistribution.UI.Playback;

namespace UniversalMediaDistribution.Desktop.Web;

/// <summary>
/// Relays only this WebView's browser audio into UMD's default WASAPI session.
/// The browser remains sandboxed; no page scripts, system mix, or microphone
/// are involved. Unsupported Windows versions retain normal browser output.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WebView2AudioRelay : INativeWebViewAudioRelay
{
    private readonly object _gate = new();
    private bool _disposed;
    private CancellationTokenSource? _stop;
    private Task _worker = Task.CompletedTask;
    private nint _attachedTo;

    public void Attach(NativeWebView view, IPlatformHandle? handle)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348) ||
            (handle ?? view.TryGetPlatformHandle()) is not IWindowsWebView2PlatformHandle { CoreWebView2: not 0 } native) return;

        try
        {
            var core = (ICoreWebView2)WebView2Interop.Wrappers.GetOrCreateObjectForComInstance(
                native.CoreWebView2, CreateObjectFlags.None);
            Marshal.ThrowExceptionForHR(core.GetBrowserProcessId(out var processId));
            lock (_gate)
            {
                if (_disposed || native.CoreWebView2 == _attachedTo) return;
                Detach();
                var previous = _worker;
                var stop = new CancellationTokenSource();
                _stop = stop;
                _attachedTo = native.CoreWebView2;
                _worker = Task.Run(async () =>
                {
                    // A new adapter may arrive before the old worker has restored
                    // its session mute. Never overlap relay ownership.
                    try
                    {
                        await previous.ConfigureAwait(false);
                        if (!stop.IsCancellationRequested) Run(processId, stop.Token);
                    }
                    catch (Exception exception)
                    {
                        ApplicationDiagnosticLog.Warning("playback.webview-audio", $"Browser output retained: {exception.Message}");
                    }
                    finally { stop.Dispose(); }
                });
            }
        }
        catch (Exception exception)
        {
            ApplicationDiagnosticLog.Warning("playback.webview-audio", $"Could not attach audio relay: {exception.Message}");
        }
    }

    public void Detach()
    {
        lock (_gate)
        {
            _attachedTo = 0;
            try { _stop?.Cancel(); }
            catch (ObjectDisposedException) { }
            _stop = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task worker;
        lock (_gate)
        {
            _disposed = true;
            Detach();
            worker = _worker;
        }
        await worker.ConfigureAwait(false);
    }

    internal static void Run(uint browserProcessId, CancellationToken stop)
    {
        Marshal.ThrowExceptionForHR(AudioInterop.CoInitializeEx(0, 0));
        try
        {
            using var browser = Process.GetProcessById(checked((int)browserProcessId));
            var enumerator = BrowserAudioSession.OpenEnumerator();
            try
            {
                var scratch = new byte[192000];
                var lastError = 0L;
                while (!stop.IsCancellationRequested && !browser.HasExited)
                {
                    try
                    {
                        using var source = BrowserAudioSession.Find(enumerator, browserProcessId);
                        if (source is not null && !Relay(enumerator, source, scratch, stop))
                            return;
                    }
                    catch (Exception exception)
                    {
                        // All output/capture resources and the source mute
                        // are restored before retrying a removed/replaced device.
                        if (lastError == 0 || Environment.TickCount64 - lastError > 30000)
                        {
                            ApplicationDiagnosticLog.Warning("playback.webview-audio", $"Browser output retained; retrying: {exception.Message}");
                            lastError = Environment.TickCount64;
                        }
                        if (stop.WaitHandle.WaitOne(2000)) break;
                    }
                    if (stop.WaitHandle.WaitOne(250)) break;
                }
            }
            finally { AudioInterop.Release(enumerator); }
        }
        finally { AudioInterop.CoUninitialize(); }
    }

    private static bool Relay(IAudioDeviceEnumerator enumerator, BrowserAudioSession source, byte[] scratch, CancellationToken stop)
    {
        // Keep the device the browser is already using (including per-app
        // routing to headphones), rather than silently moving sound elsewhere.
        Marshal.ThrowExceptionForHR(enumerator.GetDevice(source.DeviceId, out var pointer));
        var device = AudioInterop.Take<IAudioDevice>(pointer);
        try
        {
            using var capture = ProcessAudioCapture.Open(source.ProcessId);
            using var output = ProcessAudioOutput.Open(device);
            if (stop.IsCancellationRequested) return true;
            var gain = source.Volume;
            if (!output.IsAudible || gain <= 0) return false;
            var health = new AudioRelayHealth(Environment.TickCount64);
            var ownsOutput = false;
            var nextCheck = Environment.TickCount64;
            while (!stop.IsCancellationRequested)
            {
                var signalCaptured = false;
                capture.Drain((data, bytes) =>
                {
                    var samples = MemoryMarshal.Cast<byte, short>(data.AsSpan(0, bytes));
                    signalCaptured |= AudioRelayHealth.ContainsSignal(samples);
                    for (var i = 0; i < samples.Length; i++)
                    {
                        samples[i] = (short)(samples[i] * gain);
                    }
                    output.Add(data, bytes);
                }, scratch);
                var written = output.Pump();
                var now = Environment.TickCount64;
                health.Observe(now, signalCaptured, written > 0);
                if (health.MustRestoreBrowser(now))
                {
                    ApplicationDiagnosticLog.Warning("playback.webview-audio",
                        "Browser output restored: relay received no audible samples or output stopped progressing.");
                    return false; // Do not repeatedly mute an unsupported stream.
                }
                if (!ownsOutput && health.CanTakeOver)
                {
                    source.Mute();
                    ownsOutput = true;
                    ApplicationDiagnosticLog.Information("playback.webview-audio",
                        $"sourceProcessId={source.ProcessId}; outputProcessId={Environment.ProcessId}; session=process-default; device=browser; audioConfirmed=true");
                }
                if (Environment.TickCount64 >= nextCheck)
                {
                    if (!source.IsActive) return true;
                    if (!output.IsAudible) return false;
                    gain = source.Volume;
                    nextCheck = Environment.TickCount64 + 250;
                }
                if (stop.WaitHandle.WaitOne(5)) return true;
            }
            return true;
        }
        finally { AudioInterop.Release(device); }
    }

}
