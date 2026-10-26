# Olympus endpoint investigation — 2026-09-14

## Result

The endpoint paths and hosts used by `OlympusXyzApiContentProvider` still
match the website's published client. A reproducible source of 404 responses
is **expired series slugs**, not a universal endpoint migration.

The application logs recorded Olympus 404 responses during chapter discovery,
but did not include the failing title or URL. The live checks below reproduce
that failure mechanism; they cannot identify which title caused those log entries.

## Verified public endpoints

| Purpose | Endpoint | Live result with a current slug |
| --- | --- | --- |
| Search catalog | `https://olympusxyz.com/api/series/list` | 200, JSON `data` array |
| Series details | `https://olympusxyz.com/api/series/{slug}?type=comic` | 200, JSON `data` object |
| Chapter list | `https://panel.olympusxyz.com/api/series/{slug}/chapters?page=1&direction=asc&type=comic` | 200, JSON `data` array |
| Reader metadata | `https://olympusxyz.com/api/capitulo/{slug}/{chapterId}?type=comic` | 200, JSON `chapter.pages` array |

The site's entry bundle `/_nuxt/Bn70ngmo.js` referenced series component
`/_nuxt/DNEpvYmA2.js`. That component calls the same detail and chapter-list
routes. Its runtime `BACKEND_URL` is `https://panel.olympusxyz.com`.
These asset filenames are observations, not values to hard-code.

## Same-series reproduction

1. An initial catalog response listed comic ID **8**, “Los gemelos Baek”,
   with slug `los-gemelos-baek-20260913-080341532`.
2. Its detail and chapter-list requests returned **404**. The public series
   page also returned 404 with `/__stale_slug_404`; the detail response
   identified `/api/__stale_slug_404?type=comic`.
3. A subsequent catalog response listed the **same numeric ID 8** with slug
   `los-gemelos-baek-20260914-080510454`.
4. Using that refreshed slug returned **200** for details, **200** for a
   chapter-list page containing 40 entries, and **200** for reader metadata
   for chapter ID **24834**, containing 78 page references. No page images
   were downloaded.

A second homepage-linked series, `prota-m20-225-ene-su-laberinto13424`
(ID 179, “Odisea en la Mazmorra”), also returned 200 for details and chapters.

Catalog responses advertised `Cache-Control: max-age=900` and
`CF-Cache-Status: DYNAMIC`. Both the ordinary list URL and a one-off
cache-busted URL eventually returned the refreshed slug. This does not prove
which upstream cache produced the initial stale list or that cache-busting
alone is a reliable repair.

## Application implications / recommended follow-up

`SeriesListCacheLifetime` is currently one day. Search stores the mutable slug
as `ExternalId`, and the list loader can return stale cached JSON after a
refresh error. This allows expired slugs to outlive the provider's current
routes.

A robust recovery would retain the provider's numeric series ID, refresh the
catalog once after a slug-related 404, remap by that ID, and retry with the
new slug. It should preserve known chapter routes and avoid treating a failed
refresh with stale data as successful recovery. A shorter cache lifetime alone
does not repair already-imported references.

No Olympus production behavior was changed by this investigation.

## LeerCapitulo parser fix verified alongside this investigation

Replaced the attribute regex with a forward-only `ReadOnlySpan<char>` parser.
Only the matched value is materialized and HTML-decoded. Tests cover quoted
and unquoted values, case-insensitive names, boolean attributes, embedded fake
attributes, entities, duplicates, unterminated quotes, million-character
inputs, and daily-update chapter extraction with long valueless attributes.

Enumerating the previous regex over a million-character valueless attribute
followed by `href` reproduced `RegexMatchTimeoutException` at its two-second
limit. The replacement passed this input without regex backtracking.

Validation: 19 focused LeerCapitulo/Olympus tests and all 962 tests in
`UniversalMediaDistribution.UI.Tests` passed in Release configuration.
No deployment was performed.
