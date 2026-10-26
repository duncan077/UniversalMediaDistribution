# Video feed extractor audit

Checked 2026-09-09. This is a bounded sample, not a promise that every title or server works. Eight built-in video API providers were sampled (up to two recent titles, one episode and twelve servers per title), including hoster hand-offs behind wrapper pages. Existing captured-shape regression tests supply additional, explicitly marked associations. The original Downloads HAR files are no longer available in this workspace.

**Live native URL** means the current extractor returned `DirectMedia`. It does **not** mean VLC/Android playback, seeking, audio or every media segment was tested. **Fixture only** means deterministic parser tests pass, but no current usable example was observed. **Web fallback** means the native optimization failed or is unsupported; the embedded source remains selectable.

All eight video providers receive the same `VideoHosterResolver` from `ApplicationComposition`. A supported host works across providers; there is no per-provider extractor allowlist. Matching uses URLs or player markup, not the displayed server name.

## Registered extractors

### FramexTV addition — 2026-10-19

The dedicated API provider resolves `/api/stream` responses directly, rather
than adding a hoster-name alias. Movie/TV identities and season/episode numbers
are kept separate. API/backend and CDN availability are independent of catalog
presence: Inception initially returned HLS, then an upstream extraction failure;
the sampled Game of Thrones episode reported a rate-limited upstream host.

| Video feed | API provider | Extractor / verification |
| --- | --- | --- |
| FramexTV `/api/stream`, observed `lavahound` through `frappe.hybrows.workers.dev` | FramexTV movies and TV | Dedicated JSON-to-native parser; Fight Club live probe verified master/child HLS and an 8-byte media range; Spanish subtitle file contained cues |
| Official `framextv.xyz/embed/...` player | FramexTV | Selected-server interactive WebView capture enabled; safe media/DOM subtitle parsing and API subtitle retention covered by fixtures, not manually completed in a browser |

Captures retain languages/default flags and feed subtitles into desktop and
Android LibVLC. API responses are bounded to 4 MiB, subtitles to 100 tracks;
private/credential-bearing URLs are rejected. The native capture probe's bounded
playlist limit was raised from 128 KiB to 1 MiB: the sampled rewritten film
playlist was 344,491 bytes. Oversized-response rejection remains tested. No
full-movie playback, seeking, audio, or every segment was verified. No CAPTCHA
or DRM bypass was implemented. Other FramexTV provider IDs were not exhaustively
sampled and are not claimed to have separate working extractors.

### AnimeAV1 addition — 2026-09-28

Read-only probe: `dotnet run eng/AnimeAv1Probe.cs -- --resolve`. Public catalog
checks returned 20 daily titles with covers and 19 Naruto search results.
Validated Naruto (220 episodes), One Piece (1,180 episodes), and The Last:
Naruto the Movie (one episode). SUB and DUB feeds remain separate. These are
sampled links, not an exhaustive census or an end-to-end playback test.

| Video host observed on AnimeAV1 | Existing extractor / live outcome |
|---|---|
| VOE (`voe.sx`) | VOE returned native HLS; bounded media GET returned HTTP 206 and `#EXTM3U` |
| MP4Upload (`mp4upload.com`) | Mp4Upload returned native MP4; bounded media GET returned HTTP 206 with 1,024 bytes |
| YourUpload (`yourupload.com`) | YourUpload returned native URL; media endpoint returned 302 to `s246.vidcache.net`; final delivery not checked |
| StreamTape (`streamtape.com`) | Streamtape returned native URL; media endpoint returned 302 to `tapecontent.net`; final delivery not checked |
| Byse (`byselapuix.com`) | Existing Filemoon/Byse extractor recognized the host but returned HTTP 428; preserved WebView / interactive capture fallback |
| UPNShare (`animeav1.uns.bio`) | No dedicated HTTP extractor. Public API returns an encoded payload, not a directly usable media URL. Existing interactive WebView capture recognizes this host and preserves the fragment video ID |

No new host-domain aliases were necessary for the sampled feeds. UPNShare is
the remaining dedicated-extractor gap; Byse's challenge was not bypassed.
Neither interactive capture flow was manually completed in this check.
The movie's upstream `embed-undef.html` entry is discarded. Separate download
providers (TransferIt, MEGA, 1Fichier) are not offered as video streams.

### Earlier provider audit

| Video feed provider / extractor | API providers using it in available evidence | Extractor status |
|---|---|---|
| PremiunVIP | Not observed in sampled built-ins | Fixture only |
| PlusTube | Not observed | Fixture only |
| Nupload | PelisPlus.autos, SeriesFlix, PelisflixHD | **Live native URL**; includes new `/watch/` iframe hand-off to VOE; some sessions fall back |
| VOE | AnimeTMO, JKAnime, Doramasflix, SeriesFlix, PelisflixHD; PoseidonHD2; PelisPlus.autos (fixture) | **Live native URL** on several providers; individual 404/missing videos and Poseidon samples fall back |
| YourUpload | Not observed | Fixture only |
| Vixcloud | Not observed | Fixture only |
| Upzone | Not observed | Fixture only, including delegated Uqload |
| Uqload | Doramasflix (captured-shape fixture through fkplayer) | Fixture only; live fkplayer sample did not resolve |
| UpZur | Not observed | Fixture only |
| Filemoon / Byse | AnimeTMO (`byselapuix.com`), JKAnime (`bysekoze.com`); Doramasflix (capture) | Updated mirror/content recognition; **live blocked: HTTP 428 / CAPTCHA prerequisite**. Protocol fixture passes |
| Frembed | Not observed | Fixture only; delegates to other extractors |
| StreamSB | Doramasflix | **Web fallback**: current sample timed out. Generic player fixture passes, not proof of current StreamSB service |
| Streamlare | Not observed | Generic player fixture only |
| NinjaStream | Not observed | Generic player fixture only |
| Uch | Not observed | Generic player fixture only |
| Streamtape | JKAnime; Doramasflix (capture) | **Live native URL** on JKAnime; occasional request failure |
| Mp4Upload | JKAnime, AnimeTMO | **Live native URL** on JKAnime. AnimeTMO supplies `embed-undef.html`, which has no valid video ID |
| Doodstream | JKAnime (`dsvplay.com`), PoseidonHD2; Doramasflix (capture) | **Live blocked: HTTP 403**. Parser fixture passes |
| Vidmoly | Not observed | Fixture only |
| Vidoza | Not observed | Fixture only |
| LoadX | Not observed | Fixture only |
| SpeedFiles | Not observed | Fixture only |
| VidHide | JKAnime, PelisPlus.to / TioPlus, PoseidonHD2 | **Live native URL** |
| StreamWish | JKAnime, PoseidonHD2 | **Live native URL** on JKAnime delivery mirror; Poseidon headline-domain samples fall back |
| Mediafire | Not observed | Updated single/double-quoted attribute parsing; fixture passes |
| Mixdrop | JKAnime | **Live native URL**; added packed-mirror and single-quoted assignment coverage |
| PlayerWrapper | PelisPlus.to / TioPlus, PoseidonHD2 | **Live hand-off works** for supported child hosters; unsupported children remain Web |
| Zilla | AnimeTMO (captured routes) | **New shared extractor**, previously provider-local. Fixture verifies master playlist traversal and bounded segment probe; current sample uses another player |
| OK.ru | Doramasflix | **New extractor; live native URL**. Reads public inline metadata, HLS or best MP4; no login/paid-video flows |
| HTML5 | JKAnime internal `/jkplayer/umv` | **New extractor; live native URL**. Explicit video/source tags only, including relative URLs |
| DPlayer | JKAnime internal `/jkplayer/um` | **New extractor; live native URL**. Decodes active base64 video configuration, ignores commented-out old player |

“Not observed” is not a claim that the API providers never use that host. They return changing, title-dependent server lists. Generic packed-player fixture tests verify parsing, not the continued availability of retired hoster services.

## Other observed feeds and missing support

| Video feed provider / host | API provider | Status / remaining work |
|---|---|---|
| `nika.playmudos.com` | JKAnime | Provider already returns a direct HLS feed; no extractor needed |
| `animeav1.uns.bio` | AnimeTMO | **No dedicated extractor**. JS-only player with dynamic `/api/v1/player` and `/api/v1/video` flow; static HTML has no feed |
| MEGA (`mega.nz`) | AnimeTMO | **No native feed extractor**; remains embedded. Do not label an encrypted file link as ordinary MP4 |
| `watchsb.com` | Doramasflix | No matching extractor for observed URL; legacy host is not proven to use the currently implemented generic StreamSB format |
| `fkplayer.xyz` | Doramasflix | Provider has a captured server-action decoder, but current sample remains unresolved; fixture routes can delegate to Uqload |
| `do7go.com` | Doramasflix | **Unrecognized current player**; no verified extractor |
| `primeload.co` | Doramasflix (capture) | No dedicated extractor; not observed in current two-title sample |
| `pelisplus.upns.pro` | PelisPlus.to / TioPlus | **No dedicated extractor**; wrapper reveals the host but no native feed |
| `pelisplusto.4meplayer.pro` | PelisPlus.to / TioPlus | **No dedicated extractor** |
| `pelisplus.strp2p.com` | PelisPlus.to / TioPlus | **No dedicated extractor** |
| `emturbovid.com` | PelisPlus.to / TioPlus | **No dedicated extractor** |
| `hgcloud.to` | PelisPlus.to / TioPlus | **No dedicated extractor** |
| `1fichier.com` | PoseidonHD2 | **No native streaming extractor**; download-host fallback |

This means the request is **not fully complete for every observed host**. Unimplemented dynamic players need a separate verified request/response flow (a working player HAR is useful); CAPTCHA/403/deleted-video failures cannot be honestly fixed by returning an unverified URL. No CAPTCHA bypass, signed-URL persistence, or guessed CDN rewriting was added.

## API provider coverage

| API provider | Result of sample / integration |
|---|---|
| AnimeTMO | VOE resolves; new Byse mirror recognized but CAPTCHA-blocked; uns.bio and MEGA unsupported; invalid Mp4Upload ID supplied upstream |
| JKAnime | Both internal players now resolve, plus StreamWish, VidHide, Mixdrop, Mp4Upload and Streamtape. VOE mixed; Filemoon/Dood blocked |
| PelisPlus.autos | Nupload resolves; wrapper/individual server failures preserved as Web |
| Doramasflix | New OK.ru extractor and existing VOE resolve; other samples blocked or unrecognized |
| PelisPlus.to / TioPlus | Wrapper and VidHide resolve; newly observed dynamic hosts still unsupported |
| PoseidonHD2 | Wrapper and VidHide resolve; other samples blocked/unsupported |
| SeriesFlix | Nupload and delegated VOE resolve; session/individual video failures preserved |
| PelisflixHD | Migrated to `https://pelisflixhd1.top/`, including stale `.blog` links embedded in new pages. Catalog loads; VOE and Nupload-to-VOE wrappers return native URLs on “Amos del Universo”. Latest samples can still contain missing uploads |
| UMD catalog API | Reference catalog import provider: no built-in dynamic video-server discovery flow or configured public base URL |
| LeerCapitulo, Olympus, ManhwaWeb, ZonaTMO, CapibaraTraductor, Uchuujin Mangas | Manga/image providers; video extractor audit not applicable |

## Changes and verification

- Added shared OK.ru, Zilla, HTML5 and DPlayer extractors (27 → 31 registered).
- Added Byse's observed mirror and distinctive page-signature recognition.
- Updated generic packed readers for relative tagged media URLs, Mixdrop for single-quoted/packed mirror assignments, and Mediafire for quote/spacing variants.
- Fixed content-detection negative caching: a missing video no longer suppresses every other video on its host. URL case is preserved.
- Added optional redacted per-hop diagnostics: host, extractor and outcome only. Observer failures cannot affect playback.
- Shared Zilla probe validates public targets, bounds playlists/hops/time and checks segment bytes. AnimeTMO reuses it without immediately retrying failed probes.
- Full UI suite: **925 passed**. Extractor expansion/request-shape suite: **29 passed**. Catalog rebuild with AOT and trim analyzers: **0 warnings, 0 errors**. This is analyzer validation, not a new Android NativeAOT deployment.
- No deployment or browser UI changes were made. Debug UI test build hits an existing `AttachDeveloperTools` compile error in `App.axaml.cs`; the full suite was run in Release instead, with build telemetry disabled for the sandbox.

## Repeat the live audit

From the repository root:

```powershell
dotnet run --project eng/VideoFeedAudit/VideoFeedAudit.csproj
dotnet run --project eng/VideoFeedAudit/VideoFeedAudit.csproj -- jkanime doramasflix
dotnet run --project eng/VideoFeedAudit/VideoFeedAudit.csproj -- pelisflix-hd --item=pelicula/amos-del-universo/
```

The tool runs read-only provider discovery and extractor requests, prints Markdown rows, and does not launch a browser/player or save signed playback URLs. Normal hoster handshakes and small reachability probes can occur. It uses automatic redirects disabled, provider time budgets, extractor response caps and existing public-target validation. Windows TLS needed execution outside the restricted sandbox in this environment. No user browser clearance/cookies were reused.

Protocol references: [OK.ru public metadata extraction shape](https://github.com/yt-dlp/yt-dlp/blob/master/yt_dlp/extractor/odnoklassniki.py); Byse prerequisite verified against the [observed player page](https://byselapuix.com/e/350e4csxmxtq) and its loaded public video module. Fixtures contain synthetic media URLs rather than live signed tokens.

## Interactive challenge follow-up — 2026-09-10 UTC

Windows and Android now expose a visible video-check flow, reusing their existing `IWebChallengeSolver` integration. Filemoon/Byse HTTP 428 and Doodstream HTTP 403 are recorded during extraction, but the interactive window opens **only when that server is selected for playback**, not during provider discovery. An arbitrary 403 on an unrelated host does not trigger it, and generic reader challenge detection is unchanged.

The user completes the site's check and starts the actual video, then selects **Use native player**. An in-page observer captures the active player URL without reading cookies or decryption keys. A separate cookie-free HTTP probe checks the media (including a segment behind an HLS master playlist), using the browser's user agent and referrer, plus origin for HLS, matching the native playback path. Only a verified candidate is offered natively. **Keep web player**, Back, close, timeout, a failed probe, or native-player rejection preserves the selected embedded server. Windows popup requests are blocked and Android multiwindow creation is disabled; top-level navigation is limited to the original or recognized hoster domains.

Interactive work has its own two-minute deadline outside the 35-second extractor budget, plus a two-minute per-host retry cooldown. Captured signed URLs remain in memory; the new flow does not log their values or persist them in the catalog. Browser cookies remain managed by the platform WebView.

Verification: 942 UI tests and 29 extractor/request-shape tests pass; shared capture-script checks pass. Desktop Release build succeeds. Android ARM64 NativeAOT-configured compile succeeds with one existing nullable warning in the reader's `WebChallengeActivity.cs`. **A human-solved live CAPTCHA and subsequent VLC/Android playback have not been end-to-end verified.** Genuine access refusals or browser-bound streams may still require WebView.
