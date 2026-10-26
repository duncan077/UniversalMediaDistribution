"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const {
  buildExportDocument,
  buildUmdInfoDocument,
  createEmptyCaptureState,
  createDownloadFolder,
  createImageDirectory,
  createImageFileName,
  getUmdCaptureErrors,
  getImageExtension,
  getTitlePrefix,
  normalizeCaptureState,
  normalizeText,
  sanitizePathSegment
} = require("../src/shared.js");

function validCapture() {
  return {
    fields: {
      title: {
        value: "Blue Period",
        selector: "main > h1"
      },
      category: {
        value: "Manga",
        selector: ".category"
      },
      description: {
        value: "An art story.",
        selector: ".summary"
      },
      chapterNumber: {
        value: "12",
        selector: ".chapter-number"
      }
    },
    images: {
      selections: [
        {
          selector: "#pages",
          tagName: "ol",
          page: {
            url: "https://example.test/read/12",
            title: "Chapter 12"
          },
          capturedCount: 2
        }
      ],
      items: [
        {
          url: "https://example.test/001.png",
          alt: "Page one",
          width: 1200,
          height: 1800,
          sourceSelector: "#pages > img:nth-of-type(1)"
        },
        {
          url: "https://example.test/002.png",
          alt: "Page two",
          width: 1200,
          height: 1800,
          sourceSelector: "#pages > img:nth-of-type(2)"
        }
      ]
    },
    settings: {
      isPrivate: true
    },
    sourcePages: [
      {
        url: "https://example.test/catalog/item",
        title: "Example page"
      },
      {
        url: "https://example.test/read/12",
        title: "Chapter 12"
      }
    ]
  };
}

test("normalizes captured text", () => {
  assert.equal(normalizeText("  A \n  catalog\t title  "), "A catalog title");
});

test("uses the first two letters or numbers from the title", () => {
  assert.equal(getTitlePrefix("My Manga"), "My");
  assert.equal(getTitlePrefix("  #42 Stories"), "42");
  assert.equal(getTitlePrefix("Éclair"), "Ec");
  assert.equal(getTitlePrefix(""), "IM");
});

test("creates zero-based image filenames and preserves supported extensions", () => {
  assert.equal(
    createImageFileName("My Manga", 0, "https://cdn.example/page.jpeg?size=2"),
    "My_0.jpg"
  );
  assert.equal(
    createImageFileName("My Manga", 12, "https://cdn.example/page.webp"),
    "My_12.webp"
  );
  assert.throws(
    () => createImageFileName("My Manga", -1, "https://example.test/image.png"),
    /start at 0/u
  );
});

test("detects data URL MIME types and safely defaults unknown URLs", () => {
  assert.equal(getImageExtension("data:image/png;base64,AAAA"), ".png");
  assert.equal(
    getImageExtension("https://images.example.test/render?id=3"),
    ".jpg"
  );
});

test("sanitizes download path segments", () => {
  assert.equal(sanitizePathSegment('A: "Title" / Test'), "A_ _Title_ _ Test");
  assert.equal(sanitizePathSegment("..."), "capture");
});

test("builds ordered export metadata with matching filenames", () => {
  const capturedAt = "2026-07-23T12:34:56.000Z";
  const result = buildExportDocument(validCapture(), capturedAt);

  assert.equal(result.schemaVersion, 1);
  assert.equal(result.title, "Blue Period");
  assert.equal(result.category, "Manga");
  assert.equal(result.chapterNumber, "12");
  assert.equal(result.imageDirectory, "chapters/12");
  assert.equal(result.sourcePages.length, 2);
  assert.equal(result.images[0].position, 0);
  assert.equal(result.images[0].fileName, "Bl_0.png");
  assert.equal(result.images[1].fileName, "Bl_1.png");
  assert.deepEqual(result.sourceSelectors.imageLists, ["#pages"]);
});

test("creates a stable per-capture download folder", () => {
  assert.equal(
    createDownloadFolder("Blue Period", "2026-07-23T12:34:56.123Z"),
    "UniversalMediaCatalog/Blue Period/2026-07-23T12-34-56-123Z"
  );
});

test("normalizes persistent capture state without dropping chapter data", () => {
  const normalized = normalizeCaptureState(validCapture());

  assert.equal(normalized.schemaVersion, 2);
  assert.equal(normalized.fields.chapterNumber.value, "12");
  assert.equal(normalized.images.items.length, 2);
  assert.equal(normalized.images.selections.length, 1);
  assert.equal(normalized.sourcePages.length, 2);
  assert.equal(normalized.settings.isPrivate, true);

  const empty = createEmptyCaptureState();
  assert.equal(empty.fields.chapterNumber, null);
  assert.deepEqual(empty.images.items, []);
});

test("builds a UMD importer-compatible manga chapter info.json", () => {
  const capturedAt = "2026-07-23T12:34:56.000Z";
  const info = buildUmdInfoDocument(validCapture(), capturedAt);
  const item = info.items[0];
  const unit = item.units[0];
  const source = unit.sources[0];

  assert.equal(info.format, "universal-media-distribution.catalog");
  assert.equal(info.version, 1);
  assert.equal(info.exportedAt, capturedAt);
  assert.equal(item.mediaType, "book");
  assert.equal(item.subtype, "manga");
  assert.equal(item.name, "Blue Period");
  assert.deepEqual(item.categories, ["Manga"]);
  assert.equal(unit.kind, "chapter");
  assert.equal(unit.displayNumber, "12");
  assert.equal(unit.sortOrder, 11);
  assert.equal(unit.pageCount, 2);
  assert.equal(source.sourceLayout, "image_sequence");
  assert.equal(source.filePath, createImageDirectory("12"));
  assert.equal(source.fileIndex, undefined);
  assert.equal(source.torrent.infoHashV1, undefined);
  assert.equal(source.torrent.infoHashV2, undefined);
  assert.equal(source.torrent.magnetUri, undefined);
  assert.equal(source.torrent.isPrivate, true);
});

test("reports every missing input required by UMD info.json", () => {
  const errors = getUmdCaptureErrors(createEmptyCaptureState());

  assert.equal(errors.length, 3);
  assert.match(errors.join(" "), /title/u);
  assert.match(errors.join(" "), /chapter number/u);
  assert.match(errors.join(" "), /at least one image/u);
});

test("rejects image formats unsupported by the UMD reader", () => {
  const capture = validCapture();
  capture.images.items[0].url = "https://example.test/page.gif";

  const errors = getUmdCaptureErrors(capture);

  assert.equal(errors.length, 1);
  assert.match(errors[0], /unsupported by the UMD image reader/u);
});
