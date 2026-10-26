# Universal Media Distribution

One library for everything you watch and read — on your desktop, your phone,
and your television — backed by your own server instead of somebody else's.

UMD is an Avalonia client for browsing and consuming legally distributable
indie, public-domain, Creative Commons, and otherwise authorized media. It
brings torrent-delivered content, online catalogues, and live TV into a single
catalogue with one set of favourites, one reading position, and one place to
look.

---

## The problems it solves

**Your library is scattered across a dozen apps.** Films, series, anime, manga,
books and audio each live somewhere different, with a different idea of what
you have watched. UMD keeps one catalogue across all of them, with shared
favourites, progress, and personal lists.

**Your progress does not travel.** Stop on chapter 57 on the sofa and your
phone has never heard of it. UMD syncs favourites, positions, reader
preferences and lists through a server you run, and merges rather than
overwrites — offline progress on two devices survives being reconnected.

**Someone else decides what you can reach.** Sources here are yours to add and
remove: torrent catalogues, HTML and JSON providers, M3U live-TV lists. Nothing
is fetched from a provider you have not switched on.

**Televisions are an afterthought in most apps.** UMD ships a dedicated
Android TV package with a lean-back launcher, directional navigation, and a
remote-first layout — not a phone UI stretched to 1080p.

**Media apps watch you back.** There is no telemetry, no account requirement,
and no third party in the loop. The catalogue works offline; an account only
exists if you set up your own server.

---

## What it does

### One catalogue, many shapes

Films, series, anime, books, manga, web novels, audiobooks and music share a
single domain model with proper subtypes, seasons, chapters, episodes and
tracks. Titles carry multiple localized names, and cards pick the one that
matches your language.

Personal lists mix anything — a manga, a film and an audiobook in the same
list. Cards show how far a series has got and mark a chapter you have not
reached yet.

### Sources you choose

Three kinds of content source, all switched on and off per device, grouped by
kind in Settings:

- **Manual magnets** — paste a link, or open a `.torrent` file.
- **Torrent catalogues** — your own JSON endpoints.
- **Online providers** — 16 built-in HTML/JSON providers for manga, anime and
  film catalogues, plus your own server's catalogue.
- **Live TV** — 5 built-in M3U and HTML channel lists.

Search runs across the local catalogue and every enabled provider at once,
keeps local and online results for the same title side by side, and labels each
by origin.

### Reading and playback

- **Image reader** for manga and comics, paged or continuous, with lazy page
  streaming, remembered position, and JPG/PNG/WebP plus CBZ archives.
- **EPUB reader** with spine ordering, mixed text and images, chapter
  navigation and text sizing.
- **PDF reader** built on PDFium with paged and continuous layouts.
- **Video and audio** through LibVLC on desktop and the platform player on
  mobile, streamed directly from a torrent as it downloads, with resume
  positions and language/release switching.
- **Embedded web players** for hosts that only offer an embed, behind a
  built-in Adblock Plus / uBlock Origin filter engine that refuses requests at
  the network layer, hides elements before the page's own scripts run, and
  suppresses popups and click traps.

### Several people, one installation

Profiles work like a streaming service's: the catalogue is shared, everything
personal is not. Each profile keeps its own favourites, progress, lists and
reader settings, and signs into its own server account. Switching closes
whatever was open and re-reads only the personal half.

### Your server, your data

An optional self-hosted ASP.NET Core server (NativeAOT, ARM64-friendly)
provides:

- Account sign-in with PBKDF2 hashing, hashed bearer sessions and login
  throttling.
- Convergent device sync — full snapshots plus small progress patches, with
  per-record versions and deletion tombstones so offline edits merge instead of
  clobbering.
- An optional online catalogue that joins the same search.
- Friends: see what people you have added are reading, and share a personal
  list as a revocable read-only link.
- Signed release manifests for in-app updates.

Credentials, device settings, downloads, file paths and downloaded files never
leave the device.

### Torrenting done carefully

A MonoTorrent engine with sequential streaming reads, per-file priorities,
pause/resume, seeding and persistent fast-resume state. Private torrents are
enforced fail-closed: approved HTTPS trackers only, no DHT/PEX/LSD, credentials
resolved locally, and secrets kept out of diagnostics.

---

## Platforms

| Platform | Notes |
|---|---|
| Windows desktop | Bundled LibVLC runtime; signed in-app updates |
| Linux desktop | `linux-arm64` published; `linux-x64` builds without NativeAOT |
| Android phone | Separate ARM package, private app storage |
| Android TV | Dedicated ARM package, lean-back launcher, D-pad navigation |
| iOS | Head present in the solution |
| Server | NativeAOT ASP.NET Core, `linux-arm64` |

No macOS target, and no browser/WebAssembly head.

---

## How it is built

- **Avalonia 12** UI shared by every head, on **.NET 11 RC1**.
- A layered split — `Domain`, `Application`, `Catalog`, `UI` — with platform
  heads holding only composition and platform services.
- **SQLite** for the catalogue and user state, with versioned migrations,
  startup integrity checks, and automatic quarantine plus clean recovery if a
  database is found corrupt.
- Engine-neutral contracts (`ITorrentEngine`, reader and player abstractions)
  so a platform can supply its own implementation.
- ID-free catalogue JSON import/export, plus full database backup and restore
  with a pre-restore safety copy.
- English and Spanish throughout, including launcher labels and accessible
  names.

---

## Privacy and safety posture

- No telemetry, no analytics, no third-party services in the default path.
- An account is optional and only ever points at a server you configure.
- Artwork is only loaded from origins a provider is trusted for.
- Local peer discovery is disabled globally; private sessions never use
  DHT, PEX or LSD.
- Updates verify size, SHA-256, signing key, signature and installer type
  before anything is launched.

---

## Scope

UMD is a client for media you are allowed to distribute and consume. It ships
no content of its own: every source is one you add, and every provider is one
you switch on.
