using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Platform;
using UniversalMediaDistribution.UI.Playback;

namespace UniversalMediaDistribution.Desktop.Web;

/// <summary>TV input scoped to this WebView2; never global mouse/keyboard hooks.</summary>
[SupportedOSPlatform("windows")]
public sealed partial class WebView2RemoteInput : INativeWebViewRemoteInput
{
    private ICoreWebView2DevToolsProtocolEventReceiver? _receiver;
    private BindingHandler? _handler;
    private nint _attachedTo;
    private long _token;
    private NativeWebView? _view;
    private ICoreWebView2? _core;
    private string? _world;
    private string? _script;

    public Task Ready { get; private set; } = Task.CompletedTask;

    public void Attach(NativeWebView webView, IPlatformHandle? handle, Func<bool, bool> activate)
    {
        if ((handle ?? webView.TryGetPlatformHandle()) is not
            IWindowsWebView2PlatformHandle { CoreWebView2: not 0 } native ||
            native.CoreWebView2 == _attachedTo) return;

        Detach();
        var core = GetCore(native);
        Marshal.ThrowExceptionForHR(core.GetDevToolsProtocolEventReceiver("Runtime.bindingCalled", out var receiverPointer));
        try
        {
            _receiver = (ICoreWebView2DevToolsProtocolEventReceiver)WebView2Interop.Wrappers.GetOrCreateObjectForComInstance(
                receiverPointer, CreateObjectFlags.None);
        }
        finally { Marshal.Release(receiverPointer); }
        var binding = "umdRemote" + Guid.NewGuid().ToString("N");
        _handler = new BindingHandler(binding, activate);
        Marshal.ThrowExceptionForHR(_receiver.AddEventReceived(_handler, out _token));
        _attachedTo = native.CoreWebView2;
        _core = core;
        _view = webView;
        webView.NavigationCompleted += OnNavigationCompleted;
        Ready = RegisterAsync(core, binding, _handler);
    }

    private async Task RegisterAsync(ICoreWebView2 core, string binding, BindingHandler handler)
    {
        try
        {
            // Plain Enter is not a WebView2 accelerator. An isolated world
            // observes real keyboard events in each frame without exposing a
            // native-click binding to the untrusted page's JavaScript world.
            var world = binding + "World";
            await CallAsync(core, "Runtime.enable", "{}");
            await CallAsync(core, "Runtime.addBinding", $"{{\"name\":\"{binding}\",\"executionContextName\":\"{world}\"}}");
            if (!ReferenceEquals(_handler, handler)) return;
            var script = $$"""
                (() => {
                  if (globalThis.__umdRemoteInstalled || typeof globalThis.{{binding}} !== 'function') return;
                  globalThis.__umdRemoteInstalled = true;
                  const key = e => {
                    if (!e.isTrusted || e.key !== 'Enter' || e.altKey || e.ctrlKey || e.shiftKey || e.metaKey) return;
                    e.preventDefault();
                    e.stopImmediatePropagation();
                    if (e.type === 'keydown' && !e.repeat) globalThis.{{binding}}('center');
                  };
                  addEventListener('keydown', key, true);
                  addEventListener('keyup', key, true);
                  addEventListener('keypress', key, true);
                })();
                """;
            await CallAsync(core, "Page.addScriptToEvaluateOnNewDocument",
                $"{{\"source\":\"{JsonEncodedText.Encode(script)}\",\"worldName\":\"{world}\",\"runImmediately\":true}}");
            if (!ReferenceEquals(_handler, handler)) return;
            _world = world;
            _script = script;
            await InstallExistingFramesAsync(core, world, script, handler);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.TraceWarning($"WebView remote listener failed: {exception.Message}");
        }
    }

    private async void OnNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs args)
    {
        try
        {
            await Ready;
            if (ReferenceEquals(sender, _view) && _core is { } core && _handler is { } handler &&
                _world is { } world && _script is { } script)
                await InstallExistingFramesAsync(core, world, script, handler);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.TraceWarning($"WebView remote frame setup failed: {exception.Message}");
        }
    }

    private async Task InstallExistingFramesAsync(ICoreWebView2 core, string world, string script, BindingHandler handler)
    {
        // runImmediately can race the initial navigation: explicitly establish
        // the isolated world in frames that already existed when registration
        // finished. The document-start script covers subsequently created frames.
        using var tree = JsonDocument.Parse(await CallAsync(core, "Page.getFrameTree", "{}"));
        static IEnumerable<string> Frames(JsonElement branch)
        {
            yield return branch.GetProperty("frame").GetProperty("id").GetString()!;
            if (branch.TryGetProperty("childFrames", out var children))
                foreach (var child in children.EnumerateArray())
                    foreach (var id in Frames(child)) yield return id;
        }
        foreach (var id in Frames(tree.RootElement.GetProperty("frameTree")))
        {
            if (!ReferenceEquals(_handler, handler)) return;
            using var context = JsonDocument.Parse(await CallAsync(core, "Page.createIsolatedWorld",
                $"{{\"frameId\":\"{JsonEncodedText.Encode(id)}\",\"worldName\":\"{world}\"}}"));
            var contextId = context.RootElement.GetProperty("executionContextId").GetInt32();
            if (!ReferenceEquals(_handler, handler)) return;
            await CallAsync(core, "Runtime.evaluate", FormattableString.Invariant(
                $"{{\"expression\":\"{JsonEncodedText.Encode(script)}\",\"contextId\":{contextId}}}"));
        }
    }

    public void Detach()
    {
        if (_view is not null) _view.NavigationCompleted -= OnNavigationCompleted;
        _view = null;
        _core = null;
        _world = null;
        _script = null;
        _handler?.Deactivate();
        // AdapterDestroyed may arrive after WebView2 has already closed.
        _receiver?.RemoveEventReceived(_token);
        _receiver = null;
        _handler = null;
        _attachedTo = 0;
        _token = 0;
    }

    public async ValueTask ClickCenterAsync(NativeWebView webView)
    {
        if (webView.TryGetPlatformHandle() is not IWindowsWebView2PlatformHandle { CoreWebView2: not 0 } native) return;
        var source = webView.Source;
        var core = GetCore(native);
        // CSS pixels account for DPI, resizing and page zoom. Native input
        // reaches cross-origin frames and browser video controls too.
        using var metrics = JsonDocument.Parse(await CallAsync(core, "Page.getLayoutMetrics", "{}"));
        var viewport = metrics.RootElement.GetProperty("cssVisualViewport");
        var x = viewport.GetProperty("clientWidth").GetDouble() / 2;
        var y = viewport.GetProperty("clientHeight").GetDouble() / 2;
        if (!(x > 0 && y > 0) || !webView.IsEffectivelyVisible || webView.Source != source ||
            webView.TryGetPlatformHandle() is not IWindowsWebView2PlatformHandle current ||
            current.CoreWebView2 != native.CoreWebView2) return;

        string Input(string type) => FormattableString.Invariant(
            $"{{\"type\":\"{type}\",\"x\":{x},\"y\":{y},\"button\":\"left\",\"clickCount\":1}}");
        await CallAsync(core, "Input.dispatchMouseEvent", Input("mousePressed"));
        await CallAsync(core, "Input.dispatchMouseEvent", Input("mouseReleased"));
    }

    private static ICoreWebView2 GetCore(IWindowsWebView2PlatformHandle native) =>
        (ICoreWebView2)WebView2Interop.Wrappers.GetOrCreateObjectForComInstance(native.CoreWebView2, CreateObjectFlags.None);

    private static async Task<string> CallAsync(ICoreWebView2 core, string method, string json)
    {
        var completion = new ProtocolCompletion();
        Marshal.ThrowExceptionForHR(core.CallDevToolsProtocolMethod(method, json, completion));
        return await completion.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [GeneratedComClass]
    private sealed partial class BindingHandler(string binding, Func<bool, bool> activate) : ICoreWebView2DevToolsProtocolEventReceivedEventHandler
    {
        private Func<bool, bool>? _activate = activate;
        public void Deactivate() => _activate = null;
        public int Invoke(nint sender, ICoreWebView2DevToolsProtocolEventReceivedEventArgs args)
        {
            nint json = 0;
            try
            {
                if (_activate is not null && args.GetParameterObjectAsJson(out json) == 0 && json != 0)
                {
                    using var document = JsonDocument.Parse(Marshal.PtrToStringUni(json)!);
                    if (document.RootElement.GetProperty("name").GetString() == binding &&
                        document.RootElement.GetProperty("payload").GetString() == "center") _activate(false);
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                System.Diagnostics.Trace.TraceWarning($"WebView remote callback failed: {exception.Message}");
            }
            finally { if (json != 0) Marshal.FreeCoTaskMem(json); }
            return 0;
        }
    }

    [GeneratedComClass]
    private sealed partial class ProtocolCompletion : ICoreWebView2CallDevToolsProtocolMethodCompletedHandler
    {
        private readonly TaskCompletionSource<string> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<string> Task => _completion.Task;
        public int Invoke(int errorCode, string result)
        {
            if (errorCode < 0) _completion.TrySetException(new COMException("WebView2 input failed", errorCode));
            else _completion.TrySetResult(result);
            return 0;
        }
    }
}

// Vtable order and GUIDs from Microsoft.Web.WebView2 1.0.3179.45/WebView2.idl.
[GeneratedComInterface, Guid("b32ca51a-8371-45e9-9317-af021d080367")]
internal partial interface ICoreWebView2DevToolsProtocolEventReceiver
{
    [PreserveSig] int AddEventReceived(ICoreWebView2DevToolsProtocolEventReceivedEventHandler handler, out long token);
    [PreserveSig] int RemoveEventReceived(long token);
}
[GeneratedComInterface, Guid("e2fda4be-5456-406c-a261-3d452138362c")]
internal partial interface ICoreWebView2DevToolsProtocolEventReceivedEventHandler
{
    [PreserveSig] int Invoke(nint sender, ICoreWebView2DevToolsProtocolEventReceivedEventArgs args);
}
[GeneratedComInterface, Guid("653c2959-bb3a-4377-8632-b58ada4e66c4")]
internal partial interface ICoreWebView2DevToolsProtocolEventReceivedEventArgs
{
    [PreserveSig] int GetParameterObjectAsJson(out nint json);
}
[GeneratedComInterface, Guid("5c4889f0-5ef6-4c5a-952c-d8f1b92d0574")]
internal partial interface ICoreWebView2CallDevToolsProtocolMethodCompletedHandler
{
    [PreserveSig] int Invoke(int errorCode, [MarshalAs(UnmanagedType.LPWStr)] string result);
}
