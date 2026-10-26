(function initializeSidePanel() {
  "use strict";

  const utils = globalThis.UMDCaptureUtils;
  const elements = {};
  const captureFields = [
    "title",
    "category",
    "chapterNumber",
    "description"
  ];
  let activeTabId = null;
  let activePageTitle = "";
  let captureState = null;

  document.addEventListener("DOMContentLoaded", initialize);

  async function initialize() {
    mapElements();
    bindEvents();
    setControlsDisabled(true);

    try {
      const [stateResponse, tab] = await Promise.all([
        sendRuntimeMessage({ type: "UMD_CAPTURE_GET_STATE" }),
        getActiveTab()
      ]);

      if (!stateResponse?.ok) {
        throw new Error(
          stateResponse?.error || "The saved capture could not be loaded."
        );
      }

      captureState = utils.normalizeCaptureState(stateResponse.state);
      activeTabId = tab?.id ?? null;
      activePageTitle = tab?.title ?? "";
      setConnection("Persistent", "ready");
      setControlsDisabled(false);
      render();
    } catch (error) {
      setConnection("Unavailable", "error");
      showStatus(getFriendlyPageError(error), "error");
    }
  }

  function mapElements() {
    elements.connectionBadge = document.querySelector("#connectionBadge");
    elements.pageName = document.querySelector("#pageName");
    elements.titleValue = document.querySelector("#titleValue");
    elements.categoryValue = document.querySelector("#categoryValue");
    elements.chapterNumberValue = document.querySelector(
      "#chapterNumberValue"
    );
    elements.descriptionValue = document.querySelector("#descriptionValue");
    elements.titleSelector = document.querySelector("#titleSelector");
    elements.categorySelector = document.querySelector("#categorySelector");
    elements.chapterNumberSelector = document.querySelector(
      "#chapterNumberSelector"
    );
    elements.descriptionSelector = document.querySelector(
      "#descriptionSelector"
    );
    elements.imageCount = document.querySelector("#imageCount");
    elements.imageSelector = document.querySelector("#imageSelector");
    elements.imagePreview = document.querySelector("#imagePreview");
    elements.clearImagesButton = document.querySelector("#clearImagesButton");
    elements.isPrivateValue = document.querySelector("#isPrivateValue");
    elements.imageDirectory = document.querySelector("#imageDirectory");
    elements.downloadRequirements = document.querySelector(
      "#downloadRequirements"
    );
    elements.statusMessage = document.querySelector("#statusMessage");
    elements.clearButton = document.querySelector("#clearButton");
    elements.copyButton = document.querySelector("#copyButton");
    elements.downloadButton = document.querySelector("#downloadButton");
  }

  function bindEvents() {
    document.querySelectorAll("[data-picker]").forEach((button) => {
      button.addEventListener("click", () =>
        beginPicking(button.dataset.picker)
      );
    });

    document.querySelectorAll("[data-field]").forEach((field) => {
      field.addEventListener("input", () => {
        saveManualField(field.dataset.field, field.value);
      });
    });

    elements.isPrivateValue.addEventListener("change", () => {
      saveSetting("isPrivate", elements.isPrivateValue.checked);
    });

    elements.clearImagesButton.addEventListener("click", clearImages);
    elements.clearButton.addEventListener("click", clearCapture);
    elements.copyButton.addEventListener("click", copyCapture);
    elements.downloadButton.addEventListener("click", downloadCapture);

    chrome.runtime.onMessage.addListener((message) => {
      if (message?.type !== "UMD_CAPTURE_STATE_CHANGED" || !message.state) {
        return false;
      }

      captureState = utils.normalizeCaptureState(message.state);
      render();
      showStatus("Selection saved. The draft will survive page changes.", "success");
      return false;
    });

    chrome.tabs.onActivated?.addListener(() => {
      refreshActiveTab().catch(() => {});
    });
    chrome.tabs.onUpdated?.addListener((tabId, changeInfo) => {
      if (tabId === activeTabId && (changeInfo.title || changeInfo.status)) {
        refreshActiveTab().catch(() => {});
      }
    });
  }

  async function refreshActiveTab() {
    const tab = await getActiveTab();
    activeTabId = tab?.id ?? null;
    activePageTitle = tab?.title ?? "";
    renderPageName();
  }

  async function beginPicking(field) {
    try {
      await refreshActiveTab();
      if (!activeTabId) {
        throw new Error("No active browser tab is available.");
      }

      await ensureContentScript(activeTabId);
      const response = await sendTabMessage({
        type: "UMD_CAPTURE_START_PICKER",
        field
      });
      if (!response?.ok) {
        throw new Error(response?.error || "The picker could not be started.");
      }

      const label =
        field === "images"
          ? "image list"
          : field === "chapterNumber"
            ? "chapter number"
            : field;
      showStatus(
        `Picker active for ${label}. Click the highlighted page element; this panel will stay open.`,
        "success"
      );
    } catch (error) {
      showStatus(getFriendlyPageError(error), "error");
    }
  }

  async function saveManualField(field, value) {
    if (!captureState || !captureFields.includes(field)) {
      return;
    }

    const existing = captureState.fields[field] ?? {
      selector: "",
      tagName: "manual",
      page: {
        url: "",
        title: ""
      }
    };
    const selection = {
      ...existing,
      value: String(value ?? "")
    };
    captureState.fields[field] = selection;
    renderDerivedValues();

    try {
      const response = await sendRuntimeMessage({
        type: "UMD_CAPTURE_SAVE_FIELD",
        field,
        selection
      });
      if (!response?.ok) {
        throw new Error(response?.error || "The value could not be saved.");
      }
      captureState = utils.normalizeCaptureState(response.state);
    } catch (error) {
      showStatus(error.message, "error");
    }
  }

  async function saveSetting(setting, value) {
    if (!captureState) {
      return;
    }

    captureState.settings.isPrivate = Boolean(value);
    renderDerivedValues();

    try {
      const response = await sendRuntimeMessage({
        type: "UMD_CAPTURE_SAVE_SETTING",
        setting,
        value
      });
      if (!response?.ok) {
        throw new Error(response?.error || "The setting could not be saved.");
      }
      captureState = utils.normalizeCaptureState(response.state);
    } catch (error) {
      showStatus(error.message, "error");
    }
  }

  async function clearImages() {
    try {
      const response = await sendRuntimeMessage({
        type: "UMD_CAPTURE_CLEAR_IMAGES"
      });
      if (!response?.ok) {
        throw new Error(response?.error || "The images could not be cleared.");
      }

      captureState = utils.normalizeCaptureState(response.state);
      render();
      showStatus("Captured image lists cleared.");
    } catch (error) {
      showStatus(error.message, "error");
    }
  }

  async function clearCapture() {
    try {
      const response = await sendRuntimeMessage({
        type: "UMD_CAPTURE_CLEAR"
      });
      if (!response?.ok) {
        throw new Error(response?.error || "The capture could not be cleared.");
      }

      captureState = utils.normalizeCaptureState(response.state);
      render();
      showStatus("Persistent capture cleared.");
    } catch (error) {
      showStatus(error.message, "error");
    }
  }

  async function copyCapture() {
    syncVisibleState();
    const captureDocument = utils.buildExportDocument(captureState);

    try {
      await navigator.clipboard.writeText(
        JSON.stringify(captureDocument, null, 2)
      );
      showStatus("Capture metadata copied to the clipboard.", "success");
    } catch {
      showStatus("The browser did not allow clipboard access.", "error");
    }
  }

  async function downloadCapture() {
    syncVisibleState();
    const errors = utils.getUmdCaptureErrors(captureState);
    if (errors.length > 0) {
      showStatus(errors.join(" "), "error");
      return;
    }

    elements.downloadButton.disabled = true;
    elements.downloadButton.textContent = "Starting downloads…";

    try {
      const response = await sendRuntimeMessage({
        type: "UMD_CAPTURE_DOWNLOAD",
        capture: captureState
      });

      if (!response?.ok) {
        throw new Error(response?.error || "The download could not be started.");
      }

      const result = response.result;
      if (result.failures.length > 0) {
        showStatus(
          `Queued ${result.metadataFiles + result.infoFiles} of 2 JSON files and ${result.startedImages} of ${result.requestedImages} images together. Some downloads could not be started.`,
          "error"
        );
      } else {
        showStatus(
          `Queued metadata.json, info.json, and ${result.startedImages} images together.`,
          "success"
        );
      }
    } catch (error) {
      showStatus(error.message, "error");
    } finally {
      elements.downloadButton.textContent = "Download capture";
      renderDerivedValues();
    }
  }

  function syncVisibleState() {
    if (!captureState) {
      return;
    }

    for (const field of captureFields) {
      const input = elements[`${field}Value`];
      const existing = captureState.fields[field] ?? {
        selector: "",
        tagName: "manual",
        page: {
          url: "",
          title: ""
        }
      };
      captureState.fields[field] = {
        ...existing,
        value: input.value
      };
    }

    captureState.settings.isPrivate = elements.isPrivateValue.checked;
  }

  function render() {
    if (!captureState) {
      return;
    }

    const focusedElement = document.activeElement;
    for (const field of captureFields) {
      const input = elements[`${field}Value`];
      if (focusedElement !== input) {
        input.value = captureState.fields[field]?.value ?? "";
      }
      elements[`${field}Selector`].textContent =
        captureState.fields[field]?.selector || "Not selected";
    }

    elements.isPrivateValue.checked = captureState.settings.isPrivate;

    const imageCount = captureState.images.items.length;
    const selectionCount = captureState.images.selections.length;
    elements.imageCount.textContent = `${imageCount} ${
      imageCount === 1 ? "image" : "images"
    }`;
    elements.imageSelector.textContent =
      selectionCount === 0
        ? "No lists selected"
        : `${selectionCount} image ${
            selectionCount === 1 ? "list" : "lists"
          } captured across ${captureState.sourcePages.length} ${
            captureState.sourcePages.length === 1 ? "page" : "pages"
          }`;

    renderPageName();
    renderImagePreview();
    renderDerivedValues();
  }

  function renderPageName() {
    if (!captureState) {
      return;
    }

    const sourceCount = captureState.sourcePages.length;
    const sourceLabel = `${sourceCount} saved source ${
      sourceCount === 1 ? "page" : "pages"
    }`;
    elements.pageName.textContent = activePageTitle
      ? `${activePageTitle} · ${sourceLabel}`
      : sourceLabel;
  }

  function renderImagePreview() {
    const title =
      elements.titleValue.value || captureState.fields.title?.value || "";
    const images = captureState.images.items;
    const previewImages = images.slice(0, 5);

    elements.imagePreview.replaceChildren(
      ...previewImages.map((image, index) => {
        const item = document.createElement("li");
        const fileName = document.createElement("code");
        const source = document.createElement("span");
        fileName.textContent = utils.createImageFileName(
          title,
          index,
          image.url
        );
        source.textContent = image.alt || image.url;
        source.title = image.url;
        item.append(fileName, source);
        return item;
      })
    );

    if (images.length > previewImages.length) {
      const remaining = document.createElement("li");
      const spacer = document.createElement("span");
      const message = document.createElement("span");
      spacer.textContent = "…";
      message.textContent = `and ${images.length - previewImages.length} more`;
      remaining.append(spacer, message);
      elements.imagePreview.append(remaining);
    }
  }

  function renderDerivedValues() {
    if (!captureState) {
      return;
    }

    syncVisibleState();
    const chapterNumber = captureState.fields.chapterNumber?.value ?? "";
    elements.imageDirectory.textContent =
      utils.createImageDirectory(chapterNumber);

    const errors = utils.getUmdCaptureErrors(captureState);
    elements.downloadRequirements.textContent =
      errors.length === 0
        ? "Ready to create an importer-compatible info.json."
        : errors.join(" ");
    elements.downloadRequirements.className =
      errors.length === 0 ? "requirements ready" : "requirements";

    elements.copyButton.disabled = false;
    elements.clearButton.disabled = false;
    elements.clearImagesButton.disabled =
      captureState.images.items.length === 0;
    elements.downloadButton.disabled = errors.length > 0;
  }

  function setControlsDisabled(disabled) {
    document
      .querySelectorAll("button, input, textarea")
      .forEach((control) => {
        control.disabled = disabled;
      });
  }

  function setConnection(text, style) {
    elements.connectionBadge.textContent = text;
    elements.connectionBadge.className = `badge ${style}`;
  }

  function showStatus(message, style = "") {
    elements.statusMessage.hidden = false;
    elements.statusMessage.textContent = message;
    elements.statusMessage.className = `status-message ${style}`.trim();
  }

  function getFriendlyPageError(error) {
    const message = error instanceof Error ? error.message : String(error);
    if (
      /cannot access|chrome:\/|edge:\/|extensions gallery|missing host permission|receiving end does not exist/iu.test(
        message
      )
    ) {
      return "Open a normal http:// or https:// page, then try the picker again.";
    }

    return message;
  }

  async function ensureContentScript(tabId) {
    try {
      const response = await sendTabMessage(
        { type: "UMD_CAPTURE_PING" },
        tabId
      );
      if (response?.ok) {
        return;
      }
    } catch {
      // This can happen for a page that was already open when the extension loaded.
    }

    await executeScript(tabId, ["src/shared.js", "src/content.js"]);
  }

  function getActiveTab() {
    return new Promise((resolve, reject) => {
      chrome.tabs.query({ active: true, currentWindow: true }, (tabs) => {
        const runtimeError = chrome.runtime.lastError;
        if (runtimeError) {
          reject(new Error(runtimeError.message));
          return;
        }

        resolve(tabs[0]);
      });
    });
  }

  function executeScript(tabId, files) {
    return new Promise((resolve, reject) => {
      chrome.scripting.executeScript(
        {
          target: { tabId },
          files
        },
        (results) => {
          const runtimeError = chrome.runtime.lastError;
          if (runtimeError) {
            reject(new Error(runtimeError.message));
            return;
          }

          resolve(results);
        }
      );
    });
  }

  function sendTabMessage(message, tabId = activeTabId) {
    return new Promise((resolve, reject) => {
      chrome.tabs.sendMessage(tabId, message, (response) => {
        const runtimeError = chrome.runtime.lastError;
        if (runtimeError) {
          reject(new Error(runtimeError.message));
          return;
        }

        resolve(response);
      });
    });
  }

  function sendRuntimeMessage(message) {
    return new Promise((resolve, reject) => {
      chrome.runtime.sendMessage(message, (response) => {
        const runtimeError = chrome.runtime.lastError;
        if (runtimeError) {
          reject(new Error(runtimeError.message));
          return;
        }

        resolve(response);
      });
    });
  }
})();
