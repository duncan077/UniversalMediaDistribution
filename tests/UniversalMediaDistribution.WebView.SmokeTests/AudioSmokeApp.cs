using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using UniversalMediaDistribution.Desktop.Web;

namespace UniversalMediaDistribution.WebView.SmokeTests;

[SupportedOSPlatform("windows")]
public sealed class AudioSmokeApp : Avalonia.Application
{
    public override void OnFrameworkInitializationCompleted()
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
        var window = new Window { Width = 320, Height = 200, ShowInTaskbar = false, ShowActivated = false, Position = new PixelPoint(-32000, -32000) };
        desktop.MainWindow = window;
        window.Opened += async (_, _) =>
        {
            try
            {
                await RunAsync(window);
                Console.WriteLine("PASS: real WebView audio relayed into host session; silent capture restores browser audio; detach and reattach work.");
                desktop.Shutdown(0);
            }
            catch (Exception exception) { Console.Error.WriteLine(exception); desktop.Shutdown(1); }
        };
        base.OnFrameworkInitializationCompleted();
    }

    private static async Task RunAsync(Window window)
    {
        using var fixture = new FixtureServer(null);
        var view = new NativeWebView { Source = new Uri(fixture.Url) };
        await using var relay = new WebView2AudioRelay();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        view.NavigationCompleted += (_, _) => ready.TrySetResult();
        window.Content = view;
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
        // A local WebAudio source requires no media server, user cookies,
        // remote content, microphone, or real Discord connection.
        await view.InvokeScript("window.ctx=new AudioContext();window.osc=ctx.createOscillator();window.gain=ctx.createGain();gain.gain.value=0.001;osc.connect(gain).connect(ctx.destination);osc.start();ctx.resume();");
        var handle = view.TryGetPlatformHandle();
        var native = (IWindowsWebView2PlatformHandle)handle!;
        var core = (ICoreWebView2)WebView2Interop.Wrappers.GetOrCreateObjectForComInstance(native.CoreWebView2, CreateObjectFlags.None);
        Marshal.ThrowExceptionForHR(core.GetBrowserProcessId(out var browserId));
        var sourceId = await FindSourceAsync(browserId);
        for (var iteration = 0; iteration < 2; iteration++)
        {
            relay.Attach(view, handle);
            await Task.Delay(1500);
            await WaitAsync(async () => await Task.Run(() =>
            {
                var sessions = Inspect();
                return sessions.Any(s => s.Pid == sourceId && s.Muted) &&
                    sessions.Any(s => s.Pid == Environment.ProcessId && s.Peak > 0.00001f && s.Name == "UniversalMediaDistribution");
            }));
            Console.WriteLine($"Iteration {iteration + 1}: browser audio process {sourceId} muted; host {Environment.ProcessId} has nonzero audio.");
            if (iteration == 0)
            {
                // Keep the browser audio stream alive but silent. The old
                // fallback depended on its muted meter and never recovered.
                await view.InvokeScript("gain.gain.value=0;");
                await WaitAsync(async () => await Task.Run(() => Inspect().Any(s => s.Pid == sourceId && !s.Muted)));
                await view.InvokeScript("gain.gain.value=0.001;");
                await Task.Delay(2000);
                await WaitAsync(async () => await Task.Run(() => Inspect().Any(s => s.Pid == sourceId && !s.Muted && s.Peak > 0.00001f)));
                Console.WriteLine("Silent relay fell back to browser sound and did not mute it again when audio resumed.");
            }
            relay.Detach();
            await WaitAsync(async () => await Task.Run(() => Inspect().Any(s => s.Pid == sourceId && !s.Muted && s.Peak > 0.00001f)));
        }
        await view.InvokeScript("ctx.close();");
        window.Content = null;
    }

    private static async Task<uint> FindSourceAsync(uint browserId)
    {
        uint result = 0;
        await WaitAsync(async () => await Task.Run(() =>
        {
            Marshal.ThrowExceptionForHR(AudioInterop.CoInitializeEx(0, 0));
            try
            {
                var enumerator = BrowserAudioSession.OpenEnumerator();
                try
                {
                    using var source = BrowserAudioSession.Find(enumerator, browserId);
                    if (source is null) return false;
                    result = source.ProcessId;
                    return true;
                }
                finally { AudioInterop.Release(enumerator); }
            }
            finally { AudioInterop.CoUninitialize(); }
        }));
        return result;
    }

    private static async Task WaitAsync(Func<Task<bool>> condition)
    {
        var deadline = Environment.TickCount64 + 15000;
        do
        {
            if (await condition()) return;
            await Task.Delay(100);
        } while (Environment.TickCount64 < deadline);
        throw new TimeoutException("Web audio smoke condition did not become true.");
    }

    private static List<(uint Pid, bool Muted, float Peak, string? Name)> Inspect()
    {
        Marshal.ThrowExceptionForHR(AudioInterop.CoInitializeEx(0, 0));
        try
        {
            var result = new List<(uint, bool, float, string?)>();
            var enumerator = BrowserAudioSession.OpenEnumerator();
            try
            {
                Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(0, 1, out var pointer));
                var device = AudioInterop.Take<IAudioDevice>(pointer);
                try
                {
                    Marshal.ThrowExceptionForHR(device.Activate(typeof(IAudioSessionManager).GUID, 23, 0, out pointer));
                    var manager = AudioInterop.Take<IAudioSessionManager>(pointer);
                    try
                    {
                        Marshal.ThrowExceptionForHR(manager.GetSessionEnumerator(out pointer));
                        var sessions = AudioInterop.Take<IAudioSessionEnumerator>(pointer);
                        try
                        {
                            Marshal.ThrowExceptionForHR(sessions.GetCount(out var count));
                            for (var i = 0; i < count; i++)
                            {
                                Marshal.ThrowExceptionForHR(sessions.GetSession(i, out pointer));
                                var session = AudioInterop.Take<IAudioSessionControl>(pointer);
                                try
                                {
                                    session.GetProcessId(out var pid);
                                    ((IAudioSessionVolume)session).GetMute(out var muted);
                                    ((IAudioMeter)session).GetPeakValue(out var peak);
                                    session.GetDisplayName(out var name);
                                    try { result.Add((pid, muted != 0, peak, Marshal.PtrToStringUni(name))); }
                                    finally { Marshal.FreeCoTaskMem(name); }
                                }
                                finally { AudioInterop.Release(session); }
                            }
                        }
                        finally { AudioInterop.Release(sessions); }
                    }
                    finally { AudioInterop.Release(manager); }
                }
                finally { AudioInterop.Release(device); }
            }
            finally { AudioInterop.Release(enumerator); }
            return result;
        }
        finally { AudioInterop.CoUninitialize(); }
    }
}

[GeneratedComInterface, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064")]
internal partial interface IAudioMeter
{
    [PreserveSig] int GetPeakValue(out float peak);
}
