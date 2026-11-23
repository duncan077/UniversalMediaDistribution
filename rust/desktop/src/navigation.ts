import { exit, onBackButtonPress } from "@tauri-apps/api/app";
import { flushProgress } from "./player";
import { canGoBack, goBack } from "./router";

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
  const interactive = "button:enabled, input:enabled, select:enabled, summary, a[href], video[controls], audio[controls], [tabindex='0']";
  document.addEventListener("keydown", event => {
    if (event.defaultPrevented || !["ArrowUp", "ArrowDown", "ArrowLeft", "ArrowRight"].includes(event.key)) return;
    const root = topDialog() ?? document;
    const current = document.activeElement;
    const insideRoot = current instanceof HTMLElement && current !== document.body && current !== document.documentElement && current.isConnected && (root === document || root.contains(current));
    if (!insideRoot) {
      const first = Array.from(root.querySelectorAll<HTMLElement>(interactive)).find(el => el.getClientRects().length > 0 && !el.closest("[inert]"));
      if (first) {
        event.preventDefault();
        first.focus();
        first.scrollIntoView({ block: "nearest", inline: "nearest" });
      }
      return;
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
    if (horizontal && current.matches("input:not([type=checkbox]):not([type=radio]):not([type=button]):not([type=submit]):not([type=reset]), textarea") || !horizontal && current.matches("select")) return;
    const from = current.getBoundingClientRect();
    const sign = event.key === "ArrowLeft" || event.key === "ArrowUp" ? -1 : 1;
    let best: HTMLElement | undefined;
    let bestScore = Infinity;
    for (const target of root.querySelectorAll<HTMLElement>(interactive)) {
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
