using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using UniversalMediaDistribution.Desktop.Web;
using UniversalMediaDistribution.UI.Playback;

namespace UniversalMediaDistribution.WebView.SmokeTests;

/// <summary>Opt-in real WebView2 test; no TV, ADB, remote providers or user profile.</summary>
[SupportedOSPlatform("windows")]
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows()) return 2;
        if (args.Contains("--audio"))
        {
            Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", "--autoplay-policy=no-user-gesture-required");
            Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", Path.Combine(Path.GetTempPath(), "UmdWebAudioSmoke", Guid.NewGuid().ToString("N")));
            return AppBuilder.Configure<AudioSmokeApp>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
        }
        return AppBuilder.Configure<SmokeApp>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
    }
}

[SupportedOSPlatform("windows")]
public sealed class SmokeApp : Avalonia.Application
{
    public override void Initialize()
    {
        System.Diagnostics.Trace.Listeners.Add(new System.Diagnostics.ConsoleTraceListener());
        Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
        var window = new Window { Width = 800, Height = 450, Title = "UMD WebView remote smoke test" };
        desktop.MainWindow = window;
        window.Opened += async (_, _) =>
        {
            try
            {
                await RunAsync(window);
                Console.WriteLine("PASS: native center click, native Enter, held Enter, resized center, cross-origin iframe, forged-key rejection, isolated binding, disabled callback.");
                desktop.Shutdown(0);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                desktop.Shutdown(1);
            }
        };
        base.OnFrameworkInitializationCompleted();
    }

    private static async Task RunAsync(Window window)
    {
        using var child = new FixtureServer(null);
        using var parent = new FixtureServer(child.Url);
        var allowed = true;
        // Exercise the real native adapter directly: production player URL
        // validation intentionally refuses loopback addresses and stays intact.
        var input = new WebView2RemoteInput();
        var view = new NativeWebView { Source = new Uri(parent.Url) };
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        view.NavigationCompleted += (_, _) => loaded.TrySetResult();
        view.AdapterCreated += (_, args) => input.Attach(view, args.TryGetPlatformHandle(), repeat =>
        {
            Console.WriteLine($"Keyboard callback: {repeat}, allowed: {allowed}");
            if (!allowed) return false;
            if (!repeat) Dispatcher.UIThread.Post(async () => await input.ClickCenterAsync(view));
            return true;
        });
        view.AdapterDestroyed += (_, _) => input.Detach();
        var host = new ContentControl { Content = view, Focusable = true,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        window.Content = host;
        await loaded.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await input.Ready.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitAsync(async () => (await view.InvokeScript("String(!!window.fixtureReady)"))?.Trim('"') == "true");
        await Task.Delay(800);
        host.Focus(NavigationMethod.Directional);
        await input.ClickCenterAsync(view);
        await CheckClickAsync(view, 1);
        Console.WriteLine("Center click reached the cross-origin iframe.");

        window.Activate();
        var foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        var currentThread = GetCurrentThreadId();
        var attached = foregroundThread != currentThread && AttachThreadInput(currentThread, foregroundThread, true);
        try { SetForegroundWindow(window.TryGetPlatformHandle()!.Handle); }
        finally { if (attached) AttachThreadInput(currentThread, foregroundThread, false); }
        view.Focus(NavigationMethod.Directional);
        await Task.Delay(250);
        var target = GetFocus();
        var windowHandle = window.TryGetPlatformHandle()!.Handle;
        var className = new StringBuilder(256);
        GetClassName(target, className, className.Capacity);
        Console.WriteLine($"Native focused class: {className}");
        Check(target != 0 && target != windowHandle && IsChild(windowHandle, target), "Native WebView did not receive focus");
        SendEnter(windowHandle, repeats: 8);
        await Task.Delay(250);
        Console.WriteLine("Key trace result: " + await view.InvokeScript("JSON.stringify(window.keyTrace || null)"));
        await CheckClickAsync(view, 2);
        Check((await view.InvokeScript("String(!window.keyTrace)"))?.Trim('"') == "true",
            "The page also received Enter after center activation");
        await CheckClickAsync(view, 2);
        await Task.Delay(350);
        await CheckClickAsync(view, 2); // no release or auto-repeat click

        window.Width = 640;
        window.Height = 360;
        await Task.Delay(300);
        SendEnter(windowHandle, repeats: 0);
        await CheckClickAsync(view, 3);

        // Untrusted page code cannot manufacture a native center-click gesture
        // or see the host binding installed in the isolated execution world.
        await view.InvokeScript("dispatchEvent(new KeyboardEvent('keydown', {key:'Enter',bubbles:true}))");
        Check((await view.InvokeScript("String(Object.keys(globalThis).some(k => k.startsWith('umdRemote')))"))?.Trim('"') == "false",
            "Native click binding leaked into the page world");
        await Task.Delay(150);
        await CheckClickAsync(view, 3);

        allowed = false;
        SendEnter(windowHandle, repeats: 0);
        await Task.Delay(150);
        await CheckClickAsync(view, 3);
        input.Detach();
        window.Content = null;
    }

    private static async Task CheckClickAsync(NativeWebView view, int expected)
    {
        await WaitAsync(async () =>
        {
            var raw = await view.InvokeScript("JSON.stringify(window.lastClick || null)");
            if (string.IsNullOrEmpty(raw)) return false;
            using var first = JsonDocument.Parse(raw);
            using var parsed = first.RootElement.ValueKind == JsonValueKind.String
                ? JsonDocument.Parse(first.RootElement.GetString()!) : JsonDocument.Parse(raw);
            var report = parsed.RootElement;
            if (report.ValueKind == JsonValueKind.Null) return false;
            var count = report.GetProperty("count").GetInt32();
            Check(count <= expected, $"Extra center click: expected {expected}, got {count}");
            if (count != expected) return false;
            Check(report.GetProperty("trusted").GetBoolean(), "Click was not native/trusted");
            Check(Math.Abs(report.GetProperty("x").GetDouble() - report.GetProperty("width").GetDouble() / 2) <= 1,
                "Click was not horizontally centered");
            Check(Math.Abs(report.GetProperty("y").GetDouble() - report.GetProperty("height").GetDouble() / 2) <= 1,
                "Click was not vertically centered");
            return true;
        });
    }

    private static async Task WaitAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        do
        {
            if (await condition()) return;
            await Task.Delay(100);
        } while (DateTime.UtcNow < deadline);
        throw new TimeoutException("WebView smoke condition did not become true");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void SendEnter(nint window, int repeats)
    {
        // Only send actual keyboard input while this test owns foreground.
        // Posting WM_KEYDOWN does not exercise Chromium's physical key path.
        Check(GetForegroundWindow() == window, "Smoke window lost foreground; refusing to send keys elsewhere");
        var inputs = new List<NativeInput> { EnterInput(false) };
        for (var i = 0; i < repeats; i++)
            inputs.Add(EnterInput(false));
        inputs.Add(EnterInput(true));
        Check(SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<NativeInput>()) == inputs.Count,
            "Physical keyboard input failed");
    }

    private static NativeInput EnterInput(bool released) => new()
    {
        Type = 1, Data = new InputData { Keyboard = new KeyboardInput { Key = 13, Flags = released ? 2u : 0 } }
    };

    [StructLayout(LayoutKind.Sequential)] private struct NativeInput { public uint Type; public InputData Data; }
    [StructLayout(LayoutKind.Explicit, Size = 32)] private struct InputData { [FieldOffset(0)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort Key, Scan; public uint Flags, Time; public nuint Extra; }

    [DllImport("user32.dll")] private static extern nint GetFocus();
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AttachThreadInput(uint from, uint to, [MarshalAs(UnmanagedType.Bool)] bool attach);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint hwnd, StringBuilder text, int count);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsChild(nint parent, nint child);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, NativeInput[] inputs, int size);
}

internal sealed class FixtureServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly string _html;
    public string Url { get; }

    public FixtureServer(string? childUrl)
    {
        _listener.Start();
        Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/";
        _html = childUrl is null ? """
            <!doctype html><style>html,body{margin:0;width:100%;height:100%;background:#123;color:white}</style>
            <div style="position:absolute;top:50%;left:50%;transform:translate(-50%,-50%)">Remote center target</div>
            <script>let count=0;addEventListener('click',e=>parent.postMessage({count:++count,x:e.clientX,y:e.clientY,
              width:innerWidth,height:innerHeight,trusted:e.isTrusted},'*'));
              addEventListener('keydown',e=>parent.postMessage({keyTrace:e.key},'*'));</script>
            """ : $$"""
            <!doctype html><style>html,body{margin:0;width:100%;height:100%}iframe{border:0;width:100%;height:100%}</style>
            <iframe src="{{childUrl}}" onload="window.fixtureReady=true"></iframe>
            <script>addEventListener('message',e=>{if(e.origin==='{{childUrl.TrimEnd('/')}}'){
              if(e.data.keyTrace)window.keyTrace=e.data.keyTrace;else window.lastClick=e.data} });</script>
            """;
        _ = ServeAsync();
    }

    private async Task ServeAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _ = RespondAsync(client, _stop.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task RespondAsync(TcpClient client, CancellationToken token)
    {
        try
        {
            using (client)
            {
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, leaveOpen: true);
                while (await reader.ReadLineAsync(token) is { Length: > 0 }) { }
                var body = Encoding.UTF8.GetBytes(_html);
                var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(headers, token);
                await stream.WriteAsync(body, token);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }

    public void Dispose() { _stop.Cancel(); _listener.Stop(); _stop.Dispose(); }
}
