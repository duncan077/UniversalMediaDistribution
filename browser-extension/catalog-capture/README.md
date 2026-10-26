# Universal Media Catalog Capture

This is a standalone Manifest V3 browser extension. It is deliberately kept
outside the .NET solution and has no runtime dependencies.

## What it captures

- Title from a selected HTML element.
- Category from a selected HTML element.
- Chapter number from a selected HTML element.
- Description from a selected HTML element.
- Every `<img>` inside one or more selected lists or containers, in capture and
  DOM order.

The extension opens as a persistent browser side panel. Its draft is stored in
extension-local storage, so values and image lists remain available when the
page or tab changes and after the browser restarts. Selecting another image list
appends new, non-duplicate image URLs to the draft.

When downloaded, the capture contains `metadata.json`, a UMD-compatible
`info.json`, and the selected images. The generated manifest describes a
`book` / `manga` item with one chapter and an `image_sequence` source.
All JSON and image downloads are queued concurrently by the single
**Download capture** action.
Image names use the first two letters or numbers from the captured title,
followed by a zero-based position. Zero-based numbering matches the image
sequence contract used by the .NET application:

```text
Blue Period -> Bl_0.jpg, Bl_1.jpg, Bl_2.png
```

The files are placed below:

```text
Downloads/
  UniversalMediaCatalog/
    Blue Period/
      2026-07-23T12-34-56-123Z/
        metadata.json
        info.json
        chapters/
          12/
            Bl_0.jpg
            Bl_1.jpg
```

The `info.json` source points to `chapters/<chapter number>`, matching the
downloaded folder. It deliberately contains no magnet URI or info hash because
the manifest is itself part of the torrent and therefore cannot know its final
identity. The UMD torrent-local importer binds the unbound source to the active
magnet after reading `info.json`.

The browser may add a numeric suffix if a file with the same name already
exists. Image URLs without a recognizable extension default to `.jpg`.

## Install in Chrome or Edge

Chrome 114 or a current Microsoft Edge release with the Side Panel API is
required.

1. Open `chrome://extensions` in Chrome or `edge://extensions` in Edge.
2. Enable **Developer mode**.
3. Choose **Load unpacked**.
4. Select this `browser-extension/catalog-capture` folder.
5. If this is an update, choose **Reload** on the extension card.

The extension requests access to HTTP and HTTPS pages so the picker continues
working after navigation. It does not modify a page until a picker button is
used.

## Use

1. Open the catalog page you want to capture.
2. Click the extension icon to open its side panel.
3. Choose **Pick element** beside Title, Category, Chapter number, or
   Description.
4. Move over the page and click the highlighted element.
5. For images, choose **Add image list from page**, then click the parent
   element containing the ordered image list. Navigate and repeat to append
   images from more pages.
6. Optionally mark the future torrent as private.
7. Review or edit the persisted values, then choose **Download capture**.

The panel remains open while selecting. Press `Esc` while picking to cancel.
Use **Clear images** to keep the metadata but reset accumulated images, or
**Clear** to remove the entire persistent draft. Browser-internal pages such as
`chrome://extensions` cannot be captured.

The image picker recognizes normal `src`, responsive `srcset`, and common lazy
loading attributes such as `data-src`, `data-original`, and `data-lazy-src`. If
an image links directly to a larger image file, the linked file is preferred.

## Verify

With Node.js installed:

```powershell
npm --prefix browser-extension\catalog-capture test
npm --prefix browser-extension\catalog-capture run check
```
