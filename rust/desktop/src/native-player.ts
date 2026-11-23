import {invoke} from "./bridge";
import {ui} from "./locale";
import {ProviderMedia} from "./provider-playback";
import {attachPartyPlayer} from "./party";
import {nativeSourceUrl} from "./safety";
import type {Target} from "./player";

type Track = {id:number;name:string};
interface State {closed?:boolean;error?:string;ready?:boolean;paused:boolean;ended:boolean;duration:number;position:number;
  audio:Track[];subtitle:Track[];selectedAudio:number;selectedSubtitle:number;renderers?:Track[];selectedRenderer?:number}

function formatMediaTime(seconds: number): string {
  if (!Number.isFinite(seconds) || seconds < 0) return "0:00";
  const total = Math.floor(seconds);
  const h = Math.floor(total / 3600);
  const m = Math.floor((total % 3600) / 60);
  const s = total % 60;
  if (h > 0) return `${h}:${String(m).padStart(2, "0")}:${String(s).padStart(2, "0")}`;
  return `${m}:${String(s).padStart(2, "0")}`;
}

export function nativePlayer(engine:string, address:string, view:HTMLElement, header:HTMLElement, out:HTMLElement,
    label:string,target:Target|null,sidecars:{url:string;language:string;label:string;default:boolean}[],resume:number,save:(position:number,ended:boolean)=>void, onFatal:(message:string)=>void, referer?:string) {
  const id=crypto.randomUUID(), embedded=/Android|Windows/i.test(navigator.userAgent);
  const events=new AbortController(), eventOpts={signal:events.signal};
  const controls=document.createElement("div");controls.className="native-hud";view.after(controls);
  const toggle=document.createElement("button"), seek=document.createElement("input");
  toggle.type="button";toggle.className="primary";
  toggle.textContent=ui("Reproducir");seek.type="range";seek.className="native-seek";seek.min="0";seek.max="0";seek.step="1";seek.setAttribute("aria-label",ui("Posición"));
  const timeDisplay=document.createElement("span");timeDisplay.className="native-time";timeDisplay.textContent="0:00";
  const tracks=(label:string,op:string)=>{
    const el=document.createElement("select");el.setAttribute("aria-label",label);
    el.onchange=()=>void request(op,Number(el.value)).catch(fail);controls.append(el);return el;
  };
  function skip(seconds: number) {
    const b = document.createElement("button"); b.type = "button"; b.className = "quiet-btn"; b.disabled = true;
    b.textContent = seconds < 0 ? "−10 s" : "+30 s";
    b.onclick = () => { if (state?.duration) void request("seek", Math.max(0, Math.min(state.duration, state.position + seconds))).catch(fail); };
    return b;
  }
  const back = skip(-10), forward = skip(30);
  const volume = document.createElement("input"); volume.type = "range"; volume.className = "native-volume"; volume.min = "0"; volume.max = "100"; volume.value = "100";
  volume.setAttribute("aria-label", ui("Volumen"));
  const mute = document.createElement("button"); mute.type = "button"; mute.className = "quiet-btn"; mute.textContent = ui("Silenciar");
  let previousVolume = 100;
  const changeVolume = () => {
    const level = Number(volume.value);
    mute.textContent = level ? ui("Silenciar") : ui("Activar sonido");
    void request("volume", level).catch(fail);
  };
  volume.onchange = changeVolume;
  mute.onclick = () => { const level = Number(volume.value); if (level) previousVolume = level; volume.value = String(level ? 0 : previousVolume); changeVolume(); };
  seek.oninput = () => {
    const pos = Number(seek.value);
    timeDisplay.textContent = state?.duration ? `${formatMediaTime(pos)} / ${formatMediaTime(state.duration)}` : formatMediaTime(pos);
  };
  controls.append(toggle, back, seek, timeDisplay, forward, volume, mute);
  let hudTimer=0;
  const wake=()=>{
    controls.classList.remove("native-hud-idle");clearTimeout(hudTimer);
    hudTimer=window.setTimeout(()=>{if(!controls.matches(":hover,:focus-within")) controls.classList.add("native-hud-idle");},3500);
  };
  const dialog=header.closest("dialog")!;
  for(const event of ["pointermove","pointerdown","keydown","focusin"]) dialog.addEventListener(event,wake,eventOpts);
  wake();
  const audio=tracks(ui("Audio"),"audio"),subs=tracks(ui("Subtítulos"),"subtitle");
  let closed=false, fatal=false, started=false, restoring=false, restored=false, timer=0, lastSave=Date.now(), state:State|undefined;
  let queue=Promise.resolve(),previousBox="",seeks=0,plays=0,pauses=0,failures=0;
  let pauseAfterSwitch = false;
  const devices = document.createElement("select");
  devices.hidden = true;
  devices.setAttribute("aria-label", ui("Pantalla de reproducción"));
  if (engine === "libvlc" || engine === "vlc") {
    const discover = document.createElement("button");
    discover.type = "button";
    discover.className = "quiet-btn";
    discover.textContent = "Chromecast";
    discover.onclick = async () => {
      try {
        const found = await call("discover");
        if (found.error) throw Error(ui("No se pudo buscar Chromecast en la red."));
        update(found);
        devices.hidden = false;
        devices.focus();
      } catch (e) { fail(e); }
    };
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
    controls.append(discover, devices);
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
  document.addEventListener("visibilitychange",()=>{
    if(document.hidden && restored && state?.ready){
      save(state.position,state.ended);
      lastSave=Date.now();
    }
  },eventOpts);
  function fail(e:unknown) {if(!closed) out.textContent=String(e);}
  function box() {
    const r=view.getBoundingClientRect();
    return {x:r.x,y:r.y,width:r.width,height:r.height,pageWidth:window.innerWidth,
      visible:!document.hidden && view.contains(document.elementFromPoint(r.x+r.width/2,r.y+r.height/2))};
  }
  const call=(op:string,extra:Record<string,unknown>={})=>invoke<State>("native_playback",{operation:op,args:{id,...extra}});
  function request(op:string,value?:number) {
    const sidecar=engine==="android" && op==="subtitle" && value!==undefined && value < -1 ? sidecars[-value-2] : undefined;
    const task=queue.then(async()=>{if(!closed){const result=await call(op,{value,address:sidecar?.url});if(op==="seek")seeks++;update(result);}});queue=task.catch(fail);return task;
  }
  const media=new ProviderMedia(request);
  const detach=target?attachPartyPlayer(media,target,label,header,out):()=>{};
  function list(el:HTMLSelectElement, rows:Track[], selected:number) {
    const key=JSON.stringify(rows);
    if(el.dataset.rows!==key) {el.replaceChildren(...rows.map(row=>new Option(row.name,String(row.id))));el.dataset.rows=key;}
    el.value=String(selected);el.hidden=!rows.length;
  }
  function update(s:State) {
    if(closed)return;
    if(s.closed || s.error){fatal=true;onFatal(ui("No se pudo reproducir con este motor."));return;}
    state=s;
    if (Array.isArray(s.renderers)) {
      const rows = [{id:0,name:ui("Este dispositivo")}, ...s.renderers];
      const signature = JSON.stringify(rows);
      if (devices.dataset.rows !== signature) {
        devices.replaceChildren(...rows.map(r => new Option(r.name, String(r.id))));
        if (!s.renderers.length) {const empty = new Option(ui("Buscando pantallas…"), ""); empty.disabled = true; devices.add(empty);}
        devices.dataset.rows = signature;
      }
      devices.value = String(s.selectedRenderer ?? 0);
    }
    if(!s.ready)return;
    if(previousPaused!==undefined && previousPaused!==s.paused){
      if(s.paused) pauses++; else plays++;
    }
    previousPaused=s.paused;
    media.update({position:s.position,duration:s.duration||null,paused:s.paused,ended:s.ended,ready:4,seeking:false,seekable:s.duration>0,plays,pauses,seeks});
    toggle.textContent=s.paused?ui("Reproducir"):ui("Pausar");
    back.disabled = forward.disabled = !s.duration;
    seek.disabled=!s.duration;seek.max=String(s.duration);
    if(!scrubbing){
      seek.value=String(s.position);
      timeDisplay.textContent=s.duration ? `${formatMediaTime(s.position)} / ${formatMediaTime(s.duration)}` : formatMediaTime(s.position);
    }
    list(audio,s.audio,s.selectedAudio);
    const captions=engine==="android" ? [...s.subtitle,...sidecars.slice(0,100).map((t,i)=>({id:-2-i,name:t.label || t.language || ui("Subtítulos")}))] : s.subtitle;
    list(subs,engine==="android" && captions.length ? [{id:-1,name:ui("Desactivado")},...captions]:captions,s.selectedSubtitle);
    if(!restored && !restoring){
      restoring=true;
      const preferred=sidecars.slice(0,100).findIndex(t=>t.default);
      if(engine==="android" && preferred>=0)void request("subtitle",-2-preferred).catch(fail);
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
    if(closed || fatal)return;
    try {
      await queue;if(closed || fatal)return;
      const rect=box(),signature=JSON.stringify(rect);
      if(embedded && signature!==previousBox){await call("layout",{box:rect});previousBox=signature;}
      update(await call("read"));
      failures=0;
    }catch(e){failures++;fail(e);}
    finally {
      if(!closed && !fatal){
        const delay=failures ? Math.min(5000,400 * 2 ** Math.min(failures,4)) : 400;
        timer=window.setTimeout(poll,delay);
      }
    }
  }
  toggle.onclick=()=>void request(state?.paused?"play":"pause").catch(fail);
  seek.onchange=()=>{scrubbing=false;void request("seek",Number(seek.value)).then(()=>{if(!closed){restored=true;lastSave=Date.now();}}).catch(fail);};
  void (async()=>{
    try {
      const result=await call("start",{engine,source:nativeSourceUrl(address),hls:!!referer,referer,tracks:sidecars,box:box()});started=true;
      if(closed){await call("close");return;}
      update(result);await poll();
    }catch(e){if(!closed)onFatal(String(e));}
  })();
  return ()=>{closed=true;clearTimeout(hudTimer);events.abort();clearTimeout(timer);detach();controls.remove();if(restored&&state?.ready)save(state.position,state.ended);
    if(started || referer)void call("close").catch(()=>{});};
}
