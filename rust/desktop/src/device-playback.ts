import {Channel} from "@tauri-apps/api/core";
import {invokeProfile} from "./profile-context";
import {ui} from "./locale";

export interface VideoReference {
  itemId: string;
  serverId?: string;
  name: string;
  subtype: string;
  releaseYear?: number;
  unitKind: string | null;
  displayNumber: string | null;
  seasonNumber: number | null;
  provider?: string;
  providerRef?: string;
}

interface Device { id: string; name: string }
export interface VideoOffer {
  kind: "offer";
  id: string;
  from: Device;
  item: VideoReference;
  positionSeconds: number;
  paused: boolean;
}

export interface IncomingPlayback {
  offer: VideoOffer;
  signal: AbortSignal;
}

type Control = {kind: "play" | "pause" | "previous" | "next"} | {kind: "seek"; positionSeconds: number};
type EndReason = "stopped" | "rejected" | "unavailable";
type Sample = {positionSeconds: number; durationSeconds: number | null; paused: boolean};
type PlaybackStatus = Sample & {title: string; canPrevious: boolean; canNext: boolean};
type Command = Omit<VideoOffer, "from"> & {to: string}
  | {kind: "ready" | "update"; id: string; status: PlaybackStatus}
  | {kind: "control"; id: string; action: Control}
  | {kind: "end"; id: string; reason: EndReason};
type DeviceEvent = VideoOffer
  | {kind: "devices"; devices: Device[]}
  | {kind: "ready" | "update"; id: string; status: PlaybackStatus}
  | {kind: "control"; id: string; action: Control}
  | {kind: "end"; id: string; reason: EndReason}
  | {kind: "closed"; retry: boolean};

interface Surface {
  read(): Sample | null;
  play(): Promise<void>;
  pause(): void | Promise<void>;
  seek(position: number): void | Promise<void>;
  previous?: () => void;
  next?: () => void;
}

interface Receiver {
  request: IncomingPlayback;
  abort: AbortController;
  surface?: Surface;
  itemId?: string;
  ready: boolean;
  changing: boolean;
  expiry: ReturnType<typeof setTimeout>;
}

let signedIn = false;
let connectionId: string | null = null;
let devices: Device[] = [];
let ready = false;
let unsupported = false;
let retry: ReturnType<typeof setTimeout> | undefined;
let delay = 1_000;
let incoming: Receiver | undefined;
let outgoing: {id: string; surface: Surface; view: Picker; ready: boolean; timer: ReturnType<typeof setTimeout>} | undefined;
let picker: Picker | undefined;
let prompt: HTMLDialogElement | undefined;
let start: ((request: IncomingPlayback) => Promise<boolean>) | undefined;
let report: (message: string, error?: boolean) => void = () => {};

function send(command: Command) {
  return invokeProfile("device_playback_send", {connectionId, command});
}

function finishIncoming(receiver: Receiver) {
  if (incoming !== receiver) return;
  clearTimeout(receiver.expiry);
  incoming = undefined;
  if (!receiver.ready) receiver.abort.abort();
  prompt?.close();
}

function finishOutgoing(state: NonNullable<typeof outgoing>, reason: EndReason, close: boolean) {
  if (outgoing !== state) return;
  clearTimeout(state.timer);
  outgoing = undefined;
  if (close || state.ready) {
    state.view.dialog.close();
    if (!close && reason === "unavailable") report(ui("Se perdió la conexión con el otro dispositivo."), true);
  } else {
    state.view.devices(devices);
    if (reason === "unavailable") state.view.message(ui("El otro dispositivo no pudo abrir el video."));
    if (reason === "rejected") state.view.message(ui("El otro dispositivo rechazó la solicitud."));
  }
}

async function receive(offer: VideoOffer) {
  if (incoming || outgoing || !start) {
    await send({kind:"end",id:offer.id,reason:"unavailable"}).catch(() => {});
    return;
  }
  const abort = new AbortController();
  const receiver: Receiver = {
    request:{offer,signal:abort.signal}, abort, ready:false, changing:false,
    expiry:setTimeout(() => end("unavailable"), 45_000),
  };
  incoming = receiver;
  const dialog = prompt = panel(ui("Reproducir en este dispositivo"));
  const title = document.createElement("p");
  title.textContent = ui("{0} quiere reproducir «{1}» en este dispositivo.", offer.from.name, offer.item.name);
  let accepted = false;
  function end(reason: EndReason) {
    if (incoming !== receiver) return;
    finishIncoming(receiver);
    void send({kind:"end",id:offer.id,reason}).catch(() => {});
    if (reason === "unavailable" && accepted) report(ui("No se pudo abrir el video en este dispositivo."), true);
  }
  dialog.addEventListener("close", () => {
    if (prompt === dialog) prompt = undefined;
    if (!accepted) end("rejected");
    dialog.remove();
  }, {once:true});
  const reject = button(ui("Rechazar"), () => dialog.close());
  const accept = button(ui("Reproducir"), () => {
    accepted = true;
    dialog.close();
    // Close the receiver's current video before opening a different one.
    for (const player of document.querySelectorAll<HTMLDialogElement>(".player-dialog[open]")) player.close();
    const open = start!;
    void open(receiver.request).then(opened => {
      if (!opened) end("unavailable");
    }).catch(() => end("unavailable"));
  }, true);
  const row = document.createElement("div");
  row.className = "button-row";
  row.append(reject, accept);
  dialog.append(title, row);
  if (!dialog.open) dialog.showModal();
  accept.focus({preventScroll:true});
}

function event(message: DeviceEvent) {
  if (message.kind === "devices") {
    ready = true;
    delay = 1_000;
    devices = message.devices;
    if (!outgoing) picker?.devices(devices);
    return;
  }
  if (message.kind === "closed") {
    disconnect();
    if (signedIn && message.retry) {
      retry = setTimeout(connect, delay);
      delay = Math.min(30_000, delay * 2);
    } else {
      unsupported = true;
      picker?.message(ui("Este servidor no ofrece transmisión entre dispositivos."));
    }
    return;
  }
  if (message.kind === "offer") { void receive(message); return; }
  if (outgoing?.id === message.id) {
    if (message.kind === "ready") {
      outgoing.ready = true;
      clearTimeout(outgoing.timer);
      const surface = outgoing.surface;
      void Promise.resolve().then(() => surface.pause()).catch(error => report(String(error), true));
      outgoing.view.remote(message.status);
    } else if (message.kind === "update" && outgoing.ready) {
      outgoing.view.remote(message.status);
    } else if (message.kind === "end") {
      finishOutgoing(outgoing, message.reason, false);
    }
  } else if (incoming?.request.offer.id === message.id) {
    if (message.kind === "end") {
      finishIncoming(incoming);
    } else if (message.kind === "control" && incoming.ready && incoming.surface) {
      const surface = incoming.surface;
      const action = message.action;
      void Promise.resolve().then(() => {
        switch (action.kind) {
          case "play": return surface.play();
          case "pause": return surface.pause();
          case "seek": return surface.seek(action.positionSeconds);
          case "previous": return surface.previous?.();
          case "next": return surface.next?.();
        }
      }).catch(error => report(String(error), true));
    }
  }
}

function disconnect() {
  clearTimeout(retry);
  retry = undefined;
  const id = connectionId;
  connectionId = null;
  ready = false;
  unsupported = false;
  devices = [];
  if (id) void invokeProfile("device_playback_disconnect", {connectionId:id}).catch(() => {});
  if (incoming) finishIncoming(incoming);
  if (outgoing) finishOutgoing(outgoing, "unavailable", false);
  picker?.devices(devices);
}

function connect() {
  if (!signedIn || connectionId) return;
  clearTimeout(retry);
  retry = undefined;
  const id = connectionId = crypto.randomUUID();
  const label = document.documentElement.classList.contains("television") ? "Android TV"
    : /Android/i.test(navigator.userAgent) ? "Android"
    : /Windows/i.test(navigator.userAgent) ? "Windows" : "UMD Linux";
  const events = new Channel<DeviceEvent>();
  events.onmessage = message => { if (id === connectionId) event(message); };
  void invokeProfile("device_playback_connect", {connectionId:id,label,events}).catch(() => {
    if (id === connectionId) event({kind:"closed",retry:true});
  });
}

export function setupDevicePlayback(open: (request: IncomingPlayback) => Promise<boolean>, notify: typeof report) {
  start = open;
  report = notify;
  window.addEventListener("umd-account-changed", change => {
    const available = (change as CustomEvent<boolean>).detail;
    if (available === signedIn) return;
    signedIn = available;
    if (signedIn) connect(); else disconnect();
  });
  window.addEventListener("online", () => {
    if (retry && !connectionId) { clearTimeout(retry); connect(); }
  });
}

export function receivingTitle(itemId: string): boolean {
  return incoming?.itemId === itemId;
}

export function bindDevicePlayback(
  title: string, reference: VideoReference | undefined, request: IncomingPlayback | undefined,
  surface: Surface, closeLoading: () => void,
) {
  const receiver = incoming;
  const linked = receiver && (request?.offer.id === receiver.request.offer.id ||
    (receiver.changing && reference?.itemId === receiver.itemId));
  let interval: ReturnType<typeof setInterval> | undefined;
  let lastSent = 0;
  const abort = () => closeLoading();
  if (request?.signal.aborted) queueMicrotask(abort);
  else request?.signal.addEventListener("abort", abort, {once:true});
  if (linked) {
    receiver.surface = surface;
    receiver.itemId = reference?.itemId;
    receiver.changing = false;
    if (receiver.ready) clearTimeout(receiver.expiry);
    interval = setInterval(() => {
      if (incoming !== receiver || receiver.surface !== surface) { clearInterval(interval); return; }
      const state = surface.read();
      if (!state) return;
      const status = {...state, title, canPrevious:!!surface.previous, canNext:!!surface.next};
      if (!receiver.ready) {
        const offer = receiver.request.offer;
        if (Math.abs(state.positionSeconds - offer.positionSeconds) > 4 || state.paused !== offer.paused) return;
        receiver.ready = true;
        clearTimeout(receiver.expiry);
        void send({kind:"ready",id:offer.id,status}).catch(() => finishIncoming(receiver));
      } else if (Date.now() - lastSent >= 1_500) {
        void send({kind:"update",id:receiver.request.offer.id,status}).catch(() => finishIncoming(receiver));
      } else return;
      lastSent = Date.now();
    }, 400);
  }
  const cast = reference && !linked ? button(ui("Dispositivos UMD"), () => openPicker(reference, surface)) : undefined;
  return {
    cast,
    preserve() {
      if (linked && incoming === receiver) {
        receiver.changing = true;
        if (receiver.ready) {
          clearTimeout(receiver.expiry);
          receiver.expiry = setTimeout(() => {
            finishIncoming(receiver);
            void send({kind:"end",id:receiver.request.offer.id,reason:"unavailable"}).catch(() => {});
          }, 25_000);
        }
      }
    },
    detach() {
      clearInterval(interval);
      request?.signal.removeEventListener("abort", abort);
      if (linked && incoming === receiver && receiver.surface === surface) {
        receiver.surface = undefined;
        if (!receiver.changing) {
          finishIncoming(receiver);
          void send({kind:"end",id:receiver.request.offer.id,reason:"stopped"}).catch(() => {});
        }
      }
      if (outgoing?.surface === surface && !outgoing.ready) {
        const current = outgoing;
        finishOutgoing(current, "stopped", true);
        void send({kind:"end",id:current.id,reason:"stopped"}).catch(() => {});
      }
    },
  };
}

function button(label: string, activate: () => void, primary = false) {
  const element = document.createElement("button");
  element.type = "button";
  element.className = primary ? "primary" : "quiet-btn";
  element.textContent = label;
  element.onclick = activate;
  return element;
}

function panel(heading: string) {
  const dialog = document.createElement("dialog");
  dialog.className = "device-playback-dialog";
  const header = document.createElement("header");
  header.className = "dialog-header";
  const label = document.createElement("h2");
  label.textContent = heading;
  label.id = "device-playback-" + crypto.randomUUID();
  header.append(label);
  dialog.setAttribute("aria-labelledby", label.id);
  dialog.append(header);
  document.body.append(dialog);
  return dialog;
}

interface Picker {
  dialog: HTMLDialogElement;
  message(text: string): void;
  devices(list: Device[]): void;
  remote(status: PlaybackStatus): void;
}

function openPicker(item: VideoReference, surface: Surface) {
  if (picker?.dialog.open) return;
  const dialog = panel(ui("Dispositivos UMD"));
  const message = document.createElement("p");
  message.setAttribute("role","status");
  const rows = document.createElement("div");
  rows.className = "button-row";
  const remote = document.createElement("div");
  remote.hidden = true;
  const title = document.createElement("p");
  const seek = document.createElement("input");
  seek.type = "range";
  seek.min = "0";
  seek.step = "1";
  seek.setAttribute("aria-label",ui("Posición"));
  seek.onchange = () => command({kind:"seek",positionSeconds:Number(seek.value)});
  const actions = document.createElement("div");
  actions.className = "button-row";
  const previous = button(ui("Anterior"), () => command({kind:"previous"}));
  let paused = false;
  const play = button(ui("Pausar"), () => command({kind:paused ? "play" : "pause"}), true);
  const next = button(ui("Siguiente"), () => command({kind:"next"}));
  actions.append(previous,play,next);
  remote.append(title,seek,actions);
  const close = button(ui("Cerrar"), () => dialog.close());
  dialog.append(message,rows,remote,close);
  function command(action: Control) {
    if (!outgoing?.ready) return;
    void send({kind:"control",id:outgoing.id,action}).catch(error => view.message(String(error)));
  }
  const view: Picker = picker = {
    dialog,
    message: text => { message.textContent = text; },
    devices: list => {
      rows.replaceChildren(...list.map(device => button(device.name, () => {
        const state = surface.read();
        if (!state) { view.message(ui("Esperá a que el video esté listo.")); return; }
        const id = crypto.randomUUID();
        const timer = setTimeout(() => {
          if (outgoing?.id === id) finishOutgoing(outgoing,"unavailable",false);
          void send({kind:"end",id,reason:"unavailable"}).catch(() => {});
        }, 46_000);
        outgoing = {id,surface,view,ready:false,timer};
        rows.replaceChildren();
        view.message(ui("Esperando confirmación en {0}…", device.name));
        void send({kind:"offer",id,to:device.id,item,positionSeconds:state.positionSeconds,paused:state.paused}).catch(() => {
          if (outgoing?.id === id) finishOutgoing(outgoing,"unavailable",false);
        });
      })));
      if (!list.length) view.message(ui(
        !signedIn ? "Iniciá sesión en ambos dispositivos."
          : unsupported ? "Este servidor no ofrece transmisión entre dispositivos."
          : ready ? "Abrí UMD en otro dispositivo con la misma cuenta." : "Conectando con tus dispositivos…"));
      if (list.length && !outgoing) view.message("");
    },
    remote: state => {
      if (remote.hidden) message.textContent = ui("Reproduciendo en el otro dispositivo.");
      remote.hidden = false;
      title.textContent = state.title;
      paused = state.paused;
      play.textContent = ui(paused ? "Reproducir" : "Pausar");
      previous.disabled = !state.canPrevious;
      next.disabled = !state.canNext;
      close.textContent = ui("Dejar de controlar");
      seek.disabled = !state.durationSeconds;
      seek.max = String(state.durationSeconds ?? 0);
      if (document.activeElement !== seek) seek.value = String(state.positionSeconds);
    },
  };
  dialog.addEventListener("close", () => {
    if (outgoing?.view === view) {
      const state = outgoing;
      finishOutgoing(state,"stopped",false);
      void send({kind:"end",id:state.id,reason:"stopped"}).catch(() => {});
    }
    if (picker === view) picker = undefined;
    dialog.remove();
  }, {once:true});
  view.devices(devices);
  if (!dialog.open) dialog.showModal();
}
