# Windows app audio sharing

Native VLC playback uses UMD's default Windows audio session and shared WASAPI
PCM output. HDMI/SPDIF passthrough is disabled so playback remains in the
Windows audio mix.

WebView2 renders audio in a browser subprocess. On Windows build 20348 or
newer (including Windows 11), the desktop web player relays that audio into
UMD's default session. It locates the audio session only within the browser
process tree supplied by that WebView2 instance. It does not capture the
microphone, the system mix, or unrelated applications, and writes no audio
recording to disk.

The relay temporarily mutes the source browser session only after non-silent
samples have reached host output. It restores the session on close, adapter
replacement, disposal, or failure. If capture is silent or output makes no
progress for 1.5 seconds, normal browser sound resumes and the relay does not
retry on that attachment. This also applies to genuine silence; preserving
playback takes priority over app capture. A muted or zero-volume host session
also prevents takeover. Playback volume in the web page is retained. Output
uses the browser's own device, including per-app headphone routing; a removed
device or inactive source causes the relay to release and rediscover the source.
The queue is bounded to prevent increasing audio delay if an output stalls.

Older Windows versions and failed initialization retain browser audio. If
Windows denies access to a stream, the relay falls back to browser playback;
this does not make protected media capturable. A `playback.webview-audio`
diagnostic identifies the source and host process IDs or the fallback reason.

## Verification

Run the desktop unit tests and the UI player transition tests. The opt-in
native smoke test uses an isolated WebView2 profile and a quiet local WebAudio
tone, checks nonzero audio in the host process, checks the source mute and its
restoration, and repeats attachment:

```powershell
dotnet run --project tests/UniversalMediaDistribution.WebView.SmokeTests -- --audio
```

After rebuilding and restarting UMD, start a fresh application share with sound
enabled in Discord and play an embedded web video. Confirm the receiver hears
it once, then verify pause, seek, switching to native playback, and closing the
player. The automated smoke test does not exercise Discord's capture backend.
