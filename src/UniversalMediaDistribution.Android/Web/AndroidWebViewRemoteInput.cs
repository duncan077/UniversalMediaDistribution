using Android.OS;
using Android.Runtime;
using Android.Views;
using Avalonia.Controls;
using Avalonia.Platform;
using UniversalMediaDistribution.UI.Playback;
using PlatformWebView = Android.Webkit.WebView;

namespace UniversalMediaDistribution.Android.Web;

public sealed class AndroidWebViewRemoteInput : INativeWebViewRemoteInput
{
    private PlatformWebView? _view;
    private RemoteKeyListener? _listener;

    public void Attach(NativeWebView webView, IPlatformHandle? handle,
        Func<bool, bool> activate)
    {
        var native = Resolve(webView, handle);
        if (native is null || native.Handle == _view?.Handle)
        {
            return;
        }

        Detach();
        _view = native;
        _listener = new RemoteKeyListener(activate);
        native.SetOnKeyListener(_listener);
    }

    public void Detach()
    {
        _listener?.Deactivate();
        if (_view is { Handle: not 0 })
        {
            _view.SetOnKeyListener(null);
        }
        _listener?.Dispose();
        _listener = null;
        _view = null;
    }

    public ValueTask ClickCenterAsync(NativeWebView webView)
    {
        if (Resolve(webView) is not { IsShown: true, Width: > 0, Height: > 0 } native)
        {
            return ValueTask.CompletedTask;
        }

        // Coordinates are local physical pixels, not Avalonia DIPs or the
        // screen center. Dispatch through WebView so iframes and video controls
        // receive the same down/up sequence as an ordinary touchscreen tap.
        var time = SystemClock.UptimeMillis();
        var x = native.Width / 2f;
        var y = native.Height / 2f;
        using var down = MotionEvent.Obtain(time, time, MotionEventActions.Down, x, y, 0);
        using var up = MotionEvent.Obtain(time, time + 1, MotionEventActions.Up, x, y, 0);
        if (down is not null && up is not null)
        {
            down.SetSource(InputSourceType.Touchscreen);
            up.SetSource(InputSourceType.Touchscreen);
            native.DispatchTouchEvent(down);
            native.DispatchTouchEvent(up);
        }
        return ValueTask.CompletedTask;
    }

    private static PlatformWebView? Resolve(NativeWebView view, IPlatformHandle? handle = null) =>
        (handle ?? view.TryGetPlatformHandle()) is IAndroidWebViewPlatformHandle { WebKitWebView: not 0 } android
            ? Java.Lang.Object.GetObject<PlatformWebView>(android.WebKitWebView, JniHandleOwnership.DoNotTransfer)
            : null;

    private sealed class RemoteKeyListener(Func<bool, bool> activate) : Java.Lang.Object, View.IOnKeyListener
    {
        private Func<bool, bool>? _activate = activate;

        public void Deactivate() => _activate = null;

        public bool OnKey(View? view, Keycode keyCode, KeyEvent? keyEvent) =>
            keyCode is Keycode.DpadCenter or Keycode.Enter or Keycode.NumpadEnter &&
            keyEvent is { HasNoModifiers: true } &&
            _activate?.Invoke(keyEvent.Action != KeyEventActions.Down || keyEvent.RepeatCount > 0) is true;
    }
}
