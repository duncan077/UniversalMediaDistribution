import { exit, onBackButtonPress } from "@tauri-apps/api/app";
import { flushProgress } from "./player";
import { canGoBack, goBack } from "./router";

function uniformNeighbor(current: HTMLElement, key: string, interactive: string) {
  const card = current.closest<HTMLElement>(".entry-card, .shelf-row > .card, [data-virtual-index]");
  if (!card?.parentElement) return;
  if (key === "ArrowRight" && current.matches(".entry-select")) {
    return card.querySelector<HTMLButtonElement>(".entry-alternatives");
  }
  if (key === "ArrowLeft" && current.matches(".entry-alternatives")) {
    return card.querySelector<HTMLButtonElement>(".entry-select");
  }
  let next: Element | null = null;
  if (card.dataset.virtualIndex !== undefined && (key === "ArrowUp" || key === "ArrowDown")) {
    const columns = Number(card.parentElement.dataset.virtualColumns);
    const index = Number(card.dataset.virtualIndex) + (key === "ArrowUp" ? -columns : columns);
    next = card.parentElement.querySelector(`[data-virtual-index="${index}"]`);
  } else if (card.matches(".entry-card") && (key === "ArrowUp" || key === "ArrowDown")) {
    if (key === "ArrowUp" && current.closest(".entry-actions, .entry-servers")) {
      return card.querySelector<HTMLButtonElement>(".entry-select");
    }
    next = key === "ArrowUp" ? card.previousElementSibling : card.nextElementSibling;
    while (next && !next.matches(".entry-card")) {
      next = key === "ArrowUp" ? next.previousElementSibling : next.nextElementSibling;
    }
    return next?.querySelector<HTMLButtonElement>(".entry-select");
  } else if (card.matches(".shelf-row > .card") && (key === "ArrowLeft" || key === "ArrowRight")) {
    next = key === "ArrowLeft" ? card.previousElementSibling : card.nextElementSibling;
  }
  if (next) {
    const action = Array.from(card.querySelectorAll(interactive)).indexOf(current);
    const controls = next.querySelectorAll<HTMLElement>(interactive);
    return controls[action] ?? controls[0];
  }
}

function topDialog() {
  return document.activeElement?.closest<HTMLDialogElement>("dialog[open]") ??
    Array.from(document.querySelectorAll<HTMLDialogElement>("dialog[open]")).at(-1);
}

export function setupNavigation() {
  if (/Android/i.test(navigator.userAgent)) {
    void onBackButtonPress(async ({ canGoBack: webCanGoBack }) => {
      const dialog = topDialog();
      if (dialog) {
        if (dialog.dispatchEvent(new Event("cancel", { cancelable: true }))) dialog.close();
      } else if (canGoBack()) {
        goBack();
      } else if (webCanGoBack) {
        history.back();
      } else {
        await flushProgress();
        await exit(0);
      }
    });
  }

  // Escape navigates back from the Detail page when no modal is open
  document.addEventListener("keydown", event => {
    if (event.defaultPrevented || event.key !== "Escape") return;
    if (topDialog()) return;
    const target = event.target as HTMLElement | null;
    if (target?.matches("input, textarea, select")) return;
    if (canGoBack()) {
      event.preventDefault();
      goBack();
    }
  });

  if (!navigator.userAgent.includes("UMD-TV")) return;
  document.documentElement.classList.add("television");
  document.getElementById("all")?.focus();
  const searchFields = "input[type=search]";
  document.querySelectorAll<HTMLInputElement>(searchFields).forEach(input => { input.readOnly = true; });
  function enableSearch(target: EventTarget | null) {
    if (!(target instanceof HTMLInputElement) || !target.matches(searchFields) || !target.readOnly) return false;
    target.blur();
    target.readOnly = false;
    target.focus({preventScroll: true});
    return true;
  }
  document.addEventListener("focusout", event => {
    const input = event.target;
    if (input instanceof HTMLInputElement && input.matches(searchFields)) input.readOnly = true;
  });
  document.addEventListener("click", event => { enableSearch(event.target); });
  document.addEventListener("keydown", event => {
    if (event.key === "Enter" && enableSearch(event.target)) event.preventDefault();
  });
  const interactive = "button:enabled, input:enabled, select:enabled, summary, a[href], video[controls], audio[controls], [tabindex='0']";
  document.addEventListener("keydown", event => {
    if (event.defaultPrevented || !["ArrowUp", "ArrowDown", "ArrowLeft", "ArrowRight"].includes(event.key)) return;
    const dialog = topDialog();
    const page = document.querySelector<HTMLElement>("[data-page]:not([hidden])");
    const roots = dialog ? [dialog] : [page, document.querySelector("main > .toolbar"), document.getElementById("rail"), document.getElementById("app-notice-stack")];
    const candidates = () => roots.flatMap(root => root ? Array.from(root.querySelectorAll<HTMLElement>(interactive)) : []);
    const current = document.activeElement;
    const insideRoot = current instanceof HTMLElement && current !== document.body && current !== document.documentElement && current.isConnected
      && (dialog ? dialog.contains(current) : !current.closest("[data-page][hidden]"));
    if (!insideRoot) {
      const first = candidates().find(el => el.getClientRects().length > 0 && !el.closest("[inert]"));
      if (first) {
        event.preventDefault();
        first.focus();
        first.scrollIntoView({ block: "nearest", inline: "nearest" });
      }
      return;
    }
    if (!dialog && event.key === "ArrowRight" && page?.id === "page-catalog" &&
        current.matches("#rail .nav-item[aria-current='page']")) {
      const title = Array.from(page.querySelectorAll<HTMLButtonElement>(".open-title:enabled"))
        .find(button => button.getClientRects().length > 0 && !button.closest("[inert]"));
      if (title) {
        event.preventDefault();
        title.focus({preventScroll: true});
        title.scrollIntoView({block: "nearest", inline: "nearest"});
        return;
      }
    }
    const horizontal = event.key === "ArrowLeft" || event.key === "ArrowRight";
    if (current.matches("video, audio") && horizontal) return;
    if (!horizontal && current.classList.contains("player-viewport")) {
      const distance = event.key === "ArrowDown" ? 1 : -1;
      if (distance > 0 ? current.scrollTop + current.clientHeight < current.scrollHeight - 2 : current.scrollTop > 1) {
        event.preventDefault();
        current.scrollBy(0, distance * current.clientHeight * 0.8);
        return;
      }
    }
    if (horizontal && current.matches("input:not([readonly]):not([type=checkbox]):not([type=radio]):not([type=button]):not([type=submit]):not([type=reset]), textarea") || !horizontal && current.matches("select")) return;
    const neighbor = uniformNeighbor(current, event.key, interactive);
    if (neighbor && !neighbor.closest("[inert]") && neighbor.getClientRects().length) {
      event.preventDefault();
      neighbor.focus({preventScroll: true});
      neighbor.scrollIntoView({block: "nearest", inline: "nearest"});
      return;
    }
    const from = current.getBoundingClientRect();
    const sign = event.key === "ArrowLeft" || event.key === "ArrowUp" ? -1 : 1;
    let best: HTMLElement | undefined;
    let bestScore = Infinity;
    for (const target of candidates()) {
      if (target === current || !target.getClientRects().length || target.closest("[inert]")) continue;
      const to = target.getBoundingClientRect();
      const dx = (to.left + to.right - from.left - from.right) / 2;
      const dy = (to.top + to.bottom - from.top - from.bottom) / 2;
      const ahead = sign * (horizontal ? dx : dy);
      if (ahead <= 1) continue;
      const across = Math.abs(horizontal ? dy : dx);
      const score = ahead + across * 3;
      if (score < bestScore) { bestScore = score; best = target; }
    }
    if (best) {
      event.preventDefault();
      best.focus();
      best.scrollIntoView({ block: "nearest", inline: "nearest" });
    }
  });
}
