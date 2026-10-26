# Universal Media Distribution for Linux

Extract the complete archive into a directory owned by your user, then run:

```sh
./UniversalMediaDistribution.Desktop
```

The application is compiled to native code with NativeAOT: it needs no .NET
installation and ships no JIT runtime. The package includes LibVLC 3, its
playback plugins and their native dependency libraries. A system VLC
installation is not needed. Keep `libvlc.so`,
the `libvlc/` directory and the executable together; their paths are relocatable.
The included license notices and exact native package versions are under
`licenses/libvlc/`.

The baseline is Debian 12 / glibc 2.36 or newer, with an X11 desktop or XWayland.
Alpine/musl and older glibc systems are not supported by this archive. The host
still supplies its display, audio server, graphics drivers, font configuration,
OpenSSL and ICU, as on a normal .NET/Avalonia desktop installation.

Website verification uses WebKitGTK 4.1. On Debian/Ubuntu, the common desktop
prerequisites can be installed with:

```sh
sudo apt install libwebkit2gtk-4.1-0 gstreamer1.0-libav gstreamer1.0-plugins-good libfontconfig1 libice6 libsm6
```

The verification dialog opens on a provider's challenge and can be closed to
cancel. Interactive video checks use a browser window and a separate app-owned
confirmation window. Only a video the user confirms and the native playback
probe accepts is handed to LibVLC. Inaccessible cross-origin frame video stays
in the web player.

Embedded web playback is separate from verification: the pinned Avalonia
WebView version may also require WPE WebKit for its embedded control. Neither
WebKit engine is bundled. Windows-specific network filtering and audio relay
are not implemented on Linux.

For in-app updates, keep the installation in a writable directory. System-wide
installations must be updated through their administrator/package manager.
The updater does not install system dependencies.
