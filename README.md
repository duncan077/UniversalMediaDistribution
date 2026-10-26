# Universal Media Distribution

Avalonia client for browsing and consuming legally distributable indie, public-domain, Creative Commons, and otherwise authorized media delivered through torrent sources.

The application targets:

- Native desktop, Android/Android TV, and iOS with MonoTorrent 3.0.2.
- Shared Avalonia UI, catalog domain, and application services.

Browser/WebAssembly is not a target. The WebAssembly head has been removed.

See [PROJECT_PLAN.md](PROJECT_PLAN.md) for the full architecture, catalog schema, privacy rules, import/export design, and delivery roadmap. Torrent publishers should also read [docs/TORRENT_INFO_MANIFEST.md](docs/TORRENT_INFO_MANIFEST.md), [docs/PRIVATE_TORRENTS.md](docs/PRIVATE_TORRENTS.md), and [docs/CONTENT_SOURCE_PROVIDERS.md](docs/CONTENT_SOURCE_PROVIDERS.md).

## Current implementation

- Avalonia 12.1 desktop, Android, and iOS hosts targeting .NET 11.
- Shared Movie, Book, and Audio catalog domain.
- Subtype validation for movies, series, anime, books, manga, audio, and related formats.
- Multiple categories for Movie/Book titles and multiple localized alternative
  names per title. Cards and detail headers choose the exact UI-locale title,
  then the neutral language, then the primary catalog name.
- Provider/contributor filtering with persistent favorite providers. With no
  provider filter selected, titles from favorite providers are shown first and
  each group is sorted by its localized display title.
- Named personal lists can combine any saved manga, anime, movie, series, book,
  or audio title. Lists are browsable from the main menu, managed in place, and
  updated from each title's details page. Membership uses stable local catalog
  IDs, is included in full backups, and is excluded from catalog-only JSON.
- Contributor records include an optional safe website URL, and every torrent
  package carries a normalized BCP-47 language tag (`und` when unknown).
- Three distinct content-source kinds: built-in manual magnets, user-managed
  torrent-catalog JSON endpoints, and code-registered API providers whose
  enablement/base URL are local settings. Search combines the local catalog
  with every enabled provider, preserves same-title local/online results, and
  labels each result by origin.
- Selecting an online result validates and merges its ID-free item into the
  local catalog before opening it. API implementations can supply seasons,
  chapters, provider/CDN artwork, torrent sources, and transient zero-based
  direct image pages that reuse the shared paged/continuous reader and progress
  state.
- MonoSchino anime episodes expose every usable server below the player.
  Direct media streams open in the native player, while embed-only hosts open
  in the contained native web view and remain selectable from the same server
  list. Embedded players are filtered by a built-in Adblock Plus/uBlock Origin
  filter engine: requests are refused at the network layer on Windows and
  Android, element hiding and scriptlets are injected before the page's own
  scripts, and popups and click traps are suppressed everywhere. Filter lists
  are downloaded at runtime; a compiled-in seed protects a first run and an
  offline install. Same-site requests and anything that is part of playing the
  video are never blocked. The level is chosen in **Settings** and capped by
  what the device can carry; refreshes wait for a moment that is not competing
  with playback, a metered connection, or power saving. See
  [Content filtering for the embedded player](docs/CONTENT_FILTERING.md).
- PelisPlus movies and series are available through its public HTML catalog.
  Movie pages expose a full-movie unit, series retain their real seasons and
  episode numbers, and every Base64-listed server opens as an embedded-web
  option with the originating page as its referrer.
- Existing local manga chapters can resolve images dynamically without a saved
  provider link: enabled APIs are searched by primary/original/alternative
  manga names, candidates are ranked with normalized Levenshtein similarity,
  and the closest candidate containing the selected chapter supplies its full
  ordered image list. A successful match also merges current manga metadata and
  chapters into the local catalog, adding missing chapters and updating
  matching chapters without changing local IDs or progress keys.
- Per-title metadata refresh for movies/series through TMDbLib and for
  anime/manga/web novels through the MyAnimeList API, including localized
  titles, descriptions, and remote JPG/PNG/WebP cover art. Ordinary books have
  no provider action.
- Stable normalized name keys and transactional catalog JSON upserts.
- Image-sequence parser enforcing zero-based `_<position>` page ordering.
- Engine-neutral `ITorrentEngine` contract and platform capability profiles.
- Working MonoTorrent native adapter with magnet/metadata intake, per-file priorities, sequential streaming reads, downloads, seeding, pause/resume, configurable MSE policy, private-mode DHT/PEX controls, globally disabled local-peer discovery, and persistent metadata/DHT/fast-resume state.
- Torrent activity is visible from the moment a magnet, torrent file, chapter,
  episode, or other torrent-backed source is opened. A persistent activity card
  reports metadata lookup, file checking, peer discovery, selected-byte
  progress, speed, peers, and ETA, with pause, resume, stop, and dismiss
  controls. Native progress is sampled four times per second; the browser
  prototype reports the same normalized snapshot fields.
- Native downloads are also kept in a persistent **Downloads** screen. It
  restores torrent name, state, selected and total progress, speed, peer count,
  ETA, error details, and the exact managed files after restart. Downloads can
  be paused, resumed, selected, or removed individually/in bulk, with an
  explicit choice to keep or delete their managed data. A setting can delete
  downloaded torrent data when the app session ends.
- Manual or automatic incoming-magnet handling with confirmation, duplicate
  suppression, safe tracker filtering, and persistent platform preferences.
  Public intake is the default; forcing unmarked magnets into private mode is
  optional. Canonical session links preserve the MonoTorrent-compatible
  `xt=urn:btih:` form, and valid trackerless public magnets use native DHT.
- A shared **Open .torrent file** picker for native builds, with bounded
  metadata validation, v1/v2 identity extraction, duplicate suppression,
  private-flag handling, and safe tracker routing.
- Optional torrent-local `info.json` catalog import for both magnet and `.torrent` intake, restricted to the active torrent's identity and files, including item/season/unit artwork.
- Native SQLite catalog/user database with versioned migrations.
- Startup database integrity checks with automatic quarantine of corrupt SQLite
  database/sidecar files, clean schema recovery, and a visible recovery notice.
- ID-free catalog JSON import/export on native hosts.
- Native full-database backup/restore with integrity checks and a pre-restore safety copy.
- Optional convergent per-user device sync through a self-hosted server. Each
  sign-in and **Sync now** operation pulls, merges, and revision-checks the
  complete portable state; per-record logical versions and deletion tombstones
  preserve offline progress, reader preferences, favorite providers, and
  personal lists. Credentials, settings, downloads, paths, and downloaded
  files remain local.
- An optional server catalog that participates in the same contained-text
  search as local and provider content, while the local catalog remains usable
  offline. Signed-in users can publish one personal list as a read-only,
  revocable catalog link containing only that list's titles.
- Optional watch parties for signed-in users: a party is created from the
  Friends page or the current playback, shared with a six-character invite
  code, and synchronized live over a WebSocket on the same self-hosted server.
  Android TV joins too: the entry appears as **Watch together** in the
  lean-back menu, holding only the party card, with the code typed through
  the television text-input flow. A member whose stream stalls pauses the
  party until it refills, the host can optionally lock pause and seek to
  themselves, and a source playing in the embedded web player joins the sync
  too: an injected reporter reads and drives the page's own video element.
  Only a video inside a cross-origin frame stays out of reach, and the party
  card says so rather than pretending to follow along.
  Any member's pause, resume, or seek is applied to every member's player,
  what the host plays becomes the party's title, and members are aligned to
  the shared timeline when they open it. Titles cross devices by the same
  normalized name key friend progress uses; each member still resolves their
  own local or provider source. Parties live only in server memory — nothing
  about them is persisted — and an abandoned party expires on its own.
- Persistent native SQLite catalog and user settings.
- Shared catalog and detail experiences with Movie, Book, and Audio filtering; season/chapter/episode/track listings; torrent-backed item/unit artwork; and import/export controls.
- Native desktop video/audio playback from seekable MonoTorrent streams through LibVLC, with embedded Avalonia controls, seeking, volume, portable language/release switching, resume positions, and deterministic stream cleanup. Windows and Linux release packages include the LibVLC runtime; Linux packaging also includes its playback plugins and dependency libraries.
- Linux provider challenge recovery through WebKitGTK, including HttpOnly
  clearance cookies, rendered HTML/JSON/image documents and user-confirmed
  video capture. Linux x64 is included in the default production targets and
  compiled with NativeAOT inside Docker, including when publishing from Windows.
- Native Android and iOS primary-rendition playback from MonoTorrent through a
  loopback-only HTTP stream, using the platform video player and persisted resume
  positions. Android cleartext traffic is denied except for localhost.
- Android TV support in a dedicated ARM TV package, with a landscape lean-back
  launcher, 320×180 TV banner, optional touchscreen declaration,
  high-visibility focus, directional navigation, Back handling, and remote
  play/pause and seek controls. Separate ARM mobile and x86/x64 mobile packages
  avoid shipping native libraries for an unrelated CPU family.
- Native image reader with one-page and continuous top-to-bottom modes, strict
  zero-based filename ordering, lazy page streaming, persisted page/layout
  state, direct JPG/JPEG, PNG, and WebP files, and bounded CBZ archive support.
- Native EPUB reader with OPF spine ordering, mixed text and lazy JPG/JPEG,
  PNG, or WebP image blocks, image-only chapters, chapter navigation, text
  sizing, persisted progress, and bounded safe XML/archive processing.
- Native PDF reader using PDFium with lazy page rendering, paged/continuous
  layouts, persisted page progress, and bounded document/page processing.
- Shared live-session reuse and privacy-aware source resolution so catalog playback does not add a magnet-intake torrent twice or downgrade a private catalog source to a public session.
- Optional private-torrent enforcement with approved HTTPS tracker routing,
  fail-closed local credential resolution, secret-safe diagnostics, and no
  DHT/PEX/LSD or embedded unapproved tracker use in private sessions.
- Mobile lifecycle coordination for backgrounding, metered or unavailable
  networks, and low-power mode, with all app data kept in private platform
  storage.
- Deterministic desktop exit with main-window-owned lifetime, UI-stage
  reader/player shutdown, bounded LibVLC/MonoTorrent disposal, and process
  termination if native cleanup stalls.
- Responsive compact and TV navigation, complete English/Spanish app-owned UI
  resources, localized mobile launcher labels, and accessible names for
  controls, artwork, status regions, players, and reader surfaces.
- A NativeAOT-first ASP.NET Core server with health/capability discovery,
  PBKDF2 account authentication, hashed bearer sessions, login throttling,
  atomic online-catalog revisions, optimistic per-user device snapshots,
  revocable public list catalogs, signed release manifests, authenticated
  streaming package uploads, and range-enabled downloads. It uses Minimal APIs
  and source-generated JSON and is validated as a native `win-x64` executable.
- Desktop-only update checks are enabled only when a service URL and pinned
  ECDSA P-256 public key are configured. The app can check silently, but always
  asks before downloading. It verifies package size, SHA-256, signing key,
  signature, and installer type before asking Windows to launch the installer.
- An opt-in `win-x64-aot` desktop publish profile. The native executable passes
  a startup smoke test with isolated app data; TMDbLib/Newtonsoft and Mono.Nat
  still emit third-party AOT-analysis warnings, so the normal desktop release
  remains the default while those feature paths receive deeper testing.
- Repeatable desktop release archives with SHA-256 checksums.
- 650 automated .NET tests across the native MVP solution, including the
  retained browser-policy tests.

Phases 5 and 7 are still in progress. Full EPUB CSS/fixed-layout behavior,
password-protected PDF input, live-swarm tests, real Android/iOS device playback
and background validation, signed store packages, additional UI languages, and
mobile external subtitle/alternate-rendition playback remain open. The
current mobile player deliberately exposes only the primary rendition because a
MonoTorrent streaming provider can serve one HTTP stream at a time.

## Removed browser prototype

The `UniversalMediaDistribution.Browser` WebAssembly head and its delivery half
`UniversalMediaDistribution.Torrenting.WebTorrent` have both been deleted.
Torrent delivery on the native heads goes through
`UniversalMediaDistribution.Torrenting.MonoTorrent`.

## Build and test

Install .NET 11 RC1 SDK `11.0.100-rc.1.26425.128`, as pinned in
`global.json`. Run the workload restore from the repository root so Android
uses workloads compatible with that SDK; an older preview workload is not
sufficient.

```powershell
dotnet workload restore src\UniversalMediaDistribution.Android\UniversalMediaDistribution.Android.csproj
dotnet restore UniversalMediaDistribution.slnx
dotnet build UniversalMediaDistribution.slnx --no-restore
dotnet test UniversalMediaDistribution.slnx --no-build --no-restore
```

Run the desktop application:

```powershell
dotnet run --project src\UniversalMediaDistribution.Desktop
```

Publish the current Windows NativeAOT desktop candidate:

```powershell
dotnet publish src\UniversalMediaDistribution.Desktop `
  -p:PublishProfile=win-x64-aot
```

Publish the NativeAOT server:

```powershell
dotnet publish src\UniversalMediaDistribution.Server `
  -c Release -r win-x64
```

The server stores releases, accounts, private snapshots, the online catalog,
and shared-list records beneath its data root. Override that root with
`UMD_SERVER_DATA_DIRECTORY`; configure its listening address with the standard
`ASPNETCORE_URLS` setting. Set a secret `UMD_SERVER_ADMIN_TOKEN` of at least 32
characters to enable account provisioning, catalog publication, and release
uploads. See [Server deployment, accounts, sync, and desktop updates](docs/SERVER_DEPLOYMENT.md)
for HTTPS deployment, account/catalog tools, privacy boundaries, signing-key
generation, installer publishing, and client configuration.

Compile the mobile hosts:

```powershell
dotnet build src\UniversalMediaDistribution.Android
dotnet build src\UniversalMediaDistribution.iOS -p:RuntimeIdentifier=iossimulator-x64
```

Android device/emulator testing requires the Android SDK. Running or signing the
iOS app requires macOS, Xcode, and the matching iOS SDK.

Android TV layout, focus, and remote-input flows can be exercised entirely on a
Windows PC with `eng/Test-AndroidTvOnPc.ps1`. It provides a clickable PC remote,
keyboard control, scripted JSON flows, screenshots, and accessibility/focus
assertions without an APK or ADB. See
[Android TV PC testing](docs/ANDROID_TV_PC_TESTING.md).

Production creates separate Android APKs for ARM touch devices, x86/x64 touch
devices, and ARM Android TV. The TV package contains only the landscape
lean-back launcher; mobile packages contain only the standard launcher. Use an
Android TV emulator or device to validate overscan, remote focus order, decoder
support, and real torrent playback before a store release.

Create versioned desktop archives:

```powershell
.\eng\Publish-Release.ps1 -Version 0.7.0 -RuntimeIdentifiers win-x64
```

See [Release packaging](docs/RELEASE_PACKAGING.md) for supported desktop targets,
checksums, and mobile signing boundaries.

The native engine accepts two optional environment settings:

- `UMD_MSE_POLICY=preferred|required|disabled` controls MonoTorrent message-stream encryption. The default is `preferred`, which allows encrypted peers with plaintext fallback; use `required` to reject plaintext peer-wire connections.
- `UMD_FAST_RESUME_POLICY=best-effort|accurate` controls restart verification. The default is `best-effort`; `accurate` performs the stricter resume checks.
- `UMD_UI_LANGUAGE=en|es` overrides the operating-system UI culture for all
  app-owned UI text and localized catalog-title selection.
- `UMD_APPLICATION_DATA_DIRECTORY=<path>` overrides desktop app storage for
  isolated development and smoke tests.

Authenticated private tracker endpoints are also optional. The desktop resolves
them from `UMD_TRACKER_ENDPOINT_<KEY>` variables without storing the passkey in
the catalog or database. See
[Optional private torrents and tracker authentication](docs/PRIVATE_TORRENTS.md)
for route examples, derived variable names, browser behavior, and encryption
limits.

## Metadata provider setup

Open **Metadata providers & credits** on the catalog screen and save:

- A [TMDB v3 API key](https://www.themoviedb.org/settings/api) for movies, live-action series, documentaries, and shorts.
- In the native app, a [MyAnimeList API client ID](https://myanimelist.net/apiconfig) for anime movies, anime series, manga, and web novels.
- A preferred language such as `en-US`, `es-AR`, or `ja`.

Supported detail screens then show an **Update from TMDB** or
**Update from MyAnimeList** button. The lookup requires an exact current,
original, or alternative-title match; a local release year disambiguates
duplicate results. A refresh changes only the primary title, alternative names,
description, and thumbnail. It preserves the catalog ID, subtype, categories,
genres, seasons, chapters, sources, rights, and user-progress identities.
Generic books, novels, comics, light novels, and audio entries are intentionally
unchanged.

Provider credentials are masked in the UI but are not encrypted by the
application. Native hosts store them in the local SQLite settings table, so a
full `.umdbackup` also contains them. Catalog-only JSON exports never include
these settings.

This product uses the TMDB API but is not endorsed or certified by TMDB. The provider-credits panel includes TMDB's approved short logo. TMDB use is subject to its attribution and licensing terms.

Native builds accept a magnet URI or `--magnet=<encoded magnet>` as a launch
argument; registering the native OS protocol association belongs to each
installer/package. Every host also exposes an **Open .torrent file** button in
the torrent intake panel.

## Catalog capture browser extension

A standalone Chrome/Edge side-panel extension for persistently selecting title,
category, chapter number, description, and ordered image lists across web pages
lives in
[`browser-extension/catalog-capture`](browser-extension/catalog-capture).
It generates renamed chapter images plus a torrent-local `info.json` compatible
with the UMD importer. It is separate from the .NET solution and includes its
own installation and verification instructions.
