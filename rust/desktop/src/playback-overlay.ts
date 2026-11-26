import {getCurrentWindow} from "@tauri-apps/api/window";
import {ui} from "./locale";

export function attachPlaybackOverlay(
  dialog: HTMLDialogElement, viewport: HTMLElement, controls: HTMLElement,
  fullscreenButton: HTMLButtonElement, play: HTMLButtonElement, toggle: () => void,
) {
  const header = dialog.querySelector<HTMLElement>(".player-topbar")!;
  const events = new AbortController();
  const options = {signal: events.signal};
  const tv = document.documentElement.classList.contains("television");
  const desktop = !/Android/i.test(navigator.userAgent);
  const minimal = document.createElement("button");
  minimal.type = "button";
  minimal.className = "playback-center";
  minimal.dataset.paused = "true";
  minimal.setAttribute("aria-label", ui("Reproducir"));
  minimal.disabled = true;
  minimal.onclick = toggle;
  viewport.after(minimal);
  dialog.classList.add("has-playback-overlay");
  let fullscreen = dialog.classList.contains("is-playback-fullscreen");
  let idle = false;
  let timer = 0;

  function focusPlay() {
    const action = fullscreen ? minimal : play;
    (action.disabled ? viewport : action).focus({preventScroll:true});
  }

  function updateVisibility() {
    dialog.classList.toggle("is-playback-idle", idle);
    header.inert = controls.inert = fullscreen || idle;
    minimal.inert = !fullscreen || idle;
  }

  function wake() {
    idle = false;
    updateVisibility();
    clearTimeout(timer);
    timer = window.setTimeout(() => {
      idle = true;
      const active = document.activeElement;
      if (tv || header.contains(active) || controls.contains(active) || active === minimal) {
        viewport.focus({preventScroll:true});
      }
      updateVisibility();
    }, 3000);
  }

  function toggleFullscreen() {
    fullscreen = !fullscreen;
    dialog.classList.toggle("is-playback-fullscreen", fullscreen);
    fullscreenButton.setAttribute("aria-pressed", String(fullscreen));
    wake();
    focusPlay();
    if (desktop) void getCurrentWindow().setFullscreen(fullscreen).catch(() => {});
  }
  fullscreenButton.onclick = toggleFullscreen;
  dialog.addEventListener("cancel", event => {
    if (!fullscreen) return;
    event.preventDefault();
    toggleFullscreen();
  }, options);
  viewport.addEventListener("dblclick", toggleFullscreen, options);
  for (const event of ["pointermove", "pointerdown", "input", "change", "click"]) {
    dialog.addEventListener(event, wake, options);
  }
  document.addEventListener("keydown", event => {
    if (!dialog.open || event.altKey || event.ctrlKey || event.metaKey) return;
    const target = event.target instanceof Element ? event.target : null;
    if ((target?.closest("dialog[open]") ?? Array.from(document.querySelectorAll("dialog[open]")).at(-1)) !== dialog) return;
    const wasIdle = idle;
    wake();
    if (target?.matches("input, select, textarea")) return;
    if (event.key === "Escape" && fullscreen) {
      event.preventDefault();
      toggleFullscreen();
    } else if (["f", "t"].includes(event.key.toLowerCase()) && !event.repeat) {
      event.preventDefault();
      toggleFullscreen();
    } else if (tv && (wasIdle || target === viewport || !dialog.contains(target)) && event.key.startsWith("Arrow")) {
      event.preventDefault();
      focusPlay();
    } else if ((wasIdle || !dialog.contains(target) || target === dialog || target === viewport) &&
               (event.key === "Enter" || event.key === " ")) {
      event.preventDefault();
      if (!event.repeat) toggle();
      focusPlay();
    }
  }, {...options, capture:true});

  // Keep remote focus on the video surface while the controls are hidden.
  viewport.tabIndex = -1;
  fullscreenButton.setAttribute("aria-pressed", String(fullscreen));
  dialog.classList.toggle("is-playback-fullscreen", fullscreen);
  wake();
  if (tv || fullscreen) focusPlay();
  return {
    wake,
    state(paused: boolean, ready: boolean) {
      minimal.disabled = !ready;
      if (minimal.dataset.paused !== String(paused)) {
        minimal.dataset.paused = String(paused);
        const label = ui(paused ? "Reproducir" : "Pausar");
        minimal.title = label;
        minimal.setAttribute("aria-label", label);
      }
    },
    close(preserveFullscreen = false) {
      events.abort();
      clearTimeout(timer);
      minimal.remove();
      header.inert = controls.inert = false;
      dialog.classList.remove("has-playback-overlay", "is-playback-idle");
      if (!preserveFullscreen && fullscreen && desktop) {
        void getCurrentWindow().setFullscreen(false).catch(() => {});
      }
    },
  };
}
