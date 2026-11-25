import {activeProfileId,getProfilePref,windowProfile,writeProfilePref} from "./profile-context";
import {ui} from "./locale";
import {invoke} from "./bridge";
import {playBroadcast, type Stream} from "./player";
import {followProvider} from "./provider-playback";
import {isHttpsUrl} from "./safety";
import {isPageActive, navigateTo, onPageEnter, onPageLeave} from "./router";

interface Channel { name: string; group: string; url: string; logo?: string; provider: string }

export function setupStreams(report: (message: string, error?: boolean) => void) {
  const byId = <T extends HTMLElement>(id: string) => document.getElementById("streams-" + id) as T;
  const address = byId<HTMLInputElement>("address");
  const kind = byId<HTMLSelectElement>("kind");
  const submit = byId<HTMLButtonElement>("submit");
  const filter = byId<HTMLInputElement>("filter");
  const groupSelect = byId<HTMLSelectElement>("group");
  const countEl = byId<HTMLElement>("count");
  const status = byId("status");
  const fallback = document.createElement("div");
  fallback.className = "button-row";
  fallback.hidden = true;
  status.after(fallback);

  let channels: Channel[] = [];
  let limit = 60;
  let generation = 0;
  let opening = false;
  const addresses: Record<string, string> = {
    playlist: "https://iptv-org.github.io/iptv/countries/ar.m3u",
    "ip-tv-app": "https://ip-tv.app/Argentina",
    video: "",
    cablevision: "https://www.cablevisionhd.com/parrilla-directo.php",
    "tv-internet": "https://www.tvporinternet.org/",
    futbol: "https://www.librefutbol2.com/parrilla-directo.php",
  };
  const provider = byId<HTMLSelectElement>("provider");
  const providerName = (id: string) => Array.from(kind.options).find(option => option.value === id)?.text || id;
  function showAddress() {
    address.disabled = kind.value === "all";
    address.closest("label")!.hidden = address.disabled;
    address.readOnly = !["playlist", "video"].includes(kind.value);
    address.value = addresses[kind.value] || "";
    provider.closest("label")!.hidden = kind.value !== "all";
  }
  const enabled = new Map<string,HTMLInputElement>();
  const controls = document.createElement("details");
  controls.className = "streams-providers-details";
  const heading = document.createElement("summary"); heading.textContent = ui("Fuentes de TV habilitadas");
  const grid = document.createElement("div"); grid.className = "streams-providers-grid";
  controls.append(heading, grid);
  for (const id of Object.keys(addresses).filter(id => id !== "video")) {
    const label = document.createElement("label"); label.className="toggle-row";
    const check=document.createElement("input"); check.type="checkbox"; check.checked=id !== "ip-tv-app";
    const text = document.createElement("span"); text.textContent = providerName(id);
    enabled.set(id,check); label.append(check,text); grid.append(label);
    check.onchange=()=>{void writeProfilePref("tv-enabled-"+id,String(check.checked)); if(kind.value==="all") byId<HTMLFormElement>("form").requestSubmit();};
  }
  byId("form").after(controls);
  const loadPrefs=windowProfile().then(()=>{
    for (const [id,box] of enabled) {
      const saved = getProfilePref(activeProfileId(),"tv-enabled-"+id);
      if (saved !== null) box.checked = saved !== "false";
    }
  }).catch(()=>{});
  showAddress();
  let mode = kind.value;

  function clearFallback() {
    fallback.replaceChildren();
    fallback.hidden = true;
  }
  function updateWebButton() {
    byId("web").hidden = kind.value !== "video" || !isHttpsUrl(address.value);
  }
  async function open(name: string, url: string, request: number) {
    const live = mode !== "video";
    const variants = live
      ? (await invoke<(Stream & { label: string })[]>("live_variants", { address: url }))
        .map((source, index) => ({ ...source, live: true, label: source.label || (index === 0 ? ui("Calidad automática") : ui("Señal {0}", index + 1)) }))
      : undefined;
    const result = variants?.[0] ?? await invoke<Stream>("resolve_video", { address: url });
    if (request !== generation || !isPageActive("streams")) return;
    status.textContent = "";
    clearFallback();
    await playBroadcast(name, { ...result, live }, report, variants);
  }

  function populateGroups() {
    const groups = [...new Set(channels.filter(c => !provider.value || c.provider === provider.value).map(c => c.group.trim()).filter(Boolean))].sort((a, b) => a.localeCompare(b));
    groupSelect.replaceChildren(new Option(ui("Todos los grupos"), ""));
    for (const g of groups) groupSelect.add(new Option(g, g));
  }

  function render(preserveStatus = false) {
    const query = filter.value.trim().toLocaleLowerCase();
    const chosenGroup = groupSelect.value;
    const found = channels.filter(c => {
      if (provider.value && c.provider !== provider.value) return false;
      if (chosenGroup && c.group.trim() !== chosenGroup) return false;
      if (!query) return true;
      return (c.name + " " + c.group).toLocaleLowerCase().includes(query);
    });
    const rows = found.slice(0, limit).map(c => {
      const card = document.createElement("button");
      card.type = "button";
      card.className = "channel-card";
      const icon = document.createElement("span");
      icon.className = "channel-icon";
      icon.dataset.icon = "live-tv";
      if (c.logo && isHttpsUrl(c.logo)) {
        const img = document.createElement("img");
        img.alt = ""; img.loading = "lazy"; img.referrerPolicy = "no-referrer";
        img.onload = () => icon.removeAttribute("data-icon");
        img.onerror = () => { img.remove(); icon.dataset.icon = "live-tv"; };
        icon.append(img);
        img.src = c.logo;
      }
      const info = document.createElement("span");
      info.className = "channel-info";
      const title = document.createElement("strong");
      title.textContent = c.name;
      const meta = document.createElement("small");
      meta.textContent = [providerName(c.provider), c.group].filter(Boolean).join(" · ");
      info.append(title, meta);
      const pill = document.createElement("span");
      pill.className = "channel-watch-pill";
      pill.textContent = "▶ " + ui("Ver");
      card.append(icon, info, pill);
      card.onclick = async () => {
        const request = ++generation;
        opening = true;
        card.disabled = true;
        clearFallback();
        status.textContent = ui("Abriendo canal…");
        try {
          await open(c.name, c.url, request);
        } catch (error) {
          if (request !== generation) return;
          status.textContent = String(error);
          if (isHttpsUrl(c.url)) {
            const web = document.createElement("button");
            web.type = "button";
            web.className = "primary";
            web.textContent = ui("Ver en UMD");
            web.onclick = async () => {
              web.disabled = true;
              try {
                await followProvider(
                  c.url,
                  null,
                  c.name,
                  0,
                  () => {},
                  captured => playBroadcast(c.name, { ...captured.stream, live: mode !== "video" }, report),
                  { live: mode !== "video" },
                );
              } catch (err) { status.textContent = String(err); }
              finally { web.disabled = false; }
            };
            fallback.replaceChildren(web);
            fallback.hidden = false;
          }
        } finally {
          if (request === generation) opening = false;
          card.disabled = false;
        }
      };
      return card;
    });
    byId("results").replaceChildren(...rows);
    byId("more").hidden = limit >= found.length;
    countEl.textContent = found.length === 1 ? ui("1 canal") : ui("{0} canales", found.length);
    if (!preserveStatus && !opening) status.textContent = "";
  }

  function reset() {
    updateWebButton();
    ++generation;
    opening = false;
    channels = [];
    filter.value = "";
    groupSelect.replaceChildren(new Option(ui("Todos los grupos"), ""));
    limit = 60;
    byId("channels").hidden = true;
    submit.disabled = false;
    status.textContent = "";
    clearFallback();
  }

  const adFilter = document.querySelector<HTMLSelectElement>("#player-ad-filter")!;
  async function loadMode(nextMode: string | null) {
    adFilter.disabled = true;
    try {
      const resolved = await invoke<string>("player_ad_filter", { mode: nextMode });
      adFilter.value = resolved;
      settingsMode.value = resolved;
    } catch (e) {
      if (isPageActive("streams")) status.textContent = String(e);
      else report(String(e), true);
    } finally {
      adFilter.disabled = false;
    }
  }
  adFilter.onchange = () => void loadMode(adFilter.value);
  const settingsMode = document.getElementById("settings-filter-mode") as HTMLSelectElement;
  const update = document.getElementById("filters-update") as HTMLButtonElement;
  const feedback = document.getElementById("filters-status")!;
  const sourceList = document.getElementById("filters-sources")!;
  async function listStatus(force:boolean) {
    update.disabled = true;
    try {
      if (force) feedback.textContent = ui("Actualizando listas…");
      const info = await invoke<{updated:number;requested:number;sources:{name:string;updatedAt:string|null}[]}>("filter_status",{force});
      sourceList.replaceChildren(...info.sources.map(s => {
        const li=document.createElement("li"); li.textContent = s.name+" · "+(s.updatedAt ? new Date(s.updatedAt).toLocaleString() : ui("Pendiente")); return li;
      }));
      if(force) feedback.textContent=ui("Listas actualizadas: {0} de {1}",info.updated,info.requested);
    } catch(e) {feedback.textContent=String(e);} finally { update.disabled=!["standard","complete"].includes(settingsMode.value); }
  }
  document.getElementById("settings-open")!.addEventListener("click",async()=>{
    try {settingsMode.value=await invoke<string>("player_ad_filter",{mode:null}); await listStatus(false);}catch(e){feedback.textContent=String(e);}
  });
  settingsMode.onchange=async()=>{try {adFilter.value=await invoke<string>("player_ad_filter",{mode:settingsMode.value}); await listStatus(false);}catch(e){feedback.textContent=String(e);}};
  update.onclick=()=>void listStatus(true);

  function showPage() {
    updateWebButton();
    navigateTo("streams", { resetStack: true });
  }

  onPageEnter("streams", () => {
    updateWebButton();
    void loadMode(null);
    void loadPrefs.then(()=>{ if (isPageActive("streams") && mode === "all" && !channels.length && !submit.disabled) byId<HTMLFormElement>("form").requestSubmit(); });
  });
  onPageLeave("streams", () => {
    ++generation;
    opening = false;
    submit.disabled = false;
    clearFallback();
  });

  byId("open").onclick = showPage;
  const web = byId<HTMLButtonElement>("web");
  web.onclick = async () => {
    web.disabled = true;
    try {
      await followProvider(
        address.value,
        null,
        ui("Video online"),
        0,
        () => {},
        captured => playBroadcast(ui("Video online"), captured.stream, report),
      );
    }
    catch (e) { status.textContent = String(e); }
    finally { web.disabled = false; }
  };
  address.oninput = reset;
  kind.onchange = () => {
    if (mode !== "all") addresses[mode] = address.value;
    mode = kind.value;
    showAddress();
    submit.textContent = mode === "video" ? ui("Reproducir") : ui("Cargar canales");
    reset();
  };
  provider.onchange = () => { populateGroups(); limit = 60; render(); };
  filter.oninput = () => {
    ++generation;
    opening = false;
    clearFallback();
    limit = 60;
    render();
  };
  groupSelect.onchange = () => {
    ++generation;
    opening = false;
    clearFallback();
    limit = 60;
    render();
  };
  byId("more").onclick = () => { limit += 60; render(opening); };
  byId("form").onsubmit = async event => {
    event.preventDefault();
    reset();
    const request = generation;
    submit.disabled = true;
    status.textContent = ui("Cargando…");
    try {
      if (mode === "video") {
        await open(ui("Video online"), address.value, request);
      } else {
        const sources = mode === "all" ? [...enabled].filter(([,box]) => box.checked).map(([id]) => id) : [mode];
        const results = await Promise.allSettled(sources.map(async id => {
          const rows = await invoke<Channel[]>("live_channels", {address: mode === "all" ? addresses[id] : address.value, grid: !["playlist", "ip-tv-app"].includes(id)});
          return rows.map(row => ({...row, provider: id}));
        }));
        if (request !== generation) return;
        const failed: string[] = [];
        channels = results.flatMap((result, index) => {
          if (result.status === "fulfilled") return result.value;
          failed.push(providerName(sources[index])); return [];
        });
        provider.replaceChildren(new Option(ui("Todos los proveedores"), ""));
        for (const id of new Set(channels.map(c => c.provider))) provider.add(new Option(providerName(id), id));
        provider.closest("label")!.hidden = mode !== "all";
        status.textContent = failed.length ? ui("Fuentes que no respondieron: {0}", failed.join(", ")) : "";
        populateGroups();
        byId("channels").hidden = false;
        render(true);
      }
    } catch (e) {
      if (request === generation) status.textContent = String(e);
    } finally {
      if (request === generation) submit.disabled = false;
    }
  };
}
