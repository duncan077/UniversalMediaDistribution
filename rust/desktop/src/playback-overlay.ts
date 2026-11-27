import {getCurrentWindow} from "@tauri-apps/api/window";
import {ui} from "./locale";

const idleDelay = 4000;
const tvActions = "button:enabled, select:enabled, input[type=range]:enabled, summary, a[href]";

export function tvExitConfirmation(dialog: HTMLDialogElement) {
  const hint = document.createElement("div");
  hint.className = "playback-exit-hint";
  hint.setAttribute("role", "status");
  hint.hidden = true;
  dialog.append(hint);
  let firstPress = -Infinity;
  let timer = 0;
  const reset = () => {
    clearTimeout(timer);
    firstPress = -Infinity;
    hint.hidden = true;
    hint.textContent = "";
  };
  return {
    confirm() {
      const now = performance.now();
      if (now - firstPress < 160) return false;
      if (now - firstPress < 2500) { reset(); return true; }
      firstPress = now;
      hint.hidden = false;
      hint.textContent = ui("Volvé a presionar Atrás para salir del video.");
      timer = window.setTimeout(reset, 2500);
      return false;
    },
    reset,
    close() { reset(); hint.remove(); },
  };
}

export function attachPlaybackOverlay(
  dialog: HTMLDialogElement, viewport: HTMLElement, controls: HTMLElement,
  fullscreenButton: HTMLButtonElement, play: HTMLButtonElement, toggle: () => void,
) {
  const header = dialog.querySelector<HTMLElement>(".player-topbar")!;
  const events = new AbortController();
  const options = {signal: events.signal};
  const tv = document.documentElement.classList.contains("television");
  const exit = tv ? tvExitConfirmation(dialog) : undefined;
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
  let lastFocus: HTMLElement | null = null;
  let selectMenu: {select: HTMLSelectElement; panel: HTMLElement} | undefined;

  function visible(element: HTMLElement) {
    return !element.matches(":disabled") && !element.closest("[hidden], [inert]") &&
      element.getClientRects().length > 0;
  }

  function focusPlay(restore = false) {
    const action = fullscreen && !tv ? minimal : play;
    const target = restore && lastFocus && visible(lastFocus) ? lastFocus : action;
    (visible(target) ? target : viewport).focus({preventScroll:true});
  }

  function closeSelection(restoreFocus = true) {
    if (!selectMenu) return;
    const {select, panel} = selectMenu;
    selectMenu = undefined;
    panel.remove();
    lastFocus = select;
    if (restoreFocus) focusPlay(true);
  }

  function openSelection(select: HTMLSelectElement) {
    const panel = document.createElement("div");
    panel.className = "playback-tv-options";
    panel.setAttribute("role", "group");
    const label = select.getAttribute("aria-label") ?? "";
    panel.setAttribute("aria-label", label);
    const heading = document.createElement("strong");
    heading.textContent = label;
    const list = document.createElement("div");
    list.className = "playback-tv-option-list";
    panel.append(heading, list);
    for (const option of select.options) {
      if (option.disabled) continue;
      const button = document.createElement("button");
      button.type = "button";
      button.className = "quiet-btn";
      button.textContent = option.label;
      button.setAttribute("aria-pressed", String(option.selected));
      button.onclick = () => {
        closeSelection();
        if (select.disabled || select.value === option.value ||
            !Array.from(select.options).some(current => current.value === option.value && !current.disabled)) return;
        select.value = option.value;
        select.dispatchEvent(new Event("change", {bubbles:true}));
      };
      list.append(button);
    }
    if (!list.childElementCount) return;
    controls.append(panel);
    selectMenu = {select, panel};
    (list.querySelector<HTMLElement>("[aria-pressed=true]") ?? list.firstElementChild as HTMLElement).focus();
  }

  function navigateTv(key: string, current: HTMLElement) {
    const cast = Array.from(dialog.querySelectorAll<HTMLElement>(".native-cast-tools"))
      .filter(group => group.closest("details[open]"));
    const groups = selectMenu ? [selectMenu.panel] : [
      header, controls.querySelector(".native-timeline"), controls.querySelector(".native-transport"),
      controls.querySelector(".native-settings"), ...cast,
    ];
    const rows = groups.map(group => Array.from(group?.querySelectorAll<HTMLElement>(tvActions) ?? [])
      .filter(element => visible(element) && (cast.includes(group as HTMLElement) || !element.closest(".native-cast-tools"))))
      .filter(row => row.length > 0);
    const index = rows.findIndex(row => row.includes(current));
    if (index < 0) { focusPlay(); return; }
    const row = rows[index];
    const forward = key === "ArrowRight" || key === "ArrowDown";
    let next: HTMLElement | undefined;
    if (selectMenu || key === "ArrowLeft" || key === "ArrowRight") {
      next = row[row.indexOf(current) + (forward ? 1 : -1)];
    } else {
      const adjacent = rows[index + (forward ? 1 : -1)];
      next = adjacent?.includes(play) ? play : (adjacent?.find(el => el.hasAttribute("data-tv-primary")) ?? adjacent?.[0]);
    }
    if (next) {
      next.focus({preventScroll:true});
      next.scrollIntoView({block:"nearest", inline:"nearest"});
    }
  }

  function updateVisibility() {
    dialog.classList.toggle("is-playback-idle", idle);
    header.inert = controls.inert = (fullscreen && !tv) || idle;
    minimal.inert = tv || !fullscreen || idle;
  }

  function wake() {
    idle = false;
    updateVisibility();
    clearTimeout(timer);
    timer = window.setTimeout(() => {
      idle = true;
      const active = document.activeElement;
      if (tv && active instanceof HTMLElement && visible(active)) lastFocus = active;
      closeSelection(false);
      if (tv || header.contains(active) || controls.contains(active) || active === minimal) {
        viewport.focus({preventScroll:true});
      }
      updateVisibility();
    }, idleDelay);
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
    if (selectMenu) {
      event.preventDefault();
      exit?.reset();
      closeSelection();
      wake();
      return;
    }
    if (exit) {
      if (!exit.confirm()) event.preventDefault();
      return;
    }
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
    const target = event.target instanceof HTMLElement ? event.target : null;
    if ((target?.closest("dialog[open]") ?? Array.from(document.querySelectorAll("dialog[open]")).at(-1)) !== dialog) return;
    const wasIdle = idle;
    wake();
    if (tv && target) {
      if (["Escape", "Backspace", "BrowserBack"].includes(event.key)) {
        event.preventDefault();
        if (event.repeat) return;
        if (selectMenu) { exit?.reset(); closeSelection(); }
        else if (exit?.confirm()) dialog.close();
        return;
      }
      const direction = ["ArrowLeft", "ArrowRight", "ArrowUp", "ArrowDown"].includes(event.key);
      if (direction) {
        if (!wasIdle && !selectMenu && target.matches("input[type=range]") &&
            (event.key === "ArrowLeft" || event.key === "ArrowRight")) return;
        event.preventDefault();
        if (wasIdle || target === viewport) focusPlay(wasIdle);
        else navigateTv(event.key, target);
        return;
      }
      if (["Enter", " ", "Accept"].includes(event.key) &&
          (wasIdle || target === viewport || target instanceof HTMLSelectElement)) {
        event.preventDefault();
        if (event.repeat) return;
        if (target instanceof HTMLSelectElement && !wasIdle) openSelection(target);
        else focusPlay(wasIdle);
        return;
      }
    }
    if (target?.matches("input, select, textarea")) return;
    if (event.key === "Escape" && fullscreen) {
      event.preventDefault();
      toggleFullscreen();
    } else if (["f", "t"].includes(event.key.toLowerCase()) && !event.repeat) {
      event.preventDefault();
      toggleFullscreen();
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
      if (tv && ready && !idle && document.activeElement === viewport) focusPlay();
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
      exit?.close();
      closeSelection(false);
      minimal.remove();
      header.inert = controls.inert = false;
      dialog.classList.remove("has-playback-overlay", "is-playback-idle");
      if (!preserveFullscreen && fullscreen && desktop) {
        void getCurrentWindow().setFullscreen(false).catch(() => {});
      }
    },
  };
}
