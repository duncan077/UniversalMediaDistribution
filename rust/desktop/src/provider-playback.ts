import {ui} from "./locale";
import {invoke} from "./bridge";
import {attachPartyPlayer} from "./party";
import {formatTime, toggleFullscreen, type ChapterNav, type Stream, type Target} from "./player";
import {bindDevicePlayback, receivingTitle, type IncomingPlayback, type VideoReference} from "./device-playback";
import {tvExitConfirmation} from "./playback-overlay";

interface Sample {position:number; duration:number|null; paused:boolean; ended:boolean; ready:number; seeking:boolean; seekable:boolean; plays:number; pauses:number; seeks:number}
interface Report {closed:boolean; media:Sample|null}
export interface Handoff {stream:Stream; position:number; paused:boolean}
export interface ProviderSessionOptions {
  chapterNav?: ChapterNav;
  sources?: { label: string }[];
  sourceIndex?: number;
  resolveSource?: (index: number, currentPosition: number) => Promise<() => Promise<void>>;
  live?: boolean;
  reference?: VideoReference;
  incoming?: IncomingPlayback;
}
type Action = "read" | "seek" | "pause" | "play" | "close";

// This adapter exposes the same transport events as the main player to the room.
// Only fixed playback operations and timing data cross the native boundary.
export class ProviderMedia extends EventTarget {
  sample: Sample | null = null;
  constructor(private control:(action:Action, position?:number)=>Promise<void>) { super(); }
  get currentTime() { return this.sample?.position ?? 0; }
  set currentTime(position:number) { if(this.sample?.seekable) void this.control("seek",position).catch(()=>{}); }
  get duration() {return this.sample?.duration ?? Infinity;}
  get paused() {return this.sample?.paused ?? true;}
  get ended() {return this.sample?.ended ?? false;}
  get readyState() {return this.sample?.ready ?? 0;}
  get seeking() {return this.sample?.seeking ?? false;}
  play() {return this.control("play");}
  pause() {void this.control("pause").catch(()=>{});}
  update(next:Sample|null) {
    const old=this.sample;
    this.sample=next;
    if (!next) return;
    const emit=(event:string)=>this.dispatchEvent(new Event(event));
    if (!old) {emit("loadedmetadata"); return;}
    const played=old.plays!==next.plays, paused=old.pauses!==next.pauses;
    if (played && paused) emit(next.paused?"play":"pause");
    if (old.paused!==next.paused || (next.paused?paused:played)) emit(next.paused?"pause":"play");
    if (next.seeks!==old.seeks) emit("seeked");
    if (old.ready>=3 && next.ready<3) emit("waiting");
    if (old.ready<3 && next.ready>=3) emit("canplay");
  }
}

function hostBadgeText(address: string): string {
  try {
    return new URL(address).hostname.replace(/^www\./i, "");
  } catch {
    return "";
  }
}

export async function followProvider(
  address:string,
  target:Target|null,
  label:string,
  resume:number,
  save:(position:number,ended:boolean)=>void,
  openStream:(handoff:Handoff)=>Promise<void>,
  options:ProviderSessionOptions = {},
) {
  const panel=document.createElement("dialog");
  const header=document.createElement("header");
  const headerStart=document.createElement("div");
  const headerEnd=document.createElement("div");
  const view=document.createElement("div");
  const toolbar=document.createElement("footer");

  panel.className="player-dialog media-player-dialog provider-player";
  header.className="dialog-header player-topbar provider-topbar";
  headerStart.className="player-header-start";
  headerEnd.className="player-header-end";
  view.className="player-viewport provider-surface";
  toolbar.className="provider-toolbar";

  const backBtn=document.createElement("button");
  backBtn.type="button";
  backBtn.className="player-back";
  backBtn.textContent="← "+ui("Volver");

  const titleGroup=document.createElement("div");
  titleGroup.className="player-title-group";
  const title=document.createElement("h2");
  title.id="provider-playback-title-"+crypto.randomUUID();
  title.textContent=label;
  title.title=label;
  panel.setAttribute("aria-labelledby",title.id);
  titleGroup.append(title);

  const hostName=hostBadgeText(address);
  if (hostName) {
    const hostBadge=document.createElement("span");
    hostBadge.className="provider-host-badge";
    hostBadge.textContent=hostName;
    titleGroup.append(hostBadge);
  }

  const statePill=document.createElement("span");
  statePill.className="provider-state-pill";
  statePill.dataset.state="waiting";
  statePill.textContent=ui("Esperando video…");
  titleGroup.append(statePill);
  headerStart.append(backBtn,titleGroup);

  if (options.chapterNav) {
    const nav=options.chapterNav;
    const navGroup=document.createElement("div");
    navGroup.className="player-ep-group";
    const prevUnit=document.createElement("button");
    prevUnit.type="button";
    prevUnit.className="player-nav-btn";
    prevUnit.textContent="⏮ "+ui("Anterior");
    prevUnit.disabled=!nav.hasPrev;
    prevUnit.title=nav.prevLabel || ui("Anterior");
    prevUnit.onclick=()=>{transmission.preserve();void closePanel(()=>nav.openPrev());};

    const nextUnit=document.createElement("button");
    nextUnit.type="button";
    nextUnit.className="player-nav-btn";
    nextUnit.textContent=ui("Siguiente")+" ⏭";
    nextUnit.disabled=!nav.hasNext;
    nextUnit.title=nav.nextLabel || ui("Siguiente");
    nextUnit.onclick=()=>{transmission.preserve();void closePanel(()=>nav.openNext());};

    navGroup.append(prevUnit,nextUnit);
    headerEnd.append(navGroup);
  }

  let serverSelect:HTMLSelectElement|undefined;
  const activeSourceIndex=options.sourceIndex ?? 0;
  if (options.sources && options.sources.length > 1 && options.resolveSource) {
    const pill=document.createElement("label");
    pill.className="player-ctrl-pill";
    const span=document.createElement("span");
    span.textContent=ui("Servidor");
    serverSelect=document.createElement("select");
    serverSelect.setAttribute("aria-label",ui("Servidor de reproducción"));
    options.sources.forEach((s,i)=>serverSelect!.add(new Option(s.label,String(i))));
    serverSelect.value=String(activeSourceIndex);
    pill.append(span,serverSelect);
    headerEnd.append(pill);
  }

  const theatreBtn=document.createElement("button");
  theatreBtn.type="button";
  theatreBtn.className="player-icon-btn player-theatre-btn";
  theatreBtn.textContent="⤢";
  theatreBtn.title=ui("Modo cine");
  theatreBtn.setAttribute("aria-label",ui("Modo cine"));
  theatreBtn.setAttribute("aria-pressed","false");

  const fsBtn=document.createElement("button");
  fsBtn.type="button";
  fsBtn.className="player-icon-btn";
  fsBtn.textContent="⛶";
  fsBtn.title=ui("Pantalla completa");
  fsBtn.setAttribute("aria-label",ui("Pantalla completa"));
  fsBtn.setAttribute("aria-pressed","false");
  fsBtn.onclick=()=>void toggleFullscreen(panel,fsBtn);

  const close=document.createElement("button");
  close.type="button";
  close.className="close";
  close.textContent="×";
  close.title=ui("Cerrar reproductor");
  close.setAttribute("aria-label",ui("Cerrar reproductor"));

  headerEnd.append(theatreBtn,fsBtn,close);
  header.append(headerStart,headerEnd);

  // Ultra-slim top bar when Modo cine is enabled while watching inside WebView
  const immersiveBar=document.createElement("div");
  immersiveBar.className="player-immersive-bar";
  immersiveBar.hidden=true;
  const immersiveLead=document.createElement("div");
  immersiveLead.className="player-immersive-lead";
  const immersiveTitle=document.createElement("span");
  immersiveTitle.className="player-immersive-title";
  immersiveTitle.textContent=label;
  const immersiveState=document.createElement("span");
  immersiveState.className="player-immersive-time";
  immersiveState.textContent=ui("Esperando video…");
  immersiveLead.append(immersiveTitle,immersiveState);

  const immersiveActions=document.createElement("div");
  immersiveActions.className="player-immersive-actions";
  const immersiveHandoff=document.createElement("button");
  immersiveHandoff.type="button";
  immersiveHandoff.className="primary provider-handoff-btn";
  immersiveHandoff.textContent="▶ "+ui("Usar el reproductor de UMD");
  immersiveHandoff.disabled=true;
  const exitImmersiveBtn=document.createElement("button");
  exitImmersiveBtn.type="button";
  exitImmersiveBtn.className="quiet-btn";
  exitImmersiveBtn.textContent=ui("Mostrar controles");
  const immersiveClose=document.createElement("button");
  immersiveClose.type="button";
  immersiveClose.className="close";
  immersiveClose.textContent="×";
  immersiveClose.setAttribute("aria-label",ui("Cerrar reproductor"));
  immersiveActions.append(immersiveHandoff,exitImmersiveBtn,immersiveClose);
  immersiveBar.append(immersiveLead,immersiveActions);

  // Stage loading placeholder before the child WebView attaches
  const stagePlaceholder=document.createElement("div");
  stagePlaceholder.className="native-stage-placeholder";
  const stageSpinner=document.createElement("span");
  stageSpinner.className="native-stage-spinner";
  stageSpinner.setAttribute("aria-hidden","true");
  const stageLabel=document.createElement("span");
  stageLabel.textContent=ui("Esperando el video del proveedor…");
  stagePlaceholder.append(stageSpinner,stageLabel);
  view.append(stagePlaceholder);

  // Bottom bar: non-jumping status/guidance + provider focus toggle + primary UMD player handoff CTA
  const statusWrap=document.createElement("div");
  statusWrap.className="provider-status-wrap";
  const out=document.createElement("p");
  out.className="provider-status-text";
  out.setAttribute("role","status");
  out.textContent=ui("Esperando el video del proveedor…");
  statusWrap.append(out);

  const actionGroup=document.createElement("div");
  actionGroup.className="provider-actions-group";

  const enter=document.createElement("button");
  enter.type="button";
  enter.textContent=ui("Interactuar con el proveedor");
  enter.className="quiet-btn provider-focus-btn";
  enter.setAttribute("aria-pressed","false");
  enter.disabled=true;

  const handoff=document.createElement("button");
  handoff.type="button";
  handoff.className="primary provider-handoff-btn";
  handoff.textContent="▶ "+ui("Usar el reproductor de UMD");
  handoff.disabled=true;

  actionGroup.append(enter,handoff);
  toolbar.append(statusWrap,actionGroup);

  const trigger=document.activeElement;
  panel.append(header,immersiveBar,view,toolbar);
  const exit = document.documentElement.classList.contains("television") ? tvExitConfirmation(panel) : undefined;
  document.body.append(panel);
  if (!panel.open) panel.showModal();

  let session:string|undefined;
  let immersive=false;
  let statusNotice="";
  let statusError=false;

  const setNotice=(message:string,isError=false)=>{
    statusNotice=message;
    statusError=isError;
    refreshStatusText();
  };

  const bounds=()=>{
    const r=view.getBoundingClientRect();
    return {x:r.x,y:r.y,width:r.width,height:r.height,pageWidth:innerWidth};
  };
  const events=new AbortController();
  const eventOptions={signal:events.signal};

  document.addEventListener("fullscreenchange",()=>{
    fsBtn.setAttribute("aria-pressed",String(document.fullscreenElement===panel));
  },eventOptions);

  const layout=async(focus?:boolean)=>{
    if (session && panel.open) await invoke("provider_layout",{session,bounds:bounds(),focus});
  };
  const resize=new ResizeObserver(()=>void layout().catch(()=>{}));
  let providerFocused=false, lastFocusReturn=-Infinity;

  const syncFocusUi=()=>{
    panel.classList.toggle("is-provider-focused",providerFocused);
    enter.classList.toggle("is-active",providerFocused);
    enter.setAttribute("aria-pressed",String(providerFocused));
    refreshStatusText();
  };

  const setImmersive=(active:boolean)=>{
    if (immersive===active) return;
    immersive=active;
    panel.classList.toggle("is-immersive",active);
    immersiveBar.hidden=!active;
    theatreBtn.setAttribute("aria-pressed",String(active));
    void layout().catch(()=>{});
    if (active) exitImmersiveBtn.focus({preventScroll:true});
    else enter.focus({preventScroll:true});
  };
  theatreBtn.onclick=()=>setImmersive(!immersive);
  exitImmersiveBtn.onclick=()=>setImmersive(false);

  enter.onclick=()=>{
    statusNotice="";
    statusError=false;
    providerFocused=true;
    syncFocusUi();
    void layout(true).catch(e=>{providerFocused=false;syncFocusUi();setNotice(String(e),true);});
  };
  document.addEventListener("umd-provider-enter",()=>{providerFocused=true;syncFocusUi();},eventOptions);
  document.addEventListener("umd-provider-focus",()=>{
    providerFocused=false;
    lastFocusReturn=performance.now();
    syncFocusUi();
    enter.focus({preventScroll:true});
  },eventOptions);
  if (exit) document.addEventListener("umd-provider-back", () => exit.confirm(), eventOptions);
  panel.addEventListener("focusin",()=>{
    if (providerFocused && !/Android/i.test(navigator.userAgent)) {
      providerFocused=false;
      syncFocusUi();
    }
  },eventOptions);

  panel.addEventListener("cancel",event=>{
    // One TV Back press can reach both the native view and the library WebView.
    if (providerFocused || performance.now()-lastFocusReturn<(exit ? 160 : 300)) {
      event.preventDefault();
      if (providerFocused) {
        providerFocused=false;
        syncFocusUi();
        void layout(false).catch(e=>setNotice(String(e),true));
      }
      return;
    }
    if (exit) {
      if (!exit.confirm()) event.preventDefault();
      return;
    }
    if (immersive) {
      event.preventDefault();
      setImmersive(false);
    }
  });

  let closed=false, restored=false, timer:ReturnType<typeof setTimeout>, queue=Promise.resolve();
  let detach:(()=>void)|undefined, lastSave=0, previous="", readySince=0;
  const media=new ProviderMedia(control);
  const followRoom = !options.incoming && !(target && receivingTitle(target.itemId));
  let applyIncoming = !!options.incoming;
  const nav = options.chapterNav;
  const transmission = bindDevicePlayback(label,options.reference,options.incoming,{
    read:() => restored && !media.ended && media.readyState >= 2 && !media.seeking ? {
      positionSeconds:media.currentTime,
      durationSeconds:Number.isFinite(media.duration) ? media.duration : null,
      paused:media.paused,
    } : null,
    play:()=>control("play"),pause:()=>control("pause"),seek:position=>control("seek",Math.min(position,media.duration)),
    previous:nav?.hasPrev ? ()=>{transmission.preserve();void closePanel(nav.openPrev);} : undefined,
    next:nav?.hasNext ? ()=>{transmission.preserve();void closePanel(nav.openNext);} : undefined,
  },() => panel.close());
  if (transmission.cast) {
    const cast = document.createElement("details");
    cast.className = "native-cast";
    const summary = document.createElement("summary");
    summary.textContent = ui("Transmitir");
    const tools = document.createElement("div");
    tools.className = "native-cast-tools";
    tools.append(transmission.cast);
    cast.append(summary,tools);
    headerEnd.prepend(cast);
  }

  function refreshStatusText() {
    out.classList.toggle("is-error",statusError && !providerFocused);
    out.classList.toggle("is-focused-hint",providerFocused);
    if (providerFocused) {
      out.textContent=ui("Controlando el reproductor web · Pulsá Esc o Atrás para volver a la barra.");
      return;
    }
    if (statusNotice) {
      out.textContent=statusNotice;
      return;
    }
    if (media.sample) {
      out.textContent=ui("Video detectado. Elegí «Usar el reproductor de UMD» para pasarlo al reproductor nativo.");
    } else {
      out.textContent=ui("Completá la validación e iniciá el video. Después elegí «Usar el reproductor de UMD».");
    }
  }

  function persist(force:boolean) {
    const s=media.sample;
    if (!target || !restored || !s || s.duration===null || !Number.isFinite(s.position)) return;
    const key=s.position+":"+s.ended;
    if (previous!==key && (force || Date.now()-lastSave>5000)) {
      save(s.position,s.ended);previous=key;lastSave=Date.now();
    }
  }
  async function request(action:Action,position?:number) {
    return invoke<Report>("provider_playback",{session,action,position});
  }
  function control(action:Action,position?:number) {
    const run=queue.then(async()=>{
      if (closed) return;
      const result=await request(action,position);
      if(result.closed){panel.close();return;}
      if(action==="play" && result.media?.paused) throw Error(ui("Pulsá reproducir en el video del proveedor."));
    });
    queue=run.catch(e=>setNotice(String(e),true));
    return run;
  }

  const runHandoff = async () => {
    handoff.disabled = immersiveHandoff.disabled = true;
    setNotice(ui("Buscando un video para el reproductor nativo…"), false);
    try {
      const captured = await invoke<Handoff>("provider_public_stream", {session});
      if (closed) return;
      persist(true);
      closed = true;
      await request("close");
      session = undefined;
      transmission.preserve();
      panel.close();
      await openStream(captured);
    } catch (e) {
      if (panel.open) {closed = false; setNotice(String(e), true);}
    } finally {
      if (panel.open) handoff.disabled = immersiveHandoff.disabled = false;
    }
  };
  handoff.onclick = () => void runHandoff();
  immersiveHandoff.onclick = () => void runHandoff();

  if (serverSelect && options.resolveSource) {
    const resolveSource = options.resolveSource;
    serverSelect.onchange = async () => {
      const nextIndex = Number(serverSelect!.value);
      if (nextIndex === activeSourceIndex || closed) return;
      serverSelect!.disabled = true;
      setNotice(ui("Cambiando de servidor…"), false);
      try {
        const openNext = await resolveSource(nextIndex, media.sample?.position ?? resume);
        if (closed || !panel.open) return;
        persist(true);
        closed = true;
        if (session) {
          await request("close").catch(() => {});
          session = undefined;
        }
        transmission.preserve();
        panel.close();
        await openNext();
      } catch (e) {
        if (panel.open) {
          serverSelect!.value = String(activeSourceIndex);
          serverSelect!.disabled = false;
          setNotice(String(e), true);
        }
      }
    };
  }

  async function poll() {
    try {
      await queue;
      if (closed) return;
      const result=await request("read");
      if (closed) return;
      if (result.closed) {panel.close();return;}
      let sample=result.media;
      if(!sample){
        if (media.sample) {persist(true);detach?.();detach=undefined;}
        media.update(null);
        statePill.dataset.state="waiting";
        statePill.textContent=ui("Esperando video…");
        immersiveState.textContent=ui("Esperando video…");
        handoff.classList.remove("is-ready");
        immersiveHandoff.classList.remove("is-ready");
        refreshStatusText();
      } else {
        readySince ||= Date.now();
        if(!restored && Date.now()-readySince < 8000 && resume>0 && sample.duration!==null && resume<sample.duration) {
          if(!sample.seekable)return;
          const positioned=await request("seek",resume);
          if(closed)return;
          if(positioned.closed){panel.close();return;}
          if(positioned.media) sample=positioned.media;
        }
        if(applyIncoming && sample.ready>=2) {
          applyIncoming=false;
          const paused=options.incoming!.offer.paused;
          if (sample.paused !== paused) {
            const applied=await request(paused?"pause":"play");
            if(closed)return;
            if(applied.closed){panel.close();return;}
            if(applied.media) sample=applied.media;
          }
        }
        restored=true;
        const changed=media.sample?.paused!==sample.paused || sample.ended;
        media.update(sample);
        if (statusError) { statusNotice = ""; statusError = false; }
        const timePart = sample.duration && Number.isFinite(sample.duration)
          ? `${formatTime(sample.position)} / ${formatTime(sample.duration)}`
          : options.live ? ui("En vivo") : formatTime(sample.position);
        const stateText = `● ${ui("Conectado")} · ${timePart}`;
        statePill.dataset.state = "connected";
        if (statePill.textContent !== stateText) statePill.textContent = stateText;
        if (immersiveState.textContent !== timePart) immersiveState.textContent = timePart;
        handoff.classList.add("is-ready");
        immersiveHandoff.classList.add("is-ready");
        if(!detach && target && followRoom){
          detach=attachPartyPlayer(media,target,label,headerEnd,out);
        }
        refreshStatusText();
        persist(changed);
      }
    } catch(e){if(!closed)setNotice(String(e),true);}
    finally {if(!closed)timer=setTimeout(()=>void poll(),500);}
  }

  let closingPanel=false;
  async function closePanel(afterClose?:()=>void) {
    if (closingPanel) return;
    closingPanel=true;
    close.disabled=backBtn.disabled=immersiveClose.disabled=true;
    if (afterClose) panel.addEventListener("close",afterClose,{once:true});
    let timeout:ReturnType<typeof setTimeout>|undefined;
    try{
      await Promise.race([
        queue.then(async()=>{
          if(closed || !session)return;
          const r=await request("read");
          if(!closed && r.media)media.update(r.media);
        }),
        new Promise<void>(resolve=>{timeout=setTimeout(resolve,500);}),
      ]);
    }catch{/* Close dialog even if the provider WebView fails to respond. */}
    finally{clearTimeout(timeout);if(panel.open)panel.close();}
  }

  close.onclick=()=>void closePanel();
  backBtn.onclick=()=>void closePanel();
  immersiveClose.onclick=()=>void closePanel();

  panel.addEventListener("close",()=>{
    transmission.detach();
    closed=true;clearTimeout(timer);detach?.();persist(true);
    if (document.fullscreenElement===panel) void document.exitFullscreen().catch(()=>{});
    exit?.close();
    events.abort();resize.disconnect();
    if(session) void request("close").catch(()=>{});
    panel.remove();
    if(trigger instanceof HTMLElement && !document.querySelector("dialog[open]")) trigger.focus({preventScroll:true});
  },{once:true});

  try {
    session=await invoke<string>("open_filtered_player",{address,language:document.documentElement.lang,bounds:bounds()});
    if(!panel.open){await request("close");return;}
    stagePlaceholder.hidden=true;
    // Observe both the surface and the panel, since status/toolbars may move the surface.
    resize.observe(panel);resize.observe(view);
    window.addEventListener("resize",()=>void layout().catch(()=>{}),eventOptions);
    enter.disabled=handoff.disabled=immersiveHandoff.disabled=false;
    refreshStatusText();
    enter.focus({preventScroll:true});
    await layout();
    void poll();
  } catch(e) {
    if(panel.open)panel.close();
    throw e;
  }
}
