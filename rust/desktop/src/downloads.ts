import {listen} from "@tauri-apps/api/event";
import {activeProfileId,getProfilePref,writeProfilePref,windowProfile} from "./profile-context";
import {ui} from "./locale";
export interface PrivateTracker {endpoint:string;requiresAuthentication:boolean;credentialKey?:string}
import {invoke} from "./bridge";
import type {OpenedMedia, Target} from "./player";
import {isPageActive, navigateTo, onPageEnter, onPageLeave} from "./router";

interface Download {id:number; name:string; state:string; ready:boolean; received:number; total:number; error?:string}
type Open = (name:string, media:OpenedMedia, target:Target|null) => Promise<void>;
let queue: (magnet:string|null, metadata:string|null, target:Target, privateTrackers:PrivateTracker[]|null) => void;
export function queueCatalogDownload(magnet:string|null, metadata:string|null, target:Target, privateTrackers:PrivateTracker[]|null) { queue(magnet,metadata,target,privateTrackers); }

function formatBytes(bytes: number): string {
  if (bytes >= 1073741824) return (bytes / 1073741824).toFixed(2) + " GiB";
  return (bytes / 1048576).toFixed(1) + " MiB";
}

export function setupDownloads(open: Open) {
  const form = document.querySelector<HTMLFormElement>("#downloads-form")!;
  const magnetInput = document.querySelector<HTMLInputElement>("#magnet")!;
  const results = document.getElementById("downloads-results")!;
  const output = document.getElementById("downloads-status")!;
  const statTotal = document.getElementById("stat-downloads-total")!;
  const statActive = document.getElementById("stat-downloads-active")!;
  const statSize = document.getElementById("stat-downloads-size")!;
  const settingsStatus=document.getElementById("torrent-settings-status")!;
  const toggles=["auto","info","private","purge"];
  const check=(key:string)=>document.getElementById("torrent-"+key) as HTMLInputElement;
  const enabled=(key:string)=>getProfilePref(activeProfileId(),"torrent-"+key)==="on";
  for(const key of toggles)check(key).onchange=()=>void writeProfilePref("torrent-"+key,check(key).checked?"on":"off");
  document.getElementById("settings-open")!.addEventListener("click",async()=>{
    try{await windowProfile();for(const key of toggles)check(key).checked=enabled(key);
      check("resume").checked=await invoke<boolean>("torrent_options",{fastResume:null});
    }catch(e){settingsStatus.textContent=String(e);}
  });
  check("resume").onchange=()=>void invoke("torrent_options",{fastResume:check("resume").checked}).catch(e=>settingsStatus.textContent=String(e));
  const register=document.getElementById("torrent-register")!;register.hidden=/Android/i.test(navigator.userAgent);
  register.onclick=()=>void invoke("register_magnets").then(()=>{settingsStatus.textContent=ui("Asociación registrada.");}).catch(e=>settingsStatus.textContent=String(e));
  const floating=document.createElement("button");floating.type="button";floating.className="app-notice download-notice";floating.hidden=true;floating.onclick=()=>show();(document.getElementById("app-notice-stack") ?? document.body).append(floating);
  let backgroundBusy=false;
  async function background() {
    if(backgroundBusy || document.hidden || isPageActive("downloads")){floating.hidden=true;return;}
    backgroundBusy=true;
    try{const list=await invoke<Download[]>("list_downloads",{activeOnly:true});const d=list.find(d=>!d.ready && !d.error && d.state!=="paused");
      floating.hidden=!d || isPageActive("downloads");if(d)floating.textContent=d.name+" · "+formatBytes(d.received)+" / "+formatBytes(d.total);
    }finally{backgroundBusy=false;}
  }
  window.setInterval(()=>void background().catch(()=>{floating.hidden=true;}),4000);
  const targets = new Map<number, Target>();
  let timer: ReturnType<typeof setTimeout>;
  let generation = 0;
  let submitting = false;

  function button(label: string, action: () => Promise<void>, className?: string) {
    const b = document.createElement("button");
    b.type = "button";
    b.textContent = label;
    if (className) b.className = className;
    b.onclick = async () => {
      b.disabled = true;
      try { await action(); }
      catch (e) { output.textContent = String(e); }
      finally { b.disabled = false; }
    };
    return b;
  }

  const rows = new Map<number, {
    box: HTMLElement;
    badge: HTMLElement;
    state: HTMLElement;
    progress: HTMLProgressElement;
    actions: HTMLElement;
    signature: string;
    editingFiles: boolean;
  }>();

  function updateStats(list: Download[]) {
    statTotal.textContent = String(list.length);
    const active = list.filter(d => !d.ready && d.state !== "paused").length;
    statActive.textContent = String(active);
    const totalBytes = list.reduce((sum, d) => sum + d.received, 0);
    statSize.textContent = formatBytes(totalBytes);
  }

  function display(d: Download, request: number) {
    let row = rows.get(d.id);
    if (!row) {
      const box = document.createElement("section");
      box.className = "download-card";
      const header = document.createElement("div");
      header.className = "download-card-header";
      const name = document.createElement("h3");
      name.textContent = d.name;
      const badge = document.createElement("span");
      badge.className = "entry-badge";
      header.append(name, badge);
      const state = document.createElement("p");
      state.className = "download-meta";
      const progress = document.createElement("progress");
      progress.max = 100;
      const actions = document.createElement("div");
      actions.className = "button-row";
      box.append(header, state, progress, actions);
      results.append(box);
      row = { box, badge, state, progress, actions, signature: "", editingFiles: false };
      rows.set(d.id, row);
    }
    const percent = d.total ? Math.floor((100 * d.received) / d.total) : 0;
    const label = d.ready
      ? ui("Completa")
      : d.state === "paused"
        ? ui("En pausa")
        : d.state === "initializing"
          ? ui("Verificando")
          : ui("Descargando");
    row.badge.textContent = label;
    row.badge.className = "entry-badge " + (d.ready ? "seen" : d.state === "paused" ? "" : "progress");
    row.progress.value = d.ready ? 100 : percent;
    const sizeInfo = d.total
      ? `${formatBytes(d.received)} / ${formatBytes(d.total)}`
      : formatBytes(d.received);
    row.state.textContent = d.error || `${percent}% · ${sizeInfo}`;
    if (row.editingFiles) return;
    const signature = d.state + String(d.ready);
    if (row.signature === signature) return;
    row.signature = signature;
    const actions = row.actions;
    actions.replaceChildren(
      button(d.state === "paused" ? ui("Reanudar") : ui("Pausar"), async () => {
        await invoke("pause_download", { id: d.id, paused: d.state !== "paused" });
        await refresh(request);
      }, "quiet-btn"),
      button(ui("Ver archivos"), async () => {
        const files = await invoke<[number, string, boolean][]>("download_files", { id: d.id });
        if (generation !== request || !isPageActive("downloads")) return;
        const activeRow = rows.get(d.id);
        if (!activeRow) return;
        activeRow.editingFiles = true;
        const choices = new Set(files.filter(f => f[2]).map(f => f[0]));
        const options = files.map(([index, name, included]) => {
          const fileRow = document.createElement("div");
          fileRow.className = "download-file-row";
          const fileLabel = document.createElement("label");
          const checkbox = document.createElement("input");
          checkbox.type = "checkbox";
          checkbox.checked = included;
          checkbox.onchange = () => { if (checkbox.checked) choices.add(index); else choices.delete(index); };
          fileLabel.append(checkbox, name);
          fileRow.append(fileLabel);
          const playable = /\.(epub|cbz|pdf|mp4|m4v|webm|mkv|mov|mp3|aac|m4a|flac|ogg|opus|wav)$/i.test(name);
          if (included && (d.ready || playable)) {
            fileRow.append(button(d.ready ? ui("Abrir") : /\.(pdf|epub|cbz)$/i.test(name) ? ui("Leer mientras descarga") : ui("Reproducir mientras descarga"), async () => {
              const media = await invoke<OpenedMedia>(d.ready ? "open_download" : "stream_download", { id: d.id, index });
              if (generation === request && isPageActive("downloads")) await open(name, media, targets.get(d.id) ?? null);
            }, "primary"));
          }
          return fileRow;
        });
        const save = button(ui("Guardar selección"), async () => {
          await invoke("choose_download_files", { id: d.id, files: [...choices] });
          const active = rows.get(d.id);
          if (active) { active.editingFiles = false; active.signature = ""; }
          await refresh(request);
        }, "primary");
        const cancel = button(ui("Cancelar"), async () => {
          const active = rows.get(d.id);
          if (active) { active.editingFiles = false; active.signature = ""; }
          await refresh(request);
        }, "quiet-btn");
        const manifest = files.some(([, name]) => name.split(/[\\/]/).pop()?.toLowerCase() === "info.json");
        actions.replaceChildren(...options, save, cancel);
        if (manifest) {
          actions.append(button(ui("Importar catálogo del torrent"), async () => {
            const added = await invoke<number>("import_download_manifest", { id: d.id });
            output.textContent = added === 1 ? ui("Se importó 1 título.") : ui("Se importaron {0} títulos.", added);
            window.dispatchEvent(new Event("umd-catalog-changed"));
          }, "primary"));
        }
      }, "quiet-btn"),
      button(ui("Eliminar"), async () => {
        const activeRow = rows.get(d.id);
        if (!activeRow) return;
        activeRow.editingFiles = true;
        const confirm = document.createElement("div");
        confirm.className = "notice-danger inline-confirm";
        const deleteFilesLabel = document.createElement("label");
        deleteFilesLabel.className = "toggle-row";
        const deleteFiles = document.createElement("input");
        deleteFiles.type = "checkbox";
        deleteFiles.checked = true;
        deleteFilesLabel.append(deleteFiles, " " + ui("Borrar también los archivos descargados"));
        confirm.append(
          deleteFilesLabel,
          button(ui("Confirmar eliminación"), async () => {
            await invoke("delete_download", { id: d.id, deleteFiles: deleteFiles.checked });
            activeRow.editingFiles = false;
            await refresh(request);
          }, "danger-btn"),
          button(ui("Cancelar"), async () => {
            activeRow.editingFiles = false;
            activeRow.signature = "";
            await refresh(request);
          }, "quiet-btn"),
        );
        actions.replaceChildren(confirm);
      }, "danger-btn"),
    );
  }

  let emptyEl: HTMLElement | null = null;
  async function refresh(request: number) {
    clearTimeout(timer);
    try {
      const list = await invoke<Download[]>("list_downloads");
      if (generation !== request || !isPageActive("downloads")) return;
      updateStats(list);
      const activeIds = new Set(list.map(d => d.id));
      for (const [id, row] of rows) {
        if (!activeIds.has(id)) {
          row.box.remove();
          rows.delete(id);
          targets.delete(id);
        }
      }
      for (const id of targets.keys()) {
        if (!activeIds.has(id)) targets.delete(id);
      }
      if (!list.length) {
        if (!emptyEl) {
          emptyEl = document.createElement("section");
          emptyEl.className = "empty";
          const h = document.createElement("h2");
          h.textContent = ui("Sin descargas activas");
          const p = document.createElement("p");
          p.textContent = ui("Pegá un enlace magnet, abrí un archivo .torrent o iniciá una descarga desde la ficha de un título.");
          emptyEl.append(h, p);
          results.append(emptyEl);
        }
      } else {
        emptyEl?.remove();
        emptyEl = null;
        for (const d of list) display(d, request);
      }
    } catch (e) {
      if (generation === request) output.textContent = String(e);
    } finally {
      if (generation === request && isPageActive("downloads")) {
        clearTimeout(timer);
        timer = setTimeout(() => void refresh(request), 2500);
      }
    }
  }

  function show() {
    rows.clear();
    emptyEl = null;
    results.replaceChildren();
    output.textContent = "";
    navigateTo("downloads", { resetStack: true });
    void refresh(++generation);
  }

  async function add(magnet: string | null, metadata: string | null, target?: Target, privateTrackers: PrivateTracker[] | null = null) {
    if (submitting) return;
    submitting = true;
    form.inert = true;
    output.textContent = ui("Buscando metadatos…");
    try {
      const id = await invoke<number | null>("add_download", { magnet, metadata, privateTrackers, forcePrivate:enabled("private"), profileId:(await windowProfile()).activeId });
      if (id !== null && target) targets.set(id, target);
      output.textContent = id === null ? "" : ui("Torrent agregado.");
      if (id !== null) {
        magnetInput.value = "";
        if(enabled("info")){
          const files=await invoke<[number,string,boolean][]>("download_files",{id});
          if(files.some(([,name])=>/(^|[\\/])info\.json$/i.test(name))) {
            await invoke("import_download_manifest",{id});window.dispatchEvent(new Event("umd-catalog-changed"));
          }
        }
      }
      if (isPageActive("downloads")) await refresh(generation);
    } catch (e) {
      output.textContent = String(e);
    } finally {
      submitting = false;
      form.inert = false;
    }
  }

  onPageEnter("downloads", () => {
    floating.hidden=true;
    void refresh(++generation);
  });
  onPageLeave("downloads", () => {
    ++generation;
    clearTimeout(timer);
  });

  document.getElementById("downloads-open")!.onclick = show;
  document.getElementById("downloads-refresh")!.onclick = () => void refresh(++generation);
  form.onsubmit = e => { e.preventDefault(); void add(magnetInput.value, null); };
  document.getElementById("torrent-file")!.onclick = () => void add(null, null);
  let receiving=false;
  const receive=async()=>{
    if(receiving)return;receiving=true;
    try{
      await windowProfile();
      const magnet=await invoke<string|null>("next_magnet");
      if(!magnet)return;
      show();magnetInput.value=magnet;
      if(enabled("auto"))await add(magnet,null);else magnetInput.focus();
    }finally{receiving=false;}
  };
  void listen("umd-magnet",()=>void receive().catch(e=>{output.textContent=String(e);})).then(receive).catch(()=>{});
  queue = (magnet, metadata, target, privateTrackers) => { show(); void add(magnet, metadata, target, privateTrackers); };
}
