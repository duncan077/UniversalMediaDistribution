import {ui, displayTitle} from "./locale";
import {invokeProfile} from "./profile-context";
import {invoke} from "./bridge";
import type {CatalogItem} from "./main";
import {setupSharedLists} from "./shared-lists";
import {isPageActive, navigateTo, onPageEnter} from "./router";

export interface MediaListEntry { itemId?: string; name: string }
export interface MediaList { id: string; name: string; items: MediaListEntry[] }

function el<K extends keyof HTMLElementTagNameMap>(tag: K, text?: string, className?: string) {
  const node = document.createElement(tag);
  if (text !== undefined) node.textContent = text;
  if (className) node.className = className;
  return node;
}

export function inlineListMembership(
  item: CatalogItem,
  onOpenListsPage: () => void,
  signal?: AbortSignal,
): HTMLElement {
  const panel = el("section", undefined, "surface-panel detail-lists-panel");
  const title = el("h3", ui("Mis listas"));
  const chips = el("div", undefined, "list-chips");
  const controls = el("div", undefined, "detail-lists-controls");
  const status = el("p", undefined, "muted");
  status.setAttribute("role", "status");
  panel.append(title, chips, controls, status);

  let selectedListId = "";
  let busy = false;

  async function refresh() {
    if (signal?.aborted) return;
    try {
      const lists = await invokeProfile<MediaList[]>("personal_lists");
      if (signal?.aborted || !panel.isConnected) return;
      render(lists);
    } catch (e) {
      if (!signal?.aborted && panel.isConnected) status.textContent = String(e);
    }
  }

  async function mutate(fields: object) {
    if (busy) return;
    busy = true;
    panel.inert = true;
    status.textContent = "";
    try {
      await invokeProfile("edit_list", { edit: fields });
      window.dispatchEvent(new Event("umd-lists-changed"));
      await refresh();
    } catch (e) {
      status.textContent = String(e);
    } finally {
      panel.inert = false;
      busy = false;
    }
  }

  function render(lists: MediaList[]) {
    chips.replaceChildren();
    controls.replaceChildren();
    if (!lists.length) {
      const empty = el("p", ui("Todavía no tenés listas."), "muted");
      const openBtn = el("button", ui("Crear una lista"), "quiet-btn");
      openBtn.type = "button";
      openBtn.onclick = onOpenListsPage;
      controls.append(empty, openBtn);
      return;
    }

    const memberLists = lists.filter(l => l.items.some(i => i.itemId === item.syncId));
    for (const list of memberLists) {
      const chip = el("span", undefined, "list-chip");
      chip.append(el("span", list.name));
      const remove = el("button", "×", "chip-remove");
      remove.type = "button";
      remove.setAttribute("aria-label", ui("Quitar de {0}", list.name));
      remove.title = ui("Quitar de {0}", list.name);
      remove.onclick = () => void mutate({ kind: "member", id: list.id, itemId: item.syncId, enabled: false });
      chip.append(remove);
      chips.append(chip);
    }

    if (!selectedListId || !lists.some(l => l.id === selectedListId)) {
      selectedListId = lists.find(l => !l.items.some(i => i.itemId === item.syncId))?.id ?? lists[0].id;
    }

    const select = el("select") as HTMLSelectElement;
    select.setAttribute("aria-label", ui("Elegir lista"));
    for (const list of lists) {
      const opt = new Option(`${list.name} (${list.items.length})`, list.id);
      select.add(opt);
    }
    select.value = selectedListId;

    const actionBtn = el("button") as HTMLButtonElement;
    actionBtn.type = "button";

    const syncButton = () => {
      selectedListId = select.value;
      const target = lists.find(l => l.id === selectedListId);
      const isMember = !!target?.items.some(i => i.itemId === item.syncId);
      actionBtn.textContent = isMember ? ui("Quitar de la lista") : ui("Agregar a la lista");
      actionBtn.className = isMember ? "danger-btn" : "primary";
    };
    syncButton();
    select.onchange = syncButton;
    actionBtn.onclick = () => {
      const target = lists.find(l => l.id === selectedListId);
      if (!target) return;
      const isMember = target.items.some(i => i.itemId === item.syncId);
      void mutate({ kind: "member", id: target.id, itemId: item.syncId, enabled: !isMember });
    };

    controls.append(select, actionBtn);
  }

  void refresh();
  return panel;
}

export function setupLists(
  showDetails: (item: CatalogItem) => void,
  renderCard: (item: CatalogItem, onOpen: () => void, local: boolean) => HTMLElement,
  refreshLibrary: () => Promise<void>,
) {
  const content = document.querySelector<HTMLElement>("#lists-content")!;
  const form = document.querySelector<HTMLFormElement>("#lists-create")!;
  const output = document.querySelector<HTMLElement>("#lists-status")!;
  const nameInput = form.querySelector("input")!;
  let selectedId = "";
  let generation = 0;
  let pending = false;

  const publish = setupSharedLists(async () => {
    await refreshLibrary();
    window.dispatchEvent(new Event("umd-lists-changed"));
    if (isPageActive("lists")) await render();
  });

  function button(label: string, task: () => Promise<void>, className?: string) {
    const b = el("button", label, className);
    b.type = "button";
    b.onclick = () => void action(task);
    return b;
  }

  async function action(task: () => Promise<void>) {
    if (pending) return;
    pending = true;
    content.inert = form.inert = true;
    output.textContent = "";
    try {
      await task();
    } catch (e) {
      output.textContent = String(e);
    } finally {
      content.inert = form.inert = false;
      pending = false;
    }
  }

  async function edit(fields: object) {
    await invokeProfile("edit_list", { edit: fields });
    window.dispatchEvent(new Event("umd-lists-changed"));
    await render();
  }

  async function render() {
    const request = ++generation;
    const lists = await invokeProfile<MediaList[]>("personal_lists");
    if (!isPageActive("lists") || request !== generation) return;
    content.replaceChildren();

    if (!lists.length) {
      output.textContent = "";
      const empty = el("section", undefined, "empty");
      empty.append(
        el("h2", ui("Todavía no tenés listas")),
        el("p", ui("Creá tu primera lista arriba o agregá títulos desde cualquier ficha.")),
      );
      content.append(empty);
      return;
    }

    output.textContent = "";
    if (!selectedId || !lists.some(l => l.id === selectedId)) {
      selectedId = lists[0].id;
    }
    const activeList = lists.find(l => l.id === selectedId) ?? lists[0];

    // List tabs / selector chips
    const tabs = el("div", undefined, "season-bar lists-tabs");
    for (const list of lists) {
      const tab = el(
        "button",
        `${list.name} (${list.items.length})`,
        "season-chip" + (list.id === activeList.id ? " active" : ""),
      );
      tab.type = "button";
      tab.setAttribute("aria-pressed", String(list.id === activeList.id));
      tab.onclick = () => {
        selectedId = list.id;
        void render();
      };
      tabs.append(tab);
    }
    content.append(tabs);

    // Management bar for the active list (Rename / Share / Delete)
    const bar = el("div", undefined, "surface-panel list-manage-bar");
    const rename = el("input") as HTMLInputElement;
    rename.maxLength = 80;
    rename.value = activeList.name;
    rename.setAttribute("aria-label", ui("Nombre de la lista"));

    const controls = el("div", undefined, "list-manage-controls");
    const shareBtn = el("button", ui("Compartir lista"), "quiet-btn");
    shareBtn.type = "button";
    shareBtn.onclick = () => publish(activeList.id, activeList.name);

    const deleteBtn = el("button", ui("Eliminar lista"), "danger-btn");
    deleteBtn.type = "button";
    deleteBtn.onclick = () => {
      const confirm = el("div", undefined, "notice-danger inline-confirm");
      confirm.append(
        el("span", ui("¿Eliminar «{0}»?", activeList.name)),
        button(ui("Eliminar"), () => edit({ kind: "delete", id: activeList.id }), "danger-btn"),
        button(ui("Cancelar"), async () => { confirm.replaceWith(controls); }, "quiet-btn"),
      );
      controls.replaceWith(confirm);
    };

    controls.append(
      rename,
      button(ui("Renombrar"), () => edit({ kind: "rename", id: activeList.id, name: rename.value }), "quiet-btn"),
      shareBtn,
      deleteBtn,
    );
    bar.append(controls);
    content.append(bar);

    // Poster grid of the active list's items!
    if (!activeList.items.length) {
      const empty = el("section", undefined, "empty");
      empty.append(
        el("h2", ui("Esta lista está vacía")),
        el("p", ui("Abrí cualquier título de tu biblioteca para agregarlo a «{0}».", activeList.name)),
      );
      content.append(empty);
      return;
    }

    const grid = el("section", undefined, "catalog");
    content.append(grid);

    const loaded = await Promise.all(
      activeList.items.map(async (entry) => {
        if (!entry.itemId) return { entry, item: null as CatalogItem | null };
        try {
          const item = await invoke<CatalogItem>("library_item", { id: entry.itemId });
          return { entry, item };
        } catch {
          return { entry, item: null as CatalogItem | null };
        }
      }),
    );
    if (!isPageActive("lists") || request !== generation) return;

    const frag = document.createDocumentFragment();
    for (const { entry, item } of loaded) {
      if (item) {
        const card = renderCard(item, () => showDetails(item), true);
        const removeFromList = el("button", "×", "list-card-remove");
        removeFromList.type = "button";
        removeFromList.title = ui("Quitar de {0}", activeList.name);
        removeFromList.setAttribute("aria-label", ui("Quitar {0} de {1}", displayTitle(item), activeList.name));
        removeFromList.onclick = (e) => {
          e.stopPropagation();
          void action(() => edit({ kind: "member", id: activeList.id, itemId: item.syncId, enabled: false }));
        };
        card.append(removeFromList);
        frag.append(card);
      } else {
        const fallback = el("article", undefined, "surface-panel missing-list-item");
        fallback.append(el("strong", entry.name), el("p", ui("No disponible en la biblioteca local."), "muted"));
        frag.append(fallback);
      }
    }
    grid.append(frag);
  }

  function openPage() {
    navigateTo("lists", { resetStack: true });
    void render().catch(e => { output.textContent = String(e); });
  }

  onPageEnter("lists", () => {
    void render().catch(e => { output.textContent = String(e); });
  });

  form.onsubmit = e => {
    e.preventDefault();
    void action(async () => {
      await edit({ kind: "create", name: nameInput.value });
      nameInput.value = "";
    });
  };
  document.getElementById("lists-open")!.onclick = openPage;
  return openPage;
}
