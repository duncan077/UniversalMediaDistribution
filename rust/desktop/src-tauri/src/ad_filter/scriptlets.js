// Scriptlet library.
//
// These are our own implementations, written against the names and behaviours
// filter lists invoke. A list says "+js(set-constant, adsEnabled, false)"; what
// that does is defined here.
//
// Every scriptlet is defensive: an unknown name is ignored, a throw is
// swallowed, and nothing here may be the reason a page stops working. A page
// that breaks in an embedded player is a video that does not play, and the
// viewer has no way to tell why.
(() => {
  const marker = "__umdScriptlets";
  if (globalThis[marker]) { return; }

  const noop = () => {};

  const parseValue = (raw) => {
    switch (raw) {
      case "false": return false;
      case "true": return true;
      case "null": return null;
      case "undefined": return undefined;
      case "noopFunc": return noop;
      case "trueFunc": return () => true;
      case "falseFunc": return () => false;
      case "emptyArr": return [];
      case "emptyObj": return {};
      case "": return "";
      default: {
        const numeric = Number(raw);
        return Number.isNaN(numeric) || raw.trim() === "" ? raw : numeric;
      }
    }
  };

  // Walks a dotted path, defining the intermediate objects a page has not
  // created yet so the trap is in place before the page looks.
  const defineOnPath = (root, path, describe) => {
    const parts = String(path).split(".");
    const last = parts.pop();
    let owner = root;
    for (const part of parts) {
      const next = owner[part];
      if (next === undefined || next === null) {
        const created = {};
        try {
          Object.defineProperty(owner, part, {
            configurable: true,
            enumerable: false,
            value: created,
            writable: true
          });
        } catch { return; }
        owner = created;
      } else if (typeof next === "object" || typeof next === "function") {
        owner = next;
      } else {
        return;
      }
    }

    try {
      Object.defineProperty(owner, last, describe());
    } catch {
      // Some properties are non-configurable. Leaving the page as it is beats
      // throwing inside its own script.
    }
  };

  const matcher = (pattern) => {
    if (!pattern || pattern === "*") { return () => true; }
    if (pattern.length > 2 && pattern[0] === "/" && pattern.endsWith("/")) {
      try {
        const expression = new RegExp(pattern.slice(1, -1));
        return (value) => expression.test(value);
      } catch { return () => false; }
    }

    const negated = pattern[0] === "!";
    const needle = negated ? pattern.slice(1) : pattern;
    return (value) => String(value).includes(needle) !== negated;
  };

  const registry = {
    "set-constant": (path, raw) => {
      const value = parseValue(raw);
      defineOnPath(globalThis, path, () => ({
        configurable: false,
        get: () => value,
        set: noop
      }));
    },

    "abort-on-property-read": (path) => {
      defineOnPath(globalThis, path, () => ({
        configurable: false,
        get() { throw new ReferenceError(path); },
        set: noop
      }));
    },

    "abort-on-property-write": (path) => {
      defineOnPath(globalThis, path, () => ({
        configurable: false,
        get: () => undefined,
        set() { throw new ReferenceError(path); }
      }));
    },

    "abort-current-script": (path, search) => {
      const matches = matcher(search);
      defineOnPath(globalThis, path, () => ({
        configurable: false,
        get() {
          const script = document.currentScript;
          if (script && matches(script.textContent || script.src || "")) {
            throw new ReferenceError(path);
          }

          return undefined;
        },
        set: noop
      }));
    },

    "prevent-setTimeout": (search, delay) =>
      preventTimer("setTimeout", search, delay),

    "prevent-setInterval": (search, delay) =>
      preventTimer("setInterval", search, delay),

    "prevent-window-open": () => {
      try { globalThis.open = () => null; } catch { /* frozen */ }
    },

    "no-fetch-if": (search) => {
      const matches = matcher(search);
      const original = globalThis.fetch;
      if (typeof original !== "function") { return; }
      globalThis.fetch = function (input, init) {
        const target = typeof input === "string"
          ? input
          : input && input.url ? input.url : "";
        return matches(target)
          ? Promise.resolve(new Response("", { status: 200 }))
          : original.call(this, input, init);
      };
    },

    "no-xhr-if": (search) => {
      const matches = matcher(search);
      const open = XMLHttpRequest.prototype.open;
      const send = XMLHttpRequest.prototype.send;
      XMLHttpRequest.prototype.open = function (method, url, ...rest) {
        this.__umdSuppressed = matches(url);
        return open.call(this, method, url, ...rest);
      };
      XMLHttpRequest.prototype.send = function (body) {
        if (this.__umdSuppressed) { return; }
        return send.call(this, body);
      };
    },

    "json-prune": (paths) => {
      const targets = String(paths || "").split(/\s+/).filter(Boolean);
      if (targets.length === 0) { return; }
      const original = JSON.parse;
      JSON.parse = function (...args) {
        const parsed = original.apply(this, args);
        for (const target of targets) { prune(parsed, target.split(".")); }
        return parsed;
      };
    },

    "remove-attr": (attributes, selector) =>
      schedule(() => {
        const names = String(attributes || "").split(/[|\s]+/).filter(Boolean);
        for (const element of query(selector || "*")) {
          for (const name of names) { element.removeAttribute(name); }
        }
      }),

    "remove-class": (classes, selector) =>
      schedule(() => {
        const names = String(classes || "").split(/[|\s]+/).filter(Boolean);
        for (const element of query(selector || "*")) {
          element.classList.remove(...names);
        }
      }),

    "nowebrtc": () => {
      const constructors = [
        "RTCPeerConnection",
        "webkitRTCPeerConnection",
        "mozRTCPeerConnection"
      ];
      for (const name of constructors) {
        if (typeof globalThis[name] === "function") {
          try {
            globalThis[name] = function () {
              throw new Error("WebRTC is disabled");
            };
          } catch { /* frozen */ }
        }
      }
    },

    "noeval-if": (search) => {
      const matches = matcher(search);
      const original = globalThis.eval;
      if (typeof original !== "function") { return; }
      globalThis.eval = function (source) {
        return matches(String(source)) ? undefined : original.call(this, source);
      };
    }
  };

  const preventTimer = (name, search, delay) => {
    const matches = matcher(search);
    const wanted = delay === undefined || delay === "" ? null : Number(delay);
    const original = globalThis[name];
    if (typeof original !== "function") { return; }
    globalThis[name] = function (handler, timeout, ...rest) {
      const source = typeof handler === "function"
        ? handler.toString()
        : String(handler);
      if (matches(source) && (wanted === null || wanted === Number(timeout))) {
        return 0;
      }

      return original.call(this, handler, timeout, ...rest);
    };
  };

  const prune = (value, path) => {
    if (!value || typeof value !== "object") { return; }
    if (Array.isArray(value)) {
      for (const item of value) { prune(item, path); }
      return;
    }

    if (path.length === 1) { delete value[path[0]]; return; }
    prune(value[path[0]], path.slice(1));
  };

  const query = (selector) => {
    try { return document.querySelectorAll(selector); } catch { return []; }
  };

  const schedule = (action) => {
    const run = () => { try { action(); } catch { /* best effort */ } };
    if (document.readyState === "loading") {
      document.addEventListener("DOMContentLoaded", run, { once: true });
    } else {
      run();
    }
  };

  globalThis[marker] = {
    run(name, args) {
      const scriptlet = registry[name];
      if (typeof scriptlet !== "function") { return false; }
      try {
        scriptlet(...args);
        return true;
      } catch {
        // A scriptlet that fails is one missing defence, not a broken page.
        return false;
      }
    }
  };
})();
