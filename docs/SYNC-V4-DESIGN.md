# Sync v4 — stable identity, tombstones, delta sync

Status: **All three stages implemented** (2026-08-16). Stage 1: stable
IDs, redirects, migration v13. Stage 2: item versions, tombstones,
daily-fetch suppression, migration v14. Stage 3: per-record revision index on
the server, GET /api/v1/sync/changes, server-side merge on upload conflicts
and on the (now general-purpose) patch endpoint, DeltaSync capability, and a
delta-first client reconciler measuring local changes against an in-session
baseline. Full-snapshot endpoints remain the bootstrap and repair path.

Implementation notes that amend the design below: the upload delta rides the
existing PATCH sync/snapshot/progress endpoint rather than a new POST (the
endpoint always merged arbitrary partial documents; it now merges catalogs
through the real merger instead of a name-keyed union). The client baseline
is in-memory per session — the first reconcile of a session runs the full
path once and seeds it. Server-side merge applies only to version 4+ uploads;
older documents still get the conflict answer so a mixed fleet is never
restamped to a format it cannot read.
Scope: the account backup/sync system (`UserSyncDocument`, the stores, the
reconciler, the self-hosted server). Nothing here changes provider ingestion
or the reading experience.

---

## 1. Why now

Every sync failure diagnosed on 2026-08-15 traces to two structural facts:

| Failure | Root cause |
| --- | --- |
| "items[N] duplicates catalog item …" refused the restore | Identity over the wire is `mediaType + normalized name`; convergence renamed items onto colliding names |
| "Synchronized progress target … is not in the catalog" after a rename | Progress references the *name*; the merge renamed the item underneath it |
| Episodes 1–3 silently dropped, then the restore refused whole | The merge **chose between** two views of one title by content hash instead of combining them, because nothing but the name says they are the same item |
| UI froze for seconds during sync/combine | The transport is the whole database, both directions, every time |

The 2026-08-15 fixes (duplicate collapse, alias remap, unit union) patch each
symptom, but they are compensations for the same missing primitive: **items
have no identity except their current name, and no change is ever described —
only entire states exchanged.**

The proposal: give items a stable identity (Stage 1), make deletion a synced
fact (Stage 2), and exchange changed records instead of the whole database
(Stage 3). Each stage ships independently and is useful on its own.

---

## 2. What exists today (and is kept)

The current system is closer to the target than a rewrite implies:

- **Per-record versions already exist.** `UserSyncVersion { Clock, DeviceId,
  OperationId, ModifiedAt }` on progress, reader prefs, favorites, list
  states; `MergeByKey` resolves newest-wins (`CompareVersions`: clock →
  deviceId → operationId). This is a working last-writer-wins convergent
  merge. It stays.
- **Lists already implement the full target model.** `UserSyncMediaList` has a
  GUID identity, an `IsDeleted` tombstone, and versioned membership states
  (`UserSyncListItemState.IsMember`). Stage 1+2 is essentially *make catalog
  items work the way lists already do*.
- **Delta upload already exists for one collection.**
  `ExportProgressPatchAsync` sends only moved titles (measured: 10 KiB where
  the snapshot is 2.7 MiB). Stage 3 generalizes this mechanism; it does not
  invent a new one.
- **Capability negotiation exists.** `RemoteServerCapabilities` already
  carries `SyncProtocolVersion`; new behavior gates on it exactly the way the
  progress patch does.
- **The server shares the merge code.** The server project references the same
  `UniversalMediaDistribution.Catalog` library, so server-side merging
  (Stage 3) reuses `UserSyncDocumentMerger` rather than reimplementing it.

Document versions: `CurrentVersion = 3` today, with an existing v1→v2 upgrade
path (`UpgradeVersionOne`) as precedent. This design is **format v4**.

---

## 3. Goals and non-goals

Goals

1. A rename, merge, or duplicate title can never orphan user state again —
   references survive because they point at identity, not spelling.
2. Deleting a title is a fact the account remembers on every device.
3. A routine sync moves kilobytes, not the database, and never blocks the UI.
4. No flag-day migration: existing databases upgrade in place, and the
   current server keeps working until its own upgrade.

Non-goals

- Real-time / push sync. The pull-on-demand model stays.
- Multi-user collaboration semantics. One account, few devices, LWW is
  sufficient — there is no need for operation ordering or intent preservation.
- Changing local primary keys. `catalog_items.id` and every FK on it stay
  untouched.

---

## 4. Stage 1 — stable identity (format v4)

### 4.1 The ID

Every catalog item gets a **sync ID**: a GUID that is minted once, at the
moment the item first exists anywhere, and never changes afterward — renames
do not touch it.

Minting is **deterministic, client-side, offline**:

```
syncId = StableGuid(SyncIdNamespace, $"{mediaType}:{NameNormalizer.Normalize(name)}")
```

using the SHA-256 namespace+value construction that already exists as
`CreateStableGuid` in `ContentSourceCatalogService` (lifted into a shared
`CatalogStableId` helper in the Catalog project, with a new fixed namespace
GUID for catalog identity).

Why deterministic instead of random or server-assigned:

- **Offline-first.** The server is optional; identity cannot depend on a
  round trip.
- **Independent creation converges.** Two devices that each add
  "Camp Rock 3" derive the *same* GUID with no coordination. The whole class
  of "same title, two identities" collapses for exact-name matches.
- **Migration needs no coordination.** Every existing device back-fills the
  same IDs for the same rows (see 4.5). No swap step, no reference rewrite —
  the fragile "provisional ID → real ID" dance is avoided entirely.

The ID is a *birth certificate*, not a current-name hash: once minted and
stored, the name may drift freely. Local uniqueness of the mint input is
already guaranteed by the existing `UNIQUE (media_type, name_key)` constraint.

### 4.2 Local schema

```sql
ALTER TABLE catalog_items ADD COLUMN sync_id TEXT NULL;
CREATE UNIQUE INDEX idx_catalog_items_sync_id ON catalog_items(sync_id)
    WHERE sync_id IS NOT NULL;
```

The local PK (`catalog_items.id`) is untouched, so `content_units`,
`playback_progress`, `library_entries`, `user_media_list_items`,
`download_jobs` keep their FKs — the migration is one UPDATE per row, no
cascades, no risk to fielded databases.

### 4.3 Transfer format v4

```jsonc
// CatalogTransferItem gains:
"syncId": "guid",                // the sync ID ("id" would collide with the
                                 // guarantee that exports carry no database ids)
"syncVersion": { ... },          // per-item version — arrives with Stage 2

// UserSyncCatalogItemIdentity gains:
"itemId": "guid",                // optional; name stays for legacy + display

// UserSyncDocument gains a top-level collection:
"catalogRedirects": [
  { "fromId": "guid", "toId": "guid", "syncVersion": { ... } }
]
```

`Version` bumps to 4. v3 documents remain readable (`MinimumSupportedVersion`
stays 1); an ID-less item or reference falls back to today's name key.

**What a redirect is — and is not.** It is *not* an alternative name.
Alternative names (`AlternativeNames` / `CatalogTransferAlias`) are display
metadata — other titles the same work is known by — and they are unchanged by
this design. A redirect is a forwarding note between *IDs*, and it exists for
exactly one situation: two catalog entries that were created as separate items
turn out to be the same title. The real example from this account: the daily
fetch created "Camp Rock 3" and "Camp Rock 3 Película" as two entries. Under
v4 each mints its own ID (different names → different IDs). When convergence
folds them into one item, the losing ID must not simply vanish — another
device may hold a reading position recorded under it. So a note travels in
the sync document: "the old ID now means the surviving ID", like mail
forwarding after a move. Any device that meets the old ID follows the note and
repoints its records. Without it, that position would dangle — the ID-level
version of the "target is not in the catalog" failure fixed on 2026-08-15.
Exact-name duplicates never need one (they mint the same ID by construction);
redirects appear only when *fuzzy* matching merges differently-named entries.

### 4.4 Resolution and merge rules

**Reference resolution** (progress, reader prefs, favorite items, list
items), in order:

1. `itemId` present → look up by sync ID.
2. ID misses → follow `catalogRedirects` (with a chain/cycle guard) and retry.
3. Still nothing, or no ID (legacy) → today's name-key resolution.

**Item merge** (`CatalogTransferDocumentMerger`): keyed by sync ID instead of
name key. Same ID → `CombineConverged` union (the 2026-08-15 fix, unchanged).
Different IDs that fuzzy-convergence (`FindBestMatch` / `ConvergeIdentity`)
declares the same title → combine **and emit a redirect** `loserId →
winnerId`, winner chosen by GUID order — a rule with no sides, so two devices folding
the same pair in opposite directions still agree on the survivor. Until items
carry their own `syncVersion` (Stage 2), the surviving *name* for a same-ID
pair is picked by the content-hash order: deterministic everywhere, but not
yet "newest edit wins" — that arrives with item versions.

**Redirects** merge by `fromId`, newest-wins — they are ordinary versioned
records. Once a redirect exists, every device repoints its references on next
restore. This *replaces* the name-guessing `RemapOntoCatalog` added on
2026-08-15 (which stays only as the fallback for v3 documents).

**Adoption rule**: an incoming v4 item that matches a local ID-less row by
name key stamps that row with the incoming ID (never overwrites an existing
different one — that case is a redirect, not an adoption).

Validation: `CreateCatalogIdentitySets` builds content keys from IDs when
present; the "target is not in the catalog" check follows redirects before
refusing. With references following identity, the rename/merge cases that
used to strand whole accounts become structurally unreachable.

### 4.5 Migration

On first open after upgrade, per profile, one transaction:

```
for each catalog_items row where sync_id is null:
    sync_id = StableGuid(mediaType, name_key)
```

Because the derivation is deterministic, every device independently assigns
the *same* IDs to the same titles — devices converge on first sync with no
handshake. Titles held under different spellings on different devices mint
different IDs and converge through the fuzzy-match → redirect path, exactly
as new items do.

### 4.6 Compatibility

- **The server ships in the same release train.** Implementation found the
  server re-normalizes every upload through its compiled copy of the merge
  code, so an old server refuses a v4 document outright (a loud, clean
  failure — never silent stripping). No new endpoints, but the server binary
  must be rebuilt and redeployed with the v4 clients. Clients gate on the
  advertised `SyncProtocolVersion`, so against an old server they report
  "server incompatible" rather than corrupting anything.
- **Mixed fleet caveat.** A v3 client deserializes v4 JSON fine (unknown
  fields ignored) but would *re-upload without* the v4 fields, stripping IDs
  from the account snapshot. Policy: upgrade all heads in one release train
  (practical for this deployment — one publish pipeline). When the server
  next ships, add a guard: reject a full-snapshot upload whose `version` is
  lower than the account's stored document, with an error naming the device
  that needs updating. The complete fix — server-side merge that preserves
  v4 fields regardless — arrives with Stage 3.

---

## 5. Stage 2 — deletion as a synced fact

`CatalogTransferItem` gains `deleted: bool` (default false), versioned by the
item's `syncVersion` — the exact pattern `UserSyncMediaList.IsDeleted`
already uses.

- **Merge**: LWW between an edit and a delete by `syncVersion`; a newer edit
  resurrects, a newer delete removes. Ties resolve through the existing
  `CompareVersions` chain, so devices agree.
- **Import**: a winning tombstone removes the local row and its dependent
  user state — reading history, list memberships, favorite flag. **Decided
  2026-08-15**: deletion cascades; it means "remove this title from my
  account", not "hide it here".
- **Provider interplay** — the part that makes deletion *stick*: the daily
  fetch derives the sync ID for a candidate before creating it; a live
  tombstone under that ID suppresses automatic re-creation. A manual re-add
  by the user clears the tombstone with a new version. Today a deletion is
  silently undone by the next daily fetch; this rule is what fixes that.
- **Hygiene**: tombstones older than 180 days (by `syncVersion.ModifiedAt`)
  are dropped at export. A device offline longer than that re-imports the
  title at worst — annoying, not corrupting.

Note: the UI has no catalog-item deletion today. Stage 2 ships the sync
plumbing (format fields + merge rules), and the delete action lands in the UI
together with it or later — the tombstone rules above are what make that
future action safe to offer.

---

## 6. Stage 3 — delta sync

### 6.1 Protocol

Gated on a new boolean `DeltaSync` capability, following the exact
precedent of `ProgressPatch` (the client checks `capabilities.ProgressPatch`
today, not a protocol number). `SyncProtocolVersion` stays what it is: a hard
compatibility floor. Full-snapshot endpoints remain.

```
GET  /api/v1/sync/changes?since=<revision>
     → { fromRevision, toRevision,
         items[], catalogRedirects[], progress[], readerPreferences[],
         favoriteStates[], favoriteItemStates[], lists[] }   // changed only

POST /api/v1/sync/changes     body: same shape + clientRevision
     → { revision, changedSinceClient: { ...same shape... } }
```

The server applies an uploaded delta with the **shared merge code**
(`UserSyncDocumentMerger` — the Server project already references it), bumps
the revision, and returns anything the client hasn't seen. Conflict
resolution moves server-side; the client's pull-merge-push CAS retry loop
(`RemoteUserSyncReconciler`, up to 5 attempts) collapses to one round trip in
the common case.

Server storage: the canonical snapshot it already keeps, plus one index —
`record_versions (record_key → last_revision)` — maintained as writes are
applied. `changes?since=r` selects records with `last_revision > r`. No
event history is stored; there is nothing to replay or compact.

### 6.2 Client

- **Dirty tracking**: a small `sync_dirty (record_key)` table written by the
  stores on mutation — the generalization of the `_movedCatalogItems` set the
  progress patch already uses. Upload = dirty records only; clear on ack
  (only the keys that were in flight, same rule the patch path uses today).
- **Full snapshot remains** for: first sync of a device, `since` older than
  the server's index horizon, and a periodic integrity check (document hash
  mismatch → fall back to snapshot reconcile). Backup/restore semantics are
  unchanged — the snapshot *is* the backup.

### 6.3 What it buys

- Routine sync: a few KiB instead of 2.7 MiB each way, no full-document
  deserialize — which on the 32-bit TV box is also a memory-pressure fix, not
  just bandwidth.
- The multi-second merge of 2,808 + 3,400 items happens only on bootstrap or
  repair, not on every position save fallback.

---

## 7. Rollout

| Step | Ships | Requires |
| --- | --- | --- |
| 0 | ~~the 2026-08-15 fixes~~ **done** — published by the owner as 0.9.20, verified working on real devices | — |
| 1 | Format v4 clients: IDs, redirects, migration — all heads in one release | no server change |
| 2 | Item tombstones | rides any release after v4 |
| 3 | Server: upload guard, delta endpoints, server-side merge; clients enable via capabilities | server deploy |

---

## 8. Testing

- **Unit**: mint determinism (same input → same GUID, cross-platform);
  redirect chains and cycle guards; adoption rules; edit-vs-delete LWW;
  v3⇄v4 document merges; tombstone-suppressed daily fetch.
- **Real-data acceptance**: the probe pattern from 2026-08-15 —
  `UMD_PROBE_SNAPSHOT` / `UMD_PROBE_DATABASE` replaying the live account
  snapshot against a copy of the real database through the real store —
  rerun before each stage ships. It caught what fixtures could not, three
  times in one day.
- **Two-device simulation**: two `SqliteUserSyncStore`s syncing through an
  in-memory server; interleave adds/renames/deletes/positions on both sides;
  assert both exports converge to identical documents. This is the test the
  current design never had.

---

## 9. Rejected alternatives

- **Server-assigned canonical IDs with a provisional→real swap.** Requires
  the server to mint identity (breaks offline creation) and a reference
  rewrite on every device after assignment — the same fragile swap that
  caused the rename-orphaning bug, made systemic. Deterministic derivation
  achieves the "same ID everywhere" goal with zero round trips.
- **Full event-sourcing / operation log.** Every synced mutation here is
  "set state" (position = X, favorite = true, member = false, item edited);
  per-record LWW already converges without ordering, replay, idempotency, or
  compaction machinery. The one genuine *operation* — "these two IDs are one
  title" — is carried as a small redirect record instead of an op stream.
- **Rewriting local primary keys to the sync ID.** Clean end state, but a
  cascading FK migration across every table on fielded databases with no git
  safety net. The `sync_id` column gets the same result with one UPDATE per
  row.

---

## 10. Decisions (owner review, 2026-08-15)

1. **Delete cascades.** When catalog-item deletion exists, it removes the
   reading history with the title. (No delete UI exists yet — see Stage 2
   note.)
2. **"Alias" renamed to "identity redirect".** The original term collided
   with `CatalogTransferAlias` / alternative names, which are an unrelated,
   existing concept. Redirects are ID-forwarding records; see §4.3. Purge
   horizon: keep them indefinitely — they are a few dozen bytes each and
   only fuzzy merges create them.
3. **Fleet is Windows desktop + Android phone + Android TV.** No iOS or
   macOS code for now, which removes the laggard-device risk from the
   fleet-upgrade window in §4.6.
4. **Delta sync welcomed.** Gate it on a boolean `DeltaSync` capability
   (the `ProgressPatch` precedent) rather than bumping
   `SyncProtocolVersion`, which stays a hard compatibility floor.
