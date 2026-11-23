import {ui, displayTitle} from "./locale";
import {invoke} from "./bridge";
import {activeProfileId, getProfilePref, invokeProfile, setProfilePref, windowProfile, writeProfilePref} from "./profile-context";
import type {CatalogItem} from "./main";
import {remoteUrl} from "./safety";
import {isPageActive, navigateTo, onPageEnter} from "./router";

interface Edition { id: number; joint: boolean; scan: string; slug: string }
interface Entry { externalId: string; preview: { name: string; mediaType?:string;subtype?:string; genres?: string[]; categories?: string[]; thumbnailRef?: string | null; releaseYear?: number | null } }
interface Hit { item?: CatalogItem; summary?:boolean; edition?: Edition; entry?: Entry; title: string; group: string; cover?: string | null }
interface Page { items: Hit[]; nextPage: number | null }

type ContentLabels = { genres?: string[]; categories?: string[] };
const adultLabels = new Set(["adult", "adulto", "porn", "porno", "+18", "18+"]);
export function hiddenTitle(item?: ContentLabels) {
  return getProfilePref(activeProfileId(), "umd-hide-adult") === "true" && !!item &&
    [...(item.categories ?? []), ...(item.genres ?? [])].some(label => adultLabels.has(String(label).trim().toLowerCase()));
}

function similar(a:string,b:string) {
  if (a===b) return !!a;
  if (Math.abs(a.length-b.length)>2 || a.length>256 || b.length>256 || a+"s"===b || b+"s"===a) return false;
  const max=Math.min(2,Math.floor(Math.max(a.length,b.length)*0.2));
  let previous=Array.from({length:b.length+1},(_,i)=>i);
  for(let i=1;i<=a.length;i++){
    const current=[i];
    for(let j=1;j<=b.length;j++) current[j]=Math.min(current[j-1]+1, previous[j]+1,previous[j-1]+Number(a[i-1]!==b[j-1]));
    if(Math.min(...current)>max)return false;
    previous=current;
  }
  return previous[b.length]<=max;
}

export function setupProvider(onAdded: (item: CatalogItem, openDetails: boolean) => Promise<void>) {
  const form = document.querySelector<HTMLFormElement>("#provider-form")!;
  const site = document.querySelector<HTMLSelectElement>("#provider-site")!;
  const input = document.querySelector<HTMLInputElement>("#provider-query")!;
  const status = document.querySelector<HTMLElement>("#provider-status")!;
  const results = document.querySelector<HTMLElement>("#provider-results")!;
  const more = document.querySelector<HTMLButtonElement>("#provider-more")!;
  let generation = 0;
  let query = "";
  type Source = { id: string; name: string; cursor: string | null; favorite: boolean };
  let pending: Source[] = [];
  const options = Array.from(site.options).filter(o => !["all", "local", "server"].includes(o.value));
  const hide = document.getElementById("hide-adult") as HTMLInputElement;
  const prefBoxes: { box: HTMLInputElement; key: string; kind: "enabled" | "favorite" }[] = [];

  const syncPrefs = (profileId: string | null) => {
    hide.checked = getProfilePref(profileId, "umd-hide-adult") === "true";
    for (const { box, key, kind } of prefBoxes) {
      box.checked = kind === "enabled" ? getProfilePref(profileId, key) !== "false" : getProfilePref(profileId, key) === "true";
    }
  };
  const applyPref = (key: string, value: string) => {
    const id = activeProfileId();
    if (id) {
      setProfilePref(id, key, value);
      clear();
      window.dispatchEvent(new Event("umd-catalog-changed"));
      return;
    }
    void writeProfilePref(key, value).then(() => {
      clear();
      window.dispatchEvent(new Event("umd-catalog-changed"));
    }).catch(() => {});
  };
  hide.onchange = () => applyPref("umd-hide-adult", String(hide.checked));
  for (const o of options) {
    const line = document.createElement("div");
    for (const kind of ["enabled", "favorite"] as const) {
      const label = document.createElement("label");
      const box = document.createElement("input");
      const key = "provider-" + kind + "-" + o.value;
      box.type = "checkbox";
      prefBoxes.push({ box, key, kind });
      label.append(box, " " + (kind === "enabled" ? ui("Consultar {0}", o.text) : ui("Priorizar {0}", o.text)));
      box.onchange = () => applyPref(key, String(box.checked));
      line.append(label);
    }
    document.getElementById("provider-preferences")!.append(line);
  }
  syncPrefs(activeProfileId());
  void windowProfile().then(({ activeId }) => syncPrefs(activeId)).catch(() => {});
  type Location = {id:string;name:string;base:string};
  const settings = document.getElementById("source-settings") as HTMLFormElement;
  const choose = document.getElementById("source-settings-site") as HTMLSelectElement;
  const name = document.getElementById("source-settings-name") as HTMLInputElement;
  const base = document.getElementById("source-settings-base") as HTMLInputElement;
  const remove = document.getElementById("source-settings-remove") as HTMLButtonElement;
  const feedback = document.getElementById("source-settings-status")!;
  const defaults = new Map(options.map(o => [o.value,o.text]));
  let locations:Location[] = [];
  function selected() {
    const location = locations.find(l=>l.id===choose.value);
    name.value=location?.name || defaults.get(choose.value) || "";base.value=location?.base || "";
    remove.disabled=!location;
  }
  async function configure(edit?:Location, del?:string) {
    settings.inert=true;
    try {
      locations=await invoke<Location[]>("provider_locations",{edit:edit || null, remove:del || null});
      const current=site.value;
      site.querySelectorAll("option[data-custom]").forEach(o=>o.remove());
      for (const opt of options) opt.text=locations.find(l=>l.id===opt.value)?.name || defaults.get(opt.value)!;
      for(const l of locations.filter(l=>l.id.startsWith("json-"))) {const opt=new Option(l.name,l.id);opt.dataset.custom="1";site.add(opt);}
      site.value=Array.from(site.options).some(o=>o.value===current)?current:"all";
      choose.replaceChildren(new Option(ui("Agregar proveedor JSON"),""),...Array.from(site.options).filter(o=>!["local","server","all","capibaratraductor"].includes(o.value)).map(o=>new Option(o.text,o.value)));
      if(edit) choose.value=edit.id || locations.at(-1)?.id || "";
      selected(); feedback.textContent=edit || del ? ui("Preferencias guardadas.") : "";
      if(edit || del) clear();
    } catch(e){feedback.textContent=String(e);}finally{settings.inert=false;}
  }
  choose.onchange=selected;
  settings.onsubmit=e=>{e.preventDefault();void configure({id:choose.value,name:name.value,base:base.value});};
  remove.onclick=()=>void configure(undefined,choose.value);
  void configure();
  const shown = new Set<string>();
  const editions = new Map<string,{name:string;year:number|null;variants:HTMLDetailsElement;size:number}[]>();
  let unified: HTMLUListElement | undefined;
  function groupEdition(hit:Hit, provider:string) {
    const data=hit.item || hit.entry?.preview;
    const type = data?.mediaType?.toLowerCase() || (hit.edition ? "book" : provider);
    const name = hit.title.normalize("NFKD").replace(/\p{M}/gu,"").toLowerCase().replace(/[^\p{L}\p{N}]/gu,"");
    const key = [type,type==="book" ? data?.subtype?.split(".").pop() || "manga" : "",name.match(/\d+/g)?.join(",") || ""].join("|");
    const year = data?.releaseYear || null;
    const matches = editions.get(key) || [];
    const match = matches.find(candidate => (!year || !candidate.year || candidate.year===year) && similar(name,candidate.name));
    if (match) {
      match.size++; match.variants.querySelector("summary")!.textContent=ui("{0} ediciones",match.size);
      match.variants.hidden=false; match.variants.querySelector("ul")!.append(row(hit,provider,true)); return;
    }
    if (!unified) {
      const li=document.createElement("li"); unified=document.createElement("ul"); unified.className="provider-hit-grid"; li.append(unified); results.prepend(li);
    }
    const primary=row(hit,provider), variants=document.createElement("details");
    const altList=document.createElement("ul"); altList.className="provider-alt-list";
    variants.hidden=true; variants.append(document.createElement("summary"),altList);primary.append(variants);unified.append(primary);
    matches.push({name,year,variants,size:1});editions.set(key,matches);
  }

  let refreshedAt = 0;
  let refreshing = false;
  const refreshSaved = async () => {
    if (refreshing || document.hidden || Date.now() - refreshedAt < 3600000) return;
    refreshing = true;
    try {
      const { activeId } = await windowProfile();
      const sites = options.filter(o => getProfilePref(activeId, "provider-enabled-" + o.value) !== "false").map(o => o.value);
      const updated = await invoke<number>("refresh_saved_editions", { sites });
      refreshedAt = Date.now();
      if (updated) window.dispatchEvent(new Event("umd-catalog-changed"));
    } catch {
      /* Retry later or when back online instead of locking out for an hour. */
    } finally {
      refreshing = false;
    }
  };
  void refreshSaved();
  window.setInterval(() => void refreshSaved(), 3600000);
  document.addEventListener("visibilitychange", () => void refreshSaved());
  window.addEventListener("online", () => void refreshSaved());

  function clear() {
    ++generation;
    shown.clear(); editions.clear(); unified=undefined;
    results.replaceChildren();
    status.textContent = "";
    pending = [];
    more.hidden = true;
  }

  function row(hit: Hit, provider: string, compact = false) {
    const li = document.createElement("li");
    li.className = compact ? "provider-alt-row" : "provider-hit-card";
    const coverUrl = compact ? null : remoteUrl(hit.cover ?? hit.item?.thumbnailRef ?? hit.entry?.preview?.thumbnailRef);
    const thumb = compact ? null : document.createElement("div");
    if (thumb) {
      thumb.className = "provider-thumb";
      if (coverUrl) {
        const img = document.createElement("img");
        img.src = coverUrl;
        img.alt = "";
        img.loading = "lazy";
        img.referrerPolicy = "no-referrer";
        img.onerror = () => img.remove();
        thumb.append(img);
      } else {
        thumb.textContent = hit.title.slice(0, 2).toUpperCase();
      }
    }

    const label = document.createElement("div");
    label.className = "provider-hit-info";
    const titleEl = document.createElement("strong");
    titleEl.textContent = hit.title;
    const group = document.createElement("small");
    group.textContent = hit.group;
    label.append(titleEl, group);

    const add = document.createElement("button");
    add.type = "button";
    add.className = provider === "local" ? "quiet-btn" : "primary";
    add.textContent = provider === "local" ? ui("Abrir ficha") : ui("Agregar");
    add.setAttribute("aria-label", provider === "local" ? ui("Ver {0}", hit.title) : ui("Agregar {0}, edición de {1}", hit.title, hit.group));
    add.addEventListener("click", async () => {
      const request = generation;
      add.disabled = true;
      add.textContent = ui("Agregando…");
      try {
        const item = provider.startsWith("json-") && hit.item
          ? await invoke<CatalogItem>("json_provider_add",{site:provider,id:hit.item.syncId,preview:hit.summary?null:hit.item,hideAdult:hide.checked})
          : hit.item
          ? (provider === "local" ? hit.item : await invoke<CatalogItem>("import_remote_item", { id: hit.item.syncId, hideAdult: hide.checked }))
          : hit.entry
            ? await invoke<CatalogItem>("provider_add", { site: provider, entry: hit.entry, hideAdult: hide.checked })
            : await invoke<CatalogItem>("capibara_add", { edition: hit.edition, hideAdult: hide.checked });
        if (hiddenTitle(item)) throw new Error(ui("El filtro +18 ocultó esta ficha."));
        const openDetails = request === generation && isPageActive("provider");
        await onAdded(item, openDetails);
      } catch (error) {
        if (request === generation) status.textContent = String(error);
      } finally {
        add.disabled = false;
        add.textContent = provider === "local" ? ui("Abrir ficha") : ui("Agregar");
      }
    });
    if (thumb) li.append(thumb, label, add);
    else li.append(label, add);
    return li;
  }

  let daily = false;
  async function read(source: Source): Promise<{ hits: Hit[]; next: string | null }> {
    if (source.id === "local" || source.id === "server") {
      let items: CatalogItem[], next: string | null;
      if (source.id === "local") {
        const page = await invokeProfile<{ document: { items: CatalogItem[] }; continuationToken: string | null }>("library",
          { query: { q: query, cursor: source.cursor, limit: 25 } });
        items = page.document.items;
        next = page.continuationToken;
      } else {
        [items, next] = await invoke<[CatalogItem[], string | null]>("search_remote_catalog", { query, cursor: source.cursor });
      }
      return { hits: items.map(item => ({ item, title: displayTitle(item), group: source.name, cover: item.thumbnailRef })), next };
    }
    if (source.id.startsWith("json-")) {
      const [items,next,summary] = await invoke<[CatalogItem[],string|null,boolean]>("json_provider_search",{site:source.id,query,cursor:source.cursor});
      return {hits:items.map(item=>({item,summary,title:displayTitle(item),group:source.name})),next};
    }
    const page = Number(source.cursor ?? "1");
    if (source.id === "capibaratraductor") {
      const data = await invoke<Page>(daily ? "capibara_updates" : "capibara_search", { query, page });
      return { hits: data.items.map(h => ({ ...h, group: source.name + " · " + h.group })), next: daily ? null : data.nextPage?.toString() ?? null };
    }
    if (daily) {
      const entries = await invoke<Entry[]>("provider_updates", { site: source.id });
      return { hits: entries.map(entry => ({ entry, title: entry.preview.name, group: source.name, cover: entry.preview.thumbnailRef })), next: null };
    }
    const data = await invoke<{ items: Entry[]; next: string | null }>("provider_search", { site: source.id, query, page });
    return { hits: data.items.map(entry => ({ entry, title: entry.preview.name, group: source.name, cover: entry.preview.thumbnailRef })), next: data.next };
  }

  async function search(first: boolean) {
    const request = ++generation;
    status.textContent = ui("Buscando…");
    more.disabled = true;
    const [connected, profile] = await Promise.all([
      invoke<string>("server_url").catch(() => ""),
      windowProfile().catch(() => null),
    ]);
    const profileId = profile?.activeId ?? activeProfileId();
    if (request !== generation) return;
    if (first) {
      pending = Array.from(site.options)
        .filter(o => o.value !== "all" && (!daily || !["local", "server"].includes(o.value)) && (site.value === "all"
          ? (o.value !== "server" || (!!connected && getProfilePref(profileId,"online-catalog")!=="off")) && getProfilePref(profileId, "provider-enabled-" + o.value) !== "false"
          : o.value === site.value))
        .map(o => ({
          id: o.value,
          name: o.text,
          cursor: null,
          favorite: getProfilePref(profileId, "provider-favorite-" + o.value) === "true",
        }))
        .sort((a, b) => Number(b.favorite) - Number(a.favorite));
    }
    const batch = pending.sort((a, b) => Number(b.favorite) - Number(a.favorite));
    pending = [];
    const failed: string[] = [];
    const groups = batch.map(src => {
      const groupLi = document.createElement("li");
      groupLi.className = "provider-group";
      groupLi.dataset.providerName = src.name;
      return groupLi;
    });
    let index = 0;
    async function worker() {
      while (index < batch.length && request === generation) {
        const slot = index++, source = batch[slot], group = groups[slot];
        try {
          const data = await read(source);
          if (request !== generation) return;
          for (const hit of data.hits) {
            if (hiddenTitle(hit.item ?? hit.entry?.preview)) continue;
            const key = source.id + ":" + (hit.item?.syncId ?? hit.entry?.externalId ?? String(hit.edition?.joint) + ":" + hit.edition?.id);
            if (shown.has(key)) continue;
            shown.add(key);
            if (site.value==="all" && !daily) {groupEdition(hit,source.id);continue;}
            if (!group.querySelector("ul")) {
              const heading = document.createElement("h3");
              heading.className = "provider-group-heading";
              heading.textContent = source.name;
              const list = document.createElement("ul");
              list.className = "provider-hit-grid";
              group.append(heading, list);
            }
            group.querySelector("ul")!.appendChild(row(hit, source.id));
          }
          if (data.next !== null && data.next !== source.cursor) pending.push({ ...source, cursor: data.next });
        } catch (error) {
          if (request !== generation) return;
          failed.push(source.name);
          pending.push(source);
          const errBox = document.createElement("div");
          errBox.className = "provider-error-row";
          const note = document.createElement("span");
          note.textContent = source.name + " · " + String(error);
          errBox.append(note);
          if (!["server", "local"].includes(source.id) && /deneg|denied|401|403|verific/i.test(String(error))) {
            const open = document.createElement("button");
            open.type = "button";
            open.textContent = ui("Abrir sitio del proveedor");
            open.onclick = async () => {
              try { await invoke("provider_interactive", { site: source.id, language: document.documentElement.lang }); }
              catch (e) { note.textContent = String(e); }
            };
            errBox.append(open);
          }
          group.append(errBox);
        }
      }
    }
    results.append(...groups);
    await Promise.all(Array.from({ length: Math.min(batch.length, 4) }, worker));
    if (request !== generation) return;
    groups.filter(g => !g.childNodes.length).forEach(g => g.remove());
    more.disabled = false;
    more.hidden = pending.length === 0;
    const foundText = shown.size === 1 ? ui("1 edición encontrada.") : ui("{0} ediciones encontradas.", shown.size);
    status.textContent = shown.size === 0 && !failed.length
      ? ui("Sin ediciones disponibles en estas páginas.")
      : foundText + (failed.length ? " · " + ui("Fuentes sin respuesta: {0}", failed.join(", ")) : "");
  }

  function openSearch(initialQuery?: string) {
    void windowProfile().then(({ activeId }) => syncPrefs(activeId)).catch(() => {});
    navigateTo("provider", { resetStack: true });
    if (initialQuery && initialQuery.trim()) {
      clear();
      site.value = "all";
      query = input.value = initialQuery.trim();
      daily = false;
      void search(true);
    } else {
      input.focus();
    }
  }

  onPageEnter("provider", () => {
    void windowProfile().then(({ activeId }) => syncPrefs(activeId)).catch(() => {});
  });

  document.getElementById("provider-open")!.addEventListener("click", () => openSearch());
  site.addEventListener("change", clear);
  form.addEventListener("submit", event => {
    event.preventDefault();
    clear();
    daily = false;
    query = input.value.trim();
    if (query) void search(true);
  });
  document.getElementById("provider-daily")!.onclick = () => {
    clear();
    daily = true;
    if (["local", "server"].includes(site.value)) site.value = "all";
    void search(true);
  };
  const librarySearch = document.getElementById("search") as HTMLInputElement;
  librarySearch.addEventListener("keydown", e => {
    if (e.key !== "Enter" || !librarySearch.value.trim()) return;
    e.preventDefault();
    openSearch(librarySearch.value.trim());
  });
  more.addEventListener("click", () => { if (pending.length) void search(false); });
  return openSearch;
}
