# Torrent-local `info.json` publisher contract

A torrent may contain an `info.json` file that describes the movie, series, book, manga, or audio included in that same torrent. When a user opens its magnet, Universal Media Distribution can import this metadata into the local catalog without a catalog server.

The manifest uses the normal ID-free catalog JSON format. It never contains database IDs. A new `(mediaType, normalized name)` creates local IDs; the same name and media type updates the existing item while preserving its IDs and user state.

Movie and Book items may include multiple `categories`. Titles may also include multiple localized `alternativeNames`, each with a `name`, a BCP-47 `language` (or `und`), and an `aliasType` such as `translation`, `romanized`, `short`, or `other`. The older `aliases` input name remains accepted for compatibility, but exports always use `alternativeNames`.

Contributors may include an optional `url`. It must be an absolute HTTP or
HTTPS address without embedded credentials. Each `torrent` object also has a
BCP-47 `language`; use `und` when the package language is unknown and `mul` for
a genuinely multilingual package.

## Torrent layout example

```text
Open Skies/
  info.json
  art/
    cover.webp
    chapter-1.webp
  chapters/
    1/
      page_0.webp
      page_1.webp
      page_2.webp
```

The matching JSON is in [examples/torrent-info.json](examples/torrent-info.json). Replace its example info hash with the hash of the published torrent.

## Required rules

- Include zero or one file whose basename is `info.json`, ignoring letter case. More than one is ambiguous and rejected.
- Keep `info.json` at or below 4 MiB.
- Set `format` to `universal-media-distribution.catalog` and `version` to `1`.
- Do not include database IDs or user data.
- Every playable/readable `sources[]` entry must include a `torrent` object, but a torrent-local `info.json` should omit `infoHashV1`, `infoHashV2`, `magnetUri`, and `torrentMetadataBase64`. The importer binds the source to the active torrent after reading the manifest.
- Set `torrent.language` to the package language. A more specific
  `sources[].language` identifies an individual audio, subtitle, or file
  rendition and takes precedence when that source is displayed.
- Every single-file source must identify one torrent file with `fileIndex`, `filePath`, or both. If both are supplied, they must identify the same file.
- Portable alternate audio is represented by multiple `primary` single-file sources on the same movie, episode, or audio target. Each source is a complete playable rendition, `language` identifies its audio language, and `sortOrder` defines the publisher's preferred order. Renditions should share the same timeline so switching can preserve the current position.
- A Book single-file source may be a JPG/JPEG, PNG, or WebP image and opens as one reader page. Multiple loose images should use `image_sequence`; images embedded in an EPUB must be manifest-declared JPG/JPEG, PNG, or WebP resources.
- An `image_sequence` source must omit `fileIndex`; its `filePath` identifies the containing directory.
- `thumbnailRef` and `backdropRef` values on items, contributors, seasons, and units must be safe relative paths resolving to one file in this torrent.
- Paths cannot be absolute, contain an empty segment, use `.` or `..`, contain a drive/scheme colon, or contain control characters.
- `info.json` cannot itself be used as media or artwork.

For image sequences, each image filename must end with `_<position>` before its extension. Positions start at `_0`, are unique, and are contiguous. The reader sorts the numeric suffix, so `_9` correctly precedes `_10`.

## Torrent binding and trackers

A torrent cannot include its own final info hash or magnet URI inside `info.json`: changing the manifest would change that identity. The torrent-local import path therefore permits an unbound `torrent` object and replaces it with the normalized identity from the magnet that delivered the manifest.

If an `info.json` explicitly supplies `infoHashV1`, `infoHashV2`, or `magnetUri`, every supplied identity must match the active torrent or the manifest is rejected. This preserves a strict failure mode for stale or foreign manifests without requiring the impossible self-reference.

Do not rely on trackers declared inside `info.json`: they are deliberately removed during import. Tracker routing comes from the active magnet or trusted local configuration. Embedded `torrentMetadataBase64` is also removed. This prevents a torrent-local document from silently redirecting later sessions to another swarm or tracker.

Private distribution is optional. Public torrents should leave `isPrivate` false
and do not need a private tracker. For private distribution:

- Native MonoTorrent intake needs an authorized HTTPS tracker.
- Browser WebTorrent intake needs an authorized WSS tracker.
- Mark authenticated catalog routes with `requiresAuthentication`; never place a
  passkey or permanent token in `info.json` or catalog JSON.
- Keep browser and native releases on compatible swarm endpoints, or operate a hybrid WebTorrent-capable seeder when both client families must exchange the same files.

See [PRIVATE_TORRENTS.md](PRIVATE_TORRENTS.md) for local credential resolution
and the complete discovery policy.

## Import behavior

Import is transactional. A malformed document, foreign info hash, missing file, invalid artwork path, broken image sequence, or catalog validation error imports nothing. The torrent itself remains open and the UI displays a warning. Metadata lookup is limited to 30 seconds so an unavailable peer cannot leave the UI waiting indefinitely.

Automatic `info.json` import is enabled by default and can be disabled in the magnet options. Automatically opening received magnets is a separate setting and is disabled by default.

## Portable alternate-audio example

Use complete muxed renditions instead of external audio-only slave files. This works consistently with native MonoTorrent/LibVLC playback and browser WebTorrent playback:

```json
{
  "sources": [
    {
      "assetRole": "primary",
      "sourceLayout": "single_file",
      "filePath": "movie/movie.en.webm",
      "mimeType": "video/webm",
      "container": "WEBM",
      "videoQuality": "1080p",
      "language": "en",
      "sortOrder": 0,
      "torrent": {
        "displayName": "Example Movie",
        "language": "en",
        "isPrivate": true
      }
    },
    {
      "assetRole": "primary",
      "sourceLayout": "single_file",
      "filePath": "movie/movie.es.webm",
      "mimeType": "video/webm",
      "container": "WEBM",
      "videoQuality": "1080p",
      "language": "es",
      "sortOrder": 1,
      "torrent": {
        "displayName": "Example Movie",
        "language": "es",
        "isPrivate": true
      }
    }
  ]
}
```
