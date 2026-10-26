// Cosmetic runtime and built-in defences.
//
// Everything here is budgeted. This code runs on a television box with four
// small cores that is also decoding video and verifying torrent pieces, so an
// unbounded DOM walk on every mutation is not a slow feature — it is dropped
// frames. Plain selectors go into one stylesheet and cost nothing after that;
// procedural selectors and the click-trap sweep are capped, coalesced into a
// frame, and switched off once the page stops changing.
(() => {
  const marker = "__umdFilterRuntime";
  if (globalThis[marker]) { return; }

  const config = globalThis.__umdFilterConfig || {};
  const selectors = config.selectors || [];
  const genericSelectors = config.genericSelectors || [];
  const procedural = config.procedural || [];
  const scriptlets = config.scriptlets || [];
  const budgetMs = config.proceduralBudgetMs || 12;
  const idleMs = config.observerIdleMs || 5000;
  const maxElements = config.maxElementsPerPass || 2000;
  const defend = config.builtInDefenses !== false;

  const runScriptlets = () => {
    const runner = globalThis.__umdScriptlets;
    if (!runner) { return; }
    for (const entry of scriptlets) {
      runner.run(entry.name, entry.args || []);
    }
  };

  // A document-start script runs in every frame, and the generic set is tens
  // of thousands of selectors. Parsing that once per frame on a page built out
  // of frames is enough work to stall the renderer, and it buys nothing: an
  // advertisement frame is the advertiser's own document, where hiding
  // elements is pointless because the frame itself should not have loaded.
  // Rules written for this host still apply everywhere its page runs.
  const isTopDocument = (() => {
    try { return window.top === window.self; } catch { return false; }
  })();

  let cosmeticStyle = null;
  const injectStyle = () => {
    if (cosmeticStyle) { return true; }
    const applied = isTopDocument
      ? selectors.concat(genericSelectors)
      : selectors;
    if (applied.length === 0) { return true; }
    const root = document.head || document.documentElement;
    if (!root) { return false; }
    const style = document.createElement("style");
    style.id = "umd-cosmetic-style";
    // One rule with a selector list, not one rule per selector: the engine
    // parses it once and matches it as a set.
    style.textContent = applied.join(",") + "{display:none!important;}";
    root.appendChild(style);
    cosmeticStyle = style;
    return true;
  };

  // --- procedural selectors -------------------------------------------------

  const proceduralPattern =
    /^(.*?):(has-text|matches-css|upward|xpath|remove|min-text-length)\((.*)\)$/;

  const compiled = [];
  for (const selector of procedural) {
    const match = proceduralPattern.exec(selector);
    if (match) {
      compiled.push({ base: match[1] || "*", op: match[2], arg: match[3] });
    }
  }

  const textMatcher = (raw) => {
    if (raw.length > 2 && raw[0] === "/" && raw.endsWith("/")) {
      try {
        const expression = new RegExp(raw.slice(1, -1));
        return (value) => expression.test(value);
      } catch { return () => false; }
    }

    return (value) => value.includes(raw);
  };

  const applyProcedural = (deadline) => {
    let examined = 0;
    for (const rule of compiled) {
      let candidates;
      try { candidates = document.querySelectorAll(rule.base); }
      catch { continue; }

      for (const element of candidates) {
        if (++examined > maxElements || performance.now() > deadline) {
          // Out of budget. The remaining elements are picked up on the next
          // pass rather than held against this frame.
          return;
        }

        try {
          if (matches(rule, element)) { hide(rule, element); }
        } catch { /* a selector that throws is one rule, not the page */ }
      }
    }
  };

  const matches = (rule, element) => {
    switch (rule.op) {
      case "has-text":
        return textMatcher(rule.arg)(element.textContent || "");
      case "min-text-length":
        return (element.textContent || "").length >= Number(rule.arg);
      case "matches-css": {
        const separator = rule.arg.indexOf(":");
        if (separator < 0) { return false; }
        const name = rule.arg.slice(0, separator).trim();
        const wanted = rule.arg.slice(separator + 1).trim();
        return textMatcher(wanted)(getComputedStyle(element)[name] || "");
      }
      case "xpath": {
        const found = document.evaluate(
          rule.arg,
          element,
          null,
          XPathResult.BOOLEAN_TYPE,
          null);
        return found.booleanValue;
      }
      default:
        return true;
    }
  };

  const hide = (rule, element) => {
    let target = element;
    if (rule.op === "upward") {
      const steps = Number(rule.arg);
      if (Number.isNaN(steps)) {
        target = element.closest(rule.arg) || element;
      } else {
        for (let step = 0; step < steps && target.parentElement; step++) {
          target = target.parentElement;
        }
      }
    }

    if (rule.op === "remove") { target.remove(); return; }
    target.style.setProperty("display", "none", "important");
  };

  // --- built-in defences ----------------------------------------------------
  //
  // Popups and first-click theft are what these player hosts actually do, and
  // no filter list describes them. This runs on every device tier.

  const stripTargets = (root) => {
    for (const anchor of root.querySelectorAll("a[target]")) {
      if (anchor.target && anchor.target !== "_self") {
        anchor.removeAttribute("target");
      }
    }

    for (const form of root.querySelectorAll("form[target]")) {
      form.removeAttribute("target");
    }
  };

  // A layer that covers the viewport and holds no media is how the first click
  // gets stolen. Candidates are pre-filtered on cheap criteria so the sweep
  // costs a bounded number of layout reads instead of one per element.
  const clickTrapCandidates = () =>
    document.querySelectorAll(
      "body > div, body > a, body > span, body > ins, " +
      "[style*='position:fixed'], [style*='position: fixed'], " +
      "[style*='z-index']");

  const removeClickTraps = (deadline) => {
    let examined = 0;
    for (const element of clickTrapCandidates()) {
      if (++examined > maxElements || performance.now() > deadline) { return; }
      if (!(element instanceof HTMLElement) ||
          element.querySelector("video, audio, canvas")) {
        continue;
      }

      const style = getComputedStyle(element);
      if (style.position !== "fixed" && style.position !== "absolute") {
        continue;
      }

      const rect = element.getBoundingClientRect();
      const covers = rect.width >= innerWidth * 0.9 &&
        rect.height >= innerHeight * 0.9;
      if (!covers) { continue; }

      const transparent =
        style.opacity !== "" && Number(style.opacity) <= 0.05;
      const onTop = (Number(style.zIndex) || 0) >= 2147483000;
      if (transparent || onTop) { element.remove(); }
    }
  };

  // An advertisement card whose picture we refused to load is still an
  // advertisement.
  //
  // A player script can inject the card's markup without a separate request.
  // Blocking its pictures and trackers still leaves the frame behind, the empty
  // white box the viewer still sees. A failed image is therefore the signal:
  // the element that framed it goes too. It costs nothing, and unlike a
  // selector it does not stop working when the domain rotates tomorrow.
  const removeCardAround = (element) => {
    let candidate = element.parentElement;
    for (let depth = 0; candidate && depth < 4; depth++) {
      if (candidate === document.body ||
          candidate === document.documentElement ||
          candidate.querySelector("video, audio, canvas")) {
        // Never take the page itself, and never take anything holding the
        // player: a missing thumbnail must not cost the video.
        return;
      }

      const style = getComputedStyle(candidate);
      if (style.cursor === "pointer" ||
          style.position === "fixed" ||
          style.position === "absolute") {
        candidate.remove();
        return;
      }

      candidate = candidate.parentElement;
    }
  };

  const installDefenses = () => {
    try { window.open = () => null; } catch { /* frozen */ }

    globalThis.addEventListener("error", (event) => {
      const target = event.target;
      if (target instanceof HTMLImageElement ||
          target instanceof HTMLIFrameElement) {
        try { removeCardAround(target); } catch { /* best effort */ }
      }
    }, true);

    globalThis.addEventListener("click", (event) => {
      const anchor = event.target && typeof event.target.closest === "function"
        ? event.target.closest("a[href]")
        : null;
      if (!anchor) { return; }

      let url;
      try { url = new URL(anchor.href, document.baseURI); } catch { return; }
      if (!url.protocol.startsWith("http")) { return; }

      // Player pages navigate within their own site; a click that jumps to
      // another site is the advertisement.
      const host = url.hostname.toLowerCase().replace(/\.$/, "");
      const here = location.hostname.toLowerCase().replace(/\.$/, "");
      if (host !== here && !host.endsWith("." + here)) {
        event.preventDefault();
        event.stopImmediatePropagation();
      }
    }, true);
  };

  // --- scheduling -----------------------------------------------------------

  let scheduled = false;
  let lastChange = performance.now();
  let observer = null;

  const pass = () => {
    scheduled = false;
    const deadline = performance.now() + budgetMs;
    if (compiled.length > 0) { applyProcedural(deadline); }
    if (defend) {
      stripTargets(document);
      removeClickTraps(deadline);
    }

    // An embed page finishes mutating quickly. A live observer for the rest of
    // the playback session is pure cost.
    if (observer && performance.now() - lastChange > idleMs) {
      observer.disconnect();
      observer = null;
    }
  };

  const schedulePass = () => {
    lastChange = performance.now();
    if (scheduled) { return; }
    scheduled = true;
    requestAnimationFrame(pass);
  };

  const start = () => {
    injectStyle();
    pass();

    if (compiled.length === 0 && !defend) { return; }
    observer = new MutationObserver(schedulePass);
    observer.observe(document.documentElement, {
      childList: true,
      subtree: true
    });
  };

  // These three run now, not at DOMContentLoaded. The stylesheet has to be in
  // place before the body is parsed or an advertisement box is laid out and
  // painted before anything hides it — hiding it afterwards is a flash of the
  // exact content we were asked to remove. The defences have to be installed
  // before the page's own scripts capture window.open. Only the procedural
  // pass, which needs elements to walk, waits for the DOM.
  runScriptlets();
  if (defend) { installDefenses(); }
  if (!injectStyle()) {
    // At document creation even <html> may not exist yet. Keep request and
    // popup defenses active while waiting only for the root to be parsed.
    const rootObserver = new MutationObserver(() => {
      if (injectStyle()) { rootObserver.disconnect(); }
    });
    rootObserver.observe(document, { childList: true });
  }

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", start, { once: true });
  } else {
    start();
  }

  globalThis[marker] = { pass, schedulePass };
})();
