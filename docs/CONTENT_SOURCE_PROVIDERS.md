# Content source providers

Content source providers answer “where can this title and its distributable
content be obtained?” They are separate from catalog contributors, which
represent authors, studios, publishers, actors, and other credits.

## Provider kinds

### Online catalog (metadata only)

The server's online catalog uses the JSON API transport internally, but it is
not a content provider. Its descriptor sets `ProvidesUnitContent=false`. It
remains available for catalog searches, metadata imports/refreshes, and remote
catalog synchronization, controlled by the existing online-catalog setting.
It is excluded from API-provider settings groups, named content-provider
filters, chapter reader lookups, and episode/feed lookups. Enabling only this
catalog does not enable chapter/video resolution buttons.

Importing its metadata does not add an artificial distributor credit. Historic
catalog-only credits are hidden from cards and detail pages without deleting
the stored records or changing real provider/author credits. A title/chapter
misfiled by an upstream site is still upstream metadata; the app cannot infer
the real series from its images or safely discard it solely because the
chapter number has a gap.

### Manual

The built-in manual provider is the existing magnet-link intake. It is always
available, has no base URL, and cannot be removed. A user can paste a magnet or
let the operating system/browser pass one to the app. Optional torrent-local
`info.json` import remains supported.

### Torrent catalog

A user can create, rename, enable, disable, or remove a torrent-catalog
provider. Its base URL is an exact HTTP/HTTPS endpoint returning a complete,
versioned, ID-free `CatalogTransferDocument`.

Search downloads the document and filters it locally by:

- Primary and localized/alternative names.
- Description.
- Genres and categories.
- Contributor names.
- The currently selected Movie, Book, or Audio section.

Selecting a result fetches the document again, finds the same media-type/name
identity, and imports that single item through the normal catalog merge. This
means torrent mappings, private-mode rules, chapters, seasons, artwork, and
readers/players use the same validation and runtime paths as a local import.

### API

An API provider is an `IApiContentProvider` implementation registered by
application code. Settings cannot invent or remove implementations. For each
registered implementation, the user can:

- Enable or disable it.
- Replace its base URL.

The interface translates a provider-specific protocol into the UMD models:

- `SearchAsync` returns stable provider item references plus catalog previews.
- Providers with a latest-content feed opt into `SupportsDailyUpdates` and
  implement `LoadDailyUpdatesAsync`.
- `LoadItemAsync` returns the complete ID-free catalog item.
- `LoadUnitImagesAsync` receives the matched provider item plus the selected
  local chapter/episode request and returns that unit's complete ordered image
  set. A default implementation can read image sets returned by
  `LoadItemAsync`; providers with separate chapter endpoints should override it
  and fetch only the selected chapter.
- `GetTrustedArtworkOrigins` can opt provider-owned cover/CDN origins into
  bounded JPG/JPEG, PNG, and WebP artwork loading.

API provider item IDs are kept separately in the optional title-binding store,
not embedded into catalog items/chapters. Explicitly importing an online title
now remembers the selected provider route and its actual upstream title, even
when the local catalog retains a different primary name after convergence.
Metadata-only catalogs and torrent documents do not create playback bindings.
When the user
chooses **Find chapter on enabled APIs** for an existing database chapter, the
app resolves it dynamically:

1. Try a remembered pairing first, revalidating the returned title/unit. If it
   is stale, drop it and search the enabled code provider with the manga's
   primary name and up to three original/alternative names.
2. Compare every returned primary/original/alternative title with every local
   title using normalized Levenshtein similarity.
3. Ignore candidates below the `0.72` minimum similarity.
4. Load up to the three closest candidates per provider.
5. Accept only a candidate that contains an image set for the selected chapter
   kind, number, and optional season. Numeric chapter forms such as `1` and
   `01` are equivalent.
6. Load the best candidate's current complete manga item and merge its metadata
   and chapter list into the local catalog.
7. Open the selected chapter and keep progress on its existing local identity.

The merge keeps the local primary title as the catalog identity and records a
different provider title as an alternative name. Matching chapters are updated,
numeric equivalents such as `1` and `01` retain the local display number and
progress key, missing provider chapters are added, and local-only chapters and
torrent sources are retained. If the title came from an online search instead,
the normal provider-selection flow creates the manga when it does not yet
exist.

The lookup persists normal ID-free manga metadata and chapters, and remembers
successful pairings in the separate binding store when configured. A later read
tries that route before searching by name; stale routes fall back to search.

Poseidon reads the original title from the selected title's Next.js
`thisSerie`/`thisMovie` data, not unrelated recommendations. If a search starting
with a common Spanish/English article has no exact title match, it makes at
most one retry without the leading article. Returned cards still must match
the original full query and all filters. This recovers titles such as
`El Tren Infinito` without walking hundreds of broad search pages or weakening
the title/episode identity checks.

Direct image pages use absolute HTTP/HTTPS URLs without embedded credentials.
Each image set must target either the whole title or an existing unit by unit
kind, display number, and optional season number. Positions must be contiguous
and zero based: `0, 1, 2, ...`. The shared reader supports one page at a time or
continuous top-to-bottom display and stores progress against the imported local
identity.

LeerCapitulo follows at most three explicitly checked same-origin redirects,
rejecting private/foreign targets and loops. If a detail redirect merges an old
title ID into a canonical manga route, chapter parsing, remembered routes, reader
requests, and image referrers use the canonical identity/URL. This handles the
old `j5fxoekrps` entry for Aman's Animal Tales redirecting to `keusb3jkx7` without
loosening chapter ownership checks or changing local catalog/progress IDs.

SeriesFlix's current default is `https://seriesflixhd.pw/`; `.win` and its `www`
variant are marked retired so saved app settings move to the new default.
Explicit unrelated mirrors remain configurable. A metadata-only live check of
The Walking Dead loaded all 11 seasons in 26 seconds on 2026-10-19. The reported
server timeout was not reproduced and is not assumed fixed by raising limits;
the domain update removes the obsolete redirect hop. Both provider changes also
apply to the shared headless catalog crawler.

`ApplicationComposition.Configure` accepts additional
`IApiContentProvider` instances. The built-in `UmdJsonApiContentProvider` is a
reference implementation used internally by the metadata-only online catalog.
It is not offered as a reader/video provider. It calls:

```text
GET {baseUrl}/search?q={query}
GET {baseUrl}/search?q={query}&mediaType=movie|book|audio
```

The response is a `CatalogTransferDocument` containing complete matching items.
Provider-specific implementations can use any remote protocol internally; the
UI never creates those implementations from data.

## FramexTV

`FramexTvApiContentProvider` supplies movies and TV series through the public
JSON API used by `https://framextv.xyz/`. It is disabled by default and can be
enabled from API provider settings. The normal website origin selects its three
known API backends with bounded failover on connection/5xx failures, not on 429.
A custom base URL is treated as an API root, not scraped for arbitrary hosts.
Search uses `/api/search`; details and seasons use `/api/details` and
`/api/season`. Type + numeric ID are kept together so movie and TV IDs cannot
collide. Movies have one episode; episode rediscovery loads only the requested
season and rejects mismatched title/season/episode identities.
The public API currently coerces requested season 0 (specials) to season 1
(confirmed for Watch What Happens Live with Andy Cohen, ID 22980). During full
imports this mismatched specials listing is omitted without blocking regular
seasons. Explicit special lookup remains rejected and special playback URLs are
not offered, so season-one episodes cannot be mislabeled as specials. Mismatches
for regular seasons still fail validation, with requested/returned numbers in
the diagnostic message.

Playback reads `/api/stream` on demand, preserving HLS/MP4/DASH types and supplied
Referer/Origin/User-Agent headers. Subtitle tracks are captured from the same
response, or from `/api/subtitles` when absent, with URL validation, deduplication,
language/default flags, and bounded lists. Tracks reach desktop LibVLC and Android
LibVLC, including server changes; Android's system engine does not gain external
subtitle support. If the API has no usable feed, the official embed remains a
selectable WebView/interactive-native-capture fallback. The user must start video
normally; no CAPTCHA is solved automatically. DOM subtitle tracks and already
known API subtitles survive capture. This does not promise a stream for every
TMDB title; upstream extraction can be unavailable or rate-limited.

The shared server registry also includes metadata-only paginated discovery for
movies then TV. The crawler does not resolve streams or persist subtitles/video
URLs. JSON is read with `JsonDocument`, with no runtime code evaluation or
reflection serialization, to retain NativeAOT compatibility.

## AnimeAV1

`AnimeAv1ApiContentProvider` is registered as **AnimeAV1** (anime scope,
disabled by default; enable it in provider settings). It reads the public
SvelteKit `__data.json` routes at `https://animeav1.com/` for daily episode
updates, paginated title search, title metadata, complete episode lists, and
the selected episode's `embeds` grouped by SUB/DUB. Movies remain one episode.
Genre/year filters are not advertised; text search uses the site's `search`
and `page` parameters. Smaller requested pages retain an offset continuation.

The indexed JSON payload is read without executing JavaScript, recursive graph
expansion, or reflection serialization. Responses are bounded to 4 MiB;
episode lists, source counts and reference-table size are also bounded.
Cancellation flows through both catalog HTTP and the shared video resolver.
Artwork uses the site's public `cdn.animeav1.com` cover/backdrop/screenshot routes.

Video hosts use the shared `VideoHosterResolver`; no provider-specific allowlist
is introduced. UPNShare/Byse retain the existing interactive WebView capture
path when an ordinary native extraction is unavailable. Download links (MEGA,
TransferIt, 1Fichier) are not misrepresented as stream embeds. See the dated
AnimeAV1 section in `VIDEO_FEED_EXTRACTOR_AUDIT.md` for live-check limitations.

## Daily Home refresh

Opening **Home** asks every enabled API provider that supports daily updates
for its latest titles. The built-in LeerCapitulo, Olympus Scanlation,
ManhwaWeb, MonoSchino, and PelisPlus providers translate their current public
latest-feed or rendered catalog payloads into catalog previews with cover
artwork and the newest chapter or episode markers. PelisPlus movie previews
include a full-movie playback unit so a title discovered by the daily refresh
can later resolve its current embedded server list.

The app merges those previews into the local catalog. Existing descriptions,
chapter/episode history, progress identities, and torrent sources are retained;
new units are appended without reordering existing units. Opening one of those
titles still performs the normal full provider refresh, which fills any older
history not included in the lightweight latest feed.

Each provider/base-URL pair is checked at most once per local calendar day.
Native hosts persist successful check dates in SQLite. A failed provider does
not block successful providers or Home, and it is not marked complete, so it
retries the next time Home opens. The generic UMD JSON provider has no assumed
latest endpoint and therefore does not opt into this behavior.

## Search and selection behavior

An explicit search queries the local catalog and every enabled online provider.
Results are not deduplicated across origins. For example, one manga may appear
twice:

- `LOCAL`
- `ONLINE · Community catalog`

This is intentional. Selecting the local result opens its existing local
record. Selecting the online result asks that provider for the current details,
validates them, and merges the item by normalized media type plus name before
opening it. A provider result therefore creates a new local ID when absent and
retains the existing local ID and progress when the same title already exists.
This merge stores only normal UMD catalog data; it does not create a permanent
API-provider binding.

One failed provider does not discard successful local or online results. The UI
reports how many providers were unavailable without exposing provider URLs or
tracker credentials.

## Persistence and platform behavior

Native hosts store provider settings in the SQLite `application_settings`
table, so a full database backup includes them. The browser stores them in
same-origin local storage. Catalog-only JSON never includes provider settings.
Native hosts store daily-refresh checkpoints in the same settings table,
separately for each provider and configured base URL.

Browser providers must permit CORS from the application origin. Browser direct
images must also permit cross-origin image requests. Native hosts are not
subject to browser CORS.

The provider system does not weaken torrent privacy:

- Public/private torrent selection still comes from the imported source.
- Private native sources still use approved HTTPS tracker routes.
- Private browser sources still use approved WSS routes.
- Tracker credentials remain outside provider/catalog JSON.
