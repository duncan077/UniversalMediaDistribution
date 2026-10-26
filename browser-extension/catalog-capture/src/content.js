(function initializeCatalogCapture() {
  "use strict";

  if (globalThis.__UMD_CATALOG_CAPTURE_LOADED__) {
    return;
  }

  globalThis.__UMD_CATALOG_CAPTURE_LOADED__ = true;

  const utils = globalThis.UMDCaptureUtils;
  const PICKABLE_FIELDS = new Set([
    "title",
    "category",
    "description",
    "chapterNumber",
    "images"
  ]);

  let activePicker = null;
  let hoveredElement = null;
  let overlay = null;
  let overlayLabel = null;
  let toastTimeout = null;
  let animationFrame = null;

  chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
    if (!message || typeof message.type !== "string") {
      return false;
    }

    try {
      switch (message.type) {
        case "UMD_CAPTURE_PING":
          sendResponse({ ok: true });
          return false;
        case "UMD_CAPTURE_START_PICKER":
          startPicker(message.field);
          sendResponse({ ok: true });
          return false;
        default:
          return false;
      }
    } catch (error) {
      sendResponse({
        ok: false,
        error: error instanceof Error ? error.message : String(error)
      });
      return false;
    }
  });

  function getPageDetails() {
    return {
      url: location.href,
      title: document.title
    };
  }

  function startPicker(field) {
    if (!PICKABLE_FIELDS.has(field)) {
      throw new Error("Unknown picker type.");
    }

    stopPicker();
    activePicker = field;
    createOverlay();

    document.addEventListener("pointerover", handlePointerOver, true);
    document.addEventListener("click", handleSelection, true);
    document.addEventListener("keydown", handleKeyDown, true);
    window.addEventListener("scroll", scheduleOverlayUpdate, true);
    window.addEventListener("resize", scheduleOverlayUpdate, true);

    showToast(
      field === "images"
        ? "Select the element that contains the image list. Press Esc to cancel."
        : `Select the element containing the ${field}. Press Esc to cancel.`,
      "info",
      4000
    );
  }

  function stopPicker() {
    document.removeEventListener("pointerover", handlePointerOver, true);
    document.removeEventListener("click", handleSelection, true);
    document.removeEventListener("keydown", handleKeyDown, true);
    window.removeEventListener("scroll", scheduleOverlayUpdate, true);
    window.removeEventListener("resize", scheduleOverlayUpdate, true);

    activePicker = null;
    hoveredElement = null;

    if (animationFrame !== null) {
      cancelAnimationFrame(animationFrame);
      animationFrame = null;
    }

    overlay?.remove();
    overlay = null;
    overlayLabel = null;
  }

  function handlePointerOver(event) {
    const candidate = event.target;
    if (!(candidate instanceof Element) || isCaptureUi(candidate)) {
      return;
    }

    hoveredElement = candidate;
    updateOverlay();
  }

  async function handleSelection(event) {
    const selected = hoveredElement ?? event.target;
    if (!(selected instanceof Element) || isCaptureUi(selected)) {
      return;
    }

    event.preventDefault();
    event.stopPropagation();
    event.stopImmediatePropagation();

    const field = activePicker;
    const page = getPageDetails();
    if (field === "images") {
      const items = extractImages(selected);
      const selection = {
        selector: buildSelector(selected),
        tagName: selected.tagName.toLowerCase(),
        page,
        items
      };
      stopPicker();
      try {
        const response = await sendRuntimeMessage({
          type: "UMD_CAPTURE_ADD_IMAGES",
          selection
        });
        if (!response?.ok) {
          throw new Error(response?.error || "The image list could not be saved.");
        }
        const total = response.state.images.items.length;
        showToast(
          items.length === 1
            ? `Added 1 image (${total} total).`
            : `Added ${items.length} images (${total} total).`,
          items.length > 0 ? "success" : "warning"
        );
      } catch (error) {
        showToast(
          error instanceof Error ? error.message : String(error),
          "warning",
          4000
        );
      }
      return;
    }

    const value = getElementValue(selected);
    const selection = {
      value,
      selector: buildSelector(selected),
      tagName: selected.tagName.toLowerCase(),
      page
    };
    stopPicker();
    try {
      const response = await sendRuntimeMessage({
        type: "UMD_CAPTURE_SAVE_FIELD",
        field,
        selection
      });
      if (!response?.ok) {
        throw new Error(response?.error || "The selected value could not be saved.");
      }
      showToast(
        value
          ? `Captured ${getFieldLabel(field)}.`
          : `The selected ${getFieldLabel(field)} is empty.`,
        value ? "success" : "warning"
      );
    } catch (error) {
      showToast(
        error instanceof Error ? error.message : String(error),
        "warning",
        4000
      );
    }
  }

  function getFieldLabel(field) {
    return field === "chapterNumber" ? "chapter number" : field;
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

  function handleKeyDown(event) {
    if (event.key !== "Escape") {
      return;
    }

    event.preventDefault();
    event.stopPropagation();
    stopPicker();
    showToast("Selection cancelled.", "info");
  }

  function isCaptureUi(element) {
    return Boolean(element.closest("[data-umd-capture-ui]"));
  }

  function createOverlay() {
    overlay = document.createElement("div");
    overlay.dataset.umdCaptureUi = "overlay";
    overlay.setAttribute("aria-hidden", "true");
    Object.assign(overlay.style, {
      position: "fixed",
      zIndex: "2147483646",
      pointerEvents: "none",
      border: "2px solid #5468d4",
      borderRadius: "8px",
      background: "rgba(84, 104, 212, 0.12)",
      boxShadow: "0 0 0 1px rgba(255, 255, 255, 0.9)",
      display: "none",
      boxSizing: "border-box"
    });

    overlayLabel = document.createElement("div");
    overlayLabel.dataset.umdCaptureUi = "label";
    Object.assign(overlayLabel.style, {
      position: "absolute",
      left: "-2px",
      bottom: "calc(100% + 6px)",
      maxWidth: "min(420px, 80vw)",
      padding: "7px 11px",
      borderRadius: "10px",
      color: "#ffffff",
      background: "#232a45",
      font: "600 12px/1.3 Inter, system-ui, sans-serif",
      letterSpacing: "0.01em",
      whiteSpace: "nowrap",
      overflow: "hidden",
      textOverflow: "ellipsis",
      boxShadow: "0 5px 18px rgba(16, 12, 30, 0.28)"
    });

    overlay.append(overlayLabel);
    document.documentElement.append(overlay);
  }

  function scheduleOverlayUpdate() {
    if (animationFrame !== null) {
      return;
    }

    animationFrame = requestAnimationFrame(() => {
      animationFrame = null;
      updateOverlay();
    });
  }

  function updateOverlay() {
    if (!overlay || !overlayLabel || !hoveredElement) {
      return;
    }

    const rect = hoveredElement.getBoundingClientRect();
    Object.assign(overlay.style, {
      display: "block",
      top: `${rect.top}px`,
      left: `${rect.left}px`,
      width: `${Math.max(rect.width, 1)}px`,
      height: `${Math.max(rect.height, 1)}px`
    });

    const tag = hoveredElement.tagName.toLowerCase();
    const identity = hoveredElement.id
      ? `#${hoveredElement.id}`
      : Array.from(hoveredElement.classList)
          .slice(0, 2)
          .map((className) => `.${className}`)
          .join("");
    overlayLabel.textContent = `${tag}${identity} — click to capture ${getFieldLabel(activePicker)}`;

    if (rect.top < 38) {
      Object.assign(overlayLabel.style, {
        top: "calc(100% + 6px)",
        bottom: "auto"
      });
    } else {
      Object.assign(overlayLabel.style, {
        top: "auto",
        bottom: "calc(100% + 6px)"
      });
    }
  }

  function showToast(message, kind = "info", duration = 2600) {
    document.querySelector("[data-umd-capture-ui='toast']")?.remove();
    if (toastTimeout !== null) {
      clearTimeout(toastTimeout);
    }

    const colors = {
      info: "#232a45",
      success: "#0f6a4c",
      warning: "#8a5209"
    };

    const toast = document.createElement("div");
    toast.dataset.umdCaptureUi = "toast";
    toast.textContent = message;
    Object.assign(toast.style, {
      position: "fixed",
      right: "18px",
      bottom: "18px",
      zIndex: "2147483647",
      maxWidth: "min(420px, calc(100vw - 36px))",
      padding: "13px 16px",
      borderRadius: "14px",
      color: "#ffffff",
      background: colors[kind] ?? colors.info,
      font: "600 13px/1.45 Inter, system-ui, sans-serif",
      boxShadow: "0 14px 34px rgba(16, 20, 34, 0.32)"
    });

    document.documentElement.append(toast);
    toastTimeout = setTimeout(() => {
      toast.remove();
      toastTimeout = null;
    }, duration);
  }

  function getElementValue(element) {
    const tagName = element.tagName.toLowerCase();

    if (tagName === "meta") {
      return utils.normalizeText(element.getAttribute("content"));
    }

    if (["input", "textarea", "select"].includes(tagName)) {
      return utils.normalizeText(element.value);
    }

    if (tagName === "img") {
      return utils.normalizeText(
        element.getAttribute("alt") || element.getAttribute("title")
      );
    }

    return utils.normalizeText(
      element.innerText ||
        element.textContent ||
        element.getAttribute("aria-label") ||
        element.getAttribute("title")
    );
  }

  function extractImages(container) {
    const imageElements =
      container.tagName.toLowerCase() === "img"
        ? [container]
        : Array.from(container.querySelectorAll("img"));
    const images = [];
    const seenUrls = new Set();

    for (const image of imageElements) {
      const url = getBestImageUrl(image);
      if (!url || seenUrls.has(url)) {
        continue;
      }

      seenUrls.add(url);
      images.push({
        url,
        alt: image.getAttribute("alt") || "",
        width: image.naturalWidth || image.width || null,
        height: image.naturalHeight || image.height || null,
        sourceSelector: buildSelector(image)
      });
    }

    return images;
  }

  function getBestImageUrl(image) {
    const linkedImage = getLinkedImageUrl(image);
    const sourceSet =
      image.getAttribute("data-srcset") ||
      image.getAttribute("srcset") ||
      image.parentElement?.querySelector("source[srcset]")?.getAttribute("srcset");
    const sourceSetUrl = getLargestSourceSetCandidate(sourceSet);

    const candidates = [
      linkedImage,
      image.getAttribute("data-original"),
      image.getAttribute("data-full"),
      image.getAttribute("data-src"),
      image.getAttribute("data-lazy-src"),
      image.currentSrc,
      sourceSetUrl,
      image.getAttribute("src")
    ];

    for (const candidate of candidates) {
      const resolved = resolveDownloadableUrl(candidate);
      if (resolved) {
        return resolved;
      }
    }

    return "";
  }

  function getLinkedImageUrl(image) {
    const anchor = image.closest("a[href]");
    if (!anchor) {
      return "";
    }

    const href = anchor.getAttribute("href");
    if (!href) {
      return "";
    }

    try {
      const parsed = new URL(href, document.baseURI);
      return /\.(?:avif|bmp|gif|jpe?g|png|svg|tiff?|webp)$/iu.test(
        parsed.pathname
      )
        ? parsed.href
        : "";
    } catch {
      return "";
    }
  }

  function getLargestSourceSetCandidate(sourceSet) {
    if (!sourceSet) {
      return "";
    }

    const candidates = sourceSet
      .split(",")
      .map((candidate) => candidate.trim())
      .filter(Boolean)
      .map((candidate) => {
        const parts = candidate.split(/\s+/u);
        const descriptor = parts[1] ?? "1x";
        const score = Number.parseFloat(descriptor) || 1;
        return {
          url: parts[0],
          score
        };
      })
      .sort((left, right) => right.score - left.score);

    return candidates[0]?.url ?? "";
  }

  function resolveDownloadableUrl(value) {
    const candidate = String(value ?? "").trim();
    if (!candidate) {
      return "";
    }

    if (candidate.startsWith("data:image/")) {
      return candidate;
    }

    try {
      const parsed = new URL(candidate, document.baseURI);
      return ["http:", "https:"].includes(parsed.protocol) ? parsed.href : "";
    } catch {
      return "";
    }
  }

  function buildSelector(element) {
    if (element.id) {
      const idSelector = `#${escapeCss(element.id)}`;
      if (document.querySelectorAll(idSelector).length === 1) {
        return idSelector;
      }
    }

    const segments = [];
    let current = element;

    while (
      current &&
      current.nodeType === Node.ELEMENT_NODE &&
      current !== document.documentElement
    ) {
      const tagName = current.tagName.toLowerCase();
      const parent = current.parentElement;
      if (!parent) {
        segments.unshift(tagName);
        break;
      }

      const siblings = Array.from(parent.children).filter(
        (sibling) => sibling.tagName === current.tagName
      );
      const position = siblings.indexOf(current) + 1;
      segments.unshift(
        siblings.length > 1 ? `${tagName}:nth-of-type(${position})` : tagName
      );

      current = parent;
      if (segments.length >= 6 || current === document.body) {
        break;
      }
    }

    return segments.join(" > ");
  }

  function escapeCss(value) {
    if (globalThis.CSS?.escape) {
      return globalThis.CSS.escape(value);
    }

    return String(value).replace(/[^a-zA-Z0-9_-]/gu, (character) => {
      return `\\${character.codePointAt(0).toString(16)} `;
    });
  }
})();
