"use strict";

importScripts("shared.js");

const utils = globalThis.UMDCaptureUtils;
const STORAGE_KEY = "umdCatalogCaptureDraft";
let mutationQueue = Promise.resolve();

chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
  if (!message || typeof message.type !== "string") {
    return false;
  }

  handleMessage(message, _sender)
    .then((result) => sendResponse({ ok: true, ...result }))
    .catch((error) =>
      sendResponse({
        ok: false,
        error: error instanceof Error ? error.message : String(error)
      })
    );

  return true;
});

configureSidePanel();
restrictStorageAccess();
chrome.runtime.onInstalled?.addListener(configureSidePanel);

async function handleMessage(message, sender) {
  switch (message.type) {
    case "UMD_CAPTURE_GET_STATE":
      return {
        state: await loadCaptureState()
      };
    case "UMD_CAPTURE_SAVE_FIELD":
      return {
        state: await mutateCaptureState(
          (state) => saveField(state, message.field, message.selection),
          Boolean(sender.tab)
        )
      };
    case "UMD_CAPTURE_SAVE_SETTING":
      return {
        state: await mutateCaptureState((state) =>
          saveSetting(state, message.setting, message.value)
        )
      };
    case "UMD_CAPTURE_ADD_IMAGES":
      return {
        state: await mutateCaptureState(
          (state) => addImages(state, message.selection),
          true
        )
      };
    case "UMD_CAPTURE_CLEAR_IMAGES":
      return {
        state: await mutateCaptureState((state) => {
          state.images = {
            selections: [],
            items: []
          };
          return state;
        })
      };
    case "UMD_CAPTURE_CLEAR":
      return {
        state: await replaceCaptureState(utils.createEmptyCaptureState())
      };
    case "UMD_CAPTURE_DOWNLOAD":
      return {
        result: await downloadCapture(message.capture)
      };
    default:
      throw new Error("Unknown extension message.");
  }
}

function configureSidePanel() {
  if (!chrome.sidePanel?.setPanelBehavior) {
    return;
  }

  const result = chrome.sidePanel.setPanelBehavior({
    openPanelOnActionClick: true
  });
  result?.catch?.(() => {});
}

function restrictStorageAccess() {
  const result = chrome.storage?.local?.setAccessLevel?.({
    accessLevel: "TRUSTED_CONTEXTS"
  });
  result?.catch?.(() => {});
}

function mutateCaptureState(mutator, notify = false) {
  const operation = mutationQueue.then(async () => {
    const current = await loadCaptureState();
    const next = utils.normalizeCaptureState(mutator(current) ?? current);
    next.updatedAt = new Date().toISOString();
    await setStoredState(next);

    if (notify) {
      notifyStateChanged(next);
    }

    return next;
  });

  mutationQueue = operation.catch(() => {});
  return operation;
}

function replaceCaptureState(capture, notify = false) {
  const operation = mutationQueue.then(async () => {
    const next = utils.normalizeCaptureState(capture);
    next.updatedAt = new Date().toISOString();
    await setStoredState(next);

    if (notify) {
      notifyStateChanged(next);
    }

    return next;
  });

  mutationQueue = operation.catch(() => {});
  return operation;
}

async function loadCaptureState() {
  const values = await chrome.storage.local.get(STORAGE_KEY);
  return utils.normalizeCaptureState(values?.[STORAGE_KEY]);
}

function setStoredState(state) {
  return chrome.storage.local.set({
    [STORAGE_KEY]: state
  });
}

function notifyStateChanged(state) {
  const result = chrome.runtime.sendMessage({
    type: "UMD_CAPTURE_STATE_CHANGED",
    state
  });
  result?.catch?.(() => {});
}

function saveField(state, field, selection) {
  if (
    !["title", "category", "description", "chapterNumber"].includes(field)
  ) {
    throw new Error("Unknown capture field.");
  }

  const existing = state.fields[field] ?? {
    selector: "",
    tagName: "",
    page: {
      url: "",
      title: ""
    }
  };
  state.fields[field] = {
    ...existing,
    ...selection,
    value: String(selection?.value ?? ""),
    page: normalizePage(selection?.page ?? existing.page)
  };
  addSourcePage(state, state.fields[field].page);
  return state;
}

function saveSetting(state, setting, value) {
  if (setting !== "isPrivate") {
    throw new Error("Unknown capture setting.");
  }

  state.settings.isPrivate = Boolean(value);
  return state;
}

function addImages(state, selection) {
  const normalizedSelection = {
    selector: String(selection?.selector ?? ""),
    tagName: String(selection?.tagName ?? ""),
    page: normalizePage(selection?.page),
    capturedCount: Array.isArray(selection?.items) ? selection.items.length : 0
  };
  const seenUrls = new Set(state.images.items.map((image) => image.url));

  for (const image of selection?.items ?? []) {
    const url = String(image?.url ?? "");
    if (!url || seenUrls.has(url)) {
      continue;
    }

    seenUrls.add(url);
    state.images.items.push({
      url,
      alt: String(image.alt ?? ""),
      width: Number.isFinite(image.width) ? image.width : null,
      height: Number.isFinite(image.height) ? image.height : null,
      sourceSelector: String(image.sourceSelector ?? ""),
      page: normalizedSelection.page
    });
  }

  state.images.selections.push(normalizedSelection);
  addSourcePage(state, normalizedSelection.page);
  return state;
}

function normalizePage(page) {
  return {
    url: String(page?.url ?? ""),
    title: utils.normalizeText(page?.title)
  };
}

function addSourcePage(state, page) {
  if (!page?.url) {
    return;
  }

  const existing = state.sourcePages.find(
    (candidate) => candidate.url === page.url
  );
  if (existing) {
    existing.title = page.title || existing.title;
    return;
  }

  state.sourcePages.push(normalizePage(page));
}

async function downloadCapture(capture) {
  const capturedAt = new Date().toISOString();
  const metadataDocument = utils.buildExportDocument(capture, capturedAt);
  const infoDocument = utils.buildUmdInfoDocument(capture, capturedAt);

  const folder = utils.createDownloadFolder(metadataDocument.title, capturedAt);
  const imageDirectory = utils.createImageDirectory(
    metadataDocument.chapterNumber
  );

  const metadataUrl = `data:application/json;charset=utf-8,${encodeURIComponent(
    JSON.stringify(metadataDocument, null, 2)
  )}`;
  const infoUrl = `data:application/json;charset=utf-8,${encodeURIComponent(
    JSON.stringify(infoDocument, null, 2)
  )}`;

  const downloadJobs = [
    {
      kind: "metadata",
      url: metadataUrl,
      options: {
        url: metadataUrl,
        filename: `${folder}/metadata.json`,
        conflictAction: "uniquify",
        saveAs: false
      }
    },
    {
      kind: "info",
      url: infoUrl,
      options: {
        url: infoUrl,
        filename: `${folder}/info.json`,
        conflictAction: "uniquify",
        saveAs: false
      }
    },
    ...metadataDocument.images.map((image) => ({
      kind: "image",
      position: image.position,
      url: image.sourceUrl,
      options: {
        url: image.sourceUrl,
        filename: `${folder}/${imageDirectory}/${image.fileName}`,
        conflictAction: "uniquify",
        saveAs: false
      }
    }))
  ];

  const outcomes = await Promise.allSettled(
    downloadJobs.map((job) => startDownload(job.options))
  );
  const failures = [];
  let metadataFiles = 0;
  let infoFiles = 0;
  let startedImages = 0;

  outcomes.forEach((outcome, index) => {
    const job = downloadJobs[index];
    if (outcome.status === "fulfilled") {
      if (job.kind === "metadata") {
        metadataFiles += 1;
      } else if (job.kind === "info") {
        infoFiles += 1;
      } else {
        startedImages += 1;
      }
      return;
    }

    failures.push({
      kind: job.kind,
      position: job.position ?? null,
      url: job.url,
      filename: job.options.filename,
      error:
        outcome.reason instanceof Error
          ? outcome.reason.message
          : String(outcome.reason)
    });
  });

  return {
    folder,
    metadataFiles,
    infoFiles,
    requestedImages: metadataDocument.images.length,
    startedImages,
    failures
  };
}

function startDownload(options) {
  return new Promise((resolve, reject) => {
    chrome.downloads.download(options, (downloadId) => {
      const runtimeError = chrome.runtime.lastError;
      if (runtimeError) {
        reject(new Error(runtimeError.message));
        return;
      }

      resolve(downloadId);
    });
  });
}
