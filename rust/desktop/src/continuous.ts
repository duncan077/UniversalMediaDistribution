function releasePageDom(page: HTMLElement) {
  for (const canvas of page.querySelectorAll("canvas")) {
    canvas.width = 0;
    canvas.height = 0;
  }
  for (const img of page.querySelectorAll("img")) {
    img.src = "";
  }
  page.replaceChildren();
}

export function continuousReader(
  viewport: HTMLElement,
  count: number,
  initialPage: number,
  draw: (page: number, target: HTMLElement, signal: AbortSignal) => Promise<void>,
  onPage: (page: number, completed: boolean) => void,
  onEvict?: (page: number) => void,
  keepPerSide = 3,
) {
  const list = document.createElement("div");
  list.className = "continuous-pages";
  const pages = Array.from({length: count}, (_, index) => {
    const page = document.createElement("section");
    page.dataset.page = String(index + 1);
    page.style.minHeight = Math.max(120, viewport.clientHeight - 24) + "px";
    list.append(page);
    return page;
  });
  const retained = new Map<number, AbortController>();
  let running = 0;
  let closed = false;
  let frame = 0;
  let current = initialPage;
  let lastReady = false;
  let finished = false;
  const atEnd = () => lastReady && viewport.scrollTop + viewport.clientHeight >= viewport.scrollHeight - 2;

  function evict(num: number) {
    if (num === count) lastReady = false;
    retained.get(num)?.abort();
    retained.delete(num);
    const page = pages[num - 1];
    page.style.minHeight = Math.max(60, page.offsetHeight) + "px";
    onEvict?.(num);
    releasePageDom(page);
  }

  function reconcile() {
    if (closed) return;
    const first = Math.max(1, current - keepPerSide);
    const last = Math.min(count, current + keepPerSide);
    for (const num of retained.keys()) {
      if (num < first || num > last) evict(num);
    }
    const nearest = Array.from({length: last - first + 1}, (_, i) => first + i)
      .sort((a, b) => Math.abs(a - current) - Math.abs(b - current));
    for (const num of nearest) {
      if (running >= 2) break;
      if (retained.has(num)) continue;
      const controller = new AbortController();
      retained.set(num, controller);
      running++;
      const page = pages[num - 1];
      const content = document.createElement("div");
      page.append(content);
      void draw(num, content, controller.signal).then(() => {
        if (!controller.signal.aborted) {
          page.style.minHeight = Math.max(60, content.offsetHeight) + "px";
          if (num === count) { lastReady = true; scroll(); }
        }
      }).catch(e => {
        if (!controller.signal.aborted) content.textContent = String(e);
      }).finally(() => {
        running--;
        reconcile();
      });
    }
  }
  const update = () => {
    frame = 0;
    const rect = viewport.getBoundingClientRect();
    const anchor = rect.top + Math.min(rect.height / 3, 120);
    let low = 0, high = count - 1;
    while (low < high) {
      const mid = Math.floor((low + high) / 2);
      if (pages[mid].getBoundingClientRect().bottom < anchor) low = mid + 1;
      else high = mid;
    }
    const completed = atEnd();
    const page = completed ? count : low + 1;
    if (page !== current || completed !== finished) {
      current = page;
      finished = completed;
      onPage(page, completed);
      reconcile();
    }
  };
  const scroll = () => { if (!frame) frame = requestAnimationFrame(update); };
  const go = (page: number) => {
    pages[page - 1].scrollIntoView({block: "start"});
    current = page;
    finished = atEnd();
    onPage(page, finished);
    reconcile();
  };
  viewport.replaceChildren(list);
  viewport.addEventListener("scroll", scroll, {passive: true});
  go(initialPage);
  return {go, atEnd, close() {
    closed = true;
    viewport.removeEventListener("scroll", scroll);
    cancelAnimationFrame(frame);
    for (const num of retained.keys()) evict(num);
    list.remove();
  }};
}
