import {ui} from "./locale";
import { getVersion } from "@tauri-apps/api/app";
import {invoke} from "./bridge";
import {Channel} from "@tauri-apps/api/core";
import { flushProgress } from "./player";

interface Offer {
  id: string;
  version: string;
  sizeBytes: number;
  notes: string | null;
}
type Progress = { phase: "download"; bytes: number } | { phase: "verify" | "extract" | "install" };

export function setupUpdates() {
  const android = /Android/i.test(navigator.userAgent);
  if (!android && !/Windows|Linux/i.test(navigator.userAgent)) return;
  const byId = <T extends HTMLElement>(id: string) => document.getElementById(id) as T;
  const open = byId<HTMLButtonElement>("updates-open");
  const dialog = byId<HTMLDialogElement>("updates-dialog");
  const search = byId<HTMLButtonElement>("updates-check");
  const install = byId<HTMLButtonElement>("updates-install");
  if (!android) install.textContent = ui("Actualizar y reiniciar");
  const bar = byId<HTMLProgressElement>("updates-progress");
  const status = byId("updates-status");
  const notes = byId("updates-notes");
  let offer: Offer | null = null;
  let busy = false;

  function setBusy(value: boolean) {
    busy = value;
    search.disabled = install.disabled = value;
    install.hidden = !offer;
  }
  let notice: HTMLElement | undefined;
  async function check(background = false) {
    if (busy) return;
    offer = null;
    notes.hidden = bar.hidden = true;
    status.textContent = ui("Buscando una versión para este equipo…");
    setBusy(true);
    try {
      offer = await invoke<Offer | null>("check_update");
      if (offer) {
        const size = (offer.sizeBytes / 1048576).toLocaleString(document.documentElement.lang, { maximumFractionDigits: 1 });
        status.textContent = ui("Versión {0} disponible · {1} MiB", offer.version, size);
        notes.textContent = offer.notes;
        notes.hidden = !offer.notes;
        if (background && !dialog.open && sessionStorage.getItem("umd-update-notice") !== offer.version) {
          sessionStorage.setItem("umd-update-notice", offer.version);
          notice?.remove();
          notice = document.createElement("aside"); notice.className = "app-notice"; notice.setAttribute("role", "status");
          const text = document.createElement("span"); text.className = "app-notice-text"; text.textContent = ui("Versión {0} disponible", offer.version);
          const actions = document.createElement("div"); actions.className = "app-notice-actions";
          const details = document.createElement("button"); details.type = "button"; details.className = "primary"; details.textContent = ui("Ver actualización");
          const dismiss = document.createElement("button"); dismiss.type = "button"; dismiss.className = "quiet-btn"; dismiss.textContent = ui("Cerrar");
          dismiss.onclick = () => notice?.remove(); details.onclick = () => open.click();
          actions.append(details, dismiss);
          notice.append(text, actions);
          (document.getElementById("app-notice-stack") ?? document.body).append(notice);
        }
      } else {
        status.textContent = ui("El servidor no ofrece una versión más reciente.");
      }
    } catch (error) { status.textContent = String(error); }
    finally { setBusy(false); }
  }
  async function apply() {
    if (busy || !offer) return;
    setBusy(true);
    bar.value = 0;
    bar.max = offer.sizeBytes;
    bar.hidden = false;
    status.textContent = ui("Descargando la actualización…");
    const progress = new Channel<Progress>();
    progress.onmessage = event => {
      if (event.phase === "download") { bar.value = event.bytes; return; }
      bar.hidden = true;
      if (event.phase === "verify") status.textContent = ui(android ? "Verificando el instalador…" : "Verificando el ZIP…");
      if (event.phase === "extract") status.textContent = ui("Descomprimiendo la actualización…");
      if (event.phase === "install") status.textContent = ui("Seguí los pasos del instalador de Android.");
    };
    try {
      const id = offer.id;
      if (android) await flushProgress();
      const message = await invoke<string>("install_update", {id, progress});
      if (android) status.textContent = ui(message);
      else {
        status.textContent = ui("Reiniciando UMD para aplicar la actualización…");
        await flushProgress();
        await invoke("restart_update", {id});
      }
      offer = null;
    } catch (error) { status.textContent = String(error); }
    finally { bar.hidden = true; setBusy(false); }
  }
  open.hidden = false;
  open.onclick = () => {
    notice?.remove();
    if (!dialog.open) dialog.showModal();
    void getVersion().then(v => { byId("updates-current").textContent = ui("Versión instalada: {0}", v); });
    if (!busy) void check();
  };
  if (android) window.addEventListener("umd-update-install-cancelled", () => {
    status.textContent = ui("Android no completó la instalación. Podés volver a buscar la actualización.");
  });
  install.onclick = () => void apply();
  search.onclick = () => void check();
  return (address: string) => {
    if (address && !dialog.open) void check(true);
  };
}
