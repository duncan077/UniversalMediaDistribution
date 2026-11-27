import {formatMediaTime, nativePlayer} from "./native-player";
import {attachPlaybackOverlay} from "./playback-overlay";
import {ui, displayTitle} from "./locale";
import {readerMotion} from "./reader-motion";
import {followProvider} from "./provider-playback";
import {activeProfileId, getProfilePref, invokeProfile, preferredProvidersForProfile, providerEnabled, windowProfile, writeProfilePref} from "./profile-context";
import {invoke} from "./bridge";
import {isHttpsUrl, mediaUrl, remoteUrl} from "./safety";
import {Channel, convertFileSrc} from "@tauri-apps/api/core";
import {getCurrentWindow} from "@tauri-apps/api/window";
import {hideAdult} from "./content-filter";
import type HlsType from "hls.js";
import type { PDFDocumentProxy, RenderTask } from "pdfjs-dist";
import pdfWorker from "pdfjs-dist/build/pdf.worker.min.mjs?url";
import {queueCatalogDownload} from "./downloads";
import {continuousReader} from "./continuous";
import {drawImageTiles} from "./reader-tiles";
import {artworkSource} from "./artwork";
import {attachPartyPlayer, samePartyUnit, type ContentUnit} from "./party";
import {isPageActive} from "./router";
import {catalogGroup} from "./catalog-types";
import {bindDevicePlayback, receivingTitle, type IncomingPlayback, type VideoReference} from "./device-playback";

export interface Target { itemId: string; unitKind: string | null; displayNumber: string | null; seasonNumber: number | null }
export interface OpenedMedia { streaming?:boolean; kind: "audio" | "video" | "pdf" | "images" | "epub"; paths: string[] }
export interface Progress { position: number; positionKind: "page" | "seconds"; isCompleted: boolean; updatedAt?: string }
interface ProgressRow extends Progress {
  content: { unitKind?: string | null; displayNumber?: string | null; seasonNumber?: number | null };
}
interface Entry {
  index: number;
  target: Target;
  label: string;
  unitHeading: string;
  unitName: string;
  seasonNumber: number | null;
  seasonLabel: string;
  sources: Record<string, unknown>[];
  transfer?: VideoReference;
  providerLinks?: Record<string,string>;
  incoming?: IncomingPlayback;
}
type PlaybackEntry = Pick<Entry,"label" | "transfer" | "incoming" | "providerLinks"> & {target:Target | null};
export interface PlayableItem {
  onlineProviders?: Record<string, string>;
  subtype?: string;
  syncId: string;
  name: string;
  mediaType: string;
  releaseYear?: number | null;
  units?: unknown[];
  seasons?: unknown[];
  sources?: unknown[];
}
export interface ContinueAction {
  label: string;
  run: () => void;
}
export interface ContentControlsOptions {
  onContinueReady?: (cta: ContinueAction | null) => void;
  onProgressChanged?: () => void;
  onUnitsChanged?: () => void;
  signal?: AbortSignal;
  view?: EntryListView;
  scrollToHighlight?: boolean;
  serverId?: string;
}
export interface EntryListView {
  season?: number | "all";
  descending?: boolean;
  search?: string;
  limit?: number;
}
export interface ChapterNav {
  hasPrev: boolean;
  hasNext: boolean;
  prevLabel: string;
  nextLabel: string;
  openPrev: () => void;
  openNext: () => void;
}

const PROVIDERS: [string, string][] = [
  ["manhwaweb", "ManhwaWeb"],
  ["jkanime", "JKAnime"],
  ["doramasflix", "DoramasFlix"],
  ["olympusxyz", "OlympusXYZ"],
  ["framextv", "FramexTV"],
  ["animetmo", "AnimeTMO"],
  ["animeav1", "AnimeAV1"],
  ["leercapitulo", "LeerCapitulo"],
  ["zonatmo", "ZonaTMO"],
  ["uchuujinmangas", "Uchuujin Mangas"],
  ["pelisplus", "PelisPlus"],
  ["pelisplus-to", "PelisPlus.to"],
  ["pelisflix-hd", "Pelisflix HD"],
  ["poseidon-hd2", "Poseidon HD2"],
  ["seriesflix", "Seriesflix"],
];
const READER_PROVIDERS = new Set(["manhwaweb", "olympusxyz", "leercapitulo", "zonatmo", "uchuujinmangas"]);

function bookArchive(url: string, mime: unknown): "cbr" | "cbz" | "epub" | null {
  const type = typeof mime === "string" ? mime.split(";")[0].trim().toLowerCase() : "";
  const path = new URL(url).pathname.toLowerCase();
  if (["application/vnd.comicbook-rar","application/x-cbr","application/vnd.rar","application/x-rar-compressed"].includes(type) || /\.(cbr|rar)$/.test(path)) return "cbr";
  if (type === "application/epub+zip" || path.endsWith(".epub")) return "epub";
  if (["application/vnd.comicbook+zip","application/x-cbz","application/zip"].includes(type) || /\.(cbz|zip)$/.test(path)) return "cbz";
  return null;
}

function node<K extends keyof HTMLElementTagNameMap>(tag: K, text?: string, className?: string) {
  const el = document.createElement(tag);
  if (text !== undefined) el.textContent = text;
  if (className) el.className = className;
  return el;
}

function records(value: unknown): Record<string, unknown>[] {
  return Array.isArray(value) ? value.filter((v): v is Record<string, unknown> => v !== null && typeof v === "object" && !Array.isArray(v)) : [];
}

function unitKindLabel(kind: string | null): string {
  if (kind === "chapter") return ui("Capítulo");
  if (kind === "track") return ui("Pista");
  if (kind === "audio_chapter") return ui("Capítulo de audio");
  return ui("Episodio");
}

export function formatTime(seconds: number): string {
  const whole = Math.max(0, Math.floor(seconds));
  if (whole >= 3600) {
    return [
      Math.floor(whole / 3600),
      String(Math.floor(whole / 60) % 60).padStart(2, "0"),
      String(whole % 60).padStart(2, "0"),
    ].join(":");
  }
  return Math.floor(whole / 60) + ":" + String(whole % 60).padStart(2, "0");
}

function progressBadgeText(p: Progress | undefined, isBook: boolean): { text: string; kind: "seen" | "progress" | "none" } {
  if (!p) return { text: "", kind: "none" };
  if (p.isCompleted) return { text: isBook ? ui("Leído") : ui("Visto"), kind: "seen" };
  if (p.position > 0) {
    return {
      text: p.positionKind === "page" ? ui("Página {0}", Math.floor(p.position)) : formatTime(p.position),
      kind: "progress",
    };
  }
  return { text: "", kind: "none" };
}

function entries(item: PlayableItem, serverId?: string): Entry[] {
  const baseTarget: Target = { itemId: item.syncId, unitKind: null, displayNumber: null, seasonNumber: null };
  const result: Entry[] = [];
  const pushUnits = (values: unknown, seasonNumber: number | null, seasonTitle?: string) => {
    const seasonLabel = seasonNumber === null
      ? ""
      : seasonTitle
        ? `${ui("Temporada {0}", seasonNumber)} · ${seasonTitle}`
        : ui("Temporada {0}", seasonNumber);
    for (const unit of records(values)) {
      if (typeof unit.kind !== "string" || typeof unit.displayNumber !== "string") continue;
      const unitHeading = `${unitKindLabel(unit.kind)} ${unit.displayNumber}`;
      const unitName = typeof unit.name === "string" ? unit.name.trim() : "";
      const label = [seasonNumber === null ? "" : ui("Temporada {0}", seasonNumber), unitHeading, unitName].filter(Boolean).join(" · ");
      result.push({
        index: result.length,
        target: { ...baseTarget, unitKind: unit.kind, displayNumber: unit.displayNumber, seasonNumber },
        label,
        unitHeading,
        unitName,
        seasonNumber,
        seasonLabel,
        sources: records(unit.sources),
      });
    }
  };
  pushUnits(item.units, null);
  for (const season of records(item.seasons)) {
    if (typeof season.number === "number") {
      const title = typeof season.name === "string" ? season.name.trim() : undefined;
      pushUnits(season.units, season.number, title);
    }
  }
  if (records(item.sources).length || !result.length) {
    result.unshift({
      index: 0,
      target: baseTarget,
      label: displayTitle(item),
      unitHeading: displayTitle(item),
      unitName: "",
      seasonNumber: null,
      seasonLabel: "",
      sources: records(item.sources),
    });
    result.forEach((entry, idx) => { entry.index = idx; });
  }
  if (item.mediaType.toLowerCase() === "movie") {
    for (const entry of result) {
      const {itemId,displayNumber,seasonNumber} = entry.target;
      const unitKind = entry.target.unitKind?.toLowerCase() ?? null;
      if (unitKind !== null && unitKind !== "episode") continue;
      entry.transfer = {itemId,serverId,name:item.name,subtype:item.subtype ?? "movie",
        releaseYear:item.releaseYear ?? undefined,unitKind,displayNumber,seasonNumber};
      entry.providerLinks = item.onlineProviders;
    }
  }
  return result;
}

let writes = Promise.resolve();
export function flushProgress() { return writes; }
function persist(target: Target, position: number, kind: string, completed: boolean, error: (e: unknown) => void) {
  writes = writes.then(() => invokeProfile<void>("save_progress", { target, position, kind, completed })).catch(error);
}

async function openProviderSession(
  address: string,
  entry: PlaybackEntry & {target:Target},
  report: (message: string, error?: boolean) => void,
  chapterNav?: ChapterNav,
  choices?: PlaybackChoices,
) {
  await flushProgress();
  const saved = await invokeProfile<Progress | null>("get_progress", {target: entry.target});
  if (entry.incoming?.signal.aborted) return;
  const resume = choices?.transport?.position ?? entry.incoming?.offer.positionSeconds ??
    (saved?.positionKind === "seconds" && !saved.isCompleted ? saved.position : 0);
  await followProvider(
    address,
    entry.target,
    entry.label,
    resume,
    (pos, ended) => persist(entry.target, pos, "seconds", ended, e => report(String(e), true)),
    async captured => {
      const tracks = captured.stream.tracks?.length ? captured.stream.tracks : choices?.sources[choices.index]?.tracks;
      const stream = {...captured.stream, tracks};
      try {
        await play(entry, "video", [stream.url], report, stream.hls, stream, chapterNav, {
          sources: [], index:0, attempted:new Set(), ...choices, dialog:undefined,
          engine: choices?.engine && choices.engine !== "web" ? choices.engine : /Android/i.test(navigator.userAgent) ? "android" : "vlc",
          transport: {position:captured.position || resume, paused:captured.paused},
        });
      } catch(e) {report(String(e), true);}
    },
    {
      chapterNav,
      reference: transferReference(entry, choices),
      incoming: entry.incoming,
      sources: choices?.sources.map((s, i) => ({ label: s.label || ui("Servidor {0}", i + 1) })),
      sourceIndex: choices?.index ?? 0,
      resolveSource: choices && choices.sources.length > 1 ? async (nextIndex, currentPosition) => {
        const choice = choices.sources[nextIndex];
        if (!choice) throw new Error(ui("No quedan servidores disponibles para este título."));
        choices.attempted.add(nextIndex);
        const resolved = choice.embed || choice.external
          ? await invoke<VideoResolution>("resolve_video", {
              address: choice.url,
              provider: choice.provider,
              webFallback: true,
            })
          : choice;
        const nextChoices: PlaybackChoices = {
          ...choices,
          index: nextIndex,
          dialog: undefined,
          triedEngines: undefined,
          discover: false,
          transport: { position: currentPosition || resume, paused: false },
        };
        if ("providerPlayer" in resolved) {
          return () => openProviderSession(resolved.providerPlayer, entry, report, chapterNav, nextChoices);
        }
        const safeUrl = remoteUrl(resolved.url);
        if (!safeUrl) throw new Error(ui("La fuente devolvió una dirección inválida."));
        const rawTracks = resolved.tracks?.length ? resolved.tracks : choice.tracks;
        const tracks = (rawTracks ?? []).flatMap(track => {
          const trackUrl = remoteUrl(track.url);
          return trackUrl ? [{ ...track, url: trackUrl }] : [];
        });
        return () => play(entry, "video", [safeUrl], report, resolved.hls, { ...resolved, url: safeUrl, tracks }, chapterNav, nextChoices);
      } : undefined,
    },
  );
}

type VideoResolution = Stream | { providerPlayer: string };

function providerPlayerButton(name: string, address: string, external: boolean, entry: Entry, report: (message: string, error?: boolean) => void, nav?: ChapterNav, choices?: PlaybackChoices) {
  const button = node("button", external ? ui("Abrir ") + name + ui(" en el navegador") : ui("Abrir {0} en el reproductor del proveedor", name));
  button.type = "button";
  button.onclick = async () => {
    button.disabled = true;
    try {
      if (external) { await invoke("open_provider_player", {address}); return; }
      await openProviderSession(address, entry, report, nav, choices);
    } catch (e) {
      report(String(e), true);
    } finally {
      button.disabled = false;
    }
  };
  return button;
}

function isEmbeddedSource(source: Record<string, unknown>): boolean {
  return typeof source.mimeType === "string" && source.mimeType.split(";", 1)[0].trim().toLowerCase() === "text/html";
}

function catalogTracks(source: Record<string, unknown>): Subtitle[] {
  return records(source.tracks ?? source.subtitles).slice(0, 100).flatMap(raw => {
    const address = remoteUrl(raw.url ?? raw.source);
    if (!address) return [];
    const language = typeof raw.language === "string" ? raw.language : "und";
    return [{url:address, language, label:typeof raw.label === "string" ? raw.label : language, default:raw.default === true || raw.isDefault === true}];
  });
}

function catalogVideoChoice(source: Record<string, unknown>, index: number): VideoChoice | undefined {
  const url = remoteUrl(source.filePath);
  if (!url || source.mimeType === "application/pdf" || new URL(url).pathname.toLowerCase().endsWith(".pdf")) return;
  const label = [source.label, source.videoQuality, source.distributor].find(value => typeof value === "string" && value);
  const pathname = new URL(url).pathname;
  return {url, label:typeof label === "string" ? label : ui("Servidor {0}",index+1), embed:isEmbeddedSource(source),
    hls:/\.m3u8$/i.test(pathname), dash:/\.mpd$/i.test(pathname), tracks:catalogTracks(source)};
}

function catalogPlayback(entry: Entry, url: string): PlaybackChoices | undefined {
  const sources = entry.sources.flatMap((record,i) => catalogVideoChoice(record,i) ?? []);
  const index = sources.findIndex(choice => choice.url === url);
  if (index >= 0) return {sources,index,attempted:new Set([index])};
}

export function contentControls(
  item: PlayableItem,
  report: (message: string, error?: boolean) => void,
  highlightUnit?: ContentUnit,
  options: ContentControlsOptions = {},
): HTMLElement & {playFromDevice: (request: IncomingPlayback) => Promise<boolean>} {
  const isBook = item.mediaType.toLowerCase() === "book";
  const isAudio = item.mediaType.toLowerCase() === "audio";
  const available = entries(item, options.serverId);
  const hasUnits = available.some(e => e.target.unitKind !== null);
  const progressMap = new Map<number, Progress>();
  const television = document.documentElement.classList.contains("television");
  let collapseEntry: (() => void) | undefined;

  const section = node("section", undefined, "entries-section");
  const isAlive = () => section.isConnected && isPageActive("detail") && !options.signal?.aborted;

  // Seasons available (in ascending order)
  const seasons: { number: number; label: string }[] = [];
  const seenSeasons = new Set<number>();
  for (const entry of available) {
    if (entry.seasonNumber !== null && !seenSeasons.has(entry.seasonNumber)) {
      seenSeasons.add(entry.seasonNumber);
      seasons.push({ number: entry.seasonNumber, label: entry.seasonLabel || ui("Temporada {0}", entry.seasonNumber) });
    }
  }

  const listView = options.view ?? {};
  let selectedSeason: number | "all" = listView.season === "all" || (listView.season !== undefined && seenSeasons.has(listView.season)) ? listView.season :
    highlightUnit?.seasonNumber != null && seenSeasons.has(highlightUnit.seasonNumber)
      ? highlightUnit.seasonNumber
      : "all";
  let descending = listView.descending ?? false;
  let searchFilter = listView.search ?? "";
  let visibleLimit = listView.limit ?? 80;
  const profileId = activeProfileId();
  const enabledLinks = new Set(Object.entries(item.onlineProviders ?? {})
    .filter(([id, external]) => external && providerEnabled(profileId, id))
    .map(([id]) => id));
  const capibaraEnabled = providerEnabled(profileId, "capibaratraductor");
  const illustrated = ["manga", "comic"].includes(catalogGroup(item.mediaType, item.subtype));
  let capibaraLinked = illustrated && enabledLinks.has("capibaratraductor");
  const canFindChapter = illustrated &&
    (capibaraEnabled || [...READER_PROVIDERS].some(id => providerEnabled(profileId, id)));
  const canFindVideo = item.mediaType.toLowerCase() === "movie" && PROVIDERS.some(([id]) =>
    !READER_PROVIDERS.has(id) && providerEnabled(profileId, id));

  // Header row (Avalonia-style: heading + count + order toggle + mark-all-seen + local file tools)
  const headerBar = node("div", undefined, "entries-header");
  const titleGroup = node("div", undefined, "entries-title-group");
  const headingText = hasUnits
    ? (isBook ? ui("Capítulos") : isAudio ? ui("Pistas") : ui("Episodios"))
    : (isBook ? ui("Leer") : ui("Reproducir"));
  const headingEl = node("h2", headingText);
  const countBadge = node("span", hasUnits ? String(available.length) : "", "entries-count");
  countBadge.hidden = !hasUnits;
  titleGroup.append(headingEl, countBadge);

  const headerActions = node("div", undefined, "entries-toolbar-actions");

  const filterInput = node("input", undefined, "entries-filter-input") as HTMLInputElement;
  filterInput.type = "search";
  filterInput.readOnly = television;
  filterInput.placeholder = isBook ? ui("Buscar capítulo…") : isAudio ? ui("Buscar pista…") : ui("Buscar episodio…");
  filterInput.setAttribute("aria-label", filterInput.placeholder);
  filterInput.hidden = available.length <= 10;
  filterInput.value = searchFilter;

  const orderBtn = node("button", descending ? ui("↓ Descendente") : ui("↑ Ascendente"), "quiet-btn");
  orderBtn.type = "button";
  orderBtn.hidden = available.length <= 1;

  const toggleAllBtn = node("button", isBook ? ui("Marcar todo leído") : ui("Marcar todo visto"), "quiet-btn");
  toggleAllBtn.type = "button";
  toggleAllBtn.hidden = !hasUnits;
  toggleAllBtn.disabled = true;
  let progressLoaded = false;

  const pickBtn = node("button", ui("Abrir archivo…"), "quiet-btn");
  pickBtn.type = "button";
  const folderBtn = node("button", /Android|iP(?:ad|hone|od)/i.test(navigator.userAgent) ? ui("Elegir imágenes…") : ui("Carpeta de imágenes…"), "quiet-btn");
  folderBtn.type = "button";

  const hideDocPicker = isBook && navigator.userAgent.includes("UMD-NoDocumentPicker");
  if (!hideDocPicker) headerActions.append(pickBtn);
  if (isBook) headerActions.append(folderBtn);
  if (available.length > 1) headerActions.append(orderBtn);
  if (hasUnits) headerActions.append(toggleAllBtn);

  headerBar.append(titleGroup, filterInput, headerActions);
  section.append(headerBar);

  if (hideDocPicker) {
    const hint = node("p", ui("Para abrir un libro en este televisor, elegí una fuente del catálogo o un archivo de Descargas."), "muted");
    section.append(hint);
  }

  // Season pill selector (Avalonia style: wrap of buttons, NOT a dropdown!)
  const seasonBar = node("div", undefined, "season-bar");
  seasonBar.hidden = seasons.length <= 1;
  section.append(seasonBar);

  const listStatus = node("p", undefined, "entries-status");
  listStatus.setAttribute("role", "status");
  section.append(listStatus);

  const listHost = node("div", undefined, "entries-list");
  section.append(listHost);

  const showMoreBtn = node("button", ui("Mostrar más"), "load-more");
  showMoreBtn.type = "button";
  showMoreBtn.hidden = true;
  section.append(showMoreBtn);

  const launchLocalFile = async (entry: Entry, isFolder: boolean) => {
    pickBtn.disabled = folderBtn.disabled = true;
    try {
      const opened = await invoke<OpenedMedia | null>("open_media", { folder: isFolder });
      if (opened && isAlive()) {
        await play(entry, opened.kind, opened.paths.map(p => convertFileSrc(p, opened.streaming ? "umdstream" : "asset")), report, false, undefined, makeChapterNav(entry));
      }
    } catch (e) {
      report(String(e), true);
    } finally {
      pickBtn.disabled = folderBtn.disabled = false;
    }
  };

  pickBtn.onclick = () => void launchLocalFile(bestDefaultEntry(), false);
  folderBtn.onclick = () => void launchLocalFile(bestDefaultEntry(), true);

  function matchProgressRow(entry: Entry, rows: ProgressRow[]): Progress | undefined {
    for (const row of rows) {
      const c = row.content ?? {};
      const targetMatch = entry.target.unitKind === null
        ? !c.unitKind && !c.displayNumber
        : samePartyUnit(entry.target, {
            unitKind: c.unitKind ?? null,
            seasonNumber: c.seasonNumber ?? null,
            displayNumber: c.displayNumber ?? null,
          });
      if (targetMatch) {
        return {
          position: row.position,
          positionKind: row.positionKind,
          isCompleted: row.isCompleted,
          updatedAt: row.updatedAt,
        };
      }
    }
    return undefined;
  }

  function bestDefaultEntry(): Entry {
    if (highlightUnit) {
      const found = available.find(e => samePartyUnit(e.target, highlightUnit));
      if (found) return found;
    }
    let latestInProgress: { entry: Entry; ts: number } | null = null;
    let highestCompletedIdx = -1;
    for (const entry of available) {
      const p = progressMap.get(entry.index);
      if (!p) continue;
      if (!p.isCompleted && p.position > 0) {
        const ts = p.updatedAt ? Date.parse(p.updatedAt) || 0 : 0;
        if (!latestInProgress || ts >= latestInProgress.ts) latestInProgress = { entry, ts };
      } else if (p.isCompleted) {
        highestCompletedIdx = Math.max(highestCompletedIdx, entry.index);
      }
    }
    if (latestInProgress) return latestInProgress.entry;
    if (highestCompletedIdx >= 0 && highestCompletedIdx + 1 < available.length) {
      return available[highestCompletedIdx + 1];
    }
    return available[0];
  }

  // Primary launcher for an entry (used both by the entry card and by prev/next chapter navigation & hero CTA!)
  function primaryProviderFor(entry: Entry): { id: string; name: string; reader: boolean } | null {
    const number = entry.target.displayNumber;
    if (!number) return null;
    for (const [id, name] of PROVIDERS) {
      if (!enabledLinks.has(id)) continue;
      const reader = READER_PROVIDERS.has(id);
      if (reader && !illustrated) continue;
      if (entry.target.unitKind !== (reader ? "chapter" : "episode")) continue;
      return { id, name, reader };
    }
    return null;
  }

  type FoundSource = { site: string; title: string; edition: unknown };

  async function openDiscoveredSource(entry: Entry, preferred: string | undefined, open: (site: string, content: string[] | VideoChoice[]) => Promise<boolean>) {
    if (!isAlive()) return false;
    const args = {id: item.syncId, number: entry.target.displayNumber, season: entry.target.seasonNumber,
      hideAdult: hideAdult()};
    const found: FoundSource[] = [];
    const matches = new Channel<FoundSource[]>();
    let finished = false;
    let wake: (() => void) | undefined;
    const notify = () => wake?.();
    matches.onmessage = batch => { found.push(...batch); notify(); };
    options.signal?.addEventListener("abort", notify, {once: true});
    void invokeProfile("find_unit_sources", {...args, matches}).catch(() => {}).finally(() => {
      finished = true;
      notify();
    });
    try {
      let attempts = 0;
      while (isAlive() && attempts < 6) {
        if (!found.length) {
          if (finished) break;
          await new Promise<void>(resolve => { wake = resolve; });
          wake = undefined;
          continue;
        }
        const preferredIndex = found.findIndex(choice => choice.site === preferred);
        const [choice] = found.splice(Math.max(0, preferredIndex), 1);
        attempts++;
        try {
          const content = await invoke<string[] | VideoChoice[]>("select_unit_source", {...args, choice});
          if (isAlive() && await open(choice.site, content)) {
            enabledLinks.add(choice.site);
            window.dispatchEvent(new Event("umd-catalog-changed"));
            return true;
          }
        } catch { /* Try a different matched edition. */ }
      }
      return false;
    } finally {
      matches.onmessage = () => {};
      options.signal?.removeEventListener("abort", notify);
    }
  }

  async function openEntryQuick(entry: Entry, preferred?: string): Promise<boolean> {
    if (television && section.hasAttribute("data-provider-busy")) return true;
    if (television) section.dataset.providerBusy = "";
    try {
      return await openEntry(entry, preferred);
    } finally {
      if (television) delete section.dataset.providerBusy;
    }
  }

  async function openEntry(entry: Entry, preferred?: string): Promise<boolean> {
    if (entry.incoming?.signal.aborted) return false;
    const nav = makeChapterNav(entry, preferred);
    const prov = primaryProviderFor(entry);
    const number = entry.target.displayNumber;
    if (illustrated && entry.target.unitKind === "chapter" && number) {
      const candidates = [...new Set([
        preferred,
        ...PROVIDERS.filter(([id]) => READER_PROVIDERS.has(id) && enabledLinks.has(id)).map(([id]) => id),
        capibaraLinked ? "capibaratraductor" : undefined,
      ])].filter((id): id is string => !!id && providerEnabled(profileId, id));
      const args = {id: item.syncId, number, season: entry.target.seasonNumber};
      const read = async (site: string, raw: string[]) => {
        const urls = raw.map(remoteUrl).filter((url): url is string => url !== null);
        if (!urls.length || !isAlive()) return false;
        listStatus.textContent = "";
        await play(entry, "images", urls, report, false, undefined, makeChapterNav(entry, preferred ?? site));
        return true;
      };
      listStatus.textContent = ui("Abriendo {0}…", entry.unitHeading);
      try {
        for (const site of candidates) {
          if (!isAlive()) return false;
          try {
            const raw = site === "capibaratraductor"
              ? await invoke<string[]>("capibara_pages", {id: item.syncId, chapter: number})
              : await invoke<string[]>("provider_unit", {...args, site});
            if (await read(site, raw)) return true;
          } catch { /* The chapter may be absent in this edition. */ }
        }
        if ((preferred || television) && canFindChapter && isAlive() &&
            await openDiscoveredSource(entry, preferred, (site, content) => read(site, content as string[]))) {
          return true;
        }
      } catch (error) {
        if (isAlive()) report(String(error), true);
      } finally {
        listStatus.textContent = "";
      }
    }
    async function openDirect() {
      for (const source of entry.sources) {
        const url = remoteUrl(source.filePath);
        if (url && await openCatalogSource(entry, source, url, nav)) return true;
      }
      return false;
    }
    const fromDevice = !!entry.incoming || receivingTitle(item.syncId);
    const providerFirst = fromDevice && !!preferred;
    if (!providerFirst && await openDirect()) return true;
    if ((television || fromDevice) && canFindVideo && (entry.target.unitKind?.toLowerCase() === "episode" || entry.target.unitKind === null && !hasUnits)) {
      const mediaNumber = number ?? (hasUnits ? null : "1");
      const favored = profileId ? preferredProvidersForProfile(profileId) : [];
      const candidates = [...new Set([preferred, ...favored, ...PROVIDERS.map(([id]) => id)])]
        .filter((id): id is string => !!id && enabledLinks.has(id) && !READER_PROVIDERS.has(id) && id !== "capibaratraductor");
      report(ui("Buscando video…"));
      try {
        if (mediaNumber) {
          for (const site of candidates) {
          if (!isAlive() || entry.incoming?.signal.aborted) return false;
            try {
              const sources = await invoke<VideoChoice[]>("provider_unit", {
                site, id: item.syncId, number: mediaNumber, season: entry.target.seasonNumber,
              });
              if (await openProviderVideos(entry, sources, site, report, isAlive)) return true;
            } catch { /* Try the next linked provider. */ }
          }
        }
        if (providerFirst && await openDirect()) return true;
        return await openDiscoveredSource(entry, preferred,
          (site, content) => openProviderVideos(entry, content as VideoChoice[], site, report, isAlive));
      } finally {
        if (isAlive()) report("");
      }
    }
    if (providerFirst && await openDirect()) return true;
    if (fromDevice) return false;
    if ((television || preferred) && illustrated && entry.target.unitKind === "chapter") return false;
    // 4. Video provider -> trigger inline picker on card or scroll card into view
    if (prov && !prov.reader || canFindChapter || canFindVideo) {
      const targetCard = listHost.querySelector<HTMLElement>(`[data-entry-index="${entry.index}"]`);
      if (targetCard) {
        targetCard.scrollIntoView({ behavior: "smooth", block: "nearest" });
        const triggers = Array.from(targetCard.querySelectorAll<HTMLButtonElement>("[data-provider-trigger], [data-source-discovery]"));
        const trigger = (preferred ? triggers.find(button => button.dataset.providerTrigger === preferred) : undefined) ?? triggers[0];
        trigger?.click();
        return !!trigger;
      }
    }
    return false;
  }

  async function openCatalogSource(entry: Entry, source: Record<string, unknown>, url: string, nav?: ChapterNav) {
    if (entry.incoming?.signal.aborted) return false;
    const format = isBook ? bookArchive(url, source.mimeType) : null;
    const kind = source.mimeType === "application/pdf" || new URL(url).pathname.toLowerCase().endsWith(".pdf")
      ? "pdf"
      : isAudio
        ? "audio"
        : "video";
    const choices = kind === "video" ? catalogPlayback(entry,url) : undefined;
    const selected = choices?.sources[choices.index];
    try {
      if (format) {
        listStatus.textContent = ui("Abriendo libro…");
        const opened = await invoke<OpenedMedia>("open_catalog_book", { address: url, format });
        listStatus.textContent = "";
        if (!isAlive() || entry.incoming?.signal.aborted) return false;
        await play(entry, opened.kind, opened.paths.map(p => convertFileSrc(p, opened.streaming ? "umdstream" : "asset")), report, false, undefined, nav);
      } else if (kind === "video" && isEmbeddedSource(source)) {
        listStatus.textContent = ui("Buscando video…");
        const stream = await invoke<VideoResolution>("resolve_video", { address: url, webFallback: true });
        listStatus.textContent = "";
        if (!isAlive()) return false;
        if ("providerPlayer" in stream) {
          await openProviderSession(stream.providerPlayer, entry, report, nav, choices);
          return true;
        }
        const safeUrl = remoteUrl(stream.url);
        if (!safeUrl) throw new Error(ui("La fuente devolvió una dirección inválida."));
        const tracks = (stream.tracks?.length ? stream.tracks : selected?.tracks ?? []).flatMap(track => {
          const trackUrl = remoteUrl(track.url);
          return trackUrl ? [{ ...track, url: trackUrl }] : [];
        });
        await play(entry, kind, [safeUrl], report, stream.hls, { ...stream, url: safeUrl, tracks }, nav, choices);
      } else {
        const tracks = catalogTracks(source);
        const direct = selected ?? (tracks.length ? {url,hls:/\.m3u8$/i.test(new URL(url).pathname),tracks} : undefined);
        if (!isAlive()) return false;
        await play(entry, kind, [url], report, direct?.hls ?? false, direct, nav, choices);
      }
      return true;
    } catch (e) {
      listStatus.textContent = "";
      report(String(e), true);
      return false;
    }
  }

  async function startTvEntry(entry: Entry) {
    try {
      if (await openEntryQuick(entry) || !isAlive()) return;
      const optionsButton = listHost.querySelector<HTMLButtonElement>(`[data-entry-index="${entry.index}"] .entry-alternatives[aria-expanded='false']`);
      optionsButton?.click();
      report(ui("No se encontraron proveedores para esta unidad."), true);
    } catch (error) {
      if (isAlive()) report(String(error), true);
    }
  }

  function makeChapterNav(entry: Entry, preferred?: string): ChapterNav | undefined {
    if (entry.index < 0 || available.length <= 1) return undefined;
    const prevEntry = entry.index > 0 ? available[entry.index - 1] : null;
    const nextEntry = entry.index + 1 < available.length ? available[entry.index + 1] : null;
    return {
      hasPrev: !!prevEntry,
      hasNext: !!nextEntry,
      prevLabel: prevEntry ? prevEntry.unitHeading : "",
      nextLabel: nextEntry ? nextEntry.unitHeading : "",
      openPrev: () => { if (prevEntry) void openEntryQuick(prevEntry, preferred); },
      openNext: () => { if (nextEntry) void openEntryQuick(nextEntry, preferred).then(opened => {
        if (!opened && isAlive()) report(ui("No se encontraron proveedores para esta unidad."), true);
      }); },
    };
  }

  function updateContinueCta() {
    if (!options.onContinueReady) return;
    if (!available.length) {
      options.onContinueReady(null);
      return;
    }
    const targetEntry = bestDefaultEntry();
    const p = progressMap.get(targetEntry.index);
    let label: string;
    if (p && !p.isCompleted && p.position > 0) {
      const detail = p.positionKind === "page" ? ui("Pág. {0}", Math.floor(p.position)) : formatTime(p.position);
      label = hasUnits
        ? ui("Continuar · {0} · {1}", targetEntry.unitHeading, detail)
        : ui("Continuar · {0}", detail);
    } else if (hasUnits && progressMap.size > 0 && targetEntry.index > 0) {
      label = ui("Siguiente · {0}", targetEntry.unitHeading);
    } else if (hasUnits) {
      label = isBook
        ? ui("Empezar · {0}", targetEntry.unitHeading)
        : ui("Reproducir · {0}", targetEntry.unitHeading);
    } else {
      label = isBook ? ui("Leer") : ui("Reproducir");
    }
    options.onContinueReady({
      label,
      run: () => {
        if (television) {
          void initialProgress.then(() => { if (isAlive()) return startTvEntry(bestDefaultEntry()); });
          return;
        }
        if (targetEntry.seasonNumber !== null && selectedSeason !== "all" && selectedSeason !== targetEntry.seasonNumber) {
          selectedSeason = targetEntry.seasonNumber;
          renderSeasons();
          renderEntries();
        }
        void openEntryQuick(targetEntry).then(opened => {
          if (!opened) {
            const card = listHost.querySelector<HTMLElement>(`[data-entry-index="${targetEntry.index}"]`);
            if (card) card.scrollIntoView({ behavior: "smooth", block: "center" });
            else void launchLocalFile(targetEntry, false);
          }
        });
      },
    });
  }

  function filteredEntries(): Entry[] {
    let list = available;
    if (selectedSeason !== "all") {
      list = list.filter(e => e.seasonNumber === selectedSeason);
    }
    const q = searchFilter.trim().toLocaleLowerCase();
    if (q) {
      list = list.filter(e =>
        e.label.toLocaleLowerCase().includes(q) ||
        (e.target.displayNumber ?? "").toLocaleLowerCase().includes(q)
      );
    }
    return descending ? [...list].reverse() : list;
  }

  function updateToggleAllLabel() {
    const scope = filteredEntries().filter(e => e.target.unitKind !== null);
    if (!scope.length) {
      toggleAllBtn.hidden = true;
      return;
    }
    toggleAllBtn.hidden = false;
    const allSeen = scope.every(e => progressMap.get(e.index)?.isCompleted === true);
    toggleAllBtn.textContent = allSeen
      ? (isBook ? ui("Desmarcar leídos") : ui("Desmarcar vistos"))
      : (isBook ? ui("Marcar todo leído") : ui("Marcar todo visto"));
  }

  function renderSeasons() {
    seasonBar.replaceChildren();
    if (seasons.length <= 1) {
      seasonBar.hidden = true;
      return;
    }
    seasonBar.hidden = false;
    const allBtn = node("button", ui("Todas las temporadas"), "season-chip" + (selectedSeason === "all" ? " active" : ""));
    allBtn.type = "button";
    allBtn.setAttribute("aria-pressed", String(selectedSeason === "all"));
    allBtn.onclick = () => {
      selectedSeason = "all";
      visibleLimit = 80;
      renderSeasons();
      renderEntries();
    };
    seasonBar.append(allBtn);
    for (const s of seasons) {
      const btn = node("button", s.label, "season-chip" + (selectedSeason === s.number ? " active" : ""));
      btn.type = "button";
      btn.setAttribute("aria-pressed", String(selectedSeason === s.number));
      btn.onclick = () => {
        selectedSeason = s.number;
        visibleLimit = 80;
        renderSeasons();
        renderEntries();
      };
      seasonBar.append(btn);
    }
  }

  async function openProviderVideos(
    entry: Entry, sources: VideoChoice[], provider: string,
    feedback: (message: string, error?: boolean) => void, isCurrent: () => boolean,
  ) {
    const choices = sources.map(source => ({...source, provider}));
    const attempted = new Set<number>();
    const nav = makeChapterNav(entry, provider);
    let failure: unknown;
    for (const [index, source] of choices.entries()) {
      if (!isCurrent() || entry.incoming?.signal.aborted) return false;
      attempted.add(index);
      try {
        const media = source.external || source.embed
          ? await invoke<VideoResolution>("resolve_video", { address: source.url, provider, webFallback: true })
          : source;
        if (!isCurrent() || entry.incoming?.signal.aborted) return false;
        const playback: PlaybackChoices = {sources: choices, index, attempted};
        feedback("");
        if ("providerPlayer" in media) {
          await openProviderSession(media.providerPlayer, entry, feedback, nav, playback);
          return true;
        }
        const safeUrl = remoteUrl(media.url);
        if (!safeUrl) throw new Error(ui("La fuente devolvió una dirección inválida."));
        const tracks = media.tracks?.length ? media.tracks : source.tracks;
        await play(entry, "video", [safeUrl], feedback, media.hls, {...media, url: safeUrl, tracks}, nav, playback);
        return true;
      } catch (error) {
        failure = error;
      }
    }
    if (isCurrent()) throw failure ?? new Error(ui("No se encontraron servidores para este episodio."));
    return false;
  }

  function buildEntryCard(entry: Entry): HTMLElement {
    const isHighlighted = !!highlightUnit && samePartyUnit(entry.target, highlightUnit);

    const card = node(
      "article",
      undefined,
      "entry-card" +
        (isHighlighted ? " is-highlighted" : ""),
    );
    card.dataset.entryIndex = String(entry.index);

    const topRow = node("div", undefined, "entry-card-top");

    // Number badge
    const numPill = node(
      "div",
      entry.target.displayNumber ?? (isBook ? "📖" : "▶"),
      "entry-number",
    );

    // Title & meta
    const meta = node("div", undefined, "entry-meta");
    const titleRow = node("div", undefined, "entry-heading-row");
    titleRow.append(node("h3", entry.unitHeading));
    titleRow.append(node("span", undefined, "entry-badge"));
    meta.append(titleRow);
    if (entry.unitName) {
      meta.append(node("p", entry.unitName, "entry-subtitle"));
    }
    const selectEntry = television ? node("button", undefined, "entry-select") : undefined;
    if (selectEntry) {
      selectEntry.type = "button";
      selectEntry.append(numPill, meta);
      selectEntry.onclick = () => { void startTvEntry(entry); };
      topRow.append(selectEntry);
    } else {
      topRow.append(numPill, meta);
    }

    // Action buttons
    const actions = node("div", undefined, "entry-actions");
    const serverTray = node("div", undefined, "entry-servers");
    serverTray.hidden = true;
    const providerStatus = node("p", undefined, "entry-provider-status");
    providerStatus.setAttribute("role", "status");
    const reportProvider = (message: string, error = false) => {
      if (!isAlive() || !card.isConnected) return;
      providerStatus.textContent = message;
      providerStatus.classList.toggle("error", error);
    };
    const number = entry.target.displayNumber;
    let hasRemoteAction = false;
    let discovery = 0;

    // 1. Online providers (manga readers + video providers)
    for (const [provider, name] of PROVIDERS) {
      if (!number || !enabledLinks.has(provider)) continue;
      const reader = READER_PROVIDERS.has(provider);
      if (reader && !illustrated) continue;
      if (entry.target.unitKind !== (reader ? "chapter" : "episode")) continue;
      hasRemoteAction = true;
      const openBtn = node("button", reader ? ui("Leer en ") + name : ui("Ver en ") + name, "primary entry-primary");
      openBtn.type = "button";
      openBtn.dataset.providerTrigger = provider;
      openBtn.onclick = async () => {
        const request = ++discovery;
        const isCurrent = () => request === discovery && isAlive() && card.isConnected;
        card.dataset.providerBusy = "";
        openBtn.disabled = true;
        reportProvider(reader ? ui("Abriendo capítulo…") : ui("Buscando video…"));
        serverTray.replaceChildren();
        serverTray.hidden = true;
        const args = { site: provider, id: item.syncId, number, season: entry.target.seasonNumber };
        try {
          if (reader) {
            const urls = (await invoke<string[]>("provider_unit", args))
              .map(remoteUrl)
              .filter((u): u is string => u !== null);
            if (!urls.length) throw new Error(ui("La fuente devolvió una dirección inválida."));
            if (isCurrent()) {
              reportProvider("");
              await play(entry, "images", urls, reportProvider, false, undefined, makeChapterNav(entry, provider));
            }
          } else {
            const sources = await invoke<VideoChoice[]>("provider_unit", args);
            await openProviderVideos(entry, sources, provider, reportProvider, isCurrent);
          }
        } catch (e) {
          if (isCurrent()) reportProvider(String(e), true);
        } finally {
          if (request === discovery) delete card.dataset.providerBusy;
          openBtn.disabled = false;
        }
      };
      actions.append(openBtn);
    }

    // 2. CapibaraTraductor / chapter lookup for books
    if (isBook && entry.target.unitKind === "chapter" && entry.target.seasonNumber === null) {
      const showLookup = capibaraLinked;
      if (showLookup) {
        hasRemoteAction = true;
        const readBtn = node(
          "button",
          ui("Leer en CapibaraTraductor"),
          "primary entry-primary",
        );
        readBtn.type = "button";
        readBtn.onclick = async () => {
          card.dataset.providerBusy = "";
          readBtn.disabled = true;
          reportProvider(ui("Abriendo capítulo…"));
          try {
            const rawUrls = await invoke<string[]>("capibara_pages", { id: item.syncId, chapter: entry.target.displayNumber });
            const urls = rawUrls.map(remoteUrl).filter((u): u is string => u !== null);
            if (!urls.length) throw new Error(ui("La fuente devolvió una dirección inválida."));
            reportProvider("");
            if (isAlive() && card.isConnected) await play(entry, "images", urls, reportProvider, false, undefined, makeChapterNav(entry, "capibaratraductor"));
          } catch (e) {
            reportProvider(String(e), true);
          } finally {
            delete card.dataset.providerBusy;
            readBtn.disabled = false;
          }
        };
        actions.append(readBtn);
      }
    }

    const findChapter = canFindChapter && entry.target.unitKind === "chapter" && entry.target.seasonNumber === null;
    const findVideo = canFindVideo && (entry.target.unitKind === "episode" || entry.target.unitKind === null && !hasUnits);
    if (findChapter || findVideo) {
      hasRemoteAction = true;
      const find = node("button", findChapter ? ui("Buscar capítulo en proveedores") : ui("Buscar video en proveedores"), "quiet-btn");
      find.type = "button";
      find.dataset.sourceDiscovery = "";
      find.onclick = async () => {
        const request = ++discovery;
        const isCurrent = () => request === discovery && isAlive() && card.isConnected;
        const initialLabel = ui("Buscando…");
        const header = node("div", undefined, "entry-servers-header");
        const banner = node("span", initialLabel);
        const close = node("button", ui("Cerrar"), "quiet-btn");
        const buttons = node("div", undefined, "button-row");
        close.type = "button";
        close.onclick = () => {
          ++discovery;
          delete card.dataset.providerBusy;
          serverTray.replaceChildren();
          serverTray.hidden = true;
        };
        header.append(banner, close);
        serverTray.replaceChildren(header, buttons);
        serverTray.hidden = false;
        card.dataset.providerBusy = "";
        reportProvider("");
        const args = {
          id: item.syncId, number, season: entry.target.seasonNumber,
          hideAdult: hideAdult(),
        };
        const matches = new Channel<FoundSource[]>();
        matches.onmessage = found => {
          if (!isCurrent()) return;
          for (const choice of found) {
            const name = PROVIDERS.find(([id]) => id === choice.site)?.[1] ?? "CapibaraTraductor";
            const button = node("button", name + " · " + choice.title, "quiet-btn");
            button.type = "button";
            button.onclick = async () => {
              buttons.inert = true;
              reportProvider(ui("Abriendo fuente…"));
              try {
                const result = await invoke<string[] | VideoChoice[]>("select_unit_source", { ...args, choice });
                if (!isCurrent()) return;
                reportProvider("");
                window.dispatchEvent(new Event("umd-catalog-changed"));
                if (findChapter) {
                  const urls = (result as string[]).map(remoteUrl).filter((u): u is string => u !== null);
                  if (!urls.length) throw new Error(ui("La fuente devolvió una dirección inválida."));
                  await play(entry, "images", urls, reportProvider, false, undefined, makeChapterNav(entry, choice.site));
                } else {
                  await openProviderVideos(entry, result as VideoChoice[], choice.site, reportProvider, isCurrent);
                }
              } catch (error) {
                if (isCurrent()) reportProvider(String(error), true);
              } finally {
                buttons.inert = false;
              }
            };
            buttons.append(button);
            if (television && document.activeElement === find) button.focus({preventScroll: true});
          }
        };
        try {
          await invokeProfile("find_unit_sources", { ...args, matches });
          if (isCurrent()) banner.textContent = buttons.childElementCount
            ? ui("Elegí una edición")
            : ui("No se encontraron proveedores para esta unidad.");
        } catch (error) {
          if (isCurrent()) reportProvider(String(error), true);
        } finally {
          if (isCurrent()) delete card.dataset.providerBusy;
        }
      };
      actions.append(find);
    }

    // 3. Direct catalog sources (filePath / torrent / downloaded torrent files)
    for (const source of entry.sources) {
      const specs = [source.videoQuality, source.container || source.mimeType, typeof source.sizeBytes === "number" && source.sizeBytes > 0 ? (source.sizeBytes / 1048576).toLocaleString(undefined,{maximumFractionDigits:1}) + " MiB" : ""].filter(v => typeof v === "string" && v);
      if (specs.length) actions.append(node("small", specs.join(" · "), "muted"));
      const torrent = source.torrent as Record<string, unknown> | undefined;
      const magnet = typeof torrent?.magnetUri === "string" ? torrent.magnetUri : null;
      const metadata = typeof torrent?.torrentMetadataBase64 === "string" ? torrent.torrentMetadataBase64 : null;
      const sequence = Array.isArray(source.umdFileIndices);
      const indices = sequence ? source.umdFileIndices : typeof source.fileIndex === "number" ? [source.fileIndex] : [];
      if (torrent && typeof torrent.infoHashV1 === "string" && Array.isArray(indices) && indices.length) {
        hasRemoteAction = true;
        const localBtn = node("button", sequence ? ui("Leer imágenes descargadas") : ui("Abrir archivo descargado"), "primary");
        localBtn.type = "button";
        localBtn.onclick = async () => {
          localBtn.disabled = true;
          try {
            const media = await invoke<OpenedMedia>("catalog_download_files", {
              hash: torrent.infoHashV1,
              private: torrent.isPrivate === true,
              indices,
              sequence,
            });
            if (isAlive()) await play(entry, media.kind, media.paths.map(p => convertFileSrc(p, media.streaming ? "umdstream" : "asset")), report, false, undefined, makeChapterNav(entry));
          } catch (e) {
            report(String(e), true);
          } finally {
            localBtn.disabled = false;
          }
        };
        actions.append(localBtn);
      }
      if (magnet || metadata) {
        hasRemoteAction = true;
        const downloadBtn = node("button", ui("Descargar por BitTorrent"), "quiet-btn");
        downloadBtn.type = "button";
        downloadBtn.onclick = () => {
          const marked = magnet ? /[?&]x\.umd\.private=(?:1|true)(?:&|$)/i.test(magnet) : false;
          const routes = records(torrent?.trackers).filter(t => t.clientFamily !== "browser" && t.transport !== "wss");
          const trackers = torrent?.isPrivate || marked
            ? routes.flatMap(t => typeof t.endpoint === "string"
                ? [{ endpoint: t.endpoint, requiresAuthentication: t.requiresAuthentication === true, credentialKey: typeof t.credentialKey === "string" ? t.credentialKey : undefined }]
                : [])
            : null;
          queueCatalogDownload(magnet, metadata, entry.target, trackers);
        };
        actions.append(downloadBtn);
      }
      const url = remoteUrl(source.filePath);
      if (!url) continue;
      hasRemoteAction = true;
      const format = isBook ? bookArchive(url, source.mimeType) : null;
      const label = format ? ui("Leer {0}", format.toUpperCase()) : ui("Abrir fuente {0}", typeof source.language === "string" ? source.language : "online");
      const openBtn = node("button", label, "primary");
      openBtn.type = "button";
      openBtn.onclick = async () => {
        openBtn.disabled = true;
        try {
          await openCatalogSource(entry, source, url, makeChapterNav(entry));
        } finally {
          openBtn.disabled = false;
        }
      };
      actions.append(openBtn);
      if (!isBook && isEmbeddedSource(source) && isHttpsUrl(url)) {
        actions.append(providerPlayerButton(ui("la página del proveedor"), url, false, entry, report, makeChapterNav(entry), catalogPlayback(entry,url)));
      }
    }

    // Fallback: open local file for this specific entry if no remote sources exist
    if (!hasRemoteAction && !hideDocPicker) {
      const localPick = node("button", ui("Abrir archivo…"), "quiet-btn");
      localPick.type = "button";
      localPick.onclick = () => void launchLocalFile(entry, false);
      actions.append(localPick);
    }

    // 4. Mark Seen / Unseen toggle (Avalonia parity!)
    const seenBtn = node("button", undefined, "seen-toggle");
    seenBtn.type = "button";
    seenBtn.onclick = async () => {
      const nextCompleted = !(progressMap.get(entry.index)?.isCompleted === true);
      const kind = isBook ? "page" : "seconds";
      seenBtn.disabled = true;
      seenBtn.dataset.saving = "";
      try {
        const scope = nextCompleted && entry.target.unitKind ? available.filter(e => e.index <= entry.index && e.target.unitKind === entry.target.unitKind && !progressMap.get(e.index)?.isCompleted) : [entry];
        const batch = scope.map(e => ({ target:e.target, position:nextCompleted ? Math.max(1,progressMap.get(e.index)?.position || 1) : 0, kind, completed:nextCompleted }));
        await invokeProfile("save_progress_batch", { entries:batch });
        scope.forEach((e,i) => progressMap.set(e.index, {position:batch[i].position, positionKind:kind, isCompleted:nextCompleted, updatedAt:new Date().toISOString()}));
        updateVisibleProgress();
        updateToggleAllLabel();
        updateContinueCta();
        options.onProgressChanged?.();
      } catch (e) {
        report(String(e), true);
      } finally {
        delete seenBtn.dataset.saving;
        updateCardProgress(card);
      }
    };
    actions.append(seenBtn);
    if (entry.target.unitKind && options.onUnitsChanged) {
      const remove = node("button", ui("Eliminar unidad"), "danger-ghost-btn");
      remove.type = "button";
      remove.onclick = () => {
        const confirm = node("button", ui("Confirmar eliminación"), "danger-btn");
        confirm.type = "button";
        const cancel = node("button", ui("Cancelar"), "quiet-btn");
        cancel.type = "button";
        cancel.onclick = () => { confirm.replaceWith(remove); cancel.remove(); };
        confirm.onclick = async () => {
          confirm.disabled = cancel.disabled = true;
          try { await invoke("delete_unit",{target:entry.target}); options.onUnitsChanged?.(); }
          catch(e) { report(String(e),true); confirm.disabled = cancel.disabled = false; }
        };
        remove.replaceWith(confirm,cancel); cancel.focus();
      };
      actions.append(remove);
    }

    if (selectEntry) {
      const alternatives = node("button", ui("Opciones"), "quiet-btn entry-alternatives");
      alternatives.type = "button";
      alternatives.setAttribute("aria-expanded", "false");
      topRow.append(alternatives);
      actions.hidden = true;
      const close = () => {
        ++discovery;
        delete card.dataset.providerBusy;
        actions.hidden = true;
        serverTray.hidden = true;
        serverTray.replaceChildren();
        reportProvider("");
        alternatives.setAttribute("aria-expanded", "false");
      };
      alternatives.onclick = () => {
        if (!actions.hidden) {
          close();
          collapseEntry = undefined;
          return;
        }
        collapseEntry?.();
        collapseEntry = close;
        actions.hidden = false;
        alternatives.setAttribute("aria-expanded", "true");
        const firstAction = actions.querySelector<HTMLButtonElement>("button:enabled");
        firstAction?.focus({preventScroll: true});
        firstAction?.scrollIntoView({block: "nearest", inline: "nearest"});
      };
      card.append(topRow, actions);
    } else {
      topRow.append(actions);
      card.append(topRow);
    }
    card.append(serverTray, providerStatus);
    return card;
  }

  function updateCardProgress(card: HTMLElement) {
    const badge = progressBadgeText(progressMap.get(Number(card.dataset.entryIndex)), isBook);
    const seen = badge.kind === "seen";
    card.classList.toggle("is-seen", seen);
    card.classList.toggle("is-in-progress", badge.kind === "progress");
    const tag = card.querySelector<HTMLElement>(".entry-badge")!;
    tag.className = `entry-badge ${badge.kind}`;
    tag.textContent = (seen ? "✓ " : "") + badge.text;
    tag.hidden = !badge.text;
    const button = card.querySelector<HTMLButtonElement>(".seen-toggle")!;
    button.classList.toggle("active", seen);
    button.textContent = seen ? (isBook ? ui("✓ Leído") : ui("✓ Visto")) : (isBook ? ui("Marcar leído") : ui("Marcar visto"));
    button.setAttribute("aria-pressed", String(seen));
    button.disabled = !progressLoaded || Object.hasOwn(button.dataset, "saving");
  }
  function updateVisibleProgress() {
    listHost.querySelectorAll<HTMLElement>(".entry-card").forEach(updateCardProgress);
  }

  function renderEntries() {
    Object.assign(listView, {season:selectedSeason, descending, search:searchFilter, limit:visibleLimit});
    collapseEntry = undefined;
    listHost.replaceChildren();
    const list = filteredEntries();
    if (!list.length) {
      listHost.append(node("p", ui("No se encontraron resultados con este filtro."), "muted"));
      showMoreBtn.hidden = true;
      updateToggleAllLabel();
      return;
    }

    // Ensure highlighted unit is within visibleLimit
    if (highlightUnit) {
      const idx = list.findIndex(e => samePartyUnit(e.target, highlightUnit));
      if (idx >= visibleLimit) visibleLimit = idx + 20;
      listView.limit = visibleLimit;
    }

    const slice = list.slice(0, visibleLimit);
    const frag = document.createDocumentFragment();
    let lastSeason: number | null | undefined = undefined;
    const showSeasonDividers = selectedSeason === "all" && seasons.length > 0;

    for (const entry of slice) {
      if (showSeasonDividers && entry.seasonNumber !== null && entry.seasonNumber !== lastSeason) {
        lastSeason = entry.seasonNumber;
        const divider = node("div", entry.seasonLabel || ui("Temporada {0}", entry.seasonNumber), "season-divider");
        frag.append(divider);
      }
      frag.append(buildEntryCard(entry));
    }
    listHost.append(frag);
    updateVisibleProgress();

    const remaining = list.length - slice.length;
    showMoreBtn.hidden = remaining <= 0;
    if (remaining > 0) {
      showMoreBtn.textContent = ui("Mostrar más ({0} restantes)", remaining);
    }
    updateToggleAllLabel();
  }

  filterInput.oninput = () => {
    searchFilter = filterInput.value;
    visibleLimit = 80;
    renderEntries();
  };

  orderBtn.onclick = () => {
    descending = !descending;
    orderBtn.textContent = descending ? ui("↓ Descendente") : ui("↑ Ascendente");
    renderEntries();
  };

  showMoreBtn.onclick = () => {
    visibleLimit += 100;
    renderEntries();
  };

  toggleAllBtn.onclick = async () => {
    const scope = filteredEntries().filter(e => e.target.unitKind !== null);
    if (!scope.length) return;
    const allSeen = scope.every(e => progressMap.get(e.index)?.isCompleted === true);
    const nextCompleted = !allSeen;
    const kind = isBook ? "page" : "seconds";
    toggleAllBtn.disabled = true;
    try {
      const batch = scope.map(e => ({
        target: e.target,
        position: nextCompleted ? Math.max(1, progressMap.get(e.index)?.position ?? 1) : 0,
        kind,
        completed: nextCompleted,
      }));
      await invokeProfile("save_progress_batch", { entries: batch });
      const stamp = new Date().toISOString();
      for (const e of scope) {
        progressMap.set(e.index, {
          position: nextCompleted ? Math.max(1, progressMap.get(e.index)?.position ?? 1) : 0,
          positionKind: kind,
          isCompleted: nextCompleted,
          updatedAt: stamp,
        });
      }
      renderEntries();
      updateContinueCta();
      options.onProgressChanged?.();
    } catch (e) {
      report(String(e), true);
    } finally {
      toggleAllBtn.disabled = false;
    }
  };

  async function refreshProgress() {
    try {
      await writes;
      const rows = await invokeProfile<ProgressRow[]>("item_progress", { itemId: item.syncId });
      if (!isAlive()) return;
      progressMap.clear();
      for (const entry of available) {
        const matched = matchProgressRow(entry, rows);
        if (matched) progressMap.set(entry.index, matched);
      }
      if (!progressLoaded) {
        progressLoaded = true;
        toggleAllBtn.disabled = false;
      }
      updateVisibleProgress();
      updateToggleAllLabel();
      updateContinueCta();
    } catch {
      /* Progress lookup failure does not block viewing entries. */
    }
  }

  // Refresh progress badges whenever a player dialog closes while we're still on this detail page
  const onDialogClose = (event: Event) => {
    if ((event.target as Element)?.matches?.(".player-dialog")) {
      setTimeout(() => {
        if (isAlive()) void refreshProgress();
      }, 60);
    }
  };
  document.addEventListener("close", onDialogClose, { capture: true, signal: options.signal });

  if (highlightUnit && !available.some(e => samePartyUnit(e.target, highlightUnit))) {
    listStatus.textContent = ui("Esta edición no incluye la unidad de la sala.");
  }

  renderSeasons();
  renderEntries();
  updateContinueCta();

  // Scroll highlighted card into view if opened from Watch Party or Continue
  if (highlightUnit && options.scrollToHighlight !== false) {
    requestAnimationFrame(() => {
      listHost.querySelector<HTMLElement>(".entry-card.is-highlighted")?.scrollIntoView({ behavior: "smooth", block: "center" });
    });
  }

  // Async: load saved progress for all chapters/episodes + capibara link state
  const initialProgress = refreshProgress();
  if (capibaraEnabled && illustrated && available.some(e => e.target.unitKind === "chapter")) {
    void invoke<boolean>("capibara_linked", { id: item.syncId })
      .then(value => {
        const next = value || !!item.onlineProviders?.capibaratraductor;
        if (next !== capibaraLinked && isAlive()) {
          capibaraLinked = next;
          renderEntries();
        }
      })
      .catch(() => {});
  }

  return Object.assign(section, {
    async playFromDevice(request: IncomingPlayback) {
      if (!isAlive() || request.signal.aborted || item.mediaType.toLowerCase() !== "movie") return false;
      const unit = request.offer.item;
      const existing = available.find(entry => samePartyUnit(entry.target, unit));
      const entry = existing ?? {
        index:-1, label:[displayTitle(item),unit.displayNumber].filter(Boolean).join(" · "),
        unitName:"", unitHeading:unit.displayNumber ?? displayTitle(item), seasonLabel:"",
        seasonNumber:unit.seasonNumber,
        target:{itemId:item.syncId,unitKind:unit.unitKind,displayNumber:unit.displayNumber,seasonNumber:unit.seasonNumber},
        sources:[],
        transfer:{...unit,itemId:item.syncId,serverId:options.serverId ?? unit.serverId},
      };
      return openEntry({...entry,incoming:request}, unit.provider);
    },
  });
}

interface Subtitle { url: string; language: string; label: string; default: boolean }
type VideoChoice = Stream & { label: string; embed?: boolean; provider?: string };
interface PlaybackTransport {position:number; paused:boolean}
interface PlaybackChoices { sources:VideoChoice[]; index:number; attempted:Set<number>; triedEngines?: Set<string>; engine?:string; dialog?:HTMLDialogElement; transport?:PlaybackTransport; discover?:boolean }
export interface Stream { referer?:string;live?:boolean; url: string; hls: boolean; dash?: boolean; external?: boolean; tracks?: Subtitle[] }

function transferReference(entry: PlaybackEntry, choices?: PlaybackChoices) {
  if (!entry.transfer) return undefined;
  const linked = entry.providerLinks ?? {};
  const provider = choices?.sources[choices.index]?.provider ?? entry.transfer.provider ??
    Object.keys(linked).find(site => !READER_PROVIDERS.has(site) && PROVIDERS.some(([id]) => id === site));
  const candidate = provider ? linked[provider] ?? entry.transfer.providerRef : undefined;
  const providerRef = candidate && candidate.length <= 1024 && candidate.replace(/\/+$/, "").split("/")
    .every(part => part !== "." && part !== ".." && /^[a-z0-9._-]+$/i.test(part)) ? candidate : undefined;
  return {...entry.transfer,provider,providerRef};
}

export function playDownload(name: string, media: OpenedMedia, target: Target | null, report: (message: string, error?: boolean) => void) {
  return play({ label: name, target }, media.kind, media.paths.map(p => convertFileSrc(p, media.streaming ? "umdstream" : "asset")), report);
}

export function playBroadcast(name: string, stream: Stream, report: (message: string, error?: boolean) => void, sources?: VideoChoice[]) {
  const choices = sources?.length ? { sources, index: 0, attempted: new Set([0]) } : undefined;
  return play({ label: name, target: null }, "video", [stream.url], report, stream.hls, stream, undefined, choices);
}

export async function toggleFullscreen(el: HTMLElement, btn: HTMLButtonElement) {
  try {
    if (document.fullscreenElement) {
      await document.exitFullscreen();
      btn.setAttribute("aria-pressed", "false");
    } else if (el.requestFullscreen) {
      await el.requestFullscreen();
      btn.setAttribute("aria-pressed", "true");
    }
  } catch { /* Fullscreen unsupported in this webview */ }
}

function webVideoOverlay(
  dialog: HTMLDialogElement, viewport: HTMLElement, media: HTMLVideoElement,
  fullscreen: HTMLButtonElement, settings: HTMLElement, error: (e: unknown) => void,
) {
  const controls = node("div", undefined, "native-hud");
  const timeline = node("div", undefined, "native-timeline");
  const bar = node("div", undefined, "native-bar");
  const transport = node("div", undefined, "native-transport");
  const start = node("span", "0:00", "native-time");
  const end = node("span", "--:--", "native-time");
  const seek = node("input", undefined, "native-seek");
  seek.type = "range";
  seek.min = "0";
  seek.step = "1";
  seek.setAttribute("aria-label", ui("Posición"));
  const play = node("button", undefined, "primary native-play-btn");
  const mute = node("button", undefined, "quiet-btn native-mute-btn");
  const volume = node("input", undefined, "native-volume");
  volume.type = "range";
  volume.min = "0";
  volume.max = "100";
  volume.setAttribute("aria-label", ui("Volumen"));
  volume.oninput = () => { media.muted = false; media.volume = Number(volume.value) / 100; };
  mute.onclick = () => { media.muted = !media.muted; };
  const captions = node("select");
  captions.hidden = true;
  captions.setAttribute("aria-label", ui("Subtítulos"));
  settings.append(captions);
  timeline.append(start, seek, end);
  transport.append(play, mute, volume);
  bar.append(transport);
  controls.append(timeline, bar);
  controls.setAttribute("role", "group");
  controls.setAttribute("aria-label", ui("Controles de reproducción"));
  viewport.after(controls);

  const toggle = () => {
    if (media.paused) void media.play().catch(error);
    else media.pause();
  };
  play.onclick = toggle;
  const overlay = attachPlaybackOverlay(dialog, viewport, controls, fullscreen, play, toggle);
  const events = new AbortController();
  const options = {signal:events.signal};
  function updatePlayback() {
    play.textContent = media.paused ? "▶ " + ui("Reproducir") : "⏸︎ " + ui("Pausar");
    overlay.state(media.paused, true);
  }
  function updateVolume() {
    const level = media.muted ? 0 : Math.round(media.volume * 100);
    volume.value = String(level);
    volume.style.setProperty("--vol-pct", level + "%");
    mute.textContent = level ? "🔊" : "🔇";
    mute.title = ui(level ? "Silenciar" : "Activar sonido");
    mute.setAttribute("aria-label", mute.title);
  }
  const paintSeek = () => {
    const position = Number(seek.value);
    start.textContent = formatMediaTime(seek.disabled ? media.currentTime : position);
    seek.style.setProperty("--seek-pct", (seek.disabled ? 0 : position / media.duration * 100) + "%");
  };
  let scrubbing = false;
  seek.onpointerdown = () => { scrubbing = true; };
  seek.oninput = paintSeek;
  seek.onchange = () => { scrubbing = false; if (!seek.disabled) media.currentTime = Number(seek.value); };
  seek.onblur = seek.onpointercancel = () => { scrubbing = false; };
  function updateTime() {
    const duration = media.duration;
    seek.disabled = !Number.isFinite(duration) || duration <= 0;
    seek.max = seek.disabled ? "0" : String(duration);
    end.textContent = duration === Infinity ? ui("En vivo") : formatMediaTime(duration);
    if (!scrubbing) { seek.value = String(media.currentTime); paintSeek(); }
  }
  const tracks = () => Array.from(media.textTracks).filter(track => ["captions", "subtitles"].includes(track.kind));
  function updateCaptions() {
    const rows = tracks();
    captions.hidden = rows.length === 0;
    captions.replaceChildren(new Option(ui("Sin subtítulos"), "-1"), ...rows.map((track, index) =>
      new Option(track.label || track.language || ui("Subtítulos"), String(index))));
    captions.value = String(rows.findIndex(track => track.mode === "showing"));
  }
  captions.onchange = () => tracks().forEach((track, index) => { track.mode = index === Number(captions.value) ? "showing" : "disabled"; });
  for (const event of ["addtrack", "removetrack", "change"]) media.textTracks.addEventListener(event, updateCaptions, options);
  for (const event of ["play", "pause", "ended"]) media.addEventListener(event, updatePlayback, options);
  for (const event of ["timeupdate", "durationchange", "loadedmetadata"]) media.addEventListener(event, updateTime, options);
  media.addEventListener("volumechange", updateVolume, options);
  dialog.addEventListener("keydown", event => {
    if (!event.defaultPrevented && ["k", "MediaPlayPause"].includes(event.key) &&
        !(event.target as HTMLElement | null)?.matches("input, select, textarea")) {
      event.preventDefault();
      toggle();
    }
  }, options);
  updateTime();
  updateVolume();
  updatePlayback();
  return (preserveFullscreen = false) => { events.abort(); overlay.close(preserveFullscreen); };
}

async function play(
  entry: PlaybackEntry,
  kind: OpenedMedia["kind"],
  urls: string[],
  report: (message: string, error?: boolean) => void,
  hlsHint = false,
  stream?: Stream,
  chapterNav?: ChapterNav,
  choices?: PlaybackChoices,
) {
  const safeUrls = urls.map(mediaUrl).filter((u): u is string => u !== null);
  if (!safeUrls.length) {
    report(ui("La fuente devolvió una dirección inválida."), true);
    return;
  }
  const validTracks = (stream?.tracks ?? []).flatMap(track => {
    const trackUrl = mediaUrl(track.url);
    return trackUrl ? [{ ...track, url: trackUrl }] : [];
  });
  await writes;
  const profileId = (await windowProfile().catch(() => null))?.activeId ?? activeProfileId();
  const saved = entry.target ? await invokeProfile<Progress | null>("get_progress", { target: entry.target }) : null;
  if (entry.incoming?.signal.aborted) return;
  const detached = choices?.transport?.paused ?? entry.incoming?.offer.paused;
  const resume = choices?.transport?.position ?? entry.incoming?.offer.positionSeconds ??
    (saved?.positionKind === "seconds" && !saved.isCompleted ? saved.position : 0);

  if (choices?.dialog && !choices.dialog.open) return;
  const dialog = choices?.dialog ?? node("dialog", undefined, "player-dialog");
  const isMedia = kind === "video" || kind === "audio";
  dialog.classList.remove("is-native-player", "is-web-player", "has-native-video-plane");
  dialog.classList.toggle("media-player-dialog", isMedia);
  if (kind === "epub" || kind === "images") {
    const source = new URL(safeUrls[0]);
    if (source.protocol === "umdstream:" || source.hostname === "umdstream.localhost") {
      const id = decodeURIComponent(source.pathname.slice(1)).split("/")[0];
      dialog.addEventListener("close", () => {void invoke("close_book", {id}).catch(() => {});}, {once:true});
    }
  }
  const header = node("header", undefined, "dialog-header player-topbar");
  const headerStart = node("div", undefined, "player-header-start");
  const headerEnd = node("div", undefined, "player-header-end");

  const backBtn = node("button", "← " + ui("Volver"), "player-back");
  backBtn.type = "button";
  backBtn.onclick = () => dialog.close();

  const titleGroup = node("div", undefined, "player-title-group");
  const heading = node("h2", entry.label);
  heading.id = "player-title-" + crypto.randomUUID();
  heading.title = entry.label;
  dialog.setAttribute("aria-labelledby", heading.id);
  titleGroup.append(heading);
  headerStart.append(backBtn, titleGroup);

  const fsBtn = node("button", "⛶", "player-icon-btn");
  fsBtn.type = "button";
  fsBtn.setAttribute("aria-label", ui("Pantalla completa"));
  fsBtn.title = ui("Pantalla completa");
  fsBtn.setAttribute("aria-pressed", String(document.fullscreenElement === dialog));
  fsBtn.onclick = () => void toggleFullscreen(dialog, fsBtn);
  const syncFullscreen = () => fsBtn.setAttribute("aria-pressed", String(document.fullscreenElement === dialog));
  document.addEventListener("fullscreenchange", syncFullscreen);

  const close = node("button", "×", "close");
  close.type = "button";
  close.setAttribute("aria-label", ui("Cerrar reproductor"));
  close.title = ui("Cerrar reproductor");
  close.addEventListener("click", () => dialog.close());

  // Prev / Next Episode or Chapter buttons right in the Player/Reader chrome (Avalonia parity!)
  let preserveTransmission = () => {};
  let previousEpisode: (() => void) | undefined;
  let nextEpisode: (() => void) | undefined;
  if (chapterNav) {
    const navGroup = node("div", undefined, "player-ep-group");
    const prevUnit = node("button", "⏮ " + ui("Anterior"), "player-nav-btn");
    prevUnit.type = "button";
    prevUnit.disabled = !chapterNav.hasPrev;
    prevUnit.title = chapterNav.prevLabel || ui("Anterior");
    prevUnit.onclick = () => {
      preserveTransmission();
      dialog.addEventListener("close", () => chapterNav.openPrev(), { once: true });
      dialog.close();
    };
    const nextUnit = node("button", ui("Siguiente") + " ⏭", "player-nav-btn");
    nextUnit.type = "button";
    nextUnit.disabled = !chapterNav.hasNext;
    nextUnit.title = chapterNav.nextLabel || ui("Siguiente");
    nextUnit.onclick = () => {
      preserveTransmission();
      dialog.addEventListener("close", () => chapterNav.openNext(), { once: true });
      dialog.close();
    };
    navGroup.append(prevUnit, nextUnit);
    if (chapterNav.hasPrev) previousEpisode = () => prevUnit.click();
    if (chapterNav.hasNext) nextEpisode = () => nextUnit.click();
    headerEnd.append(navGroup);
  }

  headerEnd.append(fsBtn, close);
  header.append(headerStart, headerEnd);

  const output = node("p", undefined, "player-status-banner");
  output.setAttribute("role", "status");
  const viewport = node("div", undefined, "player-viewport");
  dialog.append(header, output, viewport);
  if (!dialog.isConnected) document.body.append(dialog);
  if (!dialog.open) dialog.showModal();

  const error = (e: unknown) => {
    if (dialog.open) {
      output.textContent = String(e);
      output.classList.add("is-error");
    } else {
      report(String(e), true);
    }
  };
  let cleanup: (preserveFullscreen?: boolean) => void = () => {};
  const onClose = () => {
    document.removeEventListener("fullscreenchange", syncFullscreen);
    if (document.fullscreenElement === dialog) void document.exitFullscreen().catch(() => {});
    cleanup();
    dialog.remove();
  };
  dialog.addEventListener("close", onClose, {once:true});

  if (kind === "video" || kind === "audio") {
    const primaryUrl = safeUrls[0];
    const publicStream = remoteUrl(primaryUrl) && !new URL(primaryUrl).hostname.endsWith(".localhost");
    const reference = kind === "video" && publicStream ? transferReference(entry,choices) : undefined;
    const followRoom = !entry.incoming && !(entry.target && receivingTitle(entry.target.itemId));
    const engines = node("select") as HTMLSelectElement;
    engines.setAttribute("aria-label", ui("Motor de reproducción"));
    const android = /Android/i.test(navigator.userAgent);
    engines.add(new Option(ui("Reproductor web"), "web"));
    if (android) engines.add(new Option("Android", "android"));
    engines.add(new Option("VLC", "vlc"));
    const playback: PlaybackChoices = choices ?? {sources:[],index:0,attempted:new Set()};
    const windows = /Windows/i.test(navigator.userAgent);
    const preferred = playback.engine || getProfilePref(profileId, "umd-video-engine");
    const defaultEngine = android ? "android" : windows && stream?.live ? "vlc" : "web";
    engines.value = preferred || defaultEngine;
    if (!engines.value) engines.value = defaultEngine;
    const triedEngines = playback.triedEngines ?? new Set<string>();
    triedEngines.add(engines.value);

    let switching = false;
    let getTransport = ():PlaybackTransport | undefined => choices?.transport;
    const switchTo = async (next:Stream,index:number,engine:string,discover=false) => {
      if (!dialog.open) return;
      const transport = getTransport();
      document.removeEventListener("fullscreenchange", syncFullscreen);
      dialog.removeEventListener("close",onClose);
      const interrupted = () => dialog.remove();
      dialog.addEventListener("close",interrupted,{once:true});
      cleanup(true); dialog.replaceChildren();
      try {
        await play(entry,kind,[next.url],report,next.hls,next,chapterNav,{...playback, dialog, index, engine,transport,discover,
          triedEngines: index === playback.index ? triedEngines : undefined});
      } catch(e){ dialog.close();dialog.remove();report(String(e),true); }
      finally {dialog.removeEventListener("close",interrupted);}
    };
    const currentStream=stream ?? {url:primaryUrl,hls:hlsHint};
    const changeSource=async (start:number,automatic:boolean) => {
      if(switching || !dialog.open || start===playback.index)return;
      switching=true;
      const ordered=[start,...playback.sources.map((_,i)=>i).filter(i=>i!==start)];
      try {
        for(const i of ordered) {
          if(automatic && playback.attempted.has(i))continue;
          const choice=playback.sources[i]; if(!choice)continue;
          playback.attempted.add(i);
          try {
            const next = choice.embed || choice.external ? await invoke<VideoResolution>("resolve_video", {
              address: choice.url, provider: choice.provider, webFallback: !automatic && entry.target !== null,
            }) : choice;
            if ("providerPlayer" in next) {
              if (entry.target && dialog.open) {
                const transport = getTransport();
                dialog.close();
                await openProviderSession(next.providerPlayer, {...entry, target: entry.target}, report, chapterNav,
                  {...playback,index:i,engine:engines.value,transport,dialog:undefined,triedEngines:undefined,discover:false});
              }
              return;
            }
            await switchTo({...next, tracks:next.tracks?.length? next.tracks : choice.tracks}, i, engines.value);return;
          } catch(e) { if(!automatic)throw e; }
        }
        error(ui("No quedan servidores disponibles para este título."));
      }catch(e){error(e);}finally{switching=false;}
    };
    const failed = (message:string) => {
      if(switching || !dialog.open || !header.isConnected)return;
      const alternate = engines.value === "web" ? "vlc" : "web";
      if (!triedEngines.has(alternate)) {
        switching = true;
        void switchTo(currentStream, playback.index, alternate);
        return;
      }
      const next=playback.sources.findIndex((_,i)=>!playback.attempted.has(i));
      if(next>=0){void changeSource(next,true);return;}
      error(message);
    };
    engines.onchange=()=>{ if(switching)return;switching=true;void writeProfilePref("umd-video-engine",engines.value).then(()=>switchTo(currentStream,playback.index,engines.value)).catch(e=>{switching=false;error(e);}); };
    let servers:HTMLSelectElement|undefined;
    if(playback.sources.length){
      const select=node("select");select.setAttribute("aria-label",ui("Servidor de reproducción"));
      playback.sources.forEach((s,i)=>select.add(new Option(s.label || ui("Servidor {0}",i+1),String(i))));
      select.value=String(playback.index);
      select.disabled = playback.sources.length < 2;
      select.onchange=()=>{
        select.disabled = true;
        void changeSource(Number(select.value),false).finally(()=>{if(select.isConnected){select.disabled=false;select.value=String(playback.index);}});
      };
      servers = select;
    }
    let wireless:HTMLButtonElement|undefined;
    if (windows) {
      wireless = node("button", ui("Pantalla inalámbrica"), "quiet-btn");
      wireless.type = "button";
      wireless.onclick = async () => {
        try {
          const result = await invoke<{error?:string}>("native_playback", {operation:"wireless", args:{id:crypto.randomUUID()}});
          if (result.error) throw Error(ui("El sistema no tiene una pantalla inalámbrica disponible."));
        } catch (e) {error(e);}
      };
    }
    if(stream?.live) titleGroup.append(node("span","● " + ui("En vivo"),"live-pill"));
    const isDash = kind === "video" && (stream?.dash || /\.mpd(?:\?|$)/i.test(primaryUrl));
    if (engines.value !== "web") {
      dialog.classList.add("is-native-player");
      if (android) dialog.classList.add("has-native-video-plane");
      else {
        titleGroup.append(node("span", "VLC", "player-engine-badge"));
        if (!windows) {
          fsBtn.setAttribute("aria-pressed", String(dialog.classList.contains("is-playback-fullscreen")));
          fsBtn.onclick = () => {
            const active = dialog.classList.toggle("is-playback-fullscreen");
            fsBtn.setAttribute("aria-pressed", String(active));
            void getCurrentWindow().setFullscreen(active).catch(error);
          };
        }
      }
      const player = nativePlayer(
        engines.value, primaryUrl, viewport, headerEnd, output, entry.label, entry.target, validTracks,
        resume,
        (position, ended) => { if (entry.target) persist(entry.target, position, "seconds", ended, error); }, failed, isDash ? undefined : stream?.referer, hlsHint,
        {engines,servers,wireless,fullscreenButton:fsBtn,initiallyPaused:detached ?? false, discover:playback.discover,followRoom,
          startCast:()=>{if(!switching){switching=true;void switchTo(currentStream, playback.index, "vlc", true);}}},
      );
      const device = bindDevicePlayback(entry.label,reference,entry.incoming,{
        read:player.deviceStatus,play:player.play,pause:player.pause,seek:player.seek,
        previous:previousEpisode,next:nextEpisode,
      },() => dialog.close());
      preserveTransmission = device.preserve;
      if (device.cast) player.addDeviceCast(device.cast);
      cleanup = preserveFullscreen => {
        if (preserveFullscreen) device.preserve();
        device.detach();
        player.close(preserveFullscreen);
        if (!android && !windows && !preserveFullscreen && dialog.classList.contains("is-playback-fullscreen")) {
          void getCurrentWindow().setFullscreen(false).catch(() => {});
        }
      };
      getTransport = () => player.transport() ?? choices?.transport;
      return;
    }

    dialog.classList.add("is-web-player");
    const webControls = node("div", undefined, "player-web-controls");
    const pillField = (labelText: string, control: HTMLSelectElement) => {
      const wrap = node("label", undefined, "player-ctrl-pill");
      wrap.append(node("span", labelText), control);
      return wrap;
    };
    if (servers && playback.sources.length > 1) {
      webControls.append(pillField(ui("Servidor"), servers));
    }
    const qualitySlot = node("span", undefined, "player-quality-slot");
    qualitySlot.hidden = true;
    webControls.append(qualitySlot, pillField(ui("Motor"), engines));
    if (wireless) webControls.append(wireless);
    headerEnd.insertBefore(webControls, fsBtn);

    let mediaAddress = primaryUrl;
    let webTracks = validTracks;
    let closeRelay = () => {};
    if (stream?.referer && !isDash) {
      const id = crypto.randomUUID();
      try {
        const relay = await invoke<{url: string; tracks: string[]}>("web_stream_relay", {
          id, address: primaryUrl, referer: stream.referer, hls: hlsHint, tracks: validTracks.map(track => track.url),
        });
        closeRelay = () => { void invoke("close_stream_relay", {id}).catch(() => {}); };
        if (!dialog.open || !header.isConnected) { closeRelay(); return; }
        cleanup = closeRelay;
        mediaAddress = relay.url;
        webTracks = validTracks.flatMap((track, index) => relay.tracks[index] ? [{...track, url: relay.tracks[index]}] : []);
      } catch (e) {
        failed(String(e));
        return;
      }
    }
    const media = node(kind);
    getTransport = () => media.readyState >= 1 ? {position:media.currentTime,paused:media.paused} : choices?.transport;
    media.controls = kind === "audio";
    if (media instanceof HTMLVideoElement) media.playsInline = true;
    const remote = (media as HTMLVideoElement & {remote?:{prompt:()=>Promise<void>}}).remote;
    const castMenu = node("details",undefined,"native-cast");
    const castTools = node("div",undefined,"native-cast-tools");
    castMenu.append(node("summary",ui("Transmitir")),castTools);
    if(remote && typeof remote.prompt==="function"){
      const cast=node("button","Chromecast","quiet-btn");
      cast.type="button";
      cast.title=ui("Transmitir a una pantalla");
      cast.onclick=()=>void remote.prompt().catch(error);
      castTools.append(cast);
    }
    media.preload = "metadata";
    media.setAttribute("aria-label", entry.label);
    media.setAttribute("referrerpolicy", "no-referrer");
    let ready = false;
    let lastSave = Date.now();
    let hls: HlsType | null = null;
    const save = () => {
      if (entry.target && ready && !media.seeking && Number.isFinite(media.currentTime)) {
        persist(entry.target, media.currentTime, "seconds", media.ended, error);
      }
    };
    media.addEventListener("loadedmetadata", () => {
      output.textContent = "";
      output.classList.remove("is-error");
      const shouldResume = resume > 0 && (!Number.isFinite(media.duration) || resume < media.duration);
      if (shouldResume && Math.abs(media.currentTime - resume) >= 0.01) {
        media.addEventListener("seeked", () => { ready = true; lastSave = Date.now(); }, { once: true });
        media.currentTime = resume;
      } else {
        ready = true;
        lastSave = Date.now();
      }
      if (detached === false) void media.play().catch(() => {});
    }, { once: true });
    media.addEventListener("timeupdate", () => { if (Date.now() - lastSave > 10_000) { lastSave = Date.now(); save(); } });
    media.addEventListener("pause", save);
    media.addEventListener("ended", save);
    media.addEventListener("error", () => failed(ui("No se pudo reproducir esta fuente. El formato o códec puede no ser compatible con el reproductor del sistema.")));

    let dash: import("dashjs").MediaPlayerClass | undefined;
    viewport.append(media);
    const stopOverlay = media instanceof HTMLVideoElement
      ? webVideoOverlay(dialog, viewport, media, fsBtn, webControls, error) : () => {};
    const background = () => { if (document.hidden) save(); };
    document.addEventListener("visibilitychange", background);
    const detachParty = entry.target && followRoom ? attachPartyPlayer(media, entry.target, entry.label, headerEnd, output) : () => {};
    const device = bindDevicePlayback(entry.label,reference,entry.incoming,{
      read:() => !ready || media.readyState < 2 || media.seeking || media.ended ? null : {
        positionSeconds:media.currentTime,durationSeconds:Number.isFinite(media.duration) ? media.duration : null,paused:media.paused,
      },
      play:() => media.play(),pause:() => media.pause(),
      seek:position => { if (Number.isFinite(media.duration)) media.currentTime = Math.min(position,media.duration); },
      previous:previousEpisode,next:nextEpisode,
    },() => dialog.close());
    preserveTransmission = device.preserve;
    if (device.cast) castTools.prepend(device.cast);
    if (castTools.childElementCount) webControls.append(castMenu);
    cleanup = preserveFullscreen => {
      if (preserveFullscreen) device.preserve();
      device.detach();
      stopOverlay(preserveFullscreen);
      detachParty();
      document.removeEventListener("visibilitychange", background);
      save();
      ready = false;
      media.pause();
      hls?.destroy();
      dash?.reset();
      media.removeAttribute("src");
      media.load();
      closeRelay();
    };
    if (webTracks.length) {
      media.crossOrigin = "anonymous";
      let hasDefault = false;
      for (const track of webTracks) {
        const el = node("track");
        el.kind = "subtitles";
        el.src = track.url;
        el.srclang = track.language;
        el.label = track.label;
        el.default = !hasDefault && track.default;
        hasDefault ||= el.default;
        media.append(el);
      }
    }
    if (isDash) {
      const {MediaPlayer} = await import("dashjs");
      if (!dialog.open || !viewport.isConnected) return;
      dash = MediaPlayer().create();
      dash.on(MediaPlayer.events.ERROR, () => failed(ui("La transmisión DASH se interrumpió.")));
      dash.initialize(media as HTMLVideoElement, mediaAddress, false);
    } else if ((hlsHint || /\.m3u8(?:\?|$)/i.test(primaryUrl)) && !media.canPlayType("application/vnd.apple.mpegurl")) {
      const {default: Hls} = await import("hls.js");
      if (!dialog.open || !viewport.isConnected) return;
      if (Hls.isSupported()) {
        hls = new Hls();
        hls.loadSource(mediaAddress);
        hls.attachMedia(media);
        hls.on(Hls.Events.MANIFEST_PARSED, () => {
          if (!hls || !header.isConnected || hls.levels.length < 2) return;
          const quality=node("select");quality.setAttribute("aria-label",ui("Calidad de video"));
          quality.add(new Option(ui("Calidad automática"),"-1"));
          hls.levels.forEach((l,i)=>quality.add(new Option(l.height ? l.height+"p" : Math.round(l.bitrate/1000)+" kb/s",String(i))));
          quality.onchange=()=>{if(hls)hls.currentLevel=Number(quality.value);};
          qualitySlot.replaceChildren(pillField(ui("Calidad"), quality));
          qualitySlot.hidden = false;
        });
        hls.on(Hls.Events.ERROR, (_, data) => { if (data.fatal) failed(ui("La transmisión HLS se interrumpió. Volvé a abrir la fuente para reintentar.")); });
      } else {
        media.src = mediaAddress;
      }
    } else {
      media.src = mediaAddress;
    }
    return;
  }

  // Reader (Images / Manga / PDF / EPUB)
  let pdf: PDFDocumentProxy | null = null;
  let closed = false;
  let rendering = false;
  let continuous: ReturnType<typeof continuousReader> | undefined;
  let pagedDraw: AbortController | undefined;
  const tasks = new Map<number, RenderTask>();
  const cancelPage = (pageNum: number) => {
    const existing = tasks.get(pageNum);
    if (existing) {
      tasks.delete(pageNum);
      existing.cancel();
    }
  };
  const cancelAllTasks = () => {
    for (const task of tasks.values()) task.cancel();
    tasks.clear();
  };
  const clearViewport = () => {
    pagedDraw?.abort();
    pagedDraw = undefined;
    for (const canvas of viewport.querySelectorAll("canvas")) { canvas.width = 0; canvas.height = 0; }
    for (const img of viewport.querySelectorAll("img")) { img.src = ""; }
    viewport.replaceChildren();
  };

  let page = Math.max(1, Math.floor(saved?.positionKind === "page" ? saved.position : 1));
  let pages = safeUrls.length;
  let shownPage = 0;
  const defaultMode = getProfilePref(profileId, "reader-mode") === "continuous" ? "continuous" : "paged";
  let readingMode: "paged" | "continuous" = defaultMode;
  let zoomPercent = Math.min(200, Math.max(50, Number(getProfilePref(profileId,"reader-zoom")) || 100));
  let epubFontRem = Math.min(2,Math.max(.85,Number(getProfilePref(profileId,"epub-font")) || 1.12));

  const toolbar = node("div", undefined, "reader-controls");
  viewport.tabIndex = 0;
  viewport.setAttribute("aria-label", ui("Contenido de lectura"));

  const prev = node("button", ui("← Anterior"));
  prev.type = "button";
  const next = node("button", ui("Siguiente →"));
  next.type = "button";
  const position = node("input") as HTMLInputElement;
  position.type = "number";
  position.min = "1";
  position.setAttribute("aria-label", ui("Página"));
  const total = node("span");

  // Segmented mode switch (Avalonia-style: Una página | Continua)
  const modeGroup = node("div", undefined, "segmented-control");
  modeGroup.setAttribute("role", "group");
  modeGroup.setAttribute("aria-label", ui("Modo de lectura"));
  const pagedBtn = node("button", ui("Una página"), "seg-btn active");
  pagedBtn.type = "button";
  pagedBtn.setAttribute("aria-pressed", "true");
  const contBtn = node("button", ui("Continua"), "seg-btn");
  contBtn.type = "button";
  contBtn.setAttribute("aria-pressed", "false");
  modeGroup.append(pagedBtn, contBtn);

  // Zoom controls (images / pdf) OR Font size controls (EPUB)
  const scaleGroup = node("div", undefined, "segmented-control");
  const applyScale = () => {
    viewport.style.setProperty("--reader-zoom", `${zoomPercent}%`);
    viewport.style.setProperty("--reader-zoom-scale", String(zoomPercent / 100));
    viewport.style.setProperty("--epub-font", `${epubFontRem.toFixed(2)}rem`);
  };
  if (kind === "epub") {
    const smaller = node("button", "A−", "seg-btn");
    smaller.type = "button";
    smaller.setAttribute("aria-label", ui("Reducir letra"));
    const resetFont = node("button", "100%", "seg-btn");
    resetFont.type = "button";
    const larger = node("button", "A+", "seg-btn");
    larger.type = "button";
    larger.setAttribute("aria-label", ui("Aumentar letra"));
    const updateFont = () => {
      resetFont.textContent = Math.round((epubFontRem / 1.12) * 100) + "%";
      applyScale(); void writeProfilePref("epub-font",String(epubFontRem));
    };
    smaller.onclick = () => { epubFontRem = Math.max(0.85, epubFontRem - 0.12); updateFont(); };
    resetFont.onclick = () => { epubFontRem = 1.12; updateFont(); };
    larger.onclick = () => { epubFontRem = Math.min(2.0, epubFontRem + 0.12); updateFont(); };
    resetFont.textContent=Math.round(epubFontRem / 1.12 * 100)+"%";
    scaleGroup.append(smaller, resetFont, larger);
  } else {
    const zoomOut = node("button", "−", "seg-btn");
    zoomOut.type = "button";
    zoomOut.setAttribute("aria-label", ui("Alejar"));
    const zoomReset = node("button", "100%", "seg-btn");
    zoomReset.type = "button";
    zoomReset.setAttribute("aria-label", ui("Restablecer zoom"));
    const zoomIn = node("button", "+", "seg-btn");
    zoomIn.type = "button";
    zoomIn.setAttribute("aria-label", ui("Acercar"));
    const updateZoom = () => {
      zoomReset.textContent = `${zoomPercent}%`;
      zoomOut.disabled = zoomPercent <= 50;
      zoomIn.disabled = zoomPercent >= 200;
      applyScale(); void writeProfilePref("reader-zoom",String(zoomPercent));
    };
    zoomOut.onclick = () => { zoomPercent = Math.max(50, zoomPercent - 15); updateZoom(); };
    zoomReset.onclick = () => { zoomPercent = 100; updateZoom(); };
    zoomIn.onclick = () => { zoomPercent = Math.min(200, zoomPercent + 15); updateZoom(); };
    zoomReset.textContent=zoomPercent+"%";
    scaleGroup.append(zoomOut, zoomReset, zoomIn);
  }
  applyScale();

  toolbar.append(prev, position, total, next, modeGroup, scaleGroup);
  dialog.insertBefore(toolbar, viewport);

  // Floating scroll-to-top button for continuous mode (Avalonia parity)
  const scrollTopBtn = node("button", "↑", "scroll-top-fab");
  scrollTopBtn.type = "button";
  scrollTopBtn.setAttribute("aria-label", ui("Ir al principio"));
  scrollTopBtn.title = ui("Ir al principio");
  scrollTopBtn.hidden = true;
  scrollTopBtn.onclick = () => viewport.scrollTo({ top: 0, behavior: "smooth" });
  dialog.append(scrollTopBtn);

  viewport.addEventListener("scroll", () => {
    scrollTopBtn.hidden = readingMode !== "continuous" || viewport.scrollTop < 420;
  }, { passive: true });

  dialog.classList.add("immersive-reader");
  let readerEnd = () => {};
  const motion = readerMotion(viewport, toolbar, () => readerEnd());
  dialog.addEventListener("close", () => motion.close(), { once: true });

  const pdfjs = kind === "pdf" ? await import("pdfjs-dist") : null;
  if (!dialog.open) return;
  if (pdfjs) pdfjs.GlobalWorkerOptions.workerSrc = pdfWorker;
  const readerRequests = new AbortController();
  const partialPdf = pdfjs && (new URL(safeUrls[0]).hostname === "umdstream.localhost" || safeUrls[0].startsWith("umdstream:"));
  let range;
  if (partialPdf) {
    dialog.addEventListener("close", () => readerRequests.abort(), {once:true});
    let length: number;
    try {
      const head = await fetch(safeUrls[0], {method:"HEAD",signal:readerRequests.signal});
      length = Number(head.headers.get("Content-Length"));
      if (!head.ok || !Number.isSafeInteger(length) || length <= 0) throw Error(ui("No se pudo abrir el PDF."));
    } catch (e) {
      if (!readerRequests.signal.aborted) error(e);
      return;
    }
    if (readerRequests.signal.aborted) return;
    range = new pdfjs.PDFDataRangeTransport(length, new Uint8Array());
    const transport = range;
    transport.requestDataRange = (begin,end) => {
      void (async () => {
        const bytes = new Uint8Array(end - begin);
        let cursor = begin;
        while (cursor < end && !readerRequests.signal.aborted) {
          const last = Math.min(cursor + 1048576, end);
          const res = await fetch(safeUrls[0], {headers:{Range:"bytes=" + cursor + "-" + (last-1)}, signal: readerRequests.signal});
          const data = new Uint8Array(await res.arrayBuffer());
          if (res.status !== 206 || data.length !== last-cursor) throw Error(ui("La descarga del PDF quedó incompleta."));
          bytes.set(data, cursor - begin);
          cursor = last;
        }
        if (!readerRequests.signal.aborted) transport.onDataRange(begin, bytes);
      })().catch(e => { if (!readerRequests.signal.aborted) {error(e); void loading?.destroy();} });
    };
  }
  const loading = pdfjs ? pdfjs.getDocument({ ...(range ? {range,disableAutoFetch:true,disableStream:true} : {url:safeUrls[0]}), maxImageSize: 16_000_000, cMapUrl: "/pdfjs/cmaps/", standardFontDataUrl: "/pdfjs/standard_fonts/", wasmUrl: "/pdfjs/wasm/", iccUrl: "/pdfjs/iccs/" }) : null;
  cleanup = () => { closed = true; readerRequests.abort(); continuous?.close(); cancelAllTasks(); clearViewport(); void loading?.destroy().catch(() => {}); };

  try {
    if (loading) { output.textContent = ui("Abriendo PDF…"); pdf = await loading.promise; pages = pdf.numPages; }
    if (closed) return;
    if (pages > 10_000) throw new Error(ui("El documento supera el límite de 10.000 páginas."));
    page = Math.min(page, pages);
    position.max = String(pages);
    total.textContent = kind === "epub" ? ui("de {0} secciones", pages) : ui("de {0}", pages);

    let chapterSelect: HTMLSelectElement | undefined;
    const television = navigator.userAgent.includes("UMD-TV");
    const draw = async (pageNum: number, container: HTMLElement, signal = readerRequests.signal) => {
      if (signal.aborted) return;
      if (pdf) {
        cancelPage(pageNum);
        const source = await pdf.getPage(pageNum);
        if (signal.aborted || closed || !container.isConnected) { source.cleanup(); return; }
        const native = source.getViewport({ scale: 1 });
        const pixels = television ? 4_000_000 : 16_000_000;
        const scale = Math.min(2, Math.max(0.1, (viewport.clientWidth - 40) / native.width), Math.sqrt(pixels / (native.width * native.height)));
        const size = source.getViewport({ scale });
        const canvas = node("canvas");
        canvas.width = Math.ceil(size.width);
        canvas.height = Math.ceil(size.height);
        canvas.setAttribute("aria-label", ui("Página {0}", pageNum));
        container.append(canvas);
        const task = source.render({ canvas, viewport: size });
        tasks.set(pageNum, task);
        try { await task.promise; }
        catch (e) { if (e && typeof e === "object" && (e as { name?: string }).name === "RenderingCancelledException") return; throw e; }
        finally { if (tasks.get(pageNum) === task) tasks.delete(pageNum); source.cleanup(); }
      } else if (kind === "epub") {
        const response = await fetch(safeUrls[pageNum - 1], {signal});
        if (!response.ok) throw new Error(ui("No se pudo abrir la sección del EPUB."));
        const blocks: { text?: string; tag?: string; image?: string; alt?: string }[] = await response.json();
        if (signal.aborted || closed || !container.isConnected) return;
        const article = node("article", undefined, "epub-section");
        for (const block of blocks) {
          if (block.text) article.append(node((["h1","h2","h3","h4","h5","h6","li","blockquote"].includes(block.tag ?? "") ? block.tag : "p") as "p", block.text));
          else if (block.image) {
            const safeSrc = /^image-\d+\.(png|jpe?g|webp|gif|avif)$/.test(block.image) ? safeUrls[pageNum - 1].replace(/section-\d+\.json$/, block.image) : "";
            if (safeSrc) { const img = node("img"); img.loading="lazy"; img.alt = block.alt || ""; img.src = safeSrc; article.append(img); }
          }
        }
        container.append(article);
      } else {
        if (closed || !container.isConnected) return;
        const pageUrl = mediaUrl(safeUrls[pageNum - 1]);
        if (!pageUrl) throw new Error(ui("La fuente devolvió una dirección inválida."));
        if (television && await drawImageTiles(pageUrl, viewport, container, ui("Página {0}", pageNum), signal)) return;
        if (signal.aborted || !container.isConnected) return;
        const image = node("img");
        image.alt = ui("Página {0}", pageNum);
        image.referrerPolicy = "no-referrer";
        image.src = pageUrl;
        container.append(image);
        const cancel = () => image.removeAttribute("src");
        signal.addEventListener("abort", cancel, {once: true});
        try {
          await image.decode();
        } catch (e) {
          const host = new URL(pageUrl).hostname;
          if (signal.aborted || !remoteUrl(pageUrl) || host === "localhost" || host.endsWith(".localhost")) throw e;
          image.src = artworkSource(pageUrl);
          await image.decode();
        } finally {
          signal.removeEventListener("abort", cancel);
        }
      }
    };

    const atPageEnd = () => viewport.scrollTop + viewport.clientHeight >= viewport.scrollHeight - 2;
    let recorded: {page: number; completed: boolean} | undefined;
    const update = (value: number, completed = value === pages && atPageEnd()) => {
      page = shownPage = value;
      position.value = String(page);
      if (chapterSelect) chapterSelect.value = String(page);
      prev.disabled = page <= 1;
      next.disabled = page >= pages;
      if (entry.target && (recorded?.page !== page || recorded.completed !== completed)) {
        recorded = {page, completed};
        persist(entry.target, page, "page", completed, error);
      }
    };

    const render = async () => {
      if (closed || rendering) return;
      rendering = true;
      prev.disabled = next.disabled = position.disabled = pagedBtn.disabled = contBtn.disabled = true;
      try {
        cancelAllTasks();
        clearViewport();
        output.textContent = "";
        pagedDraw = new AbortController();
        await draw(page, viewport, pagedDraw.signal);
        if (closed) return;
        viewport.scrollTop = 0;
        update(page);
      } catch (e) {
        if (!closed) { page = shownPage || 1; position.value = String(page); error(e); }
      } finally {
        rendering = false;
        if (!closed) {
          prev.disabled = page <= 1;
          next.disabled = page >= pages;
          position.disabled = pagedBtn.disabled = contBtn.disabled = false;
        }
      }
    };

    const go = (value: number) => {
      if (rendering) return;
      page = Math.max(1, Math.min(pages, Number.isFinite(value) ? Math.floor(value) : page));
      if (continuous) continuous.go(page);
      else void render();
    };

    let changingChapter = false;
    const nextChapter = () => {
      if (closed || !dialog.open || changingChapter) return false;
      update(pages, true);
      if (!chapterNav?.hasNext) return false;
      changingChapter = true;
      dialog.addEventListener("close", () => chapterNav.openNext(), {once:true});
      dialog.close();
      return true;
    };
    let downwardUntil = 0;
    const atBottom = () => continuous ? continuous.atEnd() : atPageEnd();
    const turnDown = () => {
      if (closed || rendering || (!continuous && shownPage !== page) || !atBottom()) return false;
      if (continuous || page >= pages) return nextChapter();
      go(page + 1);
      return true;
    };
    readerEnd = () => { if (continuous) turnDown(); };
    viewport.addEventListener("scroll", () => {
      if (!closed && !rendering && readingMode === "paged" && shownPage === pages && page === pages && atPageEnd()) {
        update(pages, true);
      }
      if (continuous && performance.now() <= downwardUntil && continuous.atEnd()) nextChapter();
    }, {passive:true});
    let lastWheel = -Infinity;
    let wheelTurned = false;
    viewport.addEventListener("wheel", event => {
      if (event.deltaY < 0) downwardUntil = 0;
      if (event.ctrlKey || event.deltaY <= 0 || Math.abs(event.deltaX) >= event.deltaY) return;
      const now = performance.now();
      if (now - lastWheel > 180) wheelTurned = false;
      lastWheel = now;
      downwardUntil = now + 450;
      wheelTurned ||= turnDown();
      if (wheelTurned) event.preventDefault();
    }, {passive:false});
    let touchStart: {x:number;y:number} | undefined;
    viewport.addEventListener("touchstart", event => {
      const point = event.touches.length === 1 ? event.touches[0] : null;
      touchStart = point ? {x:point.clientX,y:point.clientY} : undefined;
    }, {passive:true});
    viewport.addEventListener("touchend", event => {
      const start = touchStart, end = event.changedTouches[0];
      touchStart = undefined;
      if (!start || !end || start.y - end.clientY < Math.max(24, Math.abs(start.x - end.clientX))) return;
      downwardUntil = performance.now() + 1200;
      turnDown();
    }, {passive:true});
    viewport.addEventListener("touchcancel", () => { touchStart = undefined; }, {passive:true});

    prev.addEventListener("click", () => go(page - 1));
    next.addEventListener("click", () => go(page + 1));
    if(kind==="epub"){
      const contents=node("select");contents.setAttribute("aria-label",ui("Índice"));
      const response=await fetch(safeUrls[0].replace(/section-\d+\.json$/, "contents.json"));
      if(response.ok && dialog.open){
        const titles:string[]=await response.json();
        titles.forEach((title,i)=>contents.add(new Option(title || ui("Capítulo {0}",i+1),String(i+1))));
        contents.value=String(page);contents.onchange=()=>go(Number(contents.value));toolbar.prepend(contents);
        chapterSelect = contents;
      }
    }
    position.addEventListener("change", () => go(Number(position.value)));

    dialog.addEventListener("keydown", event => {
      const target = event.target as HTMLElement | null;
      if (target?.matches("input,select")) return;
      const isTv = document.documentElement.classList.contains("television");
      if ((target === viewport || viewport.contains(target)) && (event.key === "ArrowDown" || event.key === "PageDown")) {
        downwardUntil = performance.now() + 450;
        if (turnDown()) event.preventDefault();
        return;
      }
      if (isTv && (continuous || target !== viewport)) return;
      if (event.key === "ArrowRight" || event.key === "ArrowLeft") {
        event.preventDefault();
        go(page + (event.key === "ArrowRight" ? 1 : -1));
      } else if (chapterNav && event.key === "[" && chapterNav.hasPrev) {
        event.preventDefault();
        dialog.addEventListener("close", () => chapterNav.openPrev(), { once: true });
        dialog.close();
      } else if (chapterNav && event.key === "]" && chapterNav.hasNext) {
        event.preventDefault();
        dialog.addEventListener("close", () => chapterNav.openNext(), { once: true });
        dialog.close();
      }
    });

    const syncModeButtons = () => {
      pagedBtn.classList.toggle("active", readingMode === "paged");
      pagedBtn.setAttribute("aria-pressed", String(readingMode === "paged"));
      contBtn.classList.toggle("active", readingMode === "continuous");
      contBtn.setAttribute("aria-pressed", String(readingMode === "continuous"));
      scrollTopBtn.hidden = readingMode !== "continuous" || viewport.scrollTop < 420;
    };

    const applyMode = async () => {
      continuous?.close();
      continuous = undefined;
      cancelAllTasks();
      clearViewport();
      syncModeButtons();
      motion.enable(readingMode === "continuous");
      if (readingMode === "continuous") {
        continuous = continuousReader(viewport, pages, page, draw, update, cancelPage, television ? 1 : 3);
      } else {
        await render();
      }
    };

    const switchMode = async (nextMode: "paged" | "continuous") => {
      if (readingMode === nextMode) return;
      pagedBtn.disabled = contBtn.disabled = true;
      try {
        await writeProfilePref("reader-mode", nextMode);
        if (entry.target) await invokeProfile("reader_mode", { id: entry.target.itemId, mode: nextMode });
        readingMode = nextMode;
        if (!closed) await applyMode();
      } catch (e) {
        error(e);
      } finally {
        pagedBtn.disabled = contBtn.disabled = false;
      }
    };

    pagedBtn.onclick = () => void switchMode("paged");
    contBtn.onclick = () => void switchMode("continuous");

    if (entry.target) {
      const savedMode = await invokeProfile<string>("reader_mode", { id: entry.target.itemId, mode: null, fallback: defaultMode }).catch(() => defaultMode);
      readingMode = savedMode === "continuous" ? "continuous" : "paged";
    }
    if (closed) return;
    await applyMode();
  } catch (e) {
    if (!closed) error(e);
  }
}
