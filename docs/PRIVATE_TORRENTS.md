# Optional private torrents and tracker authentication

Public torrents are the default. A catalog source becomes private only when its
`torrent.isPrivate` value is `true`, a magnet declares `x.umd.private=1`, or the
user enables **Force unmarked magnets into private mode**. No private tracker or
credential is required for ordinary public releases.

Native public torrents may use DHT without a tracker. Browser torrents still
need a WSS tracker because browser WebTorrent peers use WebRTC and cannot use
the native BitTorrent DHT.

A trackerless public magnet is therefore valid and opens normally in a native
build. In the browser build the app reports it as valid but incompatible,
explains that WebTorrent needs a `tr=wss://...` endpoint, and suggests opening
the link in a native build. It is not reported as malformed.

## Private release routes

A private cross-platform release should publish two non-secret routes for the
same torrent identity:

```json
{
  "infoHashV1": "0123456789ABCDEF0123456789ABCDEF01234567",
  "isPrivate": true,
  "trackers": [
    {
      "name": "Native private tracker",
      "endpoint": "https://tracker.example/announce",
      "transport": "https",
      "clientFamily": "native",
      "requiresAuthentication": true
    },
    {
      "name": "Browser private tracker",
      "endpoint": "wss://tracker.example/announce",
      "transport": "wss",
      "clientFamily": "browser",
      "requiresAuthentication": true
    }
  ]
}
```

MonoTorrent selects only the native HTTPS route. WebTorrent selects only the
browser WSS route. Both descriptors retain the same v1/v2 info hash, so a
WebTorrent-capable hybrid seeder can join both transports while serving
identical torrent content. Running that seeder and the tracker services remains
deployment work outside this client.

Set `requiresAuthentication` to `false` for a private tracker that does not
require a passkey or token. UDP is never accepted for a private source.

## Native credential overrides

The desktop host resolves authenticated tracker URLs from environment variables.
The value is the complete per-user HTTPS or WSS endpoint, including a passkey
path or short-lived query token. It must retain the catalog endpoint's scheme,
host, and port.

For routes without a local credential key, the variable is derived from the
transport and host:

```powershell
$env:UMD_TRACKER_ENDPOINT_HTTPS_TRACKER_EXAMPLE = 'https://tracker.example/member/passkey/announce'
$env:UMD_TRACKER_ENDPOINT_WSS_TRACKER_EXAMPLE = 'wss://tracker.example/announce?token=short-lived-token'
```

If a platform-specific catalog installation assigns a local credential key such
as `indie-community`, its variable is
`UMD_TRACKER_ENDPOINT_INDIE_COMMUNITY`. Credential keys and endpoint overrides
are local configuration: catalog JSON does not import or export them.

Authenticated browser intake can use a per-user private magnet containing its
WSS tracker token for that session, or a browser host can supply an
`ITrackerCredentialProvider`. A shared permanent token must never be compiled
into the WASM bundle or distributable catalog.

## Enforced privacy behavior

When private mode is active:

- MonoTorrent disables DHT and peer exchange for the torrent; local peer
  discovery is disabled for the native engine.
- WebTorrent runs with DHT and local discovery disabled, sets the torrent
  private flag so WebTorrent suppresses peer exchange, and accepts only WSS
  trackers.
- Catalog playback ignores tracker URLs embedded in a private magnet and uses
  only explicit catalog routes.
- Private magnet intake rejects web seeds, exact sources, direct peers, and
  trackers outside the approved runtime list before an adapter starts.
- Private `.torrent` metadata is stripped of discovery endpoints in the browser.
  Native metadata is accepted only when its trackers exactly match the approved
  HTTPS runtime routes.
- Missing required credentials fail closed. Public torrents and unauthenticated
  private trackers continue to work without a credential provider.

Tracker objects and credential resolutions redact user information, paths, and
queries when converted to diagnostic text. Catalog validation also rejects
userinfo and common credential query parameters in distributable endpoint
definitions.

## What encryption means

HTTPS/WSS protect tracker traffic, WebRTC uses DTLS in the browser, and
MonoTorrent can use message-stream encryption. Set
`UMD_MSE_POLICY=required` if native peers must reject plaintext peer-wire
connections.

These are transport protections. They do not encrypt downloaded media from an
authorized peer. Content confidentiality would require separately encrypted
media and key distribution.
