# Android TV playback engines

Android TV now bundles LibVLCSharp 3.10.0 and VideoLAN.LibVLC.Android 3.6.5.
No separate VLC application needs to be installed.

VLC is the default for supported live streams, on-demand video, and audio on
both Android TV and phones. The **VLC / Android** button in the source row
switches engines. Each newly opened source starts with VLC again; incompatible
sources follow the boundaries below. Live streams reopen at the live edge;
seekable on-demand streams keep their position when switching.

The shared native overlay drives both engines, replacing the phone's
system-player-only MediaController. It includes:

- A seek bar with elapsed and total time; touch previews until release and
  remote arrows seek while the timeline has focus.
- Clearly separated rewind 10 seconds, play/pause, and forward 30 seconds.
- A dedicated server/quality row and engine switch, leaving room for the title.
- A live indicator instead of an unusable seek bar for live streams.
- Density-scaled touch targets, accessible labels, and outlined TV focus states.
- Replay from the beginning after a video finishes.
- Party View directly in the native player: create/join by code, copy the code,
  view participants and synchronization state, resynchronize, change the host
  control setting, open the party's content, and leave the room.
- Native options for audio/subtitle tracks and source/engine selection, plus
  mute shared across source and engine changes. Track availability depends on
  what the stream and the selected engine expose.
- Five-second auto-hide while playing video; controls stay visible for audio,
  while paused, while scrubbing, or while a player panel is open. Tap the video to reveal them. On TV, Back first hides visible
  controls during playback, then exits on the next press.

TV focus moves from transport Up to the timeline and Down to server/player
selection, then Down again to Party View, options, and mute. Menu/Info opens
options. The first OK press reveals hidden controls; its repeats and key-up
are consumed so they cannot accidentally pause the stream. Live playback skips
the hidden seek controls. Labels and hints are
available in English and Spanish. The overlay uses small native drawable shapes,
with no bitmap backdrops or blur layers. Auto-hide and progress use reusable
callbacks; hidden controls and paused activities do not poll playback.

Player panels use native modal windows with a scrollable body and a fixed close
button. Up/Down skips hidden or disabled actions; each participant has a separate
focus target so large rooms remain readable with a remote. Back closes the
keyboard or nested selector first, then the panel, and returns focus to the
button that opened it. Touch layouts resize on orientation changes. Panel
updates are coalesced and subscriptions are removed when playback closes.

The native panel shares the application's existing `MainViewModel` and party
session through `App.PlaybackViewModel`. It does not create another connection.
Opening party content returns to the catalog activity; closing the panel does
not leave the room. Party actions require a signed-in server that supports rooms.

A playback error tries the other eligible engine once for the current stream,
then tries the next server. Preparation that lasts over 20 seconds follows
the same bounded fallback. Leaving the activity cancels the preparation timer;
stale VLC callbacks cannot act on a replacement stream.

## Boundaries

- `Unknown` is Android MediaPlayer's generic error, not proof of an unsupported
  codec. A dead URL, denied CDN request, or unsupported format can look alike.
- Referrer and User-Agent reach LibVLC as media options. Required `Origin`
  headers remain on the system player: LibVLC 3 cannot send arbitrary headers,
  and the desktop HLS proxy is not part of the Android application.
- This stable Android LibVLC build does not include `dash-clearkey`. Supplied
  ClearKey metadata is retained and produces an explicit unsupported-encryption
  error instead of being silently discarded. Adding DRM support is separate.
- LibVLC is loaded lazily. Its native decoder increases APK size and memory
  while in use. Desktop LibVLC/Avalonia playback code is not imported into Android.

Diagnostics in the application log use `playback.android-open`,
`playback.android-error`, and `playback.android-start-failed`. They identify the
engine, server index/hostname, native error details, and exception type without
logging signed stream URLs, request credentials, or decryption keys.

## Build and test

```powershell
dotnet test tests/UniversalMediaDistribution.Mobile.Tests
dotnet build src/UniversalMediaDistribution.Android -c Debug `
  -p:UmdAndroidPackageProfile=tv-arm -p:UmdAndroidRuntimeIdentifier=android-arm `
  -p:UmdUseNativeAot=false -p:EmbedAssembliesIntoApk=true
```

The project explicitly selects native libraries for each runtime identifier;
the upstream package's older targets do not recognize singular
`RuntimeIdentifier`. ARM, ARM64, and x64 mappings are provided. The Java VLC
surface bridge is kept in R8 builds by `libvlc.proguard.cfg`.

PC tests cover selection, headers/options, bounded fallback, timeline
formatting/clamping, panel focus ordering, and complete remote key presses.
Compilation and checking APK contents do not verify decoding on an actual TV.
On the TV, check live HLS/TS playback, audio/video, engine switching, Back,
pause/resume, channel changes, and an unavailable stream before release.
Also check D-pad navigation through timeline/transport/server selection, long
titles, English and Spanish labels, and phone touch scrubbing/fullscreen.
For Party View, check creating and joining with the TV keyboard, Back from the
keyboard and nested selectors, a full participant list, focus after joining or
leaving, reconnecting, and pause/seek/resynchronize with a second device. Verify
audio/subtitle selection in each engine and phone rotation with a panel open.

Validation of the in-player panels (2026-09-28): 78 mobile tests and 60 selected
UI/localization/server integration tests passed, including the two-member party
test. The server's sync DELETE body is now explicitly bound; without that
annotation the runtime rejected route initialization before the party test.
Android sources compiled in an isolated check against the installed Android 36
reference assemblies. This is not an APK build: the active .NET 11 RC SDK lacks
its Android workload, and no Android device was connected. Device rendering,
decoder behavior, native dialog focus, and APK packaging remain unverified.

Upstream: [LibVLCSharp](https://www.nuget.org/packages/LibVLCSharp/3.10.0),
[Android LibVLC](https://www.nuget.org/packages/VideoLAN.LibVLC.Android/3.6.5).
