(function initializeCaptureUtilities(globalScope) {
  "use strict";

  const DEFAULT_IMAGE_EXTENSION = ".jpg";
  const IMAGE_EXTENSIONS = new Set([
    ".avif",
    ".bmp",
    ".gif",
    ".jpeg",
    ".jpg",
    ".png",
    ".svg",
    ".tif",
    ".tiff",
    ".webp"
  ]);

  const MIME_EXTENSIONS = new Map([
    ["image/avif", ".avif"],
    ["image/bmp", ".bmp"],
    ["image/gif", ".gif"],
    ["image/jpeg", ".jpg"],
    ["image/png", ".png"],
    ["image/svg+xml", ".svg"],
    ["image/tiff", ".tiff"],
    ["image/webp", ".webp"]
  ]);
  const UMD_IMAGE_EXTENSIONS = new Set([
    ".avif",
    ".jpg",
    ".png",
    ".webp"
  ]);

  function createEmptyCaptureState() {
    return {
      schemaVersion: 2,
      fields: {
        title: null,
        category: null,
        description: null,
        chapterNumber: null
      },
      images: {
        selections: [],
        items: []
      },
      settings: {
        isPrivate: false
      },
      sourcePages: [],
      updatedAt: null
    };
  }

  function normalizeCaptureState(value) {
    const source = value && typeof value === "object" ? value : {};
    const normalized = createEmptyCaptureState();

    for (const field of [
      "title",
      "category",
      "description",
      "chapterNumber"
    ]) {
      const sourceField = source.fields?.[field];
      if (!sourceField || typeof sourceField !== "object") {
        continue;
      }

      normalized.fields[field] = {
        value: String(sourceField.value ?? ""),
        selector: String(sourceField.selector ?? ""),
        tagName: String(sourceField.tagName ?? ""),
        page: normalizeSourcePage(sourceField.page)
      };
    }

    const sourceItems = Array.isArray(source.images?.items)
      ? source.images.items
      : [];
    normalized.images.items = sourceItems
      .map(normalizeImageItem)
      .filter((image) => image.url);

    if (Array.isArray(source.images?.selections)) {
      normalized.images.selections = source.images.selections
        .map(normalizeImageSelection)
        .filter((selection) => selection.selector || selection.page.url);
    } else if (source.images?.selector) {
      normalized.images.selections = [
        normalizeImageSelection({
          selector: source.images.selector,
          tagName: source.images.tagName,
          page: source.page
        })
      ];
    }

    normalized.settings.isPrivate = Boolean(source.settings?.isPrivate);

    const sourcePages = Array.isArray(source.sourcePages)
      ? source.sourcePages
      : source.page
        ? [source.page]
        : [];
    normalized.sourcePages = sourcePages
      .map(normalizeSourcePage)
      .filter((page) => page.url);
    normalized.updatedAt = source.updatedAt ? String(source.updatedAt) : null;

    return normalized;
  }

  function normalizeSourcePage(page) {
    return {
      url: String(page?.url ?? ""),
      title: normalizeText(page?.title)
    };
  }

  function normalizeImageItem(image) {
    return {
      url: String(image?.url ?? ""),
      alt: normalizeText(image?.alt),
      width: Number.isFinite(image?.width) ? image.width : null,
      height: Number.isFinite(image?.height) ? image.height : null,
      sourceSelector: String(image?.sourceSelector ?? ""),
      page: normalizeSourcePage(image?.page)
    };
  }

  function normalizeImageSelection(selection) {
    return {
      selector: String(selection?.selector ?? ""),
      tagName: String(selection?.tagName ?? ""),
      page: normalizeSourcePage(selection?.page),
      capturedCount: Number.isInteger(selection?.capturedCount)
        ? selection.capturedCount
        : 0
    };
  }

  function normalizeText(value) {
    return String(value ?? "")
      .replace(/\s+/gu, " ")
      .trim();
  }

  function getTitlePrefix(title) {
    const normalized = normalizeText(title).normalize("NFKD");
    const characters = Array.from(normalized).filter((character) =>
      /[\p{L}\p{N}]/u.test(character)
    );

    if (characters.length === 0) {
      return "IM";
    }

    return characters.slice(0, 2).join("");
  }

  function sanitizePathSegment(value, fallback = "capture") {
    const sanitized = normalizeText(value)
      .replace(/[<>:"/\\|?*\u0000-\u001f]/gu, "_")
      .replace(/[. ]+$/gu, "")
      .slice(0, 80);

    return sanitized || fallback;
  }

  function getImageExtension(url) {
    const source = String(url ?? "").trim();

    if (source.startsWith("data:")) {
      const mediaType = source
        .slice(5, source.indexOf(",") === -1 ? undefined : source.indexOf(","))
        .split(";")[0]
        .toLowerCase();
      return MIME_EXTENSIONS.get(mediaType) ?? DEFAULT_IMAGE_EXTENSION;
    }

    try {
      const parsed = new URL(source, "https://capture.invalid/");
      const path = decodeURIComponent(parsed.pathname);
      const dotIndex = path.lastIndexOf(".");
      const extension =
        dotIndex === -1 ? "" : path.slice(dotIndex).toLowerCase();

      return IMAGE_EXTENSIONS.has(extension)
        ? extension === ".jpeg"
          ? ".jpg"
          : extension
        : DEFAULT_IMAGE_EXTENSION;
    } catch {
      return DEFAULT_IMAGE_EXTENSION;
    }
  }

  function createImageFileName(title, position, url) {
    const numericPosition = Number(position);
    if (!Number.isInteger(numericPosition) || numericPosition < 0) {
      throw new RangeError("Image positions must start at 0.");
    }

    return `${getTitlePrefix(title)}_${numericPosition}${getImageExtension(url)}`;
  }

  function buildExportDocument(capture, capturedAt = new Date().toISOString()) {
    const normalizedCapture = normalizeCaptureState(capture);
    const fields = normalizedCapture.fields;
    const title = normalizeText(fields.title?.value);
    const category = normalizeText(fields.category?.value);
    const description = normalizeText(fields.description?.value);
    const chapterNumber = normalizeText(fields.chapterNumber?.value);
    const imageItems = normalizedCapture.images.items;

    const images = imageItems.map((image, index) => ({
      position: index,
      fileName: createImageFileName(title, index, image.url),
      sourceUrl: String(image.url ?? ""),
      alt: normalizeText(image.alt),
      width: Number.isFinite(image.width) ? image.width : null,
      height: Number.isFinite(image.height) ? image.height : null,
      sourceSelector: String(image.sourceSelector ?? ""),
      page: normalizeSourcePage(image.page)
    }));

    return {
      schemaVersion: 1,
      capturedAt,
      sourcePages: normalizedCapture.sourcePages,
      title,
      category,
      description,
      chapterNumber,
      imageDirectory: createImageDirectory(chapterNumber),
      isPrivate: normalizedCapture.settings.isPrivate,
      sourceSelectors: {
        title: String(fields.title?.selector ?? ""),
        category: String(fields.category?.selector ?? ""),
        description: String(fields.description?.selector ?? ""),
        chapterNumber: String(fields.chapterNumber?.selector ?? ""),
        imageLists: normalizedCapture.images.selections.map(
          (selection) => selection.selector
        )
      },
      images
    };
  }

  function createImageDirectory(chapterNumber) {
    return `chapters/${sanitizePathSegment(chapterNumber, "chapter")}`;
  }

  function getUmdCaptureErrors(capture) {
    const normalizedCapture = normalizeCaptureState(capture);
    const errors = [];
    const title = normalizeText(normalizedCapture.fields.title?.value);
    const chapterNumber = normalizeText(
      normalizedCapture.fields.chapterNumber?.value
    );

    if (!title) {
      errors.push("Capture a title.");
    }
    if (!chapterNumber) {
      errors.push("Capture or enter a chapter number.");
    }
    if (normalizedCapture.images.items.length === 0) {
      errors.push("Capture at least one image.");
    }
    const unsupportedImages = normalizedCapture.images.items.filter(
      (image) => !UMD_IMAGE_EXTENSIONS.has(getImageExtension(image.url))
    );
    if (unsupportedImages.length > 0) {
      errors.push(
        `${unsupportedImages.length} captured ${
          unsupportedImages.length === 1 ? "image uses" : "images use"
        } a format unsupported by the UMD image reader. Use JPG, PNG, WebP, or AVIF images.`
      );
    }

    return errors;
  }

  function buildUmdInfoDocument(
    capture,
    exportedAt = new Date().toISOString()
  ) {
    const normalizedCapture = normalizeCaptureState(capture);
    const errors = getUmdCaptureErrors(normalizedCapture);
    if (errors.length > 0) {
      throw new Error(errors.join(" "));
    }

    const title = normalizeText(normalizedCapture.fields.title.value);
    const category = normalizeText(normalizedCapture.fields.category?.value);
    const description = normalizeText(
      normalizedCapture.fields.description?.value
    );
    const chapterNumber = normalizeText(
      normalizedCapture.fields.chapterNumber.value
    );
    const chapterInteger = /^\d+/u.exec(chapterNumber);
    const sortOrder = chapterInteger
      ? Math.min(
          2_147_483_647,
          Math.max(0, Number.parseInt(chapterInteger[0], 10) - 1)
        )
      : 0;

    return {
      format: "universal-media-distribution.catalog",
      version: 1,
      exportedAt,
      sourceLabel: `Catalog capture for ${title}`,
      items: [
        {
          mediaType: "book",
          subtype: "manga",
          name: title,
          description,
          genres: [],
          categories: category ? [category] : [],
          alternativeNames: [],
          contributors: [],
          seasons: [],
          units: [
            {
              kind: "chapter",
              displayNumber: chapterNumber,
              sortOrder,
              name: `Chapter ${chapterNumber}`,
              pageCount: normalizedCapture.images.items.length,
              sources: [
                {
                  assetRole: "primary",
                  sourceLayout: "image_sequence",
                  filePath: createImageDirectory(chapterNumber),
                  container: "IMAGE_SEQUENCE",
                  sortOrder: 0,
                  torrent: {
                    displayName: `${title} - Chapter ${chapterNumber}`,
                    isPrivate: normalizedCapture.settings.isPrivate,
                    trackers: []
                  }
                }
              ]
            }
          ],
          distributionRights: [],
          sources: []
        }
      ]
    };
  }

  function createDownloadFolder(title, capturedAt) {
    const timestamp = String(capturedAt ?? new Date().toISOString())
      .replace(/[:.]/gu, "-");
    return `UniversalMediaCatalog/${sanitizePathSegment(title)}/${timestamp}`;
  }

  const api = Object.freeze({
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
  });

  globalScope.UMDCaptureUtils = api;

  if (typeof module !== "undefined" && module.exports) {
    module.exports = api;
  }
})(typeof globalThis === "undefined" ? this : globalThis);
