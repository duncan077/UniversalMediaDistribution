import {ui, displayTitle, setupLanguage, localizeDiagnostic} from "./locale";
import {metadataControls, setupMetadataSettings} from "./metadata";
import {activeProfileId, invokeProfile, windowProfile, writeProfilePref, getProfilePref, providerEnabled} from "./profile-context";
import {invoke} from "./bridge";
import "@fontsource-variable/archivo/wdth.css";
import "./style.css";
import {contentControls, flushProgress, formatTime, playDownload, type EntryListView} from "./player";
import {remoteUrl, safeEpubImage, safeMediaClass} from "./safety";
import {attachArtworkImage} from "./artwork";
import {virtualCatalog} from "./catalog-grid";
import {setupDownloads} from "./downloads";
import {setupAccount} from "./account";
import {setupProvider, hiddenTitle} from "./provider";
import {setupNavigation} from "./navigation";
import {setupStreams} from "./streams";
import {inlineListMembership, setupLists, type MediaList} from "./lists";
import {inlineFriendActivity, setupFriends} from "./friends";
import {setupParty, type ContentUnit} from "./party";
import {setupProfiles} from "./profiles";
import {setupUpdates} from "./updates";
import {activePage, goBack, isPageActive, navigateTo, onPageLeave, setCatalogNavId} from "./router";
import {groupMedia} from "./catalog-types";

export interface CatalogItem {
  syncId: string;
  name: string;
  mediaType: string;
  subtype: string;
  description?: string | null;
  thumbnailRef?: string | null;
  releaseYear?: number | null;
  releaseDate?: string | null;
  publicationStatus?: string | null;
  genres?: string[];
  categories?: string[];
  alternativeNames?: { name?: string; language?: string | null; aliasType?: string | null }[];
  contributors?: { name?: string; kind?: string | null; role?: string | null; url?: string | null }[];
  onlineProviders?: Record<string, string>;
  units?: unknown[];
  seasons?: unknown[];
  sources?: unknown[];
  [key: string]: unknown;
}

interface LibraryPage {
  revision: number;
  continuationToken: string | null;
  document: { items: CatalogItem[] };
  favoriteIds: string[];
  total: number;
}

interface Resume {
  item: CatalogItem;
  progress: { position: number; positionKind: string; content: ContentUnit };
}

function element<T extends HTMLElement = HTMLElement>(id: string): T {
  const result = document.getElementById(id);
  if (!result) throw new Error(`Missing element ${id}`);
  return result as T;
}

function node<K extends keyof HTMLElementTagNameMap>(tag: K, text?: string, className?: string): HTMLElementTagNameMap[K] {
  const result = document.createElement(tag);
  if (text !== undefined) result.textContent = text;
  if (className) result.className = className;
  return result;
}

const catalog = element("catalog");
const android = /Android/i.test(navigator.userAgent);
const localGrid = android ? virtualCatalog<CatalogItem>(catalog, item => item.syncId,
  item => titleCard(item, () => void showDetails(item), true)) : null;
const onlineGrid = android ? virtualCatalog<CatalogItem>(element("online-cards"), item => item.syncId,
  item => titleCard(item, () => void openOnline(item), false)) : null;
const search = element<HTMLInputElement>("search");
const media = element<HTMLSelectElement>("media-type");
const listFilter = element<HTMLSelectElement>("list-filter");
let serverAddress = "";
const serverDialog = element<HTMLDialogElement>("server-dialog");
let items: CatalogItem[] = [];
let favorites = new Set<string>();
let favoritesOnly = false;
let cursor: string | null = null;
let generation = 0;
let busy = false;
let timer: ReturnType<typeof setTimeout>;
let shelfMode = localStorage.getItem("umd-home-shelves") !== "false";

type Section = "all" | "movies" | "anime" | "books" | "audio" | "favorites";
const sections: Record<Section, { label: string; type: string; group: string }> = {
  all: { label: "Inicio", type: "", group: "" },
  movies: { label: "Cine y series", type: "movie", group: "liveAction" },
  anime: { label: "Anime", type: "movie", group: "anime" },
  books: { label: "Libros y manga", type: "book", group: "" },
  audio: { label: "Audio", type: "audio", group: "" },
  favorites: { label: "Favoritos", type: "", group: "" },
};
let section: Section = "all";
let continueList: Resume[] = [];
let resumeRequest = 0;
let featured: CatalogItem | null = null;
let featuredUnit: ContentUnit | undefined;

// Abort controller & active context for Detail page async work
let detailAbort: AbortController | null = null;
let detailSequence = 0;
let currentDetail: { item: CatalogItem; unit?: ContentUnit; entryView: EntryListView } | null = null;
const itemRefreshTimes = new Map<string, number>();

function detailProviderActive() {
  return !!document.querySelector(".player-dialog[open], #detail [data-provider-busy], #detail .entry-servers:not([hidden])");
}

onPageLeave("detail", () => {
  detailAbort?.abort();
  detailAbort = null;
  currentDetail = null;
  setTimeout(() => void flushProgress().then(refreshContinue).catch(() => {}), 0);
});

function home() {
  return section === "all" && !hasFilters();
}

function afterPaint(): Promise<void> {
  return new Promise(resolve => {
    const frame = requestAnimationFrame(() => {
      clearTimeout(fallback);
      setTimeout(resolve, 0);
    });
    const fallback = setTimeout(() => {
      cancelAnimationFrame(frame);
      resolve();
    }, 100);
  });
}

function wireShelfNav(host: HTMLElement) {
  const scroller = host.querySelector<HTMLElement>(".shelf-row, .catalog");
  const prev = host.querySelector<HTMLButtonElement>(".shelf-prev");
  const next = host.querySelector<HTMLButtonElement>(".shelf-next");
  if (!scroller || !prev || !next) return;
  const step = () => Math.max(240, scroller.clientWidth * 0.78);
  prev.onclick = () => scroller.scrollBy({ left: -step(), behavior: "smooth" });
  next.onclick = () => scroller.scrollBy({ left: step(), behavior: "smooth" });
  const sync = () => {
    const scrollable = scroller.scrollWidth > scroller.clientWidth + 4;
    prev.hidden = !scrollable || scroller.scrollLeft <= 2;
    next.hidden = !scrollable || scroller.scrollLeft + scroller.clientWidth >= scroller.scrollWidth - 2;
  };
  scroller.onscroll = sync;
  requestAnimationFrame(sync);
}

function progressSummaryText(entry: Resume): string {
  const { position, positionKind, content } = entry.progress;
  const unit = content.displayNumber
    ? ui(content.unitKind === "episode" ? "Episodio" : content.unitKind === "track" ? "Pista" : "Capítulo") + " " + content.displayNumber
    : "";
  const pos = positionKind === "page" ? ui("Pág. {0}", Math.floor(position)) : formatTime(position);
  return [unit, pos].filter(Boolean).join(" · ");
}

async function refreshContinue() {
  const request = ++resumeRequest;
  const { activeId } = await windowProfile();
  const result = await invokeProfile<Resume[]>("continue_items", {
    hideAdult: getProfilePref(activeId, "umd-hide-adult") === "true",
    limit: 12,
  });
  if (request === resumeRequest) {
    continueList = result;
    if (home()) {
      renderFeature();
      renderContinueShelf();
    }
  }
}

function renderContinueShelf() {
  const shelf = element("continue-shelf");
  const visible = continueList.filter(r => !hiddenTitle(r.item));
  shelf.hidden = !home() || visible.length <= 1;
  if (shelf.hidden) return;
  element("continue-count").textContent = String(visible.length);
  const row = element("continue-cards");
  row.replaceChildren(
    ...visible.map(entry =>
      titleCard(entry.item, () => void showDetails(entry.item, entry.progress.content), true, progressSummaryText(entry)),
    ),
  );
  wireShelfNav(shelf.querySelector(".shelf-host")!);
}

function renderFeature() {
  const hero = element("feature");
  hero.hidden = !home();
  element("shortcuts").hidden = !home();
  element("home-livetv-card").hidden = !home();
  if (hero.hidden) return;
  const latest = continueList.find(r => !hiddenTitle(r.item)) ?? null;
  featured = latest?.item ?? items[0] ?? null;
  featuredUnit = latest?.progress.content;
  if (serverAddress && !featured) { hero.hidden = true; return; }
  const art = hero.querySelector<HTMLElement>(".feature-art")!;
  const url = featured && safeImage(featured.thumbnailRef);
  art.replaceChildren();
  hero.classList.toggle("has-art", !!url);
  hero.classList.toggle("first-run", !featured);
  if (featured) hero.dataset.tone = tone(featured); else delete hero.dataset.tone;
  if (url) {
    const img = node("img");
    img.alt = "";
    attachArtworkImage(img, url, Object.keys(featured?.onlineProviders ?? {}), () => {
      img.remove();
      hero.classList.remove("has-art");
    });
    art.append(img);
  }
  element("feature-label").textContent = latest ? ui("Continuar") : featured ? mediaLabel(featured) : ui("Tu biblioteca");
  element("feature-title").textContent = featured ? displayTitle(featured) : ui("Tu biblioteca empieza acá");
  let description = featured?.description || "";
  if (latest) {
    const { position, positionKind, content } = latest.progress;
    const unit = content.displayNumber ? ui(content.unitKind === "episode" ? "Episodio" : content.unitKind === "track" ? "Pista" : "Capítulo") + " " + content.displayNumber : "";
    description = [unit, positionKind === "page" ? ui("Continuar en la página {0}", Math.floor(position)) : ui("Continuar desde {0}", formatTime(position))].filter(Boolean).join(" · ");
  }
  element("feature-description").textContent = featured ? description : ui("Importá un catálogo JSON de UMD o conectá tu servidor para agregar tus títulos.");
  const actionBtn = element("feature-action");
  actionBtn.textContent = latest ? ui("Continuar") : featured ? ui("Ver ficha") : ui("Conectar servidor");
  actionBtn.dataset.icon = latest ? "play" : featured ? "movies" : "server";
  element("empty-import").hidden = !!featured;
}

function selectSection(id: Section) {
  clearTimeout(timer);
  detailAbort?.abort();
  search.value = "";
  element("advanced").querySelectorAll<HTMLInputElement | HTMLSelectElement>("input,select").forEach(field => {
    if (field instanceof HTMLInputElement && field.type === "checkbox") field.checked = false;
    else field.value = "";
  });
  media.value = sections[id].type;
  element<HTMLSelectElement>("subtype-group").value = sections[id].group;
  section = id;
  favoritesOnly = id === "favorites";
  element<HTMLInputElement>("favorites-only").checked = favoritesOnly;
  syncSection();
  navigateTo("catalog", { resetStack: true });
  showFilters(false);
  void load(false, true);
}

function showFilters(open: boolean) {
  element("advanced").hidden = !open;
  element("filters-toggle").setAttribute("aria-expanded", String(open));
}

function syncFilterCount() {
  search.placeholder = ui(favoritesOnly ? "Buscar en favoritos" : "Buscar en tu biblioteca");
  search.setAttribute("aria-label", ui(favoritesOnly ? "Buscar en favoritos" : "Buscar en el catálogo"));
  const group = element<HTMLSelectElement>("subtype-group").value;
  const beyond = [
    ...Object.entries(filters()).filter(([key]) => key !== "subtypeGroup").map(([, value]) => value),
    media.value !== sections[section].type && media.value,
    group !== sections[section].group && group,
    listFilter.value,
    favoritesOnly && section !== "favorites",
  ].filter(Boolean).length;
  const badge = element("filters-count");
  badge.textContent = String(beyond);
  badge.hidden = !beyond;
}

function syncSection() {
  element("heading").textContent = ui(sections[section].label);
  setCatalogNavId(section);
}

function setView(list: boolean) {
  catalog.classList.toggle("list-view", list);
  element("grid-view").setAttribute("aria-pressed", String(!list));
  element("list-view").setAttribute("aria-pressed", String(list));
}

let statusTimer: ReturnType<typeof setTimeout>;
function status(message: string, error = false, keep = error) {
  const output = element("status");
  clearTimeout(statusTimer);
  output.textContent = message;
  output.classList.toggle("error", error);
  output.setAttribute("role", error ? "alert" : "status");
  if (message && !keep) statusTimer = setTimeout(() => { output.textContent = ""; }, Math.max(8000, message.length * 90));
}
element("status").addEventListener("click", () => status(""));

function filters() {
  const value = (id: string) => element<HTMLInputElement | HTMLSelectElement>(id).value.trim() || null;
  const yearInput = element<HTMLInputElement>("year");
  const rawYear = yearInput.value.trim();
  const year = rawYear && yearInput.checkValidity() ? Number(rawYear) : null;
  return {
    genre: value("genre"),
    category: value("category"),
    provider: value("distributor"),
    status: value("publication"),
    subtypeGroup: value("subtype-group"),
    year,
  };
}

function hasFilters() {
  return !!(search.value || media.value || listFilter.value || (favoritesOnly && section !== "favorites") || Object.values(filters()).some(Boolean));
}

function setBusy(value: boolean) {
  busy = value;
  element("activity").hidden = !value;
  for (const id of ["import", "empty-import", "connect", "export", "server-submit"]) {
    element<HTMLButtonElement>(id).disabled = value;
  }
}

async function action(task: () => Promise<void>) {
  if (busy) return;
  setBusy(true);
  status("");
  try {
    await task();
  } catch (error) {
    status(String(error), true);
  } finally {
    setBusy(false);
  }
}

async function refreshListFilterOptions() {
  try {
    applyListFilterOptions(await invokeProfile<MediaList[]>("personal_lists"));
  } catch {
    element("list-filter-wrap").hidden = true;
  }
}

function applyListFilterOptions(lists: MediaList[]) {
  const prev = listFilter.value;
  listFilter.replaceChildren(new Option(ui("Todas las listas"), ""));
  for (const {id, name, items} of lists) listFilter.add(new Option(`${name} (${items.length})`, id));
  if (lists.some(l => l.id === prev)) listFilter.value = prev;
  element("list-filter-wrap").hidden = lists.length === 0;
}

let recentRows: {added:CatalogItem[];updated:CatalogItem[]} = {added:[],updated:[]};
interface HomeBootstrap {
  page: LibraryPage;
  recent: typeof recentRows;
  continuing: Resume[];
  lists: MediaList[];
  serverAddress: string;
}
let recentRevision = -1;
let catalogWaiting = false;
let catalogLoad: Promise<void> | undefined;
let startupApplied = false;

function showCatalogLoading(loading: boolean) {
  catalogWaiting = loading;
  element("catalog-loading").hidden = !loading;
  catalog.setAttribute("aria-busy", String(loading));
  if (!loading) return;
  items = [];
  cursor = null;
  localGrid?.disable();
  catalog.replaceChildren();
  catalog.hidden = false;
  for (const id of ["feature", "shortcuts", "home-livetv-card", "home-shelves", "continue-shelf", "online-shelf", "empty", "more", "shelves-toggle"]) {
    element(id).hidden = true;
  }
  ++onlineSequence;
  onlineKey = "";
  onlineBusy = false;
  element("collection-bar").hidden = false;
  element("collection-title").textContent = ui("Títulos");
  element("result-count").textContent = "";
  syncFilterCount();
}

function load(append = false, transition = false, bootstrap = false) {
  const pending = loadPage(append, transition, bootstrap);
  if (!append) catalogLoad = pending;
  return pending;
}

async function loadPage(append: boolean, transition: boolean, bootstrap: boolean) {
  const request = ++generation;
  if (transition) {
    showCatalogLoading(true);
    await afterPaint();
    if (request !== generation) return;
  }
  if (!append && !bootstrap) {
    refreshOnline();
    refreshFilteredProviders();
  }
  const more = element<HTMLButtonElement>("more");
  more.disabled = true;
  try {
    const query = {
        ...filters(),
        q: search.value.trim(),
        mediaType: media.value || null,
        favoritesOnly,
        listId: listFilter.value || null,
        cursor: append ? cursor : null,
        limit: 50,
        summary: true,
    };
    const initialResume = resumeRequest;
    const initial = bootstrap ? await invokeProfile<HomeBootstrap>("home_bootstrap", {query}) : null;
    const page = initial?.page ?? await invokeProfile<LibraryPage>("library", {query});
    if (request !== generation) return;
    if (initial) {
      startupApplied = true;
      recentRows = initial.recent;
      recentRevision = page.revision;
      if (resumeRequest === initialResume) continueList = initial.continuing;
      applyListFilterOptions(initial.lists);
      if (!serverAddress) serverAddress = initial.serverAddress;
    }
    const shown = append ? items.length : 0;
    items = append ? [...items, ...page.document.items] : page.document.items;
    favorites = new Set(page.favoriteIds);
    syncAllFavoriteButtons();
    cursor = page.continuationToken;
    element("result-count").textContent = `${page.total} ${page.total === 1 ? ui("título") : ui("títulos")}`;
    syncFilterCount();
    showCatalogLoading(false);
    render(shown);
    if (onlineKey && !home() && onlineItems.length) redrawOnline();
    if (home() && recentRevision !== page.revision) {
      await invoke<typeof recentRows>("home_recent").then(rows => {
        if (generation !== request) return;
        recentRows = rows; recentRevision = page.revision; renderHomeShelves(items.filter(i => !hiddenTitle(i)));
      });
    }
  } catch (error) {
    if (request === generation) {
      showCatalogLoading(false);
      status(String(error), true);
    }
  } finally {
    if (request === generation) more.disabled = false;
  }
}

function safeImage(value: unknown): string | null {
  const remote = remoteUrl(value);
  if (remote) return remote;
  const embedded = safeEpubImage(value);
  if (embedded) return embedded;
  if (typeof value === "string" && serverAddress) {
    const trimmed = value.trim();
    if (trimmed && !trimmed.startsWith("//") && !trimmed.includes("\\") && !trimmed.split("/").includes("..")) {
      try {
        const resolved = new URL(trimmed, serverAddress);
        if ((resolved.protocol === "http:" || resolved.protocol === "https:") && !resolved.username && !resolved.password) {
          return resolved.href;
        }
      } catch {
        return null;
      }
    }
  }
  return null;
}

function tone(item: CatalogItem) {
  return String(Array.from(item.syncId).reduce((h, c) => (h * 31 + c.charCodeAt(0)) >>> 0, 0) % 6);
}

function artwork(item: CatalogItem, showBadge = false) {
  const cover = node("div", undefined, `cover media-${safeMediaClass(item.mediaType)}`);
  cover.dataset.tone = tone(item);
  cover.append(node("span", displayTitle(item), "cover-initial"), node("span", mediaLabel(item), "cover-kind"));
  const url = safeImage(item.thumbnailRef);
  if (url) {
    const image = node("img");
    image.alt = "";
    image.loading = "lazy";
    attachArtworkImage(image, url, Object.keys(item.onlineProviders ?? {}), () => image.remove());
    cover.append(image);
  }
  if (showBadge) {
    const pill = node("span", mediaLabel(item), "cover-pill");
    cover.append(pill);
  }
  return cover;
}

function mediaLabel(item: CatalogItem) {
  const type = item.mediaType.toLowerCase();
  const subtype = item.subtype.replace(/^(movie|book)\./, "").toLowerCase();
  if (subtype.startsWith("anime")) return ui("Anime");
  if (subtype === "manga") return ui("Manga");
  return type === "movie" ? ui("Cine y series") : type === "book" ? ui("Libros") : ui("Audio");
}

function statusLabel(raw: string | null | undefined): string {
  switch ((raw ?? "").toLowerCase()) {
    case "ongoing": return ui("En curso");
    case "completed": return ui("Finalizado");
    case "paused": return ui("En pausa");
    case "announced": return ui("Anunciado");
    case "cancelled": return ui("Cancelado");
    default: return "";
  }
}

function syncFavoriteButton(button: HTMLButtonElement, title: string, id: string, withText = false) {
  const enabled = favorites.has(id);
  button.dataset.icon = enabled ? "heart-filled" : "heart";
  button.classList.toggle("selected", enabled);
  if (withText) {
    button.textContent = enabled ? ui("En favoritos") : ui("Agregar a favoritos");
  }
  button.setAttribute("aria-label", enabled ? ui("Quitar {0} de favoritos", title) : ui("Agregar {0} a favoritos", title));
  button.setAttribute("aria-pressed", String(enabled));
}

function syncAllFavoriteButtons() {
  for (const control of document.querySelectorAll<HTMLButtonElement>("button[data-favorite-id]")) {
    const id = control.dataset.favoriteId;
    if (!id) continue;
    syncFavoriteButton(control, control.dataset.favoriteTitle ?? "", id, control.dataset.withText === "true");
  }
}

function favoriteButton(item: CatalogItem, withText = false) {
  const button = node("button", undefined, withText ? "favorite-pill" : "favorite");
  const title = displayTitle(item);
  button.type = "button";
  button.dataset.favoriteId = item.syncId;
  button.dataset.favoriteTitle = title;
  if (withText) button.dataset.withText = "true";
  syncFavoriteButton(button, title, item.syncId, withText);
  button.addEventListener("click", async () => {
    const nextEnabled = !favorites.has(item.syncId);
    button.disabled = true;
    try {
      await invokeProfile("favorite", { id: item.syncId, enabled: nextEnabled });
      if (nextEnabled) favorites.add(item.syncId); else favorites.delete(item.syncId);
      for (const control of document.querySelectorAll<HTMLButtonElement>("button[data-favorite-id]")) {
        if (control.dataset.favoriteId === item.syncId) {
          syncFavoriteButton(control, title, item.syncId, control.dataset.withText === "true");
        }
      }
      if (favoritesOnly) await load();
    } catch (error) {
      status(String(error), true);
    } finally {
      button.disabled = false;
    }
  });
  return button;
}

type ShelfUnit = { kind: string; number: string; later: boolean };
const queuedBadges = new Map<string, HTMLElement[]>();
let badgeTimer: ReturnType<typeof setTimeout> | undefined;

function queueUnitBadge(id: string, info: HTMLElement) {
  const cards = queuedBadges.get(id) ?? [];
  cards.push(info);
  queuedBadges.set(id, cards);
  if (badgeTimer !== undefined) return;
  badgeTimer = setTimeout(async () => {
    badgeTimer = undefined;
    const pending = new Map(queuedBadges);
    queuedBadges.clear();
    const ids = [...pending.keys()].filter(key => pending.get(key)?.some(el => el.isConnected));
    for (let start = 0; start < ids.length; start += 500) {
      const group = ids.slice(start, start + 500);
      try {
        const units = await invokeProfile<Record<string, ShelfUnit>>("shelf_units", {ids: group});
        for (const [key, unit] of Object.entries(units)) {
          if (!unit.later) continue;
          const name = ui(unit.kind === "episode" ? "Episodio" : unit.kind === "track" ? "Pista" : "Capítulo");
          const text = ui("Nuevo") + " · " + name + " " + unit.number;
          for (const card of pending.get(key) ?? []) {
            if (card.isConnected) card.append(node("span", text, "entry-badge progress"));
          }
        }
      } catch (error) { console.warn("shelf_units", error); }
    }
  }, 0);
}

export function titleCard(item: CatalogItem, onOpen: () => void, local: boolean, progressPill?: string) {
  const card = node("article", undefined, "card");
  const open = node("button", undefined, "open-title");
  open.type = "button";
  open.setAttribute("aria-label", ui("Ver {0}", displayTitle(item)));
  const cover = artwork(item, true);
  if (progressPill) {
    cover.append(node("span", progressPill, "cover-progress-pill"));
  }
  open.append(cover);
  const info = node("div", undefined, "card-info");
  info.append(
    node("h3", displayTitle(item)),
    node("p", [mediaLabel(item), item.releaseYear ?? item.releaseDate?.slice(0, 4)].filter(Boolean).join(" · ")),
  );
  if (local) queueUnitBadge(item.syncId, info);
  open.append(info);
  open.addEventListener("click", onOpen);
  card.append(open);
  if (local) card.append(favoriteButton(item));
  return card;
}

interface OnlinePage { items: CatalogItem[]; next: string | null; recent: boolean }
let onlineKey = "";
let onlineSequence = 0;
let previewSequence = 0;
let onlineItems: CatalogItem[] = [];
let onlineNext: string | null = null;
let onlineBusy = false;
let onlineFailed = false;
let onlineFetchedAt = 0;

function redrawOnline() {
  const localIds = new Set(items.map(item => item.syncId));
  const cards = element("online-cards");
  const visible = onlineItems.filter(item => (home() || !localIds.has(item.syncId)) && !hiddenTitle(item));
  if (onlineGrid && !home()) onlineGrid.set(visible);
  else {
    onlineGrid?.disable();
    cards.replaceChildren(...visible.map(item => titleCard(item, () => void openOnline(item), false)));
  }
  if (!onlineBusy && !onlineFailed) {
    element("online-status").textContent = visible.length ? "" : ui("Sin títulos adicionales del servidor.");
  }
  const more = element<HTMLButtonElement>("online-more");
  more.hidden = !onlineNext && !onlineFailed;
  more.disabled = onlineBusy;
  more.textContent = ui(onlineFailed ? "Reintentar" : "Ver más del servidor");
  const host = element("online-shelf").querySelector<HTMLElement>(".shelf-host");
  if (host) wireShelfNav(host);
}

function refreshOnline() {
  const key = serverAddress && getProfilePref(activeProfileId(), "online-catalog") !== "off" && !favoritesOnly && !listFilter.value
    ? JSON.stringify([serverAddress, search.value.trim(), media.value, filters(), hideAdult()])
    : "";
  if (key === onlineKey && (!key || onlineBusy || onlineFailed || Date.now() - onlineFetchedAt < 60_000)) return;
  onlineKey = key;
  ++onlineSequence;
  onlineItems = [];
  onlineNext = null;
  onlineBusy = false;
  onlineFailed = false;
  const shelf = element("online-shelf");
  shelf.hidden = !key;
  if (!key) return;
  const anchor = element(home() ? "home-shelves" : "empty");
  anchor.before(shelf);
  element("online-heading").textContent = ui("Catálogo del servidor");
  return loadOnline();
}

function hideAdult() {
  return getProfilePref(activeProfileId(), "umd-hide-adult") === "true";
}

async function loadOnline() {
  if (!onlineKey || onlineBusy) return;
  const seq = ++onlineSequence;
  onlineBusy = true;
  onlineFailed = false;
  element("online-status").textContent = ui("Consultando el servidor…");
  redrawOnline();
  try {
    const page = await invoke<OnlinePage>("browse_online_catalog", {
      query: { ...filters(), q: search.value.trim(), mediaType: media.value || null, cursor: onlineNext },
      recent: home(),
      hideAdult: hideAdult(),
    });
    if (seq !== onlineSequence) return;
    const known = new Set(onlineItems.map(item => item.syncId));
    for (const item of page.items) {
      if (!known.has(item.syncId)) { onlineItems.push(item); known.add(item.syncId); }
    }
    onlineNext = page.next;
    onlineFetchedAt = Date.now();
    element("online-heading").textContent = ui(page.recent ? "Últimos añadidos al servidor" : "Catálogo del servidor");
  } catch {
    if (seq !== onlineSequence) return;
    onlineFailed = true;
    element("online-status").textContent = ui("No se pudo consultar el servidor. Tu biblioteca sigue disponible.");
  } finally {
    if (seq === onlineSequence) { onlineBusy = false; redrawOnline(); }
  }
}
element("online-more").onclick = () => void loadOnline();

async function openOnline(summary: CatalogItem) {
  const source = serverAddress;
  const opening = ++previewSequence;
  const seq = onlineSequence;
  const origin = activePage();
  try {
    const item = await invoke<CatalogItem>("import_remote_item", { id: summary.syncId, server: source, hideAdult: hideAdult() });
    if (opening === previewSequence && seq === onlineSequence && source === serverAddress && isPageActive(origin)) {
      void showDetails(item, undefined, true);
    }
  } catch (error) {
    if (opening === previewSequence && seq === onlineSequence) element("online-status").textContent = String(error);
  }
}

interface ProviderFilterHit { item: CatalogItem; entry: unknown | null }
type ProviderFilterQuery = ReturnType<typeof filters> & { q: string; mediaType: string | null; cursor?: string | null };
const filteredShelf = element("filtered-provider-shelf");
const filteredCards = element("filtered-provider-cards");
const filteredStatus = element("filtered-provider-status");
const filteredMore = element<HTMLButtonElement>("filtered-provider-more");
const filteredHits = new Map<string, { hit: ProviderFilterHit; site: string }>();
let filteredKey = "";
let filteredSequence = 0;
let filteredQuery: ProviderFilterQuery;
let filteredNext = new Map<string, string | null>();
const filteredGrid = android ? virtualCatalog<{ key: string; hit: ProviderFilterHit; site: string }>(filteredCards,
  row => row.key, row => titleCard(row.hit.item, () => void openFilteredHit(row.hit, row.site), false)) : null;

function refreshFilteredProviders() {
  const current = filters();
  const sites = [...element<HTMLSelectElement>("provider-site").options]
    .map(option => option.value)
    .filter(site => site && !["all", "local", "server", "capibaratraductor"].includes(site)
      && providerEnabled(activeProfileId(), site));
  const query = {...current, q: search.value.trim(), mediaType: media.value || null};
  const allowed = (current.genre || current.year) && !favoritesOnly && !listFilter.value;
  const key = allowed && sites.length ? JSON.stringify([query, sites, hideAdult()]) : "";
  if (key === filteredKey) return;
  filteredKey = key;
  ++filteredSequence;
  filteredHits.clear();
  filteredGrid?.disable();
  filteredCards.replaceChildren();
  filteredNext = new Map(sites.map(site => [site, null]));
  filteredShelf.hidden = !key;
  filteredMore.hidden = true;
  if (!key) return;
  filteredQuery = query;
  element("filtered-provider-heading").textContent = ui("Resultados de proveedores");
  filteredMore.textContent = ui("Ver más de proveedores");
  void loadFilteredProviders();
}

async function loadFilteredProviders() {
  const seq = filteredSequence;
  const query = filteredQuery;
  const jobs = [...filteredNext].values();
  filteredNext.clear();
  filteredMore.hidden = true;
  filteredStatus.textContent = ui("Consultando proveedores…");
  let failed = 0;
  const refreshCards = () => {
    if (filteredGrid) {
      filteredGrid.set([...filteredHits].map(([key, row]) => ({key, ...row})));
    }
  };
  const worker = async () => {
    for (const [site, cursor] of jobs) {
      if (seq !== filteredSequence) return;
      try {
        const [hits, next] = await invoke<[ProviderFilterHit[], string | null]>("filtered_provider_page", {
          site, query: {...query, cursor}, hideAdult: hideAdult(),
        });
        if (seq !== filteredSequence) return;
        for (const hit of hits) {
          const key = `${site}:${hit.item.syncId}`;
          if (filteredHits.has(key) || hiddenTitle(hit.item)) continue;
          filteredHits.set(key, {hit, site});
          if (!filteredGrid) filteredCards.append(titleCard(hit.item, () => void openFilteredHit(hit, site), false));
        }
        refreshCards();
        if (next && next !== cursor) filteredNext.set(site, next);
      } catch {
        failed++;
      }
    }
  };
  await Promise.all([worker(), worker(), worker()]);
  if (seq !== filteredSequence) return;
  filteredStatus.textContent = filteredHits.size ? ""
    : ui(failed ? "Algunos proveedores no respondieron." : "Sin resultados de proveedores para estos filtros.");
  filteredMore.hidden = filteredNext.size === 0;
}
filteredMore.onclick = () => void loadFilteredProviders();

async function openFilteredHit(hit: ProviderFilterHit, site: string) {
  const seq = ++previewSequence;
  const origin = activePage();
  try {
    const item = hit.entry
      ? await invoke<CatalogItem>("provider_add", {site, entry: hit.entry, hideAdult: hideAdult()})
      : await invoke<CatalogItem>("json_provider_add", {site, id: hit.item.syncId, hideAdult: hideAdult()});
    if (seq !== previewSequence || !isPageActive(origin)) return;
    itemRefreshTimes.set(item.syncId, Date.now());
    void showDetails(item);
    void load();
  } catch (error) {
    if (seq === previewSequence) filteredStatus.textContent = String(error);
  }
}

function renderHomeShelves(visible: CatalogItem[]) {
  const host = element("home-shelves");
  const showShelves = home() && shelfMode && visible.length >= 6;
  host.hidden = !showShelves;
  catalog.hidden = showShelves;
  element("more").hidden = showShelves || cursor === null;

  const toggleBtn = element<HTMLButtonElement>("shelves-toggle");
  toggleBtn.hidden = !home() || visible.length < 6;
  toggleBtn.textContent = shelfMode ? ui("Ver todo en cuadrícula") : ui("Ver por estantes");

  if (!showShelves) {
    host.replaceChildren();
    return;
  }

  const buckets: { id: Section; title: string; items: CatalogItem[]; extra?:boolean; genre?: string }[] = ([
    {
      id: "movies" as Section,
      title: ui("Cine y series"),
      items: visible.filter(i => i.mediaType.toLowerCase() === "movie" && !i.subtype.toLowerCase().includes("anime")),
    },
    {
      id: "anime" as Section,
      title: ui("Anime"),
      items: visible.filter(i => i.mediaType.toLowerCase() === "movie" && i.subtype.toLowerCase().includes("anime")),
    },
    {
      id: "books" as Section,
      title: ui("Libros y manga"),
      items: visible.filter(i => i.mediaType.toLowerCase() === "book"),
    },
    {
      id: "audio" as Section,
      title: ui("Audio"),
      items: visible.filter(i => i.mediaType.toLowerCase() === "audio"),
    },
  ]).filter(b => b.items.length > 0);

  for (const [title, rows] of [[ui("Actualizado recientemente"),recentRows.updated],[ui("Agregado recientemente"),recentRows.added]] as const) {
    const eligible = rows.filter(i => !hiddenTitle(i));
    if (eligible.length) buckets.unshift({id:"all", title, items:eligible, extra:true});
  }
  const genres = new Map<string,CatalogItem[]>();
  for (const item of visible) for (const genre of new Set(item.genres ?? [])) {
    const rows = genres.get(genre) ?? []; rows.push(item); genres.set(genre, rows);
  }
  for (const [title, rows] of [...genres].filter(([,rows]) => rows.length >= 2).sort((a,b) => b[1].length-a[1].length).slice(0,8)) {
    buckets.push({id:"all", title, items:rows, genre:title});
  }
  const frag = document.createDocumentFragment();
  for (const bucket of buckets) {
    const sec = node("section", undefined, "shelf-section");
    const head = node("div", undefined, "collection-heading");
    const left = node("div");
    left.append(node("h2", bucket.title), node("span", `${bucket.items.length}`));
    head.append(left);
    if (bucket.genre) {
      const seeAll = node("button", ui("Ver todo →"), "quiet-btn shelf-see-all");
      seeAll.type = "button";
      seeAll.onclick = () => {
        clearTimeout(timer);
        search.value = "";
        element("advanced").querySelectorAll<HTMLInputElement | HTMLSelectElement>("input,select").forEach(field => {
          if (field instanceof HTMLInputElement && field.type === "checkbox") field.checked = false;
          else field.value = "";
        });
        element<HTMLInputElement>("genre").value = bucket.genre!;
        section = "all";
        favoritesOnly = false;
        element<HTMLInputElement>("favorites-only").checked = false;
        setCatalogNavId("all");
        navigateTo("catalog", { resetStack: true });
        syncSection();
        showFilters(true);
        void load();
      };
      head.append(seeAll);
    } else if (!bucket.extra) {
      const seeAll = node("button", ui("Ver todo →"), "quiet-btn shelf-see-all");
      seeAll.type = "button";
      seeAll.onclick = () => selectSection(bucket.id);
      head.append(seeAll);
    }

    const shelfHost = node("div", undefined, "shelf-host");
    const row = node("div", undefined, "shelf-row");
    for (const item of bucket.items.slice(0, 20)) {
      row.append(titleCard(item, () => void showDetails(item), true));
    }
    const prev = node("button", "‹", "shelf-nav shelf-prev");
    prev.type = "button";
    prev.setAttribute("aria-label", ui("Desplazar a la izquierda"));
    const next = node("button", "›", "shelf-nav shelf-next");
    next.type = "button";
    next.setAttribute("aria-label", ui("Desplazar a la derecha"));
    shelfHost.append(row, prev, next);
    sec.append(head, shelfHost);
    frag.append(sec);
  }
  host.replaceChildren(frag);
  host.querySelectorAll<HTMLElement>(".shelf-host").forEach(wireShelfNav);
}

function render(from = 0) {
  if (catalogWaiting) return;
  const visible = items.filter(item => !hiddenTitle(item));
  if (localGrid) {
    if (home() && shelfMode && visible.length >= 6) localGrid.disable();
    else localGrid.set(visible);
  }
  else {
    if (!from) catalog.replaceChildren();
    const cards = document.createDocumentFragment();
    for (const item of items.slice(from)) {
      if (hiddenTitle(item)) continue;
      cards.append(titleCard(item, () => void showDetails(item), true));
    }
    catalog.append(cards);
  }
  renderFeature();
  renderContinueShelf();
  renderHomeShelves(visible);

  const firstRun = home() && !visible.length && !serverAddress;
  element("collection-bar").hidden = firstRun;
  if (firstRun) showFilters(false);
  element("collection-title").textContent = ui(search.value.trim() ? "Resultados" : home() ? "Tu biblioteca" : "Títulos");
  element("empty").hidden = firstRun || visible.length > 0;
  const justFavorites = favoritesOnly && !hasFilters();
  element("empty-title").textContent = ui(justFavorites ? "Todavía no tenés favoritos" : favoritesOnly ? "No hay favoritos que coincidan" : serverAddress ? "Sin títulos locales para mostrar" : "Sin títulos para mostrar");
  element("empty-description").textContent = ui(justFavorites ? "Marcá el corazón de un título para encontrarlo acá." : serverAddress && !favoritesOnly ? "Abrí un título del servidor para agregarlo a tu biblioteca." : "Probá con otro título o cambiá los filtros.");
  element("empty-reset").textContent = ui((section === "all" || favoritesOnly) && hasFilters() ? "Limpiar filtros" : "Volver a la biblioteca");
  if (!catalog.hidden) element("more").hidden = cursor === null;
}

/**
 * Full-page Item Detail view (Avalonia parity — replaces the old #detail-dialog modal!).
 */
async function showDetails(initialItem: CatalogItem, unit?: ContentUnit, fromOnline = false) {
  if (hiddenTitle(initialItem)) {
    status(ui("El filtro +18 ocultó esta ficha."), true);
    return;
  }

  detailAbort?.abort();
  detailAbort = new AbortController();
  const signal = detailAbort.signal;
  const seq = ++detailSequence;
  const origin = activePage();

  // Fetch the full local item (with chapters, seasons, sources, aliases, contributors) if it came from a summary query
  let item = initialItem;
  if (!Array.isArray(item.units) && !Array.isArray(item.seasons) && !Array.isArray(item.sources)) {
    try {
      const full = await invoke<CatalogItem>("library_item", { id: item.syncId });
      item = full;
    } catch {
      /* Fall back to initialItem if item was just removed */
    }
  }
  if (seq !== detailSequence || signal.aborted || !isPageActive(origin)) return;

  const content = element("detail");
  const sameDetail = isPageActive("detail") && currentDetail?.item.syncId === item.syncId;
  const entryView = sameDetail && currentDetail ? currentDetail.entryView : {};
  const scrollY = sameDetail ? window.scrollY : 0;
  const newHighlight = !!unit && unit !== currentDetail?.unit;
  content.dataset.tone = tone(item);
  currentDetail = { item, unit, entryView };

  // Top Back bar (Avalonia DetailsBackButton + Refresh)
  const topBar = node("div", undefined, "detail-topbar");
  const backBtn = node("button", "← " + ui("Volver"), "quiet-btn detail-back-btn");
  backBtn.type = "button";
  backBtn.onclick = () => goBack();
  topBar.append(backBtn);

  const feedback = node("p", undefined, "detail-feedback");
  feedback.setAttribute("role", "status");

  interface RefreshItemResult {
    item: CatalogItem;
    changed: boolean;
    sourcesChecked: string[];
    errors: string[];
  }

  const topActions = node("div", undefined, "detail-topbar-actions");
  const refreshBtn = node("button", ui("Actualizar"), "quiet-btn detail-refresh-btn");
  refreshBtn.type = "button";
  refreshBtn.onclick = async () => {
    refreshBtn.disabled = true;
    feedback.classList.remove("error");
    feedback.textContent = ui("Actualizando ficha…");
    try {
      const res = await invokeProfile<RefreshItemResult>("refresh_item", {
        id: item.syncId,
        hideAdult: hideAdult(),
        discover: true,
      });
      itemRefreshTimes.set(item.syncId, Date.now());
      if (seq !== detailSequence || signal.aborted) return;
      if (res.changed) {
        await load();
        void refreshContinue().catch(() => {});
        if (seq === detailSequence && !signal.aborted && isPageActive("detail")) {
          await showDetails(res.item, unit);
          const activeFeedback = element("detail").querySelector<HTMLElement>(".detail-feedback");
          if (activeFeedback) {
            activeFeedback.classList.remove("error");
            activeFeedback.textContent = ui("Ficha actualizada.");
          }
        }
        return;
      }
      if (res.sourcesChecked.length > 0) {
        feedback.textContent = ui("La ficha ya está al día.");
      } else if (res.errors.length > 0) {
        feedback.textContent = ui("No se pudo actualizar la ficha.");
        feedback.classList.add("error");
      } else {
        feedback.textContent = ui("No se encontraron cambios nuevos.");
      }
    } catch (err) {
      if (seq === detailSequence && !signal.aborted) {
        feedback.textContent = String(err);
        feedback.classList.add("error");
      }
    } finally {
      if (refreshBtn.isConnected) refreshBtn.disabled = false;
    }
  };
  topActions.append(refreshBtn);
  topBar.append(topActions);

  const hero = node("section", undefined, "detail-hero-banner");
  const backdropUrl = safeImage((item.backdropRef as string | null | undefined) || item.thumbnailRef);
  if (backdropUrl) {
    const backdrop = node("div", undefined, "detail-backdrop");
    backdrop.setAttribute("aria-hidden", "true");
    const bgImg = node("img");
    bgImg.alt = "";
    attachArtworkImage(bgImg, backdropUrl, Object.keys(item.onlineProviders ?? {}), () => backdrop.remove(), "backdrop");
    backdrop.append(bgImg);
    hero.append(backdrop);
  }

  const heroGrid = node("div", undefined, "detail-hero-grid");
  const posterCol = node("div", undefined, "detail-poster-col");
  posterCol.append(artwork(item));
  posterCol.append(favoriteButton(item, true));

  const infoCol = node("div", undefined, "detail-info-col");

  // Badges / Chips row
  const badgesRow = node("div", undefined, "detail-chips");
  badgesRow.append(node("span", mediaLabel(item), "chip accent"));
  const pubStatus = statusLabel(item.publicationStatus);
  if (pubStatus) badgesRow.append(node("span", pubStatus, "chip amber"));
  const providerNames = Object.keys(item.onlineProviders ?? {}).filter(id => providerEnabled(activeProfileId(), id));
  if (providerNames.length) {
    badgesRow.append(node("span", providerNames.join(" · "), "chip mint"));

  }
  infoCol.append(badgesRow);

  const heading = node("h1", displayTitle(item));
  heading.id = "detail-title";
  infoCol.append(heading);

  // Facts line
  const unitsCount = Array.isArray(item.units) ? item.units.length : 0;
  const seasonsCount = Array.isArray(item.seasons) ? item.seasons.length : 0;
  const totalSeasonUnits: number = Array.isArray(item.seasons)
    ? item.seasons.reduce<number>((acc, s) => acc + (Array.isArray((s as { units?: unknown[] })?.units) ? (s as { units: unknown[] }).units.length : 0), 0)
    : 0;
  const totalEntries = unitsCount + totalSeasonUnits;
  const unitLabel = item.mediaType.toLowerCase() === "book" ? ui("capítulo") : item.mediaType.toLowerCase() === "audio" ? ui("pista") : ui("episodio");
  const facts: unknown[] = [
    item.subtype.replace(/^(movie|book|audio)\./, "").replaceAll("_", " "),
    item.releaseYear ?? item.releaseDate?.slice(0, 4),
  ];
  if (seasonsCount) facts.push(ui("{0} temporada{1}", seasonsCount, seasonsCount === 1 ? "" : "s"));
  if (totalEntries) facts.push(`${totalEntries} ${unitLabel}${totalEntries === 1 ? "" : "s"}`);
  infoCol.append(node("p", facts.filter(Boolean).join(" · "), "detail-facts"));

  // Primary Continue CTA placeholder (wired by contentControls!)
  const ctaHost = node("div", undefined, "detail-cta-row");
  infoCol.append(ctaHost);

  // Genres & Categories tags
  const tags = node("div", undefined, "tags");
  for (const genre of item.genres ?? []) tags.append(node("span", genre, "tag-genre"));
  for (const cat of item.categories ?? []) tags.append(node("span", cat, "tag-category"));
  if (tags.childNodes.length) infoCol.append(tags);

  // Description
  const description = node("p", item.description || ui("Sin descripción."), "description clamped");
  const more = node("button", ui("Leer más"), "read-more");
  more.type = "button";
  more.hidden = true;
  more.onclick = () => {
    more.textContent = ui(description.classList.toggle("clamped") ? "Leer más" : "Leer menos");
  };
  infoCol.append(description, more);

  // Alternative names (Avalonia parity)
  const aliases = (item.alternativeNames ?? []).filter(a => a?.name && a.name.trim() && a.name.trim() !== displayTitle(item));
  if (aliases.length) {
    const aliasWrap = node("div", undefined, "detail-meta-block");
    aliasWrap.append(node("span", ui("Otros títulos"), "detail-meta-label"));
    const list = node("div", undefined, "detail-chips");
    for (const alias of aliases.slice(0, 12)) {
      const chip = node("span", undefined, "chip outline");
      chip.append(node("span", alias.name!.trim()));
      if (alias.language) chip.append(node("small", alias.language.toUpperCase(), "chip-lang"));
      list.append(chip);
    }
    aliasWrap.append(list);
    infoCol.append(aliasWrap);
  }

  // Contributors (Avalonia parity)
  const contributors = (item.contributors ?? []).filter(c => c?.name && c.name.trim());
  if (contributors.length) {
    const contribWrap = node("div", undefined, "detail-meta-block");
    contribWrap.append(node("span", ui("Colaboradores"), "detail-meta-label"));
    const list = node("div", undefined, "detail-chips");
    for (const c of contributors.slice(0, 16)) {
      const link = remoteUrl(c.url);
      const role = c.role || c.kind;
      if (link?.startsWith("https://")) {
        const chip = node("button", undefined, "chip outline chip-link");
        chip.type = "button";
        chip.append(node("span", c.name!.trim()));
        if (role) chip.append(node("small", role, "chip-lang"));
        chip.onclick = () => void invoke("open_provider_player",{address:link}).catch(e => status(String(e),true));
        list.append(chip);
      } else {
        const chip = node("span", undefined, "chip outline");
        chip.append(node("span", c.name!.trim()));
        if (role) chip.append(node("small", role, "chip-lang"));
        list.append(chip);
      }
    }
    contribWrap.append(list);
    infoCol.append(contribWrap);
  }

  heroGrid.append(posterCol, infoCol);
  hero.append(heroGrid);

  const body = node("div", undefined, "detail-body");

  // Chapters / Episodes / Tracks list (full page, NOT a dropdown!)
  const controls = contentControls(
    item,
    (message, error = false) => {
      if (!isPageActive("detail")) { status(message, error); return; }
      feedback.textContent = message;
      feedback.classList.toggle("error", error);
    },
    unit,
    {
      signal,
      view: entryView,
      scrollToHighlight: !sameDetail || newHighlight,
      onContinueReady: (cta) => {
        ctaHost.replaceChildren();
        if (!cta) return;
        const btn = node("button", cta.label, "primary detail-continue-btn");
        btn.type = "button";
        btn.dataset.icon = "play";
        btn.onclick = cta.run;
        ctaHost.append(btn);
      },
      onUnitsChanged: () => { void invoke<CatalogItem>("library_item",{id:item.syncId}).then(updated => showDetails(updated)).catch(e => status(String(e),true)); },
      onProgressChanged: () => {
        void refreshContinue().catch(() => {});
      },
    },
  );

  // Side-by-side / stacked personal panels: List membership + Friend activity
  const personalGrid = node("div", undefined, "detail-personal-grid");
  personalGrid.append(
    inlineListMembership(item, openListsPage, signal),
    inlineFriendActivity(item, signal),
  );

  // Management actions: Metadata refresh + Delete catalog item (with inline confirmation!)
  const managePanel = node("section", undefined, "surface-panel detail-manage-panel");
  const manageRow = node("div", undefined, "detail-manage-row");
  const code = item.subtype.replace(/^(movie|book)\./, "");
  if (item.mediaType.toLowerCase() === "movie" || ["manga", "web_novel"].includes(code)) {
    manageRow.append(
      metadataControls(item, code.startsWith("anime_") || item.mediaType.toLowerCase() === "book", async (updated) => {
        await load();
        if (isPageActive("detail")) void showDetails(updated, unit);
      }),
    );
  }

  const deleteWrap = node("div", undefined, "detail-delete-wrap");
  const deleteBtn = node("button", ui("Eliminar de la biblioteca"), "danger-btn");
  deleteBtn.type = "button";
  deleteBtn.onclick = () => {
    const confirm = node("div", undefined, "notice-danger inline-confirm");
    const msg = node("span", ui("¿Eliminar «{0}» de tu biblioteca local?", displayTitle(item)));
    const yes = node("button", ui("Confirmar eliminación"), "danger-btn");
    yes.type = "button";
    yes.onclick = async () => {
      yes.disabled = true;
      try {
        await invoke<boolean>("delete_catalog_item", { id: item.syncId });
        await load();
        void refreshContinue().catch(() => {});
        goBack();
        status(ui("Título eliminado de la biblioteca."));
      } catch (err) {
        feedback.textContent = String(err);
        feedback.classList.add("error");
        yes.disabled = false;
      }
    };
    const no = node("button", ui("Cancelar"), "quiet-btn");
    no.type = "button";
    no.onclick = () => confirm.replaceWith(deleteBtn);
    confirm.append(msg, yes, no);
    deleteBtn.replaceWith(confirm);
  };
  deleteWrap.append(deleteBtn);
  managePanel.append(manageRow, deleteWrap);

  body.append(feedback, personalGrid, controls, managePanel);

  content.replaceChildren(topBar, hero, body);
  const alreadyOnDetail = isPageActive("detail");
  navigateTo("detail", { push: !alreadyOnDetail, scrollTop: !sameDetail });
  if (sameDetail && !newHighlight) window.scrollTo(0, scrollY);

  requestAnimationFrame(() => {
    more.hidden = description.scrollHeight <= description.clientHeight + 1;
  });

  const lastRefresh = itemRefreshTimes.get(item.syncId) ?? 0;
  const hasProviders = providerNames.length > 0;
  const needsArtwork = !safeImage(item.thumbnailRef);
  if (Date.now() - lastRefresh > 5 * 60_000 && (fromOnline || serverAddress || hasProviders || needsArtwork)) {
    itemRefreshTimes.set(item.syncId, Date.now());
    if (fromOnline) feedback.textContent = ui("Consultando proveedores…");
    void afterPaint().then(() => {
      if (signal.aborted) return null;
      return invokeProfile<RefreshItemResult>("refresh_item", {
        id: item.syncId,
        hideAdult: hideAdult(),
        discover: fromOnline || (!hasProviders && needsArtwork),
        refreshServer: !fromOnline,
      });
    }).then(async res => {
      if (!res || seq !== detailSequence || signal.aborted) return;
      if (fromOnline) feedback.textContent = res.errors.length && !res.sourcesChecked.length ? ui("No se pudo actualizar la ficha.") : "";
      if (!res.changed) return;
      await load();
      void refreshContinue().catch(() => {});
      if (seq === detailSequence && !signal.aborted && isPageActive("detail") && !detailProviderActive()) {
        void showDetails(res.item, unit);
      }
    }).catch(() => {
      if (fromOnline && !signal.aborted) feedback.textContent = ui("No se pudo actualizar la ficha.");
    });
  }
}

async function importCatalog() {
  await action(async () => {
    const count = await invoke<number | null>("import_catalog");
    if (count !== null) {
      await load();
      status(count === 1 ? ui("Se importó 1 título.") : ui("Se importaron {0} títulos.", count));
    }
  });
}

element("import").addEventListener("click", importCatalog);
element("empty-import").addEventListener("click", importCatalog);
element("export").addEventListener("click", () => action(async () => {
  if (await invoke<boolean>("export_catalog")) status(ui("Catálogo exportado."));
}));
element("connect").addEventListener("click", async () => {
  try {
    element<HTMLInputElement>("server-address").value = await invoke<string>("server_url");
    element("server-error").textContent = "";
    if (!serverDialog.open) serverDialog.showModal();
  } catch (error) {
    status(String(error), true);
  }
});
element("server-form").addEventListener("submit", (event) => {
  event.preventDefault();
  action(async () => {
    try {
      element("server-error").textContent = "";
      element("server-progress").textContent = ui("Conectando…");
      serverAddress = await invoke<string>("connect_server", { baseUrl: element<HTMLInputElement>("server-address").value });
      onlineKey = "";
      serverDialog.close();
      await load();
      status(ui("Servidor conectado."));
    } catch (error) {
      element("server-error").textContent = String(error);
    } finally {
      element("server-progress").textContent = "";
    }
  });
});

document.querySelectorAll<HTMLButtonElement>("[data-close]").forEach(button => {
  button.addEventListener("click", () => button.closest("dialog")?.close());
});
for (const id of Object.keys(sections) as Section[]) {
  element(id).addEventListener("click", () => selectSection(id));
}
element("empty-reset").onclick = () => selectSection((section === "all" || favoritesOnly) && hasFilters() ? section : "all");
element("feature-action").onclick = () => {
  if (featured) void showDetails(featured, featuredUnit);
  else element("connect").click();
};
element("shelves-toggle").onclick = () => {
  shelfMode = !shelfMode;
  localStorage.setItem("umd-home-shelves", String(shelfMode));
  render();
};
setView(localStorage.getItem("umd-catalog-view") === "list");
for (const mode of ["list", "grid"]) {
  element(mode + "-view").onclick = () => {
    setView(mode === "list");
    localStorage.setItem("umd-catalog-view", mode);
    if (home() && shelfMode) {
      shelfMode = false;
      localStorage.setItem("umd-home-shelves", "false");
      render();
    }
  };
}

const menu = element<HTMLDialogElement>("mobile-nav");
const rail = element("rail");
const compact = matchMedia("(max-width: 740px)");
function adaptMenu() {
  if (menu.open) menu.close();
  (compact.matches ? menu : element("rail-host")).append(rail);
}
compact.addEventListener("change", adaptMenu);
adaptMenu();
element("menu-open").onclick = () => {
  if (!menu.open) menu.showModal();
  rail.querySelector<HTMLButtonElement>(".active")?.focus();
};
rail.addEventListener("click", event => {
  if (menu.open && (event.target as Element).closest("button")) menu.close();
}, true);

setupMetadataSettings();
const settings = element<HTMLDialogElement>("settings-dialog");
element("donation-copy").onclick = async () => {
  const address = element<HTMLInputElement>("donation-address");
  const output = element("donation-status");
  try {
    await navigator.clipboard.writeText(address.value);
    output.textContent = ui("Dirección copiada");
  } catch {
    address.select();
    output.textContent = ui("Seleccioná y copiá la dirección.");
  }
};
const onlineToggle = element<HTMLInputElement>("online-enabled");
onlineToggle.onchange = async () => {
  await writeProfilePref("online-catalog", onlineToggle.checked ? "on" : "off");
  await load();
};
element("settings-open").onclick = async () => {
  const profile = await windowProfile();
  onlineToggle.checked = getProfilePref(profile.activeId, "online-catalog") !== "off";
  if (!settings.open) settings.showModal();
};
document.querySelectorAll<HTMLElement>("[data-open]").forEach(button => {
  button.onclick = () => element(button.dataset.open!).click();
});
element("settings-dialog").addEventListener("click", event => {
  if ((event.target as Element).closest("#connect,#import,#export,#account,#updates-open")) element<HTMLDialogElement>("settings-dialog").close();
}, true);

search.addEventListener("input", () => {
  if (!isPageActive("catalog")) navigateTo("catalog", { resetStack: true });
  clearTimeout(timer);
  ++generation;
  ++onlineSequence;
  onlineKey = "";
  onlineBusy = false;
  element("online-shelf").hidden = true;
  timer = setTimeout(() => load(), 180);
});
function filteredSection(): Section {
  const group = element<HTMLSelectElement>("subtype-group").value;
  return (Object.keys(sections) as Section[]).find(id =>
    id !== "favorites" && sections[id].type === media.value && sections[id].group === group
  ) ?? (media.value === "book" ? "books" : "all");
}
media.addEventListener("change", () => {
  const group = element<HTMLSelectElement>("subtype-group");
  if (media.value !== groupMedia(group.value)) group.value = "";
});
element("advanced").addEventListener("change", (event) => {
  const year = element<HTMLInputElement>("year");
  if (event.target === year && (year.value.trim() !== "" || year.validity.badInput) && !year.reportValidity()) return;
  const favBox = element<HTMLInputElement>("favorites-only");
  favoritesOnly = favBox.checked;
  const group = element<HTMLSelectElement>("subtype-group");
  if (event.target === group && group.value) media.value = groupMedia(group.value);
  if (section === "favorites" && !favoritesOnly) {
    section = filteredSection();
    syncSection();
  } else if (!favoritesOnly) {
    section = filteredSection();
    syncSection();
  }
  void load();
});
element("more").addEventListener("click", () => load(true));
new IntersectionObserver(entries => {
  const more = element<HTMLButtonElement>("more");
  if (entries.some(entry => entry.isIntersecting) && isPageActive("catalog") && !catalog.hidden && cursor !== null && !more.disabled && !document.querySelector("dialog[open]")) {
    void load(true);
  }
}, { rootMargin: "600px 0px" }).observe(element("more"));

element("filters-toggle").onclick = () => {
  showFilters(element("advanced").hidden);
  if (!element("advanced").hidden) {
    media.focus();
    void invoke<Record<string,string[]>>("filter_choices").then(choices => {
      for (const id of ["category","year","distributor"]) element(id+"-choices").replaceChildren(...choices[id].map(v => new Option(v,v)));
    }).catch(e => status(String(e),true));
  }
};
element("filters-clear").onclick = () => {
  selectSection(section);
  element("filters-toggle").focus();
};
document.addEventListener("keydown", event => {
  if (!document.querySelector("dialog[open]") && (event.ctrlKey || event.metaKey) && event.key.toLowerCase() === "f") {
    event.preventDefault();
    search.focus();
  }
});

setupLanguage(flushProgress);
const startAccount = setupAccount(async () => {
  await refreshListFilterOptions();
  await load();
  void refreshContinue().catch(() => {});
  if (isPageActive("detail") && currentDetail) {
    const { item: { syncId: id }, unit } = currentDetail;
    try {
      const fresh = await invoke<CatalogItem>("library_item", { id });
      if (isPageActive("detail") && currentDetail?.item.syncId === id && !detailProviderActive()) {
        void showDetails(fresh, unit);
      }
    } catch { /* Item may have been removed by sync */ }
  }
}, status);
setupNavigation();
const initialProfile = setupProfiles();
const checkUpdates = setupUpdates();
const openProviderSearch = setupProvider(async (item, openDetails) => {
  if (openDetails) void showDetails(item);
  await load();
});
setupFriends(openProviderSearch);
setupParty((item, unit) => void showDetails(item, unit));
setupStreams(status);
setupDownloads((name, mediaItem, target) => playDownload(name, mediaItem, target, status));
const openListsPage = setupLists(
  (item) => void showDetails(item),
  titleCard,
  () => load(),
);
void (async () => {
  await load(false, true, true);
  if (!startupApplied) {
    await Promise.allSettled([
      refreshContinue(),
      refreshListFilterOptions(),
      invoke<string>("server_url").then(address => {
        if (!serverAddress) serverAddress = address;
      }),
    ]);
  }
  await Promise.allSettled([
    initialProfile,
    refreshOnline(),
    invoke<string | null>("library_notice").then(message => {
      if (message) status(localizeDiagnostic(message), false, true);
    }),
  ]);
  let pending: Promise<void> | undefined;
  do {
    pending = catalogLoad;
    await pending;
  } while (pending !== catalogLoad);
  await afterPaint();
  void startAccount().catch(e => status(String(e), true));
  checkUpdates?.(serverAddress);
})().catch(e => status(String(e), true));

for (const [id, restore] of [["copy-library", false], ["restore-library", true]] as const) {
  element(id).addEventListener("click", async () => {
    const out = element("database-result");
    if (restore && !window.confirm(ui("Al volver a abrir UMD se reemplazará la biblioteca y todos sus perfiles por la copia elegida. UMD guardará una copia de la biblioteca actual. ¿Querés elegir el archivo?"))) return;
    const buttons = [element<HTMLButtonElement>("copy-library"), element<HTMLButtonElement>("restore-library")];
    buttons.forEach(b => b.disabled = true);
    try {
      const done = await invoke<boolean>("database_copy", { restore });
      out.textContent = done ? (restore ? ui("Copia preparada. Cerrá y volvé a abrir UMD para restaurarla.") : ui("Copia completa guardada.")) : "";
    } catch (e) { out.textContent = String(e); } finally { buttons.forEach(b => b.disabled = false); }
  });
}

window.addEventListener("umd-catalog-changed", () => { void load(); void refreshContinue().catch(() => {}); });
window.addEventListener("umd-provider-availability-changed", () => {
  refreshFilteredProviders();
  if (isPageActive("detail") && currentDetail) {
    const { item, unit } = currentDetail;
    void showDetails(item, unit);
  }
});
window.addEventListener("umd-lists-changed", async () => {
  const filteringByList = !!listFilter.value;
  await refreshListFilterOptions();
  if (filteringByList) await load();
});
document.addEventListener("close", event => {
  if ((event.target as Element).matches(".player-dialog")) {
    setTimeout(() => void flushProgress().then(refreshContinue).catch(() => {}), 0);
  }
}, true);
