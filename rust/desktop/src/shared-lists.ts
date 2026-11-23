import {ui} from "./locale";
import {invoke} from "./bridge";
import {invokeProfile} from "./profile-context";
import {flushProgress} from "./player";

interface Published {shareId: string; name: string; url: string; itemCount: number}
interface Preview {link: string; name: string; ownerDisplayName: string; catalog: {items: {name: string}[]}}
function el<K extends keyof HTMLElementTagNameMap>(tag: K, text?: string) {
  const node = document.createElement(tag);
  if (text !== undefined) node.textContent = text;
  return node;
}
export function setupSharedLists(saved: () => Promise<void>) {
  const dialog = el("dialog");
  const title = el("h2"); title.id = "shared-title"; dialog.setAttribute("aria-labelledby", title.id);
  const header = el("header"); header.className = "dialog-header";
  const close = el("button","×"); close.type = "button"; close.className = "close"; close.setAttribute("aria-label",ui("Cerrar"));
  close.onclick = () => dialog.close(); header.append(title,close);
  const body = el("div"); const status = el("p"); status.setAttribute("role","status");
  dialog.append(header,body,status); document.body.append(dialog);
  let generation = 0;
  dialog.addEventListener("close", () => { if (!dialog.open) generation++; });
  function open(heading: string) {
    generation++; body.replaceChildren(); status.textContent = ""; title.textContent = heading;
    body.inert = false; if (!dialog.open) dialog.showModal();
  }
  async function run<T>(task: () => Promise<T>, done: (result: T) => void) {
    if (body.inert) return;
    const current = generation;
    body.inert = true; status.textContent = ui("Conectando…");
    try {
      const result = await task();
      if (current === generation && dialog.open) { status.textContent = ""; done(result); }
    } catch (e) { if (current === generation && dialog.open) status.textContent = String(e); }
    finally { if (current === generation) body.inert = false; }
  }
  function button(label: string, click: () => void) {
    const b = el("button",label); b.type = "button"; b.onclick = click; return b;
  }
  function showLink(row: Published) {
    const box = el("section"); box.append(el("h3",row.name));
    const field = el("input"); field.type = "url"; field.readOnly = true; field.value = row.url; field.setAttribute("aria-label",ui("Enlace público"));
    const copy = button(ui("Copiar enlace"), async () => {
      try { await navigator.clipboard.writeText(row.url); status.textContent = ui("Enlace copiado."); }
      catch { field.focus(); field.select(); status.textContent = ui("Enlace seleccionado para copiar."); }
    });
    const revoke = button(ui("Retirar enlace"), () => {
      const confirmation = el("div"); confirmation.append(el("p",ui("¿Retirar el enlace público de «{0}»?", row.name)),
        button(ui("Retirar"), () => void run(() => invokeProfile("revoke_list",{shareId:row.shareId}), () => {
          box.remove(); status.textContent = ui("Enlace retirado.");
        })), button(ui("Cancelar"), () => confirmation.replaceWith(revoke)));
      revoke.replaceWith(confirmation);
    });
    box.append(field,copy,revoke); body.append(box);
  }
  document.getElementById("shared-owned")!.onclick = () => {
    open(ui("Mis enlaces publicados"));
    void run(() => invokeProfile<Published[]>("published_lists"), rows => {
      if (!rows.length) status.textContent = ui("Todavía no publicaste listas en esta cuenta.");
      rows.forEach(showLink);
    });
  };
  document.getElementById("shared-open")!.onclick = () => {
    open(ui("Abrir lista compartida"));
    const form = el("form"); const label = el("label",ui("Enlace de UMD")); label.className = "field";
    const input = el("input"); input.type = "url"; input.required = true; label.append(input);
    const submit = el("button",ui("Ver lista")); submit.type = "submit"; form.append(label,submit); body.append(form); input.focus();
    form.onsubmit = event => {
      event.preventDefault();
      void run(() => invoke<Preview>("open_shared_list",{link:input.value}), preview => {
        const count = preview.catalog.items.length;
        body.replaceChildren(
          el("h3",preview.name),
          el("p", count === 1 ? ui("{0} · 1 título", preview.ownerDisplayName) : ui("{0} · {1} títulos", preview.ownerDisplayName, count)),
        );
        const list = el("ul"); for (const item of preview.catalog.items.slice(0,100)) list.append(el("li",item.name)); body.append(list);
        if (count>100) {
          const extra = count - 100;
          body.append(el("p", extra === 1 ? ui("Y 1 título más.") : ui("Y {0} títulos más.", extra)));
        }
        const save = button(ui("Guardar una copia en mis listas"), () => void run(async () => {
          const created = await invokeProfile<boolean>("save_shared_list",{link:preview.link,name:preview.name,catalog:preview.catalog});
          await saved(); return created;
        }, created => { save.disabled = true; status.textContent = created ? ui("Copia guardada en Mis listas.") : ui("Este perfil ya tiene una copia."); }));
        body.append(save);
      });
    };
  };
  return (id: string, name: string) => {
    open(ui("Compartir lista"));
    body.append(el("p",ui("Se sincronizará tu perfil con tu cuenta. Cualquier persona que tenga el enlace podrá ver «{0}» y las fuentes de sus títulos.", name)),
      button(ui("Publicar enlace"), () => void run(async () => {
        await flushProgress(); return invokeProfile<Published>("publish_list",{listId:id});
      }, row => { body.replaceChildren(); showLink(row); })));
  };
}
