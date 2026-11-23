import {ui} from "./locale";
import {invoke} from "./bridge";
import {attachPartyPlayer} from "./party";
import type {Stream, Target} from "./player";

interface Sample {position:number; duration:number|null; paused:boolean; ended:boolean; ready:number; seeking:boolean; seekable:boolean; plays:number; pauses:number; seeks:number}
interface Report {closed:boolean; media:Sample|null}
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

export function followProvider(session:string,target:Target,label:string,resume:number,save:(position:number,ended:boolean)=>void, openStream:(stream:Stream)=>Promise<void>) {
  const panel=document.createElement("dialog"),header=document.createElement("header"),out=document.createElement("p");
  const title=document.createElement("h2"),close=document.createElement("button");
  title.id="provider-playback-title-"+crypto.randomUUID();
  panel.setAttribute("aria-labelledby",title.id);
  title.textContent=label; close.type="button"; close.className="quiet-btn"; close.textContent=ui("Cerrar reproductor");header.className="dialog-header";
  out.setAttribute("role","status");out.textContent=ui("Esperando el video del proveedor…");
  header.append(title,close); panel.append(header,out);document.body.append(panel);if(!panel.open)panel.showModal();
  let closed=false, restored=false, timer:ReturnType<typeof setTimeout>, queue=Promise.resolve();
  let detach:(()=>void)|undefined, lastSave=0, previous="", readySince=0;
  function persist(force:boolean) {
    const s=media.sample;
    if (!restored || !s || s.duration===null || !Number.isFinite(s.position)) return;
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
      if(action==="play" && result.media?.paused) throw Error(ui("Pulsá reproducir en la ventana del proveedor."));
    });
    queue=run.catch(e=>{out.textContent=String(e);});
    return run;
  }
  const handoff = document.createElement("button");
  handoff.type = "button";
  handoff.className = "primary";
  handoff.textContent = ui("Usar el reproductor de UMD");
  handoff.hidden = true;
  header.insertBefore(handoff, close);
  handoff.onclick = async () => {
    handoff.disabled = true;
    try {
      const stream = await invoke<Stream>("provider_public_stream", {session});
      if (closed) return;
      persist(true);
      panel.close();
      await openStream(stream);
    } catch (e) {out.textContent = String(e);} finally {handoff.disabled = false;}
  };
  const media=new ProviderMedia(control);
  async function poll() {
    try {
      await queue;
      if (closed) return;
      const result=await request("read");
      if (closed) return;
      if (result.closed) {panel.close();return;}
      const sample=result.media;
      handoff.hidden = !sample;
      if(!sample){
        if (media.sample) {persist(true);detach?.();detach=undefined;}
        media.update(null);
        out.textContent=ui("Esperando un video accesible. Si está en un marco de otro sitio, el progreso y la sala no están disponibles.");
      } else {
        readySince ||= Date.now();
        if(!restored && Date.now()-readySince < 8000 && resume>0 && sample.duration!==null && resume<sample.duration) {
          if(!sample.seekable)return;
          const positioned=await request("seek",resume);
          if(closed)return;
          if(positioned.closed){panel.close();return;}
          if(positioned.media) sample.position=positioned.media.position;
        }
        restored=true;
        const changed=media.sample?.paused!==sample.paused || sample.ended;
        media.update(sample);
        if(!detach){
          out.textContent=ui("Video conectado.");
          detach=attachPartyPlayer(media,target,label,header,out);
        }
        persist(changed);
      }
    } catch(e){if(!closed)out.textContent=String(e);}
    finally {if(!closed)timer=setTimeout(()=>void poll(),500);}
  }
  close.onclick=async()=>{
    close.disabled=true;
    let timeout:ReturnType<typeof setTimeout>|undefined;
    try{
      await Promise.race([
        queue.then(async()=>{
          if(closed)return;
          const r=await request("read");
          if(!closed && r.media)media.update(r.media);
        }),
        new Promise<void>(resolve=>{timeout=setTimeout(resolve,500);}),
      ]);
    }catch{/* Close dialog even if the provider WebView fails to respond. */}
    finally{clearTimeout(timeout);if(panel.open)panel.close();}
  };
  panel.addEventListener("close",()=>{
    closed=true;clearTimeout(timer);detach?.();persist(true);
    void request("close").catch(()=>{});panel.remove();
  },{once:true});
  void poll();
}
