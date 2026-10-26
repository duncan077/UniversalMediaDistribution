using Avalonia.Controls;
using Avalonia.Platform;

namespace UniversalMediaDistribution.UI.Playback;

/// <summary>
/// Native keyboard and pointer input for TV web players. A real native click
/// reaches cross-origin frames and browser video controls; DOM .click() does not.
/// All methods run on the UI thread. No pointer/touch listeners are installed.
/// </summary>
public interface INativeWebViewRemoteInput
{
    /// <param name="activate">
    /// Receives false for a fresh OK press, true for repeats/releases.
    /// Returns true when the app owns the gesture and browser defaults should stop.
    /// </param>
    void Attach(NativeWebView webView, IPlatformHandle? handle,
        Func<bool, bool> activate);

    void Detach();

    ValueTask ClickCenterAsync(NativeWebView webView);
}
