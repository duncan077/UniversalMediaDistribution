import assert from "node:assert/strict";
import {readdirSync, readFileSync} from "node:fs";
import test from "node:test";
import {catalogGroup, groupMedia} from "../src/catalog-types.ts";
import {
  isHttpsUrl,
  mediaUrl,
  nativeSourceUrl,
  remoteUrl,
  safeEpubImage,
  safeMediaClass,
} from "../src/safety.ts";
import {
  clearProfilePrefs,
  disabledProvidersForProfile,
  getProfilePref,
  preferredProvidersForProfile,
  profilePrefKey,
  providerEnabled,
  setProfilePref,
} from "../src/profile-prefs.ts";

function memoryStorage(initial = {}) {
  const map = new Map(Object.entries(initial));
  return {
    get length() { return map.size; },
    key(index) { return [...map.keys()][index] ?? null; },
    getItem(key) { return map.has(key) ? map.get(key) : null; },
    setItem(key, value) { map.set(key, String(value)); },
    removeItem(key) { map.delete(key); },
    snapshot() { return Object.fromEntries(map); },
  };
}

test("catalog filters preserve the type when opening same-name books", () => {
  assert.equal(catalogGroup("book", "book.manga"), "manga");
  assert.equal(catalogGroup("book", "book.manhwa"), "manga");
  assert.equal(catalogGroup("book", "book.light_novel"), "prose");
  assert.equal(catalogGroup("book", "book.comic"), "comic");
  assert.equal(groupMedia("prose"), "book");
});

test("remoteUrl rejects credentials and non-HTTP(S) schemes", () => {
  assert.equal(remoteUrl("https://example.com/poster.jpg"), "https://example.com/poster.jpg");
  assert.equal(remoteUrl("http://127.0.0.1:9005/a.png"), "http://127.0.0.1:9005/a.png");
  assert.equal(remoteUrl("https://user:pass@example.com/poster.jpg"), null);
  assert.equal(remoteUrl("https://user@example.com/poster.jpg"), null);
  assert.equal(remoteUrl("javascript:alert(1)"), null);
  assert.equal(remoteUrl("data:image/png;base64,AAAA"), null);
  assert.equal(remoteUrl("asset://localhost/file.mp4"), null);
  assert.equal(remoteUrl(null), null);
});

test("mediaUrl allows HTTP(S), asset, and umdstream URLs without credentials", () => {
  assert.equal(mediaUrl("https://cdn.example.com/video.m3u8"), "https://cdn.example.com/video.m3u8");
  assert.equal(mediaUrl("http://asset.localhost/C%3A%5Cvideo.mp4"), "http://asset.localhost/C%3A%5Cvideo.mp4");
  assert.equal(mediaUrl("http://umdstream.localhost/1/0"), "http://umdstream.localhost/1/0");
  assert.equal(mediaUrl("asset://localhost/%2Ftmp%2Fbook.pdf"), "asset://localhost/%2Ftmp%2Fbook.pdf");
  assert.equal(mediaUrl("umdstream://localhost/stream/1"), "umdstream://localhost/stream/1");
  assert.equal(mediaUrl("https://secret:token@cdn.example.com/v.mp4"), null);
  assert.equal(mediaUrl("asset://user@localhost/tmp/v.mp4"), null);
  assert.equal(mediaUrl("file:///etc/passwd"), null);
  assert.equal(mediaUrl("javascript:void(0)"), null);
});

test("safeEpubImage only accepts non-SVG data:image/ URIs", () => {
  assert.equal(safeEpubImage("data:image/png;base64,iVBORw0KGgo="), "data:image/png;base64,iVBORw0KGgo=");
  assert.equal(safeEpubImage("DATA:IMAGE/JPEG;base64,/9j/4AAQ"), "DATA:IMAGE/JPEG;base64,/9j/4AAQ");
  assert.equal(safeEpubImage("data:image/svg+xml;base64,PHN2Zz48L3N2Zz4="), null);
  assert.equal(safeEpubImage("data:image/svg+xml,<svg onload=alert(1)>"), null);
  assert.equal(safeEpubImage("https://example.com/image.png"), null);
  assert.equal(safeEpubImage("javascript:alert(1)"), null);
});

test("isHttpsUrl matches case-insensitively", () => {
  assert.equal(isHttpsUrl("https://example.com"), true);
  assert.equal(isHttpsUrl("HTTPS://example.com/live"), true);
  assert.equal(isHttpsUrl("  HtTpS://example.com "), true);
  assert.equal(isHttpsUrl("http://example.com"), false);
  assert.equal(isHttpsUrl("javascript:https://example.com"), false);
});

test("safeMediaClass allowlists known media types", () => {
  assert.equal(safeMediaClass("movie"), "movie");
  assert.equal(safeMediaClass("BOOK"), "book");
  assert.equal(safeMediaClass(" audio "), "audio");
  assert.equal(safeMediaClass("movie injected-class"), "other");
  assert.equal(safeMediaClass("__proto__"), "other");
  assert.equal(safeMediaClass(undefined), "other");
});

test("nativeSourceUrl converts local and Windows UNC asset paths properly", () => {
  assert.equal(
    nativeSourceUrl("http://asset.localhost/C%3A%5CUsers%5Ctest%20user%5Cvideo.mp4"),
    "file:///C:/Users/test%20user/video.mp4",
  );
  assert.equal(
    nativeSourceUrl("http://asset.localhost/%5C%5Cserver%5Cshare%5Cmy%20file.mp4"),
    "file://server/share/my%20file.mp4",
  );
  assert.equal(
    nativeSourceUrl("http://asset.localhost/%5C%5C%3F%5CUNC%5Cnas%5Cmovies%5Cclip.mkv"),
    "file://nas/movies/clip.mkv",
  );
  assert.equal(
    nativeSourceUrl("asset://localhost/%2Fhome%2Fuser%2Ffilm.mp4"),
    "file:///home/user/film.mp4",
  );
  assert.equal(
    nativeSourceUrl("https://example.com/stream.m3u8"),
    "https://example.com/stream.m3u8",
  );
});

test("per-profile preferences isolate profiles, preserve legacy fallback, and clean up on delete", () => {
  const storage = memoryStorage({
    "umd-ui-language": "en",
    "umd-hide-adult": "true",
    "provider-favorite-jkanime": "true",
  });

  // Fallback read of legacy global key when profile-scoped key is unset.
  assert.equal(getProfilePref("p1", "umd-hide-adult", storage), "true");
  assert.deepEqual(preferredProvidersForProfile("p1", storage), ["jkanime"]);

  // Writes target only the active profile and do not leak to other profiles.
  setProfilePref("p1", "umd-hide-adult", "false", storage);
  setProfilePref("p1", "provider-favorite-jkanime", "false", storage);
  setProfilePref("p1", "provider-favorite-manhwaweb", "true", storage);
  setProfilePref("p1", "umd-video-engine", "vlc", storage);
  setProfilePref("p1", "reader-speed", "2", storage);

  assert.equal(getProfilePref("p1", "umd-hide-adult", storage), "false");
  assert.equal(getProfilePref("p2", "umd-hide-adult", storage), "true");
  assert.deepEqual(preferredProvidersForProfile("p1", storage), ["manhwaweb"]);
  assert.deepEqual(preferredProvidersForProfile("p2", storage), ["jkanime"]);
  assert.equal(getProfilePref("p1", "umd-video-engine", storage), "vlc");
  assert.equal(getProfilePref("p2", "umd-video-engine", storage), null);
  assert.equal(storage.getItem(profilePrefKey("p1", "reader-speed")), "2");

  // Deleting p1 removes all p1 keys without touching global or p2 keys.
  setProfilePref("p2", "reader-speed", "3", storage);
  clearProfilePrefs("p1", storage);
  assert.equal(storage.getItem(profilePrefKey("p1", "umd-hide-adult")), null);
  assert.equal(storage.getItem(profilePrefKey("p1", "umd-video-engine")), null);
  assert.equal(storage.getItem(profilePrefKey("p1", "reader-speed")), null);
  assert.equal(storage.getItem(profilePrefKey("p2", "reader-speed")), "3");
  assert.equal(storage.getItem("umd-ui-language"), "en");
});

test("provider availability respects profile overrides and keeps priority when re-enabled", () => {
  const storage = memoryStorage({
    "provider-enabled-jkanime": "false",
    "provider-favorite-jkanime": "true",
  });
  assert.equal(providerEnabled("p1", "manhwaweb", storage), true);
  assert.deepEqual(preferredProvidersForProfile("p1", storage), []);

  setProfilePref("p1", "provider-enabled-jkanime", "true", storage);
  setProfilePref("p1", "provider-enabled-json-example", "false", storage);
  assert.deepEqual(disabledProvidersForProfile("p1", storage), ["json-example"]);
  assert.deepEqual(disabledProvidersForProfile("p2", storage), ["jkanime"]);
  assert.deepEqual(preferredProvidersForProfile("p1", storage), ["jkanime"]);
});

test("locale.ts ui() guards prototype keys with Object.hasOwn and translates English strings", async () => {
  const prevStorage = globalThis.localStorage;
  const prevNav = Object.getOwnPropertyDescriptor(globalThis, "navigator");
  globalThis.localStorage = memoryStorage({"umd-ui-language": "en"});
  Object.defineProperty(globalThis, "navigator", {
    configurable: true,
    value: {languages: ["en-US"], language: "en-US"},
  });
  try {
    const {ui, localizeDiagnostic} = await import("../src/locale.ts");
    for (const protoKey of ["toString", "constructor", "valueOf", "hasOwnProperty", "__proto__"]) {
      assert.equal(ui(protoKey), protoKey);
    }
    assert.equal(ui("Ver en UMD"), "Watch in UMD");
    assert.equal(ui("Audio"), "Audio");
    assert.equal(ui("El filtro +18 ocultó esta ficha."), "The +18 filter hid this title.");
    assert.equal(ui("En progreso"), "In progress");
    assert.equal(ui("Pista ") + "3", "Track 3");
    assert.equal(ui("Capítulo de audio ") + "2", "Audio chapter 2");
    assert.equal(ui("Se importó 1 título."), "Imported 1 title.");
    assert.equal(ui("Se importaron {0} títulos.", 4), "Imported 4 titles.");
    assert.equal(localizeDiagnostic("El archivo no es un instalador MSI válido."), "This file is not a valid MSI installer.");
    assert.equal(localizeDiagnostic("toString"), "toString");
  } finally {
    if (prevStorage === undefined) delete globalThis.localStorage;
    else globalThis.localStorage = prevStorage;
    if (prevNav) Object.defineProperty(globalThis, "navigator", prevNav);
  }
});

test("every dialog.showModal() in desktop src is guarded against already-open dialogs", () => {
  const srcDir = new URL("../src/", import.meta.url);
  for (const file of readdirSync(srcDir)) {
    if (!file.endsWith(".ts")) continue;
    const text = readFileSync(new URL(file, srcDir), "utf8");
    const lines = text.split(/\r?\n/);
    for (let i = 0; i < lines.length; i++) {
      const line = lines[i];
      if (!line.includes(".showModal()")) continue;
      const context = lines.slice(Math.max(0, i - 3), i + 1).join(" ");
      assert.match(context, /if\s*\(\s*!?\w+\.open\s*\)/, `Unchecked showModal() in ${file}:${i + 1}`);
    }
  }
});
