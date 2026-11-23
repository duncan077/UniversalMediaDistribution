export type PageId =
  | "catalog"
  | "detail"
  | "streams"
  | "provider"
  | "lists"
  | "downloads"
  | "friends"
  | "party";

interface HistoryEntry {
  page: PageId;
  scrollY: number;
  navId?: string;
}

const navMap: Partial<Record<PageId, string>> = {
  streams: "streams-open",
  provider: "provider-open",
  lists: "lists-open",
  downloads: "downloads-open",
  friends: "friends-open",
  party: "party-open",
};

let currentPage: PageId = "catalog";
let activeCatalogNavId = "all";
const backStack: HistoryEntry[] = [];
const leaveListeners = new Map<PageId, Set<() => void>>();
const enterListeners = new Map<PageId, Set<() => void>>();

export function activePage(): PageId {
  return currentPage;
}

export function isPageActive(page: PageId): boolean {
  return currentPage === page;
}

export function setCatalogNavId(id: string) {
  activeCatalogNavId = id;
  if (currentPage === "catalog") syncSidebarActive();
}

export function onPageLeave(page: PageId, callback: () => void) {
  let set = leaveListeners.get(page);
  if (!set) {
    set = new Set();
    leaveListeners.set(page, set);
  }
  set.add(callback);
}

export function onPageEnter(page: PageId, callback: () => void) {
  let set = enterListeners.get(page);
  if (!set) {
    set = new Set();
    enterListeners.set(page, set);
  }
  set.add(callback);
}

function syncSidebarActive() {
  const activeNav =
    currentPage === "catalog"
      ? activeCatalogNavId
      : currentPage === "detail"
        ? (backStack.at(-1)?.navId ?? activeCatalogNavId)
        : navMap[currentPage];
  const rail = document.getElementById("rail");
  if (!rail) return;
  for (const btn of rail.querySelectorAll<HTMLButtonElement>(".nav-item")) {
    const isActive = btn.id === activeNav;
    btn.classList.toggle("active", isActive);
    if (btn.hasAttribute("aria-pressed")) {
      btn.setAttribute("aria-pressed", String(isActive));
    }
    if (isActive) btn.setAttribute("aria-current", "page");
    else btn.removeAttribute("aria-current");
  }
}

export function navigateTo(page: PageId, options: { push?: boolean; resetStack?: boolean; scrollTop?: boolean } = {}) {
  const prev = currentPage;
  const scrollY = window.scrollY;
  if (options.resetStack) {
    backStack.length = 0;
  } else if (options.push && prev !== page) {
    const prevNav =
      prev === "catalog" ? activeCatalogNavId : navMap[prev];
    backStack.push({ page: prev, scrollY, navId: prevNav });
    if (backStack.length > 20) backStack.shift();
  }
  if (prev !== page) {
    leaveListeners.get(prev)?.forEach((cb) => cb());
  }
  currentPage = page;
  for (const el of document.querySelectorAll<HTMLElement>("[data-page]")) {
    el.hidden = el.dataset.page !== page;
  }
  syncSidebarActive();
  if (options.scrollTop !== false) {
    window.scrollTo(0, 0);
  }
  if (prev !== page) {
    enterListeners.get(page)?.forEach((cb) => cb());
  }
}

export function canGoBack(): boolean {
  return currentPage === "detail" || backStack.length > 0;
}

export function goBack(): boolean {
  const entry = backStack.pop();
  if (!entry) {
    if (currentPage !== "catalog") {
      navigateTo("catalog", { resetStack: true });
      return true;
    }
    return false;
  }
  const prev = currentPage;
  if (prev !== entry.page) {
    leaveListeners.get(prev)?.forEach((cb) => cb());
  }
  currentPage = entry.page;
  for (const el of document.querySelectorAll<HTMLElement>("[data-page]")) {
    el.hidden = el.dataset.page !== entry.page;
  }
  syncSidebarActive();
  requestAnimationFrame(() => window.scrollTo(0, entry.scrollY));
  if (prev !== entry.page) {
    enterListeners.get(entry.page)?.forEach((cb) => cb());
  }
  return true;
}
