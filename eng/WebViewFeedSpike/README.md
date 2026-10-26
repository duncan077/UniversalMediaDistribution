# WebView feed capture spike

Isolated Windows-only test executable. It is not referenced by the application or release packaging. It uses the production `VideoChallengeCapture` observer and cookie-free `VideoChallengeMediaProbe`, but a separate WinForms/WebView2 UI and fresh browser profile per run.

```powershell
dotnet run --project eng/WebViewFeedSpike -c Release -- https://animetmo.com/ver/youjo-senki-ii/10
dotnet run --project eng/WebViewFeedSpike -c Release -- https://pelisplus.autos/pelicula/spider-man-un-nuevo-dia/
```

Choose a server, press Play, complete any site check manually, and choose **Verify captured native feed** when enabled. A successful probe checks playlist/segment reachability without cookies; it does not prove VLC playback or that the uploaded content matches its label. The URL field allows another explicit test without restarting.

Diagnostics are under `%TEMP%/UMD-WebViewFeedSpike/<run-id>/events.log`. They include hostnames, response statuses, media types, observer state and click-target tags/classes, but not signed media URLs, headers, cookies, or keys. The adjacent disposable WebView profile contains normal browser-managed site data. Captured feed URLs are held only in application memory.

The spike blocks popups and permission prompts. Its experimental overlay filter removes the two observed ad domains, full-screen off-site anchors, and full-window empty-frame overlays. This is **not shipped production ad-blocking behavior**. It is installed both at document start and after top-level navigation, with a delayed CSS-layout recheck. Real player frames and CAPTCHA frames are not intentionally targeted. This filter is a diagnostic experiment, not a generally verified policy.

## Verified baseline (2026-09-10 UTC)

AnimeTMO `/ver/youjo-senki-ii/10`:

| Selected server | Actual player host | Result |
|---|---|---|
| UPNShare | `animeav1.uns.bio` | Browser playback, HLS captured behind `blob:`, cookie-free probe passed |
| Byse | `f7hyg4q.org`, nested under `byselapuix.com` | Browser playback, HLS captured behind `blob:`, cookie-free probe passed |

These checks did not exercise Android or VLC. Other servers on the episode were not tested. The production resolver now opts UPNShare (`uns.bio`/`upns.pro`) and recognized Filemoon/Byse embeds into the existing interactive selection flow without requiring an HTTP 428 first. The `upns.pro` alias is routed but was not covered by this AnimeTMO sample. Repeated prompts are suppressed per URL, not for every episode on the same host. Direct media remains unchanged.

## PelisPlus movie attempt (2026-09-10 UTC)

The requested `/pelicula/spider-man-un-nuevo-dia/` page passes its automatic browser verification and loads in WebView2. It shows Latino, Español Castellano and Subtitulado choices. The probe remains disabled: repeated server/play interactions hit advertising surfaces instead of producing a playing video. One captured click targeted an empty-host frame's BODY; subsequent runs exposed rotating off-site ad link destinations. Blocking native popups, the observed ad domains and experimental full-screen overlay shapes did not establish playback. Reinjecting diagnostics after navigation confirmed the shared observer was already installed; it did not produce a media capture.

**Result: inconclusive for this movie's hosters, not an extractor pass.** No native URL, successful segment probe or VLC playback was obtained for this movie. The spike is left available for a manual server/Play interaction. No production ad-blocking changes were made from this experiment.
