import {invoke} from "./bridge";
import {ui} from "./locale";
import {ProviderMedia} from "./provider-playback";
import {attachPartyPlayer} from "./party";
import {attachPlaybackOverlay} from "./playback-overlay";
import {nativeSourceUrl} from "./safety";
import type {Target} from "./player";

type Track = {id:number;name:string};
type TrackKind = "audio" | "subtitle";
interface State {closed?:boolean;error?:string;trackFailed?:boolean;ready?:boolean;pointerMoved?:boolean;paused:boolean;ended:boolean;duration:number;position:number;
  audio?:Track[];subtitle?:Track[];selectedAudio:number;selectedSubtitle:number;renderers?:Track[];selectedRenderer?:number;rendererFailed?:boolean}
interface PlayerOptions {
  engines:HTMLSelectElement; servers?:HTMLSelectElement; wireless?:HTMLButtonElement;
  fullscreenButton:HTMLButtonElement;
  initiallyPaused:boolean; discover?:boolean; followRoom:boolean; startCast:()=>void;
}

export function formatMediaTime(seconds: number): string {
  if (!Number.isFinite(seconds) || seconds < 0) return "0:00";
  const total = Math.floor(seconds);
  const h = Math.floor(total / 3600);
  const m = Math.floor((total % 3600) / 60);
  const s = total % 60;
  if (h > 0) return `${h}:${String(m).padStart(2, "0")}:${String(s).padStart(2, "0")}`;
  return `${m}:${String(s).padStart(2, "0")}`;
}

export function nativePlayer(engine:string, address:string, view:HTMLElement, header:HTMLElement, out:HTMLElement,
    label:string,target:Target|null,sidecars:{url:string;language:string;label:string;default:boolean}[],resume:number,save:(position:number,ended:boolean)=>void, onFatal:(message:string)=>void, referer:string|undefined, hls:boolean, options:PlayerOptions) {
  const id=crypto.randomUUID(), embedded=/Android|Windows/i.test(navigator.userAgent);
  const tv = document.documentElement.classList.contains("television");
  const events=new AbortController(), eventOpts={signal:events.signal};
  const dialog=header.closest("dialog")!;

  // Subtle stage placeholder inside the viewport until the native surface reports ready
  const stagePlaceholder=document.createElement("div");
  stagePlaceholder.className="native-stage-placeholder";
  const stageBadge=document.createElement("span");
  stageBadge.className="native-stage-spinner";
  stageBadge.setAttribute("aria-hidden","true");
  const stageText=document.createElement("span");
  stageText.textContent=ui("Cargando video…");
  stagePlaceholder.append(stageBadge,stageText);
  view.append(stagePlaceholder);

  const controls=document.createElement("div");controls.className="native-hud";view.after(controls);
  controls.setAttribute("role","group");controls.setAttribute("aria-label",ui("Controles de reproducción"));
  const timeline=document.createElement("div"), mainBar=document.createElement("div");
  const transport=document.createElement("div"), settings=document.createElement("div");
  timeline.className="native-timeline";
  mainBar.className="native-bar";
  transport.className="native-transport";
  settings.className="native-settings";
  mainBar.append(transport,settings);
  controls.append(timeline,mainBar);

  function field(fieldLabel:string, control:HTMLSelectElement) {
    const wrapper=document.createElement("label"), name=document.createElement("span");
    wrapper.className="native-field";name.textContent=fieldLabel;wrapper.append(name,control);return wrapper;
  }

  const toggle=document.createElement("button"), seek=document.createElement("input");
  toggle.type="button";toggle.className="primary native-play-btn";
  toggle.disabled=true;
  const toggleGlyph=document.createElement("span"), toggleLabel=document.createElement("span");
  toggleGlyph.className="native-btn-glyph";toggleGlyph.setAttribute("aria-hidden","true");toggleGlyph.textContent="▶";
  toggleLabel.className="native-btn-label";toggleLabel.textContent=ui("Reproducir");
  toggle.append(toggleGlyph,toggleLabel);

  seek.type="range";seek.className="native-seek";seek.min="0";seek.max="0";seek.step="1";seek.disabled=true;
  seek.setAttribute("aria-label",ui("Posición"));
  const syncSeekFill=(pos:number,dur:number)=>{
    const pct = dur > 0 ? Math.min(100, Math.max(0, (pos / dur) * 100)) : 0;
    seek.style.setProperty("--seek-pct", `${pct.toFixed(2)}%`);
  };
  syncSeekFill(0,0);

  const elapsedDisplay=document.createElement("span");
  elapsedDisplay.className="native-time native-time-current";
  elapsedDisplay.textContent="0:00";
  const durationDisplay=document.createElement("span");
  durationDisplay.className="native-time native-time-total";
  durationDisplay.textContent="--:--";

  const tracks=(trackLabel:string,kind:TrackKind)=>{
    const el=document.createElement("select");el.setAttribute("aria-label",trackLabel);
    el.disabled = true;
    el.add(new Option(ui("Cargando…"), ""));
    el.onchange=()=>void selectTrack(kind,Number(el.value));
    return el;
  };
  function skip(seconds: number) {
    const b = document.createElement("button"); b.type = "button"; b.className = "quiet-btn native-skip-btn"; b.disabled = true;
    const labelText = seconds < 0 ? ui("Retroceder 10 segundos") : ui("Adelantar 30 segundos");
    b.textContent = seconds < 0 ? "↺ 10s" : "30s ↻";
    b.title = labelText;
    b.setAttribute("aria-label", labelText);
    b.onclick = () => { if (state?.duration) void request("seek", Math.max(0, Math.min(state.duration, state.position + seconds))).catch(fail); };
    return b;
  }
  const back = skip(-10), forward = skip(30);

  const volumeGroup = document.createElement("div");
  volumeGroup.className = "native-volume-group";
  const volume = document.createElement("input"); volume.type = "range"; volume.className = "native-volume"; volume.min = "0"; volume.max = "100"; volume.value = "100";
  volume.setAttribute("aria-label", ui("Volumen"));
  const mute = document.createElement("button"); mute.type = "button"; mute.className = "quiet-btn native-mute-btn";
  let previousVolume = 100;
  const syncVolumeUi = (level: number) => {
    volume.style.setProperty("--vol-pct", `${Math.min(100, Math.max(0, level))}%`);
    const muteLabel = level ? ui("Silenciar") : ui("Activar sonido");
    mute.textContent = level === 0 ? "🔇" : level < 50 ? "🔉" : "🔊";
    mute.title = muteLabel;
    mute.setAttribute("aria-label", muteLabel);
  };
  syncVolumeUi(100);
  const changeVolume = () => {
    const level = Number(volume.value);
    syncVolumeUi(level);
    void request("volume", level).catch(fail);
  };
  volume.oninput = () => syncVolumeUi(Number(volume.value));
  volume.onchange = changeVolume;
  mute.onclick = () => { const level = Number(volume.value); if (level) previousVolume = level; volume.value = String(level ? 0 : previousVolume); changeVolume(); };
  volumeGroup.append(mute, volume);

  const stateChip = document.createElement("span");
  stateChip.className = "native-state-chip";
  stateChip.textContent = ui("Conectando…");

  seek.oninput = () => {
    const pos = Number(seek.value);
    const dur = state?.duration ?? 0;
    elapsedDisplay.textContent = formatMediaTime(pos);
    syncSeekFill(pos, dur);
  };
  timeline.append(elapsedDisplay, seek, durationDisplay);
  transport.append(...(tv ? [back, toggle, forward] : [toggle, back, forward]), volumeGroup, stateChip);

  const audio=tracks(ui("Audio"),"audio"),subs=tracks(ui("Subtítulos"),"subtitle");
  subs.dataset.tvPrimary = "";
  const externalCaptions = engine === "android"
    ? sidecars.slice(0,100).map((track,i) => ({id:-2-i,name:track.label || track.language || ui("Subtítulos")})) : [];
  const audioField = field(ui("Audio"), audio), subtitleField = field(ui("Subtítulos"), subs);
  if (tv) settings.append(subtitleField, audioField);
  if(options.servers && options.servers.options.length > 1) settings.append(field(ui("Servidor"), options.servers));
  if (!tv) settings.append(audioField, subtitleField);
  settings.append(field(ui("Motor"),options.engines));

  let closed=false, fatal=false, started=false, restoring=false, restored=false, timer=0, lastSave=Date.now(), state:State|undefined;
  let queue=Promise.resolve(),previousBox="",seeks=0,plays=0,pauses=0,failures=0;
  let pauseAfterSwitch = options.initiallyPaused;
  let polling = false;
  let layoutFrame = 0;
  const trackChanges = {
    audio: {pending:false,revision:0},
    subtitle: {pending:false,revision:0},
  };
  const trackRevisions = () => ({audio:trackChanges.audio.revision,subtitle:trackChanges.subtitle.revision});

  const captionsAvailable = ui("Sin subtítulos");
  const audioAvailable = ui("Sin pistas de audio");
  const devices = document.createElement("select");
  const castStatus = document.createElement("span");
  castStatus.setAttribute("role","status");
  devices.hidden = true;
  devices.setAttribute("aria-label", ui("Pantalla de reproducción"));
  const cast = document.createElement("details"), castLabel=document.createElement("summary"), castTools=document.createElement("div");
  cast.className="native-cast";castLabel.textContent=ui("Transmitir");castTools.className="native-cast-tools";
  cast.append(castLabel,castTools);settings.append(cast);
  const discover = document.createElement("button");
  discover.type = "button";discover.className = "quiet-btn";discover.textContent = "Chromecast";
  const supportsCast = engine === "libvlc" || engine === "vlc";
  const scan = async (focus:boolean) => {
      discover.disabled = true;
      try {
        const found = await call("discover");
        if (found.error) throw Error(ui("No se pudo buscar Chromecast en la red."));
        update(found);
        devices.hidden = false;
        if (focus) devices.focus({preventScroll:true});
      } catch(e) {castStatus.textContent=String(e);} finally {discover.disabled=false;}
  };
  discover.onclick = ()=>supportsCast ? void scan(true) : options.startCast();
  castTools.append(discover,devices,castStatus);
  if(options.wireless)castTools.append(options.wireless);
  if (supportsCast) {
    devices.onchange = async () => {
      const position = state?.position ?? 0;
      const paused = state?.paused ?? false;
      devices.disabled = true;
      try {
        const found = await call("renderer", {value: Number(devices.value)});
        if (found.error) throw Error(ui("No se pudo cambiar de pantalla."));
        resume = position; restoring = false; restored = false; pauseAfterSwitch = paused;
        update(found);
      } catch (e) { fail(e); } finally { devices.disabled = false; }
    };
  }

  let previousPaused:boolean|undefined;
  let scrubbing=false;
  const stopScrubbing=()=>{scrubbing=false;};
  seek.addEventListener("pointerdown",()=>{scrubbing=true;},eventOpts);
  seek.addEventListener("pointerup",stopScrubbing,eventOpts);
  seek.addEventListener("pointercancel",stopScrubbing,eventOpts);
  window.addEventListener("pointerup",stopScrubbing,eventOpts);
  seek.addEventListener("keydown",e=>{
    if(["ArrowLeft","ArrowRight","ArrowUp","ArrowDown","Home","End","PageUp","PageDown"].includes(e.key)) scrubbing=true;
  },eventOpts);
  seek.addEventListener("keyup",stopScrubbing,eventOpts);
  seek.addEventListener("blur",stopScrubbing,eventOpts);

  function fail(e:unknown) {
    if(!closed) {
      out.textContent=String(e);
      out.classList.add("is-error");
    }
  }
  function box() {
    const r=view.getBoundingClientRect();
    // Windows draws VLC in a child window above the WebView. Leave the visible
    // controls accessible without resizing or moving the underlying video.
    const cutouts = /Windows/i.test(navigator.userAgent)
      ? Array.from(dialog.querySelectorAll<HTMLElement>(":scope > .player-topbar, :scope > .native-hud, :scope > .playback-center, :scope > .player-status-banner"))
        .filter(el => !el.inert && el.getClientRects().length > 0)
        .map(el => {
          const rect = el.getBoundingClientRect();
          return {left:rect.left-r.left, top:rect.top-r.top, right:rect.right-r.left, bottom:rect.bottom-r.top,
            round:el.classList.contains("playback-center")};
        }) : undefined;
    return {x:r.x,y:r.y,width:r.width,height:r.height,pageWidth:window.innerWidth,
      cutouts,visible:!document.hidden && dialog.contains(document.elementFromPoint(r.x+r.width/2,r.y+r.height/2))};
  }
  const call=(op:string,extra:Record<string,unknown>={})=>invoke<State>("native_playback",{operation:op,args:{id,...extra}});

  const scheduleLayout=()=>{
    if(!embedded || !started || closed || fatal || layoutFrame)return;
    layoutFrame=requestAnimationFrame(()=>{
      layoutFrame=0;
      if(closed || fatal || !started)return;
      const rect=box(), signature=JSON.stringify(rect);
      if(signature===previousBox)return;
      previousBox=signature;
      const task=queue.then(async()=>{if(!closed && !fatal) await call("layout",{box:rect});});
      queue=task.catch(fail);
    });
  };
  const resizeObserver=new ResizeObserver(scheduleLayout);
  resizeObserver.observe(dialog);
  resizeObserver.observe(view);
  resizeObserver.observe(controls);
  resizeObserver.observe(header.closest(".player-topbar")!);
  window.addEventListener("resize",scheduleLayout,eventOpts);
  document.addEventListener("fullscreenchange",scheduleLayout,eventOpts);

  function request(op:string,value?:number) {
    const task = queue.then(async () => {
      if (closed) return;
      const revisions = trackRevisions();
      const result = await call(op, {value});
      if (op === "seek") seeks++;
      update(result, revisions);
    });
    queue = task.catch(fail);
    return task;
  }

  async function selectTrack(kind: TrackKind, value: number) {
    if (closed || fatal || !state?.ready) return;
    const change = trackChanges[kind];
    const [control, field, empty, message] = kind === "audio"
      ? [audio, "selectedAudio", audioAvailable, ui("No se pudo cambiar la pista de audio.")] as const
      : [subs, "selectedSubtitle", captionsAvailable, ui("No se pudo cambiar el subtítulo.")] as const;
    let selected = state[field];
    if (change.pending) {
      list(control, undefined, selected, empty);
      return;
    }
    change.pending = true;
    control.setAttribute("aria-busy", "true");
    try {
      const sidecar = kind === "subtitle" && engine === "android" && value < -1 ? sidecars[-value-2] : undefined;
      const result = await call(kind, {value,address:sidecar?.url,referer:sidecar ? referer : undefined});
      if (closed || result.closed) return;
      if (result.trackFailed || result.error || !result.ready) throw Error(message);
      selected = result[field];
      if (state) state[field] = selected;
      change.revision++;
      if (out.textContent === message) {
        out.textContent = "";
        out.classList.remove("is-error");
      }
    } catch {
      fail(message);
    } finally {
      change.pending = false;
      control.removeAttribute("aria-busy");
      if (!closed) list(control, undefined, selected, empty);
    }
  }

  const togglePlayPause=()=>{
    if(!state?.ready)return;
    void request(state.paused?"play":"pause").catch(fail);
  };
  toggle.onclick=togglePlayPause;
  const overlay=embedded
    ? attachPlaybackOverlay(dialog,view,controls,options.fullscreenButton,toggle,togglePlayPause)
    : undefined;
  const chromeObserver = new MutationObserver(scheduleLayout);
  if (/Windows/i.test(navigator.userAgent)) chromeObserver.observe(dialog, {subtree:true, attributes:true, attributeFilter:["inert"]});

  // Keyboard & TV remote media shortcuts
  dialog.addEventListener("keydown",event=>{
    if(event.defaultPrevented || event.altKey || event.ctrlKey || event.metaKey)return;
    if(Array.from(document.querySelectorAll("dialog[open]")).at(-1)!==dialog)return;
    if(event.key==="MediaPlayPause"){event.preventDefault();togglePlayPause();return;}
    if(event.key==="MediaPlay"){if(state?.ready && state.paused){event.preventDefault();void request("play").catch(fail);}return;}
    if(event.key==="MediaPause"){if(state?.ready && !state.paused){event.preventDefault();void request("pause").catch(fail);}return;}
    if(event.key==="MediaRewind"){if(state?.duration){event.preventDefault();back.click();}return;}
    if(event.key==="MediaFastForward"){if(state?.duration){event.preventDefault();forward.click();}return;}

    const targetEl=event.target as HTMLElement|null;
    if(targetEl?.matches("input, select, textarea"))return;
    const key=event.key.toLowerCase();
    if(key==="k" || (event.key===" " && !targetEl?.matches("button, summary, a"))){
      event.preventDefault();
      togglePlayPause();
    } else if(key==="j" || (!tv && event.key==="ArrowLeft")){
      if(state?.duration){event.preventDefault();back.click();}
    } else if(key==="l" || (!tv && event.key==="ArrowRight")){
      if(state?.duration){event.preventDefault();forward.click();}
    } else if(key==="m"){
      event.preventDefault();
      mute.click();
    } else if(!overlay && (key==="f" || key==="t")){
      event.preventDefault();
      options.fullscreenButton.click();
    }
  },eventOpts);

  document.addEventListener("visibilitychange",()=>{
    if(document.hidden && restored && state?.ready){
      save(state.position,state.ended);
      lastSave=Date.now();
    }
    if(!document.hidden && started && !closed && !fatal) {
      clearTimeout(timer);
      void poll();
    }
  },eventOpts);

  const media=new ProviderMedia(request);
  const detach=target && options.followRoom?attachPartyPlayer(media,target,label,header,out):()=>{};
  function list(el:HTMLSelectElement, rows:Track[]|undefined, selected:number|undefined, empty:string) {
    if (rows) {
      const key=JSON.stringify(rows);
      if(el.dataset.rows!==key) {
        el.replaceChildren(...(rows.length ? rows.map(row=>new Option(row.name,String(row.id))) : [new Option(empty,"")]));
        el.dataset.rows=key;
        el.disabled=rows.length < 2;
      }
    }
    if (selected === undefined) return;
    const value=String(selected);
    if(el.value!==value && [...el.options].some(option=>option.value===value)) el.value=value;
  }
  function update(s:State, revisions = trackRevisions()) {
    if(closed)return;
    if(s.closed || s.error){fatal=true;onFatal(ui("No se pudo reproducir con este motor."));return;}
    if (state) {
      if (trackChanges.audio.pending || revisions.audio !== trackChanges.audio.revision) s.selectedAudio = state.selectedAudio;
      if (trackChanges.subtitle.pending || revisions.subtitle !== trackChanges.subtitle.revision) s.selectedSubtitle = state.selectedSubtitle;
    }
    state=s;
    if (Array.isArray(s.renderers)) {
      const rows = [{id:0,name:ui("Este dispositivo")}, ...s.renderers];
      const signature = JSON.stringify(rows);
      if (devices.dataset.rows !== signature) {
        devices.replaceChildren(...rows.map(r => new Option(r.name, String(r.id))));
        castStatus.textContent = s.renderers.length ? "" : ui("Buscando pantallas…");
        devices.dataset.rows = signature;
      }
      devices.value = String(s.selectedRenderer ?? 0);
    }
    if (s.rendererFailed) castStatus.textContent = ui("No se pudo cambiar de pantalla.");
    if(!s.ready)return;
    if(s.pointerMoved) overlay?.wake();
    stagePlaceholder.hidden = true;
    stateChip.hidden = true;
    if(previousPaused!==undefined && previousPaused!==s.paused){
      if(s.paused) pauses++; else plays++;
    }
    previousPaused=s.paused;
    media.update({position:s.position,duration:s.duration||null,paused:s.paused,ended:s.ended,ready:4,seeking:false,seekable:s.duration>0,plays,pauses,seeks});
    const actionLabel=s.paused?ui("Reproducir"):ui("Pausar");
    const glyph=s.paused?"▶":"⏸︎";
    if(toggleLabel.textContent!==actionLabel)toggleLabel.textContent=actionLabel;
    if(toggleGlyph.textContent!==glyph)toggleGlyph.textContent=glyph;
    toggle.disabled=false;
    overlay?.state(s.paused,true);
    back.disabled = forward.disabled = !s.duration;
    seek.disabled=!s.duration;seek.max=String(s.duration);
    const totalLabel = s.duration ? formatMediaTime(s.duration) : ui("En vivo");
    if(durationDisplay.textContent!==totalLabel)durationDisplay.textContent=totalLabel;
    if(!scrubbing){
      seek.value=String(s.position);
      syncSeekFill(s.position, s.duration);
      const currentText = formatMediaTime(s.position);
      if(elapsedDisplay.textContent!==currentText)elapsedDisplay.textContent=currentText;
    }
    list(audio,s.audio,trackChanges.audio.pending ? undefined : s.selectedAudio,audioAvailable);
    const captions=engine==="android" && s.subtitle ? [...s.subtitle,...externalCaptions] : s.subtitle;
    list(subs,engine==="android" && captions?.length ? [{id:-1,name:ui("Desactivado")},...captions]:captions,
      trackChanges.subtitle.pending ? undefined : s.selectedSubtitle,captionsAvailable);
    if(!restored && !restoring){
      restoring=true;
      const preferred=sidecars.slice(0,100).findIndex(t=>t.default);
      if(engine==="android" && preferred>=0)void selectTrack("subtitle",-2-preferred);
      if (pauseAfterSwitch) {pauseAfterSwitch = false; void request("pause").catch(fail);}
      if(resume>0 && (!s.duration || resume<s.duration)){
        void request("seek",resume).then(()=>{
          if(!closed){restored=true;lastSave=Date.now();}
        }).catch(fail);
        return;
      }
      restored=true;
      lastSave=Date.now();
      return;
    }
    if(!restored)return;
    if(Date.now()-lastSave>5000){save(s.position,s.ended);lastSave=Date.now();}
  }
  async function poll() {
    if(closed || fatal || polling)return;
    polling=true;
    try {
      await queue;if(closed || fatal)return;
      const rect=box(),signature=JSON.stringify(rect);
      if(embedded && signature!==previousBox){await call("layout",{box:rect});previousBox=signature;}
      const revisions = trackRevisions();
      update(await call("read"), revisions);
      failures=0;
    }catch(e){failures++;fail(e);}
    finally {
      polling=false;
      if(!closed && !fatal){
        const delay=failures ? Math.min(5000,400 * 2 ** Math.min(failures,4))
          : document.hidden ? 2500 : state?.ready && state.paused ? 1200 : 400;
        timer=window.setTimeout(poll,delay);
      }
    }
  }
  seek.onchange=()=>{scrubbing=false;void request("seek",Number(seek.value)).then(()=>{if(!closed){restored=true;lastSave=Date.now();}}).catch(fail);};
  void (async()=>{
    try {
      const initialRect=box();
      previousBox=JSON.stringify(initialRect);
      const result=await call("start",{engine,source:nativeSourceUrl(address),hls,referer,tracks:sidecars,box:initialRect});started=true;
      if(closed){await call("close");return;}
      update(result);
      if(options.discover && supportsCast && !fatal) {cast.open=true;void scan(true);}
      await poll();
    }catch(e){if(!closed)onFatal(String(e));}
  })();
  return {
    transport:()=>restored && state?.ready ? {position:state.position,paused:state.paused} : undefined,
    deviceStatus:()=>restored && !fatal && state?.ready && !state.ended
      ? {positionSeconds:state.position, durationSeconds:state.duration || null, paused:state.paused}
      : null,
    play:()=>request("play"),
    pause:()=>request("pause"),
    seek:(position:number)=>request("seek",Math.min(position,state?.duration || position)),
    addDeviceCast:(button:HTMLButtonElement)=>castTools.prepend(button),
    close:(preserveFullscreen=false)=>{
      closed=true;
      cancelAnimationFrame(layoutFrame);
      resizeObserver.disconnect();
      chromeObserver.disconnect();
      events.abort();
      clearTimeout(timer);
      detach();
      stagePlaceholder.remove();
      overlay?.close(preserveFullscreen);
      controls.remove();
      if(restored&&state?.ready)save(state.position,state.ended);
      if(started || referer)void call("close").catch(()=>{});
    },
  };
}
