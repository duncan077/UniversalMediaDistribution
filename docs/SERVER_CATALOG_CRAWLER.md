# Server database, catalog crawler, and recovery

## Storage and migration

The server uses SQLite (`server.db` in `UMD_SERVER_DATA_DIRECTORY`) on a local
disk. This suits the existing single Linux ARM64 server and reuses the SQLite
packages already used by the clients; no database service or password is
required. Do not put the database on NFS/SMB or share it across machines. The
HTTP server and its local crawler may run together: WAL permits concurrent
readers, and SQLite transactions coordinate their writes.

Accounts, password hashes, session-token hashes, private sync snapshots,
shared lists, friend state, and release manifests are stored as typed JSON
documents inside SQLite. The public catalog, name aliases, identity redirects,
crawl queue, checkpoints, retries, runs, and overlap lock use indexed tables.
Installer/package binaries stay in the existing `packages` directory;
temporary watch-party rooms remain in memory.

On first startup, legacy JSON files are imported in one transaction. Existing
passwords, sessions, account IDs, sync revisions, and reading state are retained.
Original files are left untouched, but **are not read again after migration**.
An invalid legacy document stops startup instead of silently dropping data.

Before the first database-backed deployment:

1. Stop the old server and any scheduled writers; back up its entire data
   directory and keep the service environment/signing keys separately secured.
2. Run the new binary with the existing `UMD_SERVER_DATA_DIRECTORY` and
   `--initialize-database`, or start the new server (which performs the same
   migration before reporting healthy).
3. Verify sign-in, a sync download, and the catalog; create a verified database
   backup as below before enabling the crawler.

Never run the old JSON-writing server beside the new one. Once new writes have
been accepted, rolling back to that older binary would expose stale JSON and
lose visibility of later changes. Restore a matching pre-upgrade backup for a
legacy rollback, understanding that post-backup changes would be lost. A normal
binary-only rollback is safe only between compatible database versions.

Private sync documents are not subjected to the public crawler's metadata-only
policy. Existing thumbnails, provider context, chapter progress, reader
preferences, lists, IDs, and logical clocks remain intact. Legacy delta indexes
whose keys no longer match normalized records fall back to a full snapshot;
the next successful write rebuilds the index without resetting its revision.

## What the crawler keeps

The crawler reuses the app's provider parsers and requests full title metadata
and episode/chapter listings. It saves names and aliases, descriptions, media
type, release metadata, contributors, genres, seasons, and episode/chapter
numbers/names. It does **not** save artwork, thumbnail URLs, media sources,
reader-page images, or provider-specific chapter URLs. It never opens players
or calls video-feed extractors. Internal provider ID + title ID/route bindings
are retained to resume discovery and refresh the same title.

Before validating scraped metadata, duplicate alternative names (including the
legacy `aliases` field) are collapsed using the validator's normalized name,
language, and alias-type identity. The first spelling is retained; distinct
languages/types and original names remain. Malformed aliases still fail
validation. This normalization is not applied to manual uploads or private sync
documents, whose validation rules remain unchanged.

Catalog identity converges through normalized names and aliases, with media
subtype and known release-year checks. Existing non-null metadata and chapter
lists are merged rather than erased by sparse provider responses. Renames keep
the existing ID; converged IDs are redirected and their crawl bindings moved.
There is no fuzzy-name matching, and a provider omitting a title/chapter does
not delete it. Unchanged metadata does not increment the catalog revision.

## Provider discovery coverage

The app and server share the same lazy registry: 16 built-in sites plus the
optional external UMD API. Discovery does not depend on a user's search text.

| Provider ID | Discovery | Scope / limits |
| --- | --- | --- |
| `animetmo` | Full `az-catalog.json` index | Stable 500-title slices; existing parser safety limits remain |
| `olympusxyz` | Full `api/series/list` JSON index | Comics supported by the app; not Olympus novels |
| `animeav1` | Paged SvelteKit catalog JSON | Honors the upstream-reported extent, not a promise of the entire site |
| `framextv` | Paged public `/api/discover`, movies then TV | Full details/season metadata; upstream reported extent, not proof of playable feeds; no stream/subtitle calls in crawler |
| `manhwaweb` | Paged JSON library via discovered backend | No old search-page cap; chapter list from title detail |
| `capibaratraductor` | Paged `api/manga-custom` JSON | Same `nsfw=false` scope as the app; checks both `workType` and `manga.bookType.code` to exclude prose mislabeled as manga; detail supplies full chapter list |
| `doramasflix` | GraphQL `paginationDorama` / `paginationMovie` | Separate series and movie cursors |
| `leercapitulo` | Paged `/manga/?page=N` HTML | Full detail parser; no reader pages requested |
| `zonatmo` | Paged `/biblioteca` HTML | Library titles and detail chapter metadata |
| `uchuujinmangas` | Paged `/manga/?order=update&page=N` HTML | Detail chapter metadata |
| `jkanime` | Paged `/directorio?p=N` HTML | Existing detail/episode JSON parsers |
| `pelisplus` | Paged movie and series HTML directories | Browser challenges can block unattended access |
| `pelisplus-to` | Paged movie and series HTML directories | Uses the configured/redirected TioPlus origin |
| `pelisflix-hd` | Paged movie and series HTML directories | Uses current mirror and existing retired-link normalization |
| `poseidon-hd2` | Paged movie and series HTML directories | Reuses HTML/Next.js detail parsers |
| `seriesflix` | Paged series HTML directory | Movie navigation goes to PelisflixHD, already covered separately |
| `umd-json-api` | External `/current` catalog snapshot, locally sliced | Requires an external base URL; bounded by the existing document-size limit |

All 15 built-in sites returned titles on two successive discovery pages in the
2026-10-03 live probe; second pages contained additional title IDs. A further
probe loaded and validated one title's metadata from each site without opening
players (including an Olympus list of 555 chapters). This is a
smoke check, not an assertion that every title, every page, or future requests
work. Some sites intermittently require a browser challenge. The headless
crawler records the failure and backs off; it does not solve CAPTCHAs or treat
a challenge/empty/repeated page as completed backfill. Earlier PelisPlus requests
returned 403 even though the later probe succeeded.

The optional UMD source has no default upstream. Set
`UMD_CRAWLER_BASE_UMD_JSON_API=https://external.example/api/v1/catalog/` only for
an intended external catalog, **not this same server**. Unconfigured UMD is
reported as `configuration_required` and skipped.

## Hourly operation

Run `UniversalMediaDistribution.Server --scrape-once` in the existing server
environment. It is a one-shot command and does not open an HTTP listener.

Each run checks recent updates, queues a bounded number of directory pages,
and loads queued title details. Each provider has one sequential worker,
starting at most one title per configured interval (initially two seconds; slower when upstream calls take
longer). Providers advance independently, so one large or slow queue does not
prevent the others from progressing. There is no fixed title-count cap: workers
continue until their due queue is empty, the run's time budget expires, or a
provider error/backoff stops that worker. Previews are read in bounded 100-title
batches; a failed title is attempted at most once per run, including explicit
retry mode. New titles and due refreshes share the queue
so neither starves the other. A title normally refreshes after seven days;
daily-update discovery can make it due sooner. Completed directories are
rescanned after seven days. Large backfills intentionally span many hours.
Previously queued titles now recognized as an unsupported format are logged
as informational skips rather than provider failures. Their earlier title
failures are cleared and they are checked again after seven days, so they do
not block subsequent supported titles or become permanently excluded after a
source corrects its metadata. Existing catalog entries are not deleted.

Checkpoints survive restarts. Failures use 1–24 hour exponential backoff;
HTTP 429/503 honor a bounded `Retry-After`. Requests are paced per host, public
HTTP(S) only, with private-address/DNS and redirect validation. A database
lease prevents concurrent crawler runs and expires after a killed process.
Cancellation saves the run outcome and releases the lease.

| Environment variable | Default / accepted range |
| --- | --- |
| `UMD_SERVER_DATA_DIRECTORY` | Must match the HTTP server's durable directory |
| `UMD_CRAWLER_PAGES_PER_PROVIDER` | `2`; 1–100 |
| `UMD_CRAWLER_MAX_MINUTES` | `45`; 1–50 |
| `UMD_CRAWLER_OPERATION_SECONDS` | `180`; 10–1200; individual HTTP requests also have a 90-second limit |
| `UMD_CRAWLER_DISABLED_PROVIDERS` | Empty; comma-separated exact IDs from the table |
| `UMD_CRAWLER_BASE_<ID>` | Optional public base-URL override; uppercase ID with `-` replaced by `_` |

`UMD_CRAWLER_TITLES_PER_PROVIDER` is retired and ignored, including old values
still present in the service environment. The two-second title cadence replaces
that count limit; HTTP pacing and provider Retry-After remain in place.

The admin's provider table has an **Intervalo (s)** field and **Guardar** button
per provider. Values are whole seconds from 1 to 60; the initial value is 2.
They are stored in `crawler_provider_settings` in the same server database,
survive restarts, and are read by both manual runs and `--scrape-once` at the
start of each run. Saving during an active run affects the next run, not the
current worker. Each provider's HTTP requests (including discovered backend
hosts) and title starts respect its interval; providers sharing an HTTP host
also share the per-host queue. Retry-After/backoff still wins over this setting.
There is no environment-file edit or service restart needed to change it.

Logs contain provider IDs, stages, safe failure categories, and changed-title
counts, not fetched markup, media URLs, credentials, or user reading state.
Exit codes: `0` completed/budget exhausted/already running, `2` upstream or
title failures, `130` cancelled. Run outcomes are also kept in `crawler_runs`;
per-provider retries are in `crawler_providers`. A partial run commits its
successful titles and retries failures later.

### Recommended: systemd user timer

Templates are in `eng/systemd/umd-catalog-crawler.service` and `.timer`. They
match the publishing script's default `%h/apps/umd-server` layout and load the
existing `config/environment`. Adjust paths if your deployment differs, and
ensure its data-directory setting matches the running server before enabling.
Install both files under the deployment user's `.config/systemd/user/`, then:

```sh
systemctl --user daemon-reload
systemctl --user enable --now umd-catalog-crawler.timer
systemctl --user start umd-catalog-crawler.service
journalctl --user -u umd-catalog-crawler.service -n 100
systemctl --user list-timers umd-catalog-crawler.timer
```

The timer starts once per hour and runs one catch-up after downtime. It requires
the same user service manager/linger setup as `umd-server`. Stop the timer and
the one-shot service before a database restore or incompatible deployment.

### Alternative: cron

Use **either** the timer **or** cron. For the default deployment layout, a
deployment-user crontab can contain the following. The environment file must
be trusted, readable only by that user, and valid shell assignment syntax.
The `logs` directory must already exist and the log should be rotated.

```cron
0 * * * * /bin/sh -c 'set -a; . /home/duncan-umd/apps/umd-server/config/environment; set +a; exec /home/duncan-umd/apps/umd-server/current/UniversalMediaDistribution.Server --scrape-once' >> /home/duncan-umd/apps/umd-server/logs/catalog-crawler.log 2>&1
```

These are installation instructions only: adding the files to the repository
does not enable a timer, edit a crontab, or start production crawling.

## Public catalog access

Existing search and metadata routes remain available. `/api/v1/catalog/current`
returns the legacy full document while it fits the 30 MiB snapshot bound;
larger catalogs return HTTP 413 with `catalog_requires_pagination`.

`GET /api/v1/catalog/items?limit=50` returns `revision`, `continuationToken`, and
`document` (the normal catalog format). Pass that token as `cursor` until null.
Limits are 1–100 items and approximately 8 MiB per page. Search is indexed by
media type and bounded to 500 results / 8 MiB. A single oversized item returns
a structured 413 error rather than an unbounded response.

Pages reflect the current database revision, not a long-held snapshot. If a
client needs a consistent full export while the crawler is writing, compare
page revisions and restart when they change, or pause the crawler briefly.
Metadata hashes are streamed from a consistent read transaction, so metadata
remains available after the full catalog outgrows one response. Manual admin
publication still **replaces** the public catalog; stop the crawler first if
that replacement must remain unchanged.

## Web diagnostics

Open `/admin` on the deployed server and enter its existing
`UMD_SERVER_ADMIN_TOKEN`. The page uses the token as a bearer header and keeps
it only in browser memory; ordinary account login does not grant access. The
HTML shell contains no server data. The diagnostics API requires the admin
token, and administrative responses are not cached.

The page shows catalog size/revision, process uptime and memory, database size,
HTTP request/failure counts, per-provider discovery and title queues, last
attempt/success, retry times, initial catalog traversal progress, and the last
25 scraper runs. It refreshes every 30 seconds while visible (every 5 seconds
while the scraper is active). An expired
crawler lease with an unfinished run is shown as interrupted, and missing
recent runs prompt a manual run or cron/timer check.

The **Ejecutar scraper** section can start one provider or all enabled providers
without installing a cron/timer. Runs start in the background and continue if
the browser disconnects. Run history tracks progress; logs record failures only. The HTTP service uses
the same environment settings, bounded batches, provider overrides, and database
lease as `--scrape-once`: manual and scheduled workers cannot overlap. A run is
not an unlimited full-catalog import; subsequent runs resume pending work.
Restarting/stopping the HTTP service cancels its current manual run and saves
the checkpoints. Disabled or unconfigured providers cannot be selected.

**Reintentar fallos pendientes** bypasses ordinary failure backoff for providers
and queued failed titles. Provider throttling/Retry-After and configuration
requirements are still respected; successfully loaded titles are not forcibly
refreshed before their normal due date. Cron remains necessary only for automatic
recurring updates, not for these on-demand executions.

Only failed crawler operations and server/HTTP failures (error level) are
stored in the same SQLite database. This works across the separate HTTP and
`--scrape-once` processes and survives restarts. Events include provider, run,
stage, failure category, HTTP status when available, and a bounded diagnostic
message; fetched URLs and response bodies are not stored. Retention is 24 hours,
with an additional cap of 10,000 errors. The HTTP service physically deletes
expired errors and legacy informational/warning events at startup and every
minute, including while idle. Writes and log reads also apply the cleanup.
Successful operations, startup/shutdown, and configuration changes are no longer
persisted as events. Catalog data, checkpoints, statistics, and scraper run
history are unaffected. System journal and cron output files have their own
retention policies. The page provides provider filters and pagination through
retained failures.

Authenticated read-only routes are `GET /api/v1/admin/diagnostics` and
`GET /api/v1/admin/diagnostics/logs?provider=olympusxyz&level=error&limit=100`.
The log route returns `items` and an optional `before` cursor; pass the cursor
as `before` for older events. Limits are 1–200 events. All assets and JSON
metadata are packaged with the server, including NativeAOT builds; there is
no separate frontend deployment or external JavaScript dependency.

The authenticated mutation route is `POST /api/v1/admin/diagnostics/crawl`, with
a JSON body such as `{"providerId":"olympusxyz","retryFailures":true}`. Omit
`providerId` or set it to `null` for all enabled providers. The request limit is
4 KiB. It returns `202` with `status`, `runId`, `providerId`, and `message` when
accepted; `409` means another manual/scheduled worker owns the lease, and `400`
means an invalid/disabled/unconfigured provider or invalid crawler configuration.
An ordinary account session is not sufficient; the existing admin token is required.

`PUT /api/v1/admin/diagnostics/provider-interval` saves one provider's cadence,
with JSON such as `{"providerId":"olympusxyz","intervalSeconds":5}`. It uses
the same admin authorization and 4 KiB body limit. Unknown providers, fractional
seconds, and values outside 1–60 return `400` without changing the setting.
Diagnostics include each provider's effective `intervalSeconds`, even before
its first crawl; changes are recorded in the configuration log.

The HTTP process and scheduled scraper should load the same environment file;
the dashboard labels disabled providers from `UMD_CRAWLER_DISABLED_PROVIDERS`
in the HTTP process. Updating the binary does not install or enable a cron/timer.

## Backups and restore

With the same data-directory environment, run:

```sh
./current/UniversalMediaDistribution.Server --backup /absolute/secure/backups/server-2026-10-03.db
```

The command refuses to overwrite a file. It uses SQLite's online backup API,
includes committed data still in WAL, verifies database integrity, and then
renames the temporary file to the requested destination. On Linux new database
and backup files are owner-read/write only. The HTTP server can keep running.
**Do not copy just a live `server.db` file:** uncheckpointed transactions may
still be in `server.db-wal`.

The database backup includes account/sync state and catalog/crawler state. Also
back up installer/package files, service configuration, TLS material, and the
publisher's private signing key separately; they are not in SQLite. Treat
database backups and retained legacy JSON as private account data and keep an
encrypted off-host copy. No automatic backup retention/deletion is installed.

To restore, stop both the HTTP server and crawler. Preserve the current data
directory for recovery. Create a new private local data directory, place the
chosen backup there as `server.db`, restore the corresponding package files,
and point the service environment at that directory. Do not mix it with an
old `server.db-wal` or `server.db-shm`. Start the server, check health/sign-in/
sync/catalog, then re-enable the timer. Restoring intentionally rolls all state
back to the backup time, including session revocations; consider whether
previously revoked tokens must be revoked again before reopening public access.

The source uses generated JSON metadata and SQLite's NativeAOT-compatible
package stack. The production publishing script performs the actual Linux
ARM64 NativeAOT compilation; a Windows test run is not a substitute for that
target build and deployment smoke check.
