# Server deployment and desktop updates

The optional server is local-first friendly: clients still browse their local
catalog, read, play, track progress, and manage downloads when it is absent.
It provides health/capability discovery, a public online catalog, private
account/device snapshots, revocable shared-list catalogs, and signed desktop
release distribution. None of these features is required for local use.

## Build and publish the production release

`eng/Publish-Production.ps1` is the repeatable production entry point. It runs
the tests and formatting check, builds Windows and Linux desktop archives,
builds and verifies architecture/profile-specific Android APKs, creates the
minimal server source archive, compiles the NativeAOT ARM64 server on the Linux
host, performs an isolated health check, switches the service atomically with
rollback, publishes the signed update manifests, and verifies the public server
and manifests.

On the first signed release, initialize and securely back up the release key:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
  -File .\eng\Publish-Production.ps1 `
  -Version 0.7.1 `
  -ReleaseNotes 'Android and update fixes.' `
  -InitializeSigningKey `
  -Publish
```

The script securely prompts for the SSH password unless PuTTY/Pageant is used
with `-UseSshAgent`. For unattended execution, set
`UMD_DEPLOY_SSH_PASSWORD` only in the publishing process. Packages and signed
manifests are staged over SSH and submitted to the service through its loopback
listener. At the end of a successful `-Publish` run, the script reads the
existing administrative API token from the server's service environment and
displays the `/admin` URL and password in the console. It does not write the
password to release artifacts. `Publish-ServerRelease.ps1` also displays the
URL and its configured admin token after a successful upload; `-PrepareOnly`
does not display credentials.

For later releases, use a new version and omit `-InitializeSigningKey`:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
  -File .\eng\Publish-Production.ps1 `
  -Version 0.7.2 `
  -ReleaseNotes 'Release notes shown by the updater.' `
  -Publish
```

Artifacts and checksums are written beneath `artifacts/deploy/<version>`.
Release versions are immutable; use a new version for every public package.
Pass `-OverwriteArtifacts` only to rebuild an interrupted local artifact
directory before anything was published.

If compilation completed but a later deployment or upload step failed, resume
from the checked artifacts without rebuilding the clients:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
  -File .\eng\Publish-Production.ps1 `
  -Version 0.7.2 `
  -ReleaseNotes 'Release notes shown by the updater.' `
  -ReuseClientArtifacts `
  -Publish
```

Resume mode verifies every saved SHA-256 checksum and revalidates every Android
signature, version, device profile, and exact CPU architecture set before
deploying or publishing.
It cannot be combined with `-OverwriteArtifacts`.

For a production Android certificate, set `UMD_ANDROID_KEYSTORE_PATH`,
`UMD_ANDROID_KEY_ALIAS`, `UMD_ANDROID_KEYSTORE_PASSWORD`, and
`UMD_ANDROID_KEY_PASSWORD` in the publishing process. If no keystore is set,
the Android toolchain uses its development certificate, which can update only
installations signed with that same certificate.

## Publish the NativeAOT server

```powershell
dotnet publish src\UniversalMediaDistribution.Server `
  -c Release `
  -r win-x64 `
  -o artifacts\server-aot\win-x64
```

## Configure the service

Configure the process with environment variables or equivalent secret/service
configuration:

| Variable | Purpose |
| --- | --- |
| `ASPNETCORE_URLS` | Public listen URL, normally behind an HTTPS reverse proxy |
| `UMD_SERVER_DATA_DIRECTORY` | Durable server data root |
| `UMD_SERVER_ADMIN_TOKEN` | Administrative bearer token for account provisioning, catalog/release publishing, and the `/admin` diagnostics page; at least 32 characters |
| `UMD_SERVER_MAX_PACKAGE_BYTES` | Optional maximum installer size; defaults to 2 GiB |
| `UMD_SERVER_MAX_JSON_BYTES` | Optional catalog/sync JSON limit; defaults to 40 MiB and must be 1 KiB to 256 MiB |
| `UMD_SERVER_PASSWORD_ITERATIONS` | Optional PBKDF2-HMAC-SHA256 work factor; defaults to 310,000 and must be 100,000 to 2,000,000 |
| `UMD_SERVER_SESSION_DAYS` | Optional user-session lifetime; defaults to 30 and must be 1 to 365 |
| `UMD_SERVER_ALLOW_REGISTRATION` | Enables self-service account creation when `true`; defaults to `false` |

Without `UMD_SERVER_ADMIN_TOKEN`, public health, capabilities, manifests, and
packages remain readable, and existing accounts can still sign in and sync.
Administrative account, catalog, and release writes return `503`.

Put the public service behind HTTPS. Limit upload routes to trusted operators
and store the admin token in the host's secret manager, not in source control or
command history. The single-server deployment now stores durable state in
SQLite on a local disk; its HTTP process and hourly crawler coordinate writes
through transactions. Do not share this database over a network filesystem.
See [server database, crawler, and recovery](SERVER_CATALOG_CRAWLER.md) for the
one-time JSON migration, hourly schedule, verified online backups, and the
important rollback restriction when upgrading from the JSON-writing server.

The server stores password salts and PBKDF2 hashes, never plaintext passwords.
Only SHA-256 hashes of random session tokens are written to disk. Account
responses are marked `no-store`, login failures are throttled per account and
source address, and credential requests are capped at 32 KiB. If a reverse
proxy is used, apply an additional login rate limit there because the
application intentionally does not trust forwarded-address headers by default.

## Create accounts

Self-service registration is off by default. Provision an account without
creating a user session by setting the admin token in the publishing process
and using the secure password prompt:

```powershell
$env:UMD_SERVER_ADMIN_TOKEN = '<server-secret>'
pwsh .\eng\New-ServerAccount.ps1 `
  -ServerUrl https://media.example.com `
  -Username alice.reader `
  -DisplayName 'Alice'
```

Alternatively set `UMD_SERVER_ALLOW_REGISTRATION=true`; the app then shows a
working **Create account** action.

Sign-in survives a restart. The app keeps the session token in its local
database and, on the next start, confirms it against `/api/v1/accounts/me`
before showing the account as signed in; a token the server no longer honours is
discarded and the sign-in form is shown instead. Closing the app does not revoke
the session, so `UMD_SERVER_SESSION_DAYS` (30 by default) is what decides how
long a device stays signed in. Only **Sign out**, changing the server address, or
the server rejecting the token ends it, and each of those deletes the stored
token. A failure to reach the server is treated as temporary and keeps the token,
so restarting while offline does not sign the user out.

The stored token is a bearer credential protected only by the local database
file, so it is left out of catalog exports and sync snapshots, and restoring a
full-database backup replaces the restored row with the session actually running
on that device rather than adopting the backup's.

## Publish the optional online catalog

Export an ID-free UMD catalog JSON file from the app, then publish it:

```powershell
$env:UMD_SERVER_ADMIN_TOKEN = '<server-secret>'
pwsh .\eng\Publish-OnlineCatalog.ps1 `
  -ServerUrl https://media.example.com `
  -CatalogPath .\catalog.json
```

The server can also populate this catalog automatically from the existing API
providers; see [hourly catalog crawling](SERVER_CATALOG_CRAWLER.md#hourly-operation).
The crawler saves metadata and episode/chapter lists, not artwork or provider
chapter/media URLs.

Publishing validates the document, writes a new atomic revision, and returns
its item count and SHA-256. Public search is case/accent-insensitive and uses
contained text across names, descriptions, genres, categories, and
contributors. In the app, open **Settings > Online account & sync**, enter the
same server address, enable **Use the server's online catalog**, and save.
Server results then appear beside local and other provider results; local
browsing continues to work offline.

## Device backup and synchronization

Sign in from **Settings > Online account & sync**. Sign-in performs an initial
sync, and **Sync now** can be used at any time. A synchronization:

1. downloads the latest account snapshot;
2. merges it with the complete local state;
3. uploads the merged document against the revision that was just read; and
4. automatically repeats the pull/merge/write cycle if another device wins the
   revision race.

Progress and reader preferences use deterministic hybrid logical versions.
Favorite changes, list deletion, and list-membership changes also retain
deletion tombstones, so a device that was offline cannot resurrect an older
favorite, list, or membership when it reconnects. Concurrent lists with the
same name are preserved and receive deterministic conflict suffixes. Catalog
titles are unioned; conflicting representations of the same natural title use
a deterministic canonical winner so every device reaches the same result.

Snapshots contain portable catalog data, progress, reader preferences,
favorite-provider keys, and personal lists. They exclude application/provider
settings, API keys, tracker credentials, download records, paths, and
downloaded files. Revision conflicts return HTTP `409`; compatible clients
merge the winning revision and retry instead of overwriting it.

## Share a list

After signing in, select a personal list and choose **Publish list**. The app
uploads the current snapshot and receives an unguessable public URL containing
only that list's catalog titles. The URL is read-only and works as a normal UMD
catalog source. Each later **Upload this device** refreshes already-published
lists; deleting a source list and uploading removes its public link.
**Stop sharing** deletes the public record immediately without deleting the
local list or private snapshot.

## HTTP surface

| Access | Endpoints |
| --- | --- |
| Public | `/health`, `/api/v1/capabilities`, online catalog reads/search, release reads/packages, and an individual shared-list catalog |
| Registration setting | `POST /api/v1/accounts/register` |
| User bearer token | account profile/logout, sync snapshot reads/writes/deletion, and shared-list publish/list/revoke |
| Admin bearer token | `POST /api/v1/admin/accounts`, online catalog publication, release package upload, and release-manifest publication |

## Create a release signing key

The tooling requires PowerShell 7 because it uses modern .NET cryptography:

```powershell
pwsh .\eng\New-ReleaseSigningKey.ps1 -KeyId umd-release-1
```

This creates:

- a PEM PKCS#8 ECDSA P-256 private key used only by the publisher; and
- a Base64 SubjectPublicKeyInfo public key pinned by desktop clients.

Back up the private key securely and keep it out of the repository. Losing it
prevents publishing updates trusted by existing clients. Exposing it requires a
client release that pins a new key.

## Publish a desktop release archive

The desktop updater takes the release archive produced by
`eng\Publish-Release.ps1`: `.zip`, `.tar.gz`, or `.tgz`. It replaces the files
of the running install and restarts the app, so no separate installer is
required. The archive must contain `UniversalMediaDistribution.Desktop`
(`.exe` on Windows), either at its root or inside a single wrapper folder.

Set the admin token only in the publishing process, then upload the archive
and atomically make its signed manifest current:

```powershell
$env:UMD_SERVER_ADMIN_TOKEN = '<server-secret>'
pwsh .\eng\Publish-ServerRelease.ps1 `
  -ServerUrl https://updates.example.com `
  -InstallerPath artifacts\release\0.8.0\umd-0.8.0-win-x64.zip `
  -Version 0.8.0 `
  -SigningKeyId umd-release-1 `
  -PrivateKeyPath artifacts\release-keys\umd-release-1.private.pkcs8.pem `
  -ReleaseNotes 'Downloads management and optional secure updates.'
```

The publisher streams the package, supplies its SHA-256, signs that digest as
fixed-width ECDSA P-256/SHA-256, and publishes the manifest only after the
package upload succeeds.

## Configure desktop clients

Like Android, the desktop reads the release endpoint from the optional server
the user connected in **Settings > Online account & sync**, so the update server
does not have to be configured separately. The official release scripts embed
the signing key ID and **public** verification key in the desktop executable.
The private signing key is never added to the application. This is required
because the updater replaces the running executables, while Android can rely on
the system installer enforcing the APK certificate.

The following values remain available as runtime overrides for development and
self-hosted builds:

| Variable | Purpose |
| --- | --- |
| `UMD_UPDATE_SIGNING_KEY_ID` | Override the embedded key ID |
| `UMD_UPDATE_SIGNING_PUBLIC_KEY` | Override the embedded Base64 public key |
| `UMD_UPDATE_SERVICE_URL` | Optional override for deployments that publish releases somewhere other than their sync server |
| `UMD_UPDATE_CHANNEL` | Optional channel; defaults to `stable` |

Development builds made without embedded or environment-provided signing
values hide the update controls and make no update request. Packaged builds
show **Settings > Desktop updates > Check for updates**, check at startup, and
show an available-update notice that disappears on its own after 30 seconds.
Discovery never downloads, replaces, or installs anything. The user must press
**Update and restart**.

Before anything is staged, the desktop verifies:

1. HTTPS origin (loopback HTTP is accepted only for local development);
2. configured channel, runtime identifier, and signing key ID;
3. declared and actual byte length;
4. declared and actual SHA-256;
5. ECDSA P-256/SHA-256 signature over the package digest;
6. a supported archive extension; and
7. that the extracted payload contains the application executable.

Archives are downloaded and extracted beneath the app's private data directory
under `updates/<version>`, leaving the running install untouched until every
check passes. The updater then writes `apply-update.cmd` (or `apply-update.sh`),
which waits for the app to exit, copies the install directory to
`updates/<version>/backup`, replaces the installed files, restores the backup if
the copy fails, and starts the new version. Progress is logged to
`updates/<version>/apply-update.log`.

If the install directory is not writable - a `Program Files` install, for
example - Windows asks for administrator approval before the helper runs, and
the app is relaunched through Explorer so it does not keep running elevated. On
Linux and macOS a read-only install directory is reported as an error instead.

## Configure Android clients

Android builds distributed outside the Play Store read the release endpoint from
the UMD server the user has configured in Settings, so no build-time server
configuration is needed. The updater selects a release target from the device
profile and process architecture:

| Release target | APK | Included ABIs |
| --- | --- | --- |
| `android-arm` | ARM phone/tablet | `arm64-v8a` |
| `android-tv-arm` | ARM Android TV | `armeabi-v7a` |

Each package carries a single ABI because NativeAOT compiles exactly one
runtime identifier. `android-x86-x64` is no longer published: NativeAOT has no
`android-x86` runtime pack, and the remaining `x86_64` audience is emulators
and a few Chromebooks.

For example, publish the ARM touch APK with:

```powershell
pwsh .\eng\Publish-ServerRelease.ps1 `
  -ServerUrl https://updates.example.com `
  -InstallerPath artifacts\android\umd-android-0.8.0-arm.apk `
  -Version 0.8.0 `
  -RuntimeIdentifier android-arm `
  -SigningKeyId umd-release-1 `
  -PrivateKeyPath artifacts\release-keys\umd-release-1.private.pkcs8.pem
```

The production script publishes both targets. The universal `android` bridge is
no longer built: it existed so that pre-0.8 installs, which only know that
identifier, could learn the per-device target, and it names four ABIs so it
cannot be AOT compiled. Pass `-IncludeLegacyAndroidBridge` to rebuild it for
another rollout.

Not building the bridge leaves whatever was last published on the server in
place, which keeps the fallback below working. To remove it from the server as
well, pass `-DeleteLegacyAndroidBridge`; the production script requires every
split Android target, verifies them publicly, and only then removes the legacy
`stable/android` manifest and package directory. Do that only once no client
still depends on it — after removal, any device whose exact target is missing
has no update path at all.

The television shell has no settings area, so it has no manual "check for
updates" control, but it does the same startup check and shows the same prompt,
held open for 60 seconds instead of 30 because a remote takes longer to reach
it. Split builds fall back to the legacy `android` manifest when a self-hosted
server has not published their exact target yet.

The app verifies the HTTPS origin, the declared byte length, and the SHA-256
digest, then hands the APK to the system installer through a `FileProvider`
URI. Android asks the user to confirm the install and refuses any package that
is not signed with the installed app's certificate, so the APK must be signed
with the same key as the installed build. When the user has not yet allowed this
app to install packages, the app opens that settings screen instead and reports
what is needed; Android TV builds that lack the per-app screen fall back to the
device security settings.
