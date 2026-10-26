# Native VLC surface regression probe

Run on Windows with a desktop session:

```powershell
dotnet run --project eng/NativeVlcSurfaceProbe/NativeVlcSurfaceProbe.csproj -c Release -p:UsedAvaloniaProducts=
```

This creates temporary off-screen Win32 windows and uses real libVLC. It does
not open the application catalog, download media, or change user settings.
The desktop application manifest is required for native child-window hosting.

The probe compares stock `LibVLCSharp.Avalonia` 3.10.0 `VideoView` with the
application's corrected `LibVlcVideoView`, then exercises `LibVlcPlayerView`
itself through three detach/reattach cycles. It verifies that VLC's HWND is a
live child of the probe window and is cleared on detach. It exits nonzero if
the corrected view or the full player's readiness gate fails.

The stock view reproduces a missing HWND after reattachment. Its native
creation callback saves the new handle but does not attach the media player;
its `Initialized` callback is not raised again. The application override binds
the newly created child handle explicitly, leaving the existing destruction
cleanup and the no-detached-playback safeguard intact.

This tests native window lifecycle, not CDN availability or video decoding.

Add `-- --casting` to also check the bundled Chromecast discovery module,
native event registration, local output selection, and the Windows cast picker
(opening, selecting, busy state and inline errors). This briefly starts local
network discovery, without connecting to a receiver or opening Windows' Win+K
panel. Actual casting, seek/pause and Miracast require a compatible receiver
and should also be tested manually with a local file and a streamed source.
