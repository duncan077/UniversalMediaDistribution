import {ui} from "./locale";
import {invoke} from "./bridge";
import {Channel} from "@tauri-apps/api/core";
import {invokeProfile} from "./profile-context";
import type {CatalogItem} from "./main";
import {hiddenTitle} from "./content-filter";
import type {Target} from "./player";
import {isPageActive, navigateTo, onPageEnter} from "./router";

export interface PartyTitle {
  mediaType: string;
  name: string;
  title: string;
  unitKind: string | null;
  seasonNumber: number | null;
  displayNumber: string | null;
}
interface Room {
  partyId: string;
  joinCode: string;
  hostAccountId: string;
  hostControlsOnly: boolean;
  content: PartyTitle | null;
  members: {
    accountId: string;
    displayName: string;
    isHost: boolean;
    isConnected: boolean;
    isBuffering: boolean;
  }[];
  playback: { isPlaying: boolean; positionSeconds: number; updatedAt: string };
}
interface Update {
  reason: string;
  sequence: number;
  state?: Room;
}
interface Reply {
  accountId: string;
  state: Room | null;
  serverTime?: string | null;
}
type Action = { kind: string } & Record<string, unknown>;
let room: Room | null = null,
  account = "",
  connected = false,
  busy = false,
  busyKind: string | null = null,
  pickingEdition = false,
  generation = 0,
  clockOffset = 0;
let retry: ReturnType<typeof setTimeout> | undefined,
  retryDelay = 1000;
let overlayDialog: HTMLDialogElement,
  overlayBody: HTMLElement,
  overlayStatus: HTMLElement,
  pageBody: HTMLElement,
  pageStatus: HTMLElement,
  trigger: HTMLButtonElement;
let openTitle: (item: CatalogItem, unit: PartyTitle) => void;
type PartyMedia = Pick<HTMLMediaElement, "currentTime" | "duration" | "readyState" | "paused" | "ended" | "seeking" | "play" | "pause" | "addEventListener">;
interface BoundPlayer {
  close: () => void;
  media: PartyMedia;
  title: PartyTitle;
  apply: (reason: string) => void;
}
let player: BoundPlayer | null = null;
let signature = "";
let followNotice: HTMLElement | undefined;
let history: string[] = [];
let signedIn: boolean | null = null;

function node<K extends keyof HTMLElementTagNameMap>(tag: K, text = "", className?: string) {
  const el = document.createElement(tag);
  if (text) el.textContent = text;
  if (className) el.className = className;
  return el;
}
function button(label: string, run: () => void, className?: string) {
  const b = node("button", label, className);
  b.type = "button";
  b.onclick = run;
  return b;
}
function norm(name: string) {
  return Array.from(name.normalize("NFC"), (letter) => {
    const upper = letter.toUpperCase();
    return Array.from(upper).length === 1 ? upper : letter;
  })
    .join("")
    .trim()
    .replace(/\s+/gu, " ");
}
export type ContentUnit = Pick<PartyTitle, "unitKind" | "seasonNumber" | "displayNumber">;
function displayNumberKey(value: string | null) {
  const label = (value ?? "").trim();
  const number = /^([+-]?)(\d[\d,]*|)(?:\.(\d*))?([+-]?)$/.exec(label);
  if (!number || (!number[2] && !number[3]) || (number[1] && number[4])) return norm(label);
  const integer = number[2].replaceAll(",", "").replace(/^0+/, "") || "0";
  const fraction = (number[3] || "").replace(/0+$/, "");
  const negative = (number[1] || number[4]) === "-" && (integer !== "0" || !!fraction);
  return (negative ? "-" : "") + integer + (fraction ? "." + fraction : "");
}
export function samePartyUnit(a: ContentUnit, b: ContentUnit) {
  return (a.unitKind ?? "").toLowerCase() === (b.unitKind ?? "").toLowerCase()
    && (a.seasonNumber ?? 0) === (b.seasonNumber ?? 0)
    && displayNumberKey(a.displayNumber) === displayNumberKey(b.displayNumber);
}
function same(a: PartyTitle | null | undefined, b: PartyTitle | null | undefined) {
  return (
    !!a &&
    !!b &&
    norm(a.name) === norm(b.name) &&
    a.mediaType.toLowerCase() === b.mediaType.toLowerCase() &&
    samePartyUnit(a, b)
  );
}
function host() {
  return !!room && room.hostAccountId === account;
}
function position() {
  if (!room) return 0;
  const p = room.playback,
    elapsed = (Date.now() + clockOffset - Date.parse(p.updatedAt)) / 1000;
  return p.positionSeconds + (p.isPlaying && Number.isFinite(elapsed) ? Math.max(0, elapsed) : 0);
}
function activeContainer(): { body: HTMLElement; status: HTMLElement; isOverlay: boolean } | null {
  if (overlayDialog?.open) return { body: overlayBody, status: overlayStatus, isOverlay: true };
  if (isPageActive("party")) return { body: pageBody, status: pageStatus, isOverlay: false };
  return null;
}
function setStatus(text: string) {
  if (overlayStatus) overlayStatus.textContent = text;
  if (pageStatus) pageStatus.textContent = text;
}
function showError(error: unknown) {
  setStatus(String(error));
}

function renderInto(body: HTMLElement, isOverlay: boolean) {
  body.replaceChildren();
  body.inert = busy && busyKind !== "current";
  if (signedIn === false) {
    const panel = node("div", "", "surface-panel party-card");
    panel.append(
      node("p", ui("Iniciá sesión para crear una sala o unirte con un código.")),
      button(ui("Cuenta y respaldo"), () => {
        if (isOverlay && overlayDialog.open) overlayDialog.close();
        document.querySelector<HTMLButtonElement>("#account")?.click();
      }, "primary"),
    );
    body.append(panel);
    return;
  }
  if (!room) {
    const grid = node("div", "", "party-setup-grid");

    const createPanel = node("section", "", "surface-panel party-card");
    createPanel.append(
      node("h2", ui("Crear una sala nueva")),
      node("p", ui("Generá un código de 6 caracteres para compartir con tus amigos y sincronizar la reproducción."), "muted"),
      button(
        ui("Crear sala"),
        () =>
          void act({
            kind: "create",
            content: player?.title ?? null,
            positionSeconds: player?.media.currentTime || 0,
          }),
        "primary",
      ),
    );

    const joinPanel = node("section", "", "surface-panel party-card");
    joinPanel.append(node("h2", ui("Unirme a una sala")));
    const form = node("form", "", "party-join-form");
    const label = node("label", ui("Código de la sala"), "field");
    const input = node("input") as HTMLInputElement;
    input.required = true;
    input.maxLength = 6;
    input.minLength = 6;
    input.placeholder = "ABC123";
    input.autocapitalize = "characters";
    input.spellcheck = false;
    label.append(input);
    const joinBtn = node("button", ui("Unirme"), "primary");
    form.append(label, joinBtn);
    form.onsubmit = (e) => {
      e.preventDefault();
      void act({ kind: "join", joinCode: input.value.trim().toUpperCase() });
    };
    joinPanel.append(form);

    grid.append(createPanel, joinPanel);
    body.append(grid);
    return;
  }

  // Active room view
  const codeCard = node("section", "", "surface-panel party-code-card");
  const codeLeft = node("div");
  codeLeft.append(node("span", ui("Código para invitar"), "stat-label"), node("strong", room.joinCode, "party-join-code"));
  const copyBtn = button(ui("Copiar código"), () => {
    void navigator.clipboard.writeText(room!.joinCode)
      .then(() => setStatus(ui("Código copiado.")))
      .catch(() => {});
  }, "quiet-btn");
  codeCard.append(codeLeft, copyBtn);

  const membersCard = node("section", "", "surface-panel party-card");
  membersCard.append(node("h2", ui("Participantes") + ` (${room.members.length})`));
  const members = node("ul", "", "party-members-list");
  for (const m of room.members) {
    const li = node("li");
    const left = node("strong", m.displayName);
    const badges = node("div", "", "compare-badges");
    if (m.isHost) badges.append(node("span", ui("Anfitrión"), "entry-badge progress"));
    badges.append(node(
      "span",
      m.isBuffering ? ui("Cargando") : m.isConnected ? ui("En línea") : ui("Desconectado"),
      "entry-badge " + (m.isConnected && !m.isBuffering ? "seen" : ""),
    ));
    li.append(left, badges);
    members.append(li);
  }
  membersCard.append(members);

  const contentCard = node("section", "", "surface-panel party-card");
  contentCard.append(node("h2", ui("Reproducción actual")));
  const title = room.content;
  if (title) {
    contentCard.append(
      node("p", title.title && title.title !== title.name ? title.name + " · " + title.title : title.name),
    );
    if (!same(title, player?.title)) {
      contentCard.append(button(ui("Abrir título en mi biblioteca"), () => void findTitle(title), "primary"));
    }
  } else {
    contentCard.append(node("p", ui("El anfitrión puede elegir un título desde el reproductor."), "muted"));
  }
  if (host()) {
    const label = node("label", "", "toggle-row");
    const check = node("input") as HTMLInputElement;
    check.type = "checkbox";
    check.checked = room.hostControlsOnly;
    check.onchange = () => {
      const next = check.checked;
      if (busy && busyKind !== "current") {
        check.checked = room?.hostControlsOnly ?? !next;
        return;
      }
      void act({ kind: "options", hostControlsOnly: next });
    };
    label.append(check, " " + ui("Solo el anfitrión controla la reproducción"));
    contentCard.append(label);
    if (player && !same(player.title, title)) {
      const current = player;
      contentCard.append(
        button(
          ui("Ver este título en la sala"),
          () =>
            void send({
              type: "content",
              content: current.title,
              positionSeconds: current.media.currentTime,
            }).catch(showError),
          "primary",
        ),
      );
    }
  }
  const footer = node("div", "", "button-row");
  footer.append(button(ui("Salir de la sala"), () => void act({ kind: "leave" }), "danger-btn"));
  contentCard.append(footer);

  if (!host() && same(title, player?.title)) contentCard.append(button(ui("Resincronizar"), () => player?.apply("seek")));
  body.append(codeCard, membersCard, contentCard);
  if (history.length) {
    const details = node("details"); details.append(node("summary", ui("Actividad de la sala")));
    const log = node("ol"); for (const line of history) log.append(node("li", line)); details.append(log); body.append(details);
  }
}

function render() {
  if (trigger) trigger.textContent = room ? ui("Sala · ") + room.joinCode : ui("Sala de reproducción");
  const target = activeContainer();
  if (!target || pickingEdition) return;
  const key = JSON.stringify([
    target.isOverlay,
    room?.partyId,
    history[0],
    room?.members,
    room?.content,
    room?.hostControlsOnly,
    player?.title,
    connected,
    busy,
    busyKind,
    signedIn,
  ]);
  if (signature === key) return;
  signature = key;
  renderInto(target.body, target.isOverlay);
}

async function findTitle(title: PartyTitle) {
  const target = activeContainer();
  if (!target) return;
  try {
    const ids = await invoke<string[]>("party_titles", { mediaType: title.mediaType, name: title.name });
    if (!activeContainer() || !same(room?.content, title)) return;
    const items = ids.length ? (await invoke<CatalogItem[]>("library_summaries", {ids})).filter(item => !hiddenTitle(item)) : [];
    if (!activeContainer() || !same(room?.content, title)) return;
    if (!items.length) {
      setStatus(ui("Este título todavía no está en tu biblioteca. Importá su catálogo y volvé a abrirlo."));
      return;
    }
    const cur = activeContainer();
    if (!cur || !same(room?.content, title)) return;
    const open = (item: CatalogItem) => {
      pickingEdition = false;
      player?.close();
      if (overlayDialog.open) overlayDialog.close();
      openTitle(item, title);
    };
    if (items.length === 1) {
      open(items[0]);
      return;
    }
    pickingEdition = true;
    signature = "";
    const picker = node("section", "", "surface-panel party-card");
    picker.append(node("h3", ui("Elegí tu edición.")));
    const row = node("div", "", "button-row");
    items.forEach((i) =>
      row.append(button([i.name, i.subtype, i.releaseYear].filter(Boolean).join(" · "), () => open(i), "primary")),
    );
    row.append(button(ui("Cancelar"), () => {
      pickingEdition = false;
      signature = "";
      render();
    }, "quiet-btn"));
    picker.append(row);
    cur.body.replaceChildren(picker);
  } catch (e) {
    showError(e);
  }
}

async function send(command: Record<string, unknown>) {
  if (!room || !connected) throw Error(ui("La sala está desconectada."));
  await invokeProfile("party_send", { partyId: room.partyId, command });
}
function reset() {
  history = []; followNotice?.remove();
  generation++;
  clearTimeout(retry);
  retryDelay = 1000;
  room = null;
  account = "";
  clockOffset = 0;
  connected = false;
  busy = false;
  busyKind = null;
  pickingEdition = false;
  signature = "";
  render();
}
function reconnect(g: number) {
  if (g !== generation || !room) return;
  connected = false;
  setStatus(ui("Conexión interrumpida. Reconectando…"));
  render();
  clearTimeout(retry);
  retry = setTimeout(() => {
    if (g !== generation) return;
    if (busy) reconnect(g);
    else void act({ kind: "current" });
  }, retryDelay);
  retryDelay = Math.min(15000, retryDelay * 2);
}
async function connect(g: number, id: string) {
  let sequence = -1;
  const events = new Channel<Update>();
  events.onmessage = (event) => {
    if (g !== generation) return;
    if (event.reason === "socketClosed") {
      reconnect(g);
      return;
    }
    if (event.state?.partyId !== id || event.sequence <= sequence) return;
    sequence = event.sequence;
    const previous = room?.content;
    room = event.state;
    const title = room.content;
    if (!["heartbeat", "tick"].includes(event.reason)) {
      const names: Record<string,string> = {play:"Reproducción", pause:"Pausa", seek:"Cambio de posición", content:"Cambio de título", joined:"Se unió un participante", left:"Salió un participante"};
      history.unshift(new Date().toLocaleTimeString() + " · " + ui(names[event.reason] || event.reason));
      history.length = Math.min(30, history.length);
    }
    if (!same(previous, title)) {
      followNotice?.remove();
      if (title && !host() && !activeContainer() && !same(title, player?.title)) {
        followNotice = node("aside", "", "app-notice"); followNotice.setAttribute("role","status");
        const actions = node("div", "", "app-notice-actions");
        actions.append(
          button(ui("Seguir al anfitrión"), () => { followNotice?.remove(); openOverlay(); void findTitle(title); }, "primary"),
          button(ui("Cerrar"), () => followNotice?.remove(), "quiet-btn"),
        );
        followNotice.append(node("span", title.title || title.name, "app-notice-text"), actions);
        (document.getElementById("app-notice-stack") ?? document.body).append(followNotice);
      }
    }
    connected = true;
    retryDelay = 1000;
    setStatus("");
    render();
    player?.apply(event.reason);
  };
  await invokeProfile("party_connect", { partyId: id, events });
}
async function act(action: Action) {
  const userOverride = busy && busyKind === "current" && (action.kind === "leave" || action.kind === "options");
  if (busy && !userOverride) return;
  const start = userOverride ? ++generation : generation;
  busy = true;
  busyKind = action.kind;
  setStatus(ui("Conectando…"));
  render();
  let current = start;
  try {
    const started = Date.now();
    const reply = await invokeProfile<Reply>("party_action", { action });
    if (start !== generation) return;
    if (reply.serverTime) {
      const now = Date.now();
      const rtt = Math.max(0, now - started);
      const t = Date.parse(reply.serverTime);
      if (Number.isFinite(t)) clockOffset = t + rtt / 2 - now;
    }
    const changed = reply.state?.partyId !== room?.partyId || reply.accountId !== account;
    signedIn = true;
    account = reply.accountId;
    if (changed || !connected || (action.kind !== "options" && action.kind !== "current")) room = reply.state;
    if (changed || !connected) {
      current = ++generation;
      connected = false;
      clearTimeout(retry);
      if (room) await connect(current, room.partyId);
    }
    if (current === generation) setStatus("");
  } catch (e) {
    if (current === generation) {
      showError(e);
      if (room && !connected) reconnect(current);
    }
  } finally {
    if (current === generation) {
      busy = false;
      busyKind = null;
      render();
    }
  }
}

function openPage() {
  pickingEdition = false;
  navigateTo("party", { resetStack: true });
  signature = "";
  render();
  if (signedIn !== false) void act({ kind: "current" });
}
function openOverlay() {
  pickingEdition = false;
  if (!overlayDialog.open) overlayDialog.showModal();
  signature = "";
  render();
  if (signedIn !== false) void act({ kind: "current" });
}

export function setupParty(show: (item: CatalogItem, unit: PartyTitle) => void) {
  overlayDialog = document.querySelector("#party-overlay-dialog")!;
  overlayBody = document.querySelector("#party-overlay-content")!;
  overlayStatus = document.querySelector("#party-overlay-status")!;
  pageBody = document.querySelector("#party-content")!;
  pageStatus = document.querySelector("#party-status")!;
  trigger = document.querySelector("#party-open")!;
  openTitle = show;
  trigger.onclick = openPage;
  onPageEnter("party", () => {
    pickingEdition = false;
    signature = "";
    render();
    if (signedIn !== false) void act({ kind: "current" });
  });
  overlayDialog.addEventListener("close", () => { pickingEdition = false; signature = ""; });
  window.addEventListener("umd-account-changed", (e) => {
    signedIn = (e as CustomEvent<boolean>).detail;
    setStatus("");
    reset();
    if (signedIn) void act({ kind: "current" });
  });
}

export function attachPartyPlayer(
  media: PartyMedia,
  target: Target,
  label: string,
  header: HTMLElement,
  output: HTMLElement,
) {
  const abort = new AbortController(),
    opts = { signal: abort.signal };
  let attached: BoundPlayer | undefined,
    wasFollowing = false,
    buffering = false;
  let ignorePlay = false,
    ignorePause = false,
    seekTo: number | null = null;
  const listening = () =>
    !!attached && player === attached && connected && same(room?.content, attached.title);
  const publish = (type: string, extra: Record<string, unknown> = {}) => {
    if (listening())
      void send({ type, ...extra }).catch((e) => {
        if (!abort.signal.aborted && output.isConnected) output.textContent = String(e);
      });
  };
  function apply(reason: string) {
    if (!listening()) {
      if (wasFollowing && room && connected && !media.paused) media.pause();
      wasFollowing = false;
      return;
    }
    wasFollowing = true;
    if (media.readyState < 1 || !room) return;
    const to = Math.min(position(), Number.isFinite(media.duration) ? media.duration : Infinity);
    if (Number.isFinite(to) && Math.abs(to - media.currentTime) > (reason === "seek" ? 0.25 : 1.5)) {
      seekTo = to;
      media.currentTime = to;
    }
    if (room.playback.isPlaying && media.paused && !media.ended) {
      ignorePlay = true;
      void media.play().catch(() => {
        ignorePlay = false;
        if (!abort.signal.aborted && output.isConnected && listening()) output.textContent = ui("Pulsá reproducir para seguir a la sala.");
      });
    } else if (!room.playback.isPlaying && !media.paused) {
      ignorePause = true;
      media.pause();
    }
  }
  function transport(type: string) {
    if (!listening()) return;
    if (room?.hostControlsOnly && !host()) {
      apply("seek");
      return;
    }
    publish(type, { positionSeconds: media.currentTime });
  }
  media.addEventListener("play", () => { if (ignorePlay) { ignorePlay = false; return; } transport("play"); }, opts);
  media.addEventListener("pause", () => { if (ignorePause) { ignorePause = false; return; } transport("pause"); }, opts);
  media.addEventListener("seeked", () => {
    const to = seekTo;
    seekTo = null;
    if (to !== null && Math.abs(media.currentTime - to) < 1) return;
    transport("seek");
  }, opts);
  media.addEventListener("loadedmetadata", () => apply("seek"), opts);
  const stall = (value: boolean) => {
    if (listening() && buffering !== value) {
      buffering = value;
      publish("buffering", { isBuffering: value });
    }
  };
  media.addEventListener("waiting", () => { if (!media.seeking && !media.paused) stall(true); }, opts);
  media.addEventListener("canplay", () => stall(false), opts);
  media.addEventListener("playing", () => stall(false), opts);
  const heartbeat = setInterval(() => {
    if (listening() && host() && !media.paused && !media.seeking)
      publish("position", { positionSeconds: media.currentTime });
  }, 5000);
  const control = button(ui("Sala de reproducción"), openOverlay, "quiet-btn");
  control.disabled = true;
  header.insertBefore(control, header.lastElementChild);
  void invoke<CatalogItem[]>("library_summaries", { ids: [target.itemId] })
    .then(([item]) => {
      if (!item || abort.signal.aborted) return;
      attached = {
        media,
        close: () => header.closest("dialog")?.close(),
        title: {
          mediaType: item.mediaType,
          name: item.name,
          title: label,
          unitKind: target.unitKind,
          seasonNumber: target.seasonNumber,
          displayNumber: target.displayNumber,
        },
        apply,
      };
      player = attached;
      control.disabled = false;
      render();
      apply("seek");
    })
    .catch(() => control.remove());
  return () => {
    if (listening()) {
      if (buffering) publish("buffering", { isBuffering: false });
      if (host() && !media.paused) publish("pause", { positionSeconds: media.currentTime });
    }
    abort.abort();
    clearInterval(heartbeat);
    control.remove();
    if (player === attached) player = null;
    render();
  };
}
