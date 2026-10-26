using Avalonia.Platform;
using LibVLCSharp.Avalonia;
using UniversalMediaDistribution.UI.Diagnostics;

namespace UniversalMediaDistribution.Playback.LibVLC;

/// <summary>
/// Rebinds VLC when Avalonia creates or recreates the Windows video child.
/// LibVLCSharp.Avalonia 3.10 attaches on Initialized/MediaPlayer changes,
/// but not on native handle recreation. Initialized only fires once.
/// </summary>
internal sealed class LibVlcVideoView : VideoView
{
    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        var handle = base.CreateNativeControlCore(parent);
        if (OperatingSystem.IsWindows() &&
            handle.HandleDescriptor == "HWND" &&
            handle.Handle != IntPtr.Zero &&
            MediaPlayer is { } player)
        {
            // Use only the child created for this control, never the top-level
            // application window. The base destroy callback clears VLC's HWND.
            player.Hwnd = handle.Handle;
            ApplicationDiagnosticLog.Information(
                "playback.libvlc-surface-bound",
                "stage=native-child-created; hasHandle=True");
        }

        return handle;
    }
}
