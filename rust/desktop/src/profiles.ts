import {ui} from "./locale";
import {invoke} from "./bridge";
import {flushProgress} from "./player";
import {clearProfilePrefs, windowProfile, type Profiles} from "./profile-context";

export function setupProfiles() {
  const element = <T extends HTMLElement>(id: string) => document.getElementById(id) as T;
  const trigger = element<HTMLButtonElement>("profiles-open");
  const dialog = element<HTMLDialogElement>("profiles-dialog");
  const list = element("profiles-list");
  const form = element<HTMLFormElement>("profile-form");
  const name = element<HTMLInputElement>("profile-name");
  const confirm = element("profile-confirm");
  const status = element("profiles-status");
  const create = element<HTMLButtonElement>("profile-new");
  const titleHeading = element("profiles-title");
  let editing: string | null = null;
  let removing: string | null = null;
  let working = false;
  let choosing = false;
  function setChooserMode(active: boolean) {
    choosing = active;
    dialog.classList.toggle("profile-chooser-mode", active);
    titleHeading.textContent = active ? ui("Elegí un perfil") : ui("Perfiles locales");
  }
  function reset() {
    editing = removing = null;
    form.hidden = confirm.hidden = true;
    create.hidden = choosing;
    status.textContent = "";
  }
  function editor(id: string | null, value: string) {
    reset(); editing = id; create.hidden = true; form.hidden = false;
    name.value = value; name.focus(); name.select();
  }
  function button(label: string, use: () => void) {
    const control = document.createElement("button");
    control.type = "button"; control.textContent = label; control.onclick = use; return control;
  }
  function showCurrent(state: Profiles) {
    const active = state.profiles.find(p => p.id === state.activeId);
    if (active) trigger.textContent = ui("Perfil · {0}", active.name);
  }
  async function refresh() {
    const state = await invoke<Profiles>("profiles");
    const activeId = (await windowProfile()).activeId;
    if (!state.profiles.some(profile => profile.id === activeId)) {
      await flushProgress();
      window.location.reload(); return;
    }
    // Another window can change the startup profile while this window is open.
    state.activeId = activeId;
    showCurrent(state); list.replaceChildren();
    for (const p of state.profiles) {
      const row = document.createElement("section"); row.className = "profile-row";
      const label = document.createElement("strong"); label.textContent = p.name;
      const actions = document.createElement("div"); actions.className = "button-row";
      const current = p.id === state.activeId;
      const select = button(current && !choosing ? ui("En uso") : ui("Usar"), () => current ? dialog.close() : change({action:"select",id:p.id}, true));
      select.disabled = current && !choosing;
      if (choosing) select.className = current ? "primary" : "quiet-btn";
      actions.append(select);
      if (!choosing) {
        actions.append(button(ui("Renombrar"), () => editor(p.id,p.name)));
        if (state.profiles.length > 1) actions.append(button(ui("Eliminar"), () => {
          reset(); removing = p.id; create.hidden = true; confirm.hidden = false;
          element("profile-warning").textContent = ui("¿Eliminar el perfil «{0}» con sus favoritos, listas, progreso y preferencias de este dispositivo?", p.name);
          element("profile-keep").focus();
        }));
      }
      row.append(label,actions); list.append(row);
    }
  }
  async function change(edit: Record<string,string>, reload = false) {
    if (working) return;
    working = true; dialog.inert = true;
    try {
      await flushProgress();
      await invoke("edit_profile",{edit});
      if (edit.action === "delete" && edit.id) clearProfilePrefs(edit.id);
      if (reload || (edit.action === "delete" && edit.id === (await windowProfile()).activeId)) {
        sessionStorage.setItem("umd-profile-choice", "1");
        window.location.reload(); return;
      }
      reset(); await refresh(); dialog.inert = false; create.focus();
    } catch(e) {status.textContent = String(e);}
    finally { dialog.inert = false; working = false; }
  }
  trigger.onclick = async () => {
    setChooserMode(false);
    reset(); if (!dialog.open) dialog.showModal();
    try {await refresh();} catch(e) {status.textContent = String(e);}
  };
  create.onclick = () => editor(null,"");
  form.onsubmit = event => {
    event.preventDefault();
    void change(editing ? {action:"rename",id:editing,name:name.value} : {action:"create",name:name.value});
  };
  element("profile-cancel").onclick = element("profile-keep").onclick = () => {reset(); create.focus();};
  element("profile-delete").onclick = () => {if (removing) void change({action:"delete",id:removing});};
  return windowProfile().then(async state => {
    showCurrent(state);
    if (state.profiles.length > 1 && !sessionStorage.getItem("umd-profile-choice")) {
      setChooserMode(true);
      reset(); await refresh();
      await new Promise<void>(resolve => {
        dialog.addEventListener("close", () => {
          sessionStorage.setItem("umd-profile-choice", "1");
          setChooserMode(false);
          resolve();
        }, {once:true});
        if (!dialog.open) dialog.showModal();
      });
    }
  }).catch(e => {status.textContent=String(e);});
}
