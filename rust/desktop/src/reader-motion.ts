import {ui} from "./locale";
import {activeProfileId, getProfilePref, windowProfile, writeProfilePref} from "./profile-context";

export function readerMotion(view: HTMLElement, toolbar: HTMLElement) {
  const controls = document.createElement("span");
  controls.className = "reader-motion";
  const toggle = document.createElement("button");
  toggle.type = "button";
  toggle.className = "quiet-btn";
  const speed = document.createElement("input"); speed.type="range"; speed.min=".25";speed.max="10";speed.step=".25";
  const rate=document.createElement("output");
  speed.setAttribute("aria-label", ui("Velocidad de desplazamiento"));

  const applySpeed = (profileId: string | null) => {
    speed.value = getProfilePref(profileId, "reader-speed") || "1";
    rate.textContent=speed.value+"×";
  };
  applySpeed(activeProfileId());
  if (!activeProfileId()) {
    void windowProfile().then(({activeId}) => applySpeed(activeId)).catch(() => {});
  }
  speed.onchange = () => { void writeProfilePref("reader-speed", speed.value); };
  speed.oninput=()=>{rate.textContent=speed.value+"×";};
  controls.append(toggle,speed,rate);
  for(const n of [1,3,5,10]){
    const preset=document.createElement("button");preset.type="button";preset.className="seg-btn";preset.textContent=n+"×";
    preset.onclick=()=>{speed.value=String(n);rate.textContent=n+"×";void writeProfilePref("reader-speed",speed.value);};
    controls.append(preset);
  }
  const anchor=document.createElement("span");anchor.className="reader-anchor";anchor.textContent="↕";anchor.hidden=true;view.after(anchor);
  controls.hidden = true;
  toolbar.append(controls);
  const events = new AbortController();
  const options = {signal: events.signal};
  let frame = 0, last = 0, remainder = 0, origin = 0, velocity = 0;
  let motion: "auto" | "mouse" | null = null;
  let pointer: number | null = null;
  function stop() {
    motion = null; anchor.hidden=true;
    if (pointer !== null && view.hasPointerCapture(pointer)) view.releasePointerCapture(pointer);
    pointer = null;
    cancelAnimationFrame(frame);
    view.classList.remove("middle-scroll");
    toggle.textContent = ui("Desplazamiento automático");
    toggle.setAttribute("aria-pressed", "false");
  }
  function tick(time: number) {
    if (!motion || !view.closest("dialog[open]") || document.hidden) { stop(); return; }
    const rate = motion === "mouse" ? velocity : Number(speed.value) * 60;
    const distance = remainder + rate * Math.min(0.05, (time - last) / 1000);
    const whole = Math.trunc(distance);
    remainder = distance - whole;
    last = time;
    view.scrollTop += whole;
    if (motion === "auto" && view.scrollTop + view.clientHeight >= view.scrollHeight - 1) { stop(); return; }
    frame = requestAnimationFrame(tick);
  }
  function start(kind: "mouse" | "auto") {
    stop();
    motion = kind; anchor.hidden=kind!=="mouse";
    remainder = 0; last = performance.now(); velocity = 0;
    view.classList.toggle("middle-scroll", kind === "mouse");
    toggle.textContent = ui("Pausar desplazamiento");
    toggle.setAttribute("aria-pressed", "true");
    frame = requestAnimationFrame(tick);
  }
  toggle.onclick = () => motion ? stop() : start("auto");
  const dialog = view.closest("dialog")!;
  dialog.addEventListener("pointerdown", e => {
    if (!motion) return;
    if (motion === "auto" && controls.contains(e.target as Node)) return;
    if (toggle.contains(e.target as Node)) return;
    stop();
  }, options);
  view.addEventListener("pointerdown", e => {
    if (motion) return;
    if (!controls.hidden && e.pointerType === "mouse" && e.button === 1) {
      e.preventDefault(); origin = e.clientY;const bounds=dialog.getBoundingClientRect();anchor.style.left=(e.clientX-bounds.left)+"px";anchor.style.top=(origin-bounds.top)+"px"; pointer = e.pointerId; view.setPointerCapture(pointer);
    }
  }, options);
  view.addEventListener("auxclick", e => {if (!controls.hidden && e.button === 1) e.preventDefault();}, options);
  view.addEventListener("pointerup", e => {if (pointer === e.pointerId) { e.preventDefault(); start("mouse"); }}, options);
  view.addEventListener("pointercancel", stop, options);
  window.addEventListener("pointermove", e => {
    if (motion !== "mouse") return;
    const delta = e.clientY - origin;
    velocity = Math.sign(delta) * Math.min(1200, Math.max(0, Math.abs(delta) - 12) * 4);
  }, options);
  view.addEventListener("wheel", stop, {...options, passive:true});
  window.addEventListener("blur", stop, options);
  document.addEventListener("visibilitychange", () => {if (document.hidden) stop();}, options);
  dialog.addEventListener("cancel", e => {if (motion) {e.preventDefault();stop();}}, options);
  dialog.addEventListener("keydown", e => {
    if (motion && !controls.contains(e.target as Node) && /^(Arrow|Page|Home|End)/.test(e.key)) stop();
    if (!controls.hidden && e.target === view && (e.key === " " || e.key === "Enter")) {
      e.preventDefault(); toggle.click();
    }
  }, options);
  const header = dialog.querySelector("header");
  let idle=0;
  const wake=()=>{dialog.classList.remove("reader-idle");clearTimeout(idle);idle=window.setTimeout(()=>{
    if(!toolbar.matches(":hover") && !header?.matches(":hover") && !dialog.querySelector("button:focus-visible,select:focus-visible,input:focus-visible")) dialog.classList.add("reader-idle");
  },2400);};
  for(const event of ["pointermove","pointerdown","keydown","focusin"])dialog.addEventListener(event,wake,options);
  wake();stop();
  return {
    enable(active:boolean) {stop(); controls.hidden = !active;},
    close() {stop();clearTimeout(idle);events.abort();controls.remove();anchor.remove();}
  };
}
