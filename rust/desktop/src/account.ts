import {ui} from "./locale";
import {activeProfileId,getProfilePref,invokeProfile} from "./profile-context";
import {invoke} from "./bridge";
import {flushProgress} from "./player";

interface BackupInfo { revision: number; updatedAt: string; hasBackup: boolean; itemCount: number | null }
interface Login { username: string; displayName: string; backup: BackupInfo | null; backupError?: string; automatic: boolean }
function el<K extends keyof HTMLElementTagNameMap>(tag: K, text?: string) { const result = document.createElement(tag); if (text !== undefined) result.textContent = text; return result; }

export function setupAccount(reloaded: () => Promise<void>, report: (message: string, error?: boolean) => void) {
  const button = document.getElementById("account") as HTMLButtonElement;
  const dialog = el("dialog"); dialog.className = "account-dialog"; document.body.append(dialog);
  const header = el("header"); header.className = "dialog-header";
  const title = el("h2", ui("Cuenta y respaldo")); title.id = "account-title";
  dialog.setAttribute("aria-labelledby", title.id);
  const close = el("button", "×"); close.type = "button"; close.className = "close"; close.setAttribute("aria-label", ui("Cerrar cuenta")); close.addEventListener("click", () => dialog.close());
  header.append(title, close); dialog.append(header);
  const content = el("div"); const message = el("p"); message.setAttribute("role", "status"); dialog.append(content, message);
  let logged: Login | null = null; let working = false;
  let syncing = false;
  async function sync(background: boolean) {
    const session = logged;
    if (!session || syncing || (background && (!session.automatic || document.hidden || dialog.open || working))) return;
    const username = session.username;
    syncing = true;
    try {
      await flushProgress();
      const result = await invokeProfile<{changed: boolean; backup?: BackupInfo}>("account_sync", {background});
      if (logged?.username !== username) return;
      if (result.backup) logged.backup = result.backup;
      if (result.changed || !background) await reloaded();
      if (dialog.open) { render(); message.textContent = ui("Perfil sincronizado."); }
    } catch (error) {
      try {
        const current = await invokeProfile<Login | null>("account_current");
        if (logged?.username === username && current) {
          logged.automatic = current.automatic;
          logged.backup = current.backup;
          if (dialog.open) render();
        }
      } catch { /* Keep the original sync error available. */ }
      if (!background) throw error;
      report(String(error), true);
    } finally { syncing = false; }
  }
  let syncDebounce: ReturnType<typeof setTimeout> | undefined;
  window.setInterval(() => void sync(true), 60_000);
  window.addEventListener("online", () => void sync(true));
  document.addEventListener("visibilitychange", () => { if (!document.hidden) void sync(true); });
  window.addEventListener("umd-profile-changed", () => {
    if (!logged?.automatic) return;
    clearTimeout(syncDebounce);
    syncDebounce = setTimeout(() => void sync(true), 2500);
  });
  void invokeProfile<Login | null>("account_current").then(session => {
    if (logged) return;
    if (session) { logged = session; button.textContent = session.displayName; void sync(true); }
    window.dispatchEvent(new CustomEvent("umd-account-changed", {detail: !!session}));
  }).catch(() => {});
  async function action(task: () => Promise<void>) {
    if (working) return; working = true; message.textContent = ui("Conectando…"); message.className = "";
    content.querySelectorAll<HTMLInputElement | HTMLButtonElement>("button,input").forEach(b => b.disabled = true);
    try { await flushProgress(); await task(); } catch (e) { message.textContent = String(e); message.className = "error"; }
    finally { working = false; content.querySelectorAll<HTMLInputElement | HTMLButtonElement>("button,input").forEach(b => b.disabled = false); }
  }
  function field(form: HTMLFormElement, title: string, type: string, max: number) {
    const label = el("label", title); label.className = "field"; const input = el("input"); input.type = type; input.required = true; input.maxLength = max; label.append(input); form.append(label); return input;
  }
  function render() {
    content.replaceChildren(); message.textContent = "";
    if (!logged) {
      const form = el("form"); const url = field(form, ui("Servidor"), "url", 2048); url.placeholder = "https://tu-servidor";
      const user = field(form, ui("Usuario"), "text", 64); user.autocomplete = "username"; user.minLength = 3;
      const password = field(form, ui("Contraseña"), "password", 256); password.autocomplete = "current-password"; password.minLength = 1;
      const display = field(form, ui("Nombre visible"), "text", 100); display.required = false; display.parentElement!.hidden = true;
      const device = field(form, ui("Nombre del dispositivo"), "text", 100); device.required = false;
      device.placeholder = /Android/i.test(navigator.userAgent) ? "UMD Android" : "UMD Desktop";
      const option = el("label"); option.className = "toggle-row";
      const register = el("input"); register.type = "checkbox";
      option.append(register, el("span", ui("Crear una cuenta"))); form.append(option);
      register.addEventListener("change", () => {display.parentElement!.hidden = !register.checked; password.autocomplete = register.checked ? "new-password" : "current-password"; password.minLength=register.checked?10:1;});
      const submit = el("button", ui("Continuar")); submit.type = "submit"; submit.className = "primary"; form.append(submit);
      const hint = el("p", ui("La cuenta se recuerda en este perfil hasta cerrar sesión o hasta que venza el acceso. El respaldo incluye catálogo, favoritos y posiciones guardadas.")); hint.className = "muted"; form.append(hint);
      form.addEventListener("submit", event => { event.preventDefault(); void action(async () => {
        try { logged = await invokeProfile<Login>("account_login", { baseUrl: url.value, username: user.value, password: password.value, register: register.checked, displayName: display.value.trim() || null, deviceName: device.value.trim() || device.placeholder }); password.value = ""; button.textContent = logged.displayName; window.dispatchEvent(new CustomEvent("umd-account-changed",{detail:true})); render(); }
        finally { password.value = ""; }
      }); }); content.append(form);
      void invoke<string>("server_url").then(v => { if (!url.value) url.value = v; }).catch(() => {});
      return;
    }
    content.append(el("p", ui("Sesión de {0} (@{1})", logged.displayName, logged.username)));
    const info = logged.backup;
    if (info) content.append(el("p", info.hasBackup ? ui("Respaldo: {0} títulos · {1} · revisión {2}", info.itemCount ?? 0, new Date(info.updatedAt).toLocaleString(document.documentElement.lang), info.revision) : ui("Esta cuenta todavía no tiene un respaldo.")));
    else content.append(el("p", logged.backupError || ui("Revisá el respaldo antes de guardar.")));
    const controls = el("div"); controls.className = "button-row";
    const combine = el("button", ui("Sincronizar ahora")); combine.type = "button"; combine.className = "quiet-btn";
    combine.onclick = () => void action(() => sync(false));
    const automaticLabel = el("label"); automaticLabel.className = "toggle-row";
    const automatic = el("input"); automatic.type = "checkbox"; automatic.checked = logged.automatic;
    automatic.onchange = () => void action(async () => {
      const enabled = automatic.checked;
      try { await invokeProfile("account_set_automatic", {enabled}); }
      catch (error) { automatic.checked = !enabled; throw error; }
      logged!.automatic = enabled;
      if (enabled) await sync(false);
      else message.textContent = ui("Sincronización automática desactivada.");
    });
    automaticLabel.append(automatic, el("span", ui("Sincronizar automáticamente")));
    const syncHint = el("p", ui("Combina favoritos, listas, preferencias y progreso de esta cuenta. También incorpora los títulos que falten en cada biblioteca."));
    syncHint.className = "muted";
    content.append(automaticLabel, syncHint);
    controls.append(combine);
    const upload = el("button", ui("Guardar respaldo")); upload.type = "button"; upload.className = "primary";
    upload.addEventListener("click", () => action(async () => { await invokeProfile("account_upload"); logged!.backup = await invokeProfile<BackupInfo>("account_backup_info"); render(); message.textContent = ui("Respaldo guardado en tu cuenta."); }));
    const review = el("button", ui("Revisar respaldo")); review.type = "button"; review.className = "quiet-btn"; review.addEventListener("click", () => action(async () => { logged!.backup = await invokeProfile<BackupInfo>("account_backup_info"); render(); message.textContent = ui("Respaldo actualizado. Podés revisar la fecha y cantidad antes de restaurar."); }));
    if (info) controls.append(upload); controls.append(review); content.append(controls);
    if (info?.hasBackup) {
      const warning = el("p", ui("Restaurar reemplaza los favoritos, listas y posiciones de este perfil. Con varios perfiles, los títulos del respaldo se agregan o actualizan en la biblioteca compartida. Con un solo perfil, se reemplaza el catálogo.")); warning.className = "muted"; content.append(warning);
      const restore = el("button", ui("Restaurar este respaldo")); restore.type = "button"; restore.className = "quiet-btn";
      const confirmation = el("div"); confirmation.className = "notice-danger"; confirmation.hidden = true;
      const confirmRow = el("div"); confirmRow.className = "button-row";
      const accept = el("button", ui("Confirmar restauración")); accept.type = "button"; accept.className = "danger-btn";
      const cancel = el("button", ui("Cancelar")); cancel.type = "button"; cancel.className = "quiet-btn";
      confirmRow.append(accept, cancel);
      confirmation.append(confirmRow);
      restore.onclick = () => { restore.hidden = true; confirmation.hidden = false; cancel.focus(); };
      cancel.onclick = () => { confirmation.hidden = true; restore.hidden = false; restore.focus(); };
      accept.addEventListener("click", () => action(async () => {
        await invokeProfile<number>("account_restore", {reviewedRevision:info.revision}); await reloaded(); dialog.close(); report(ui("Biblioteca restaurada desde tu cuenta."));
      })); content.append(restore, confirmation);
    }
    const logout = el("button", ui("Cerrar sesión")); logout.type = "button"; logout.className = "logout"; logout.addEventListener("click", () => action(async () => { await invokeProfile("account_logout",{purgeDownloads:getProfilePref(activeProfileId(),"torrent-purge")==="on"}); logged = null; window.dispatchEvent(new CustomEvent("umd-account-changed",{detail:false})); button.textContent = ui("Cuenta y respaldo"); render(); })); content.append(logout);
  }
  button.addEventListener("click", async () => {
    if (working) { if (!dialog.open) dialog.showModal(); return; }
    try {
      const previousUsername = logged?.username ?? null;
      const current = await invokeProfile<Login | null>("account_current");
      logged = current;
      button.textContent = current ? current.displayName : ui("Cuenta y respaldo");
      if ((current?.username ?? null) !== previousUsername) {
        window.dispatchEvent(new CustomEvent("umd-account-changed", {detail: !!current}));
      }
      render();
      if (!dialog.open) dialog.showModal();
    } catch(e) {report(String(e),true);}
  });
}
