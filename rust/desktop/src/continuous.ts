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
  draw: (page: number, target: HTMLElement) => Promise<void>,
  onPage: (page: number) => void,
  onEvict?: (page: number) => void,
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
  const observer = new IntersectionObserver(entries => {
    for (const entry of entries) {
      const page = entry.target as HTMLElement;
      const pageNumber = Number(page.dataset.page);
      if (!entry.isIntersecting) {
        if (page.firstChild) {
          page.style.minHeight = Math.max(120, page.offsetHeight) + "px";
          onEvict?.(pageNumber);
          releasePageDom(page);
        }
        continue;
      }
      if (page.firstChild) continue;
      const content = document.createElement("div");
      page.append(content);
      void draw(pageNumber, content).then(() => {
        if (content.isConnected) page.style.minHeight = Math.max(60, content.offsetHeight) + "px";
      }).catch(e => { if (content.isConnected) content.textContent = String(e); });
    }
  }, {root: viewport, rootMargin: "100% 0px"});
  let frame = 0;
  let current = initialPage;
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
    const page = viewport.scrollTop + viewport.clientHeight >= viewport.scrollHeight - 2 ? count : low + 1;
    if (page !== current) { current = page; onPage(page); }
  };
  const scroll = () => { if (!frame) frame = requestAnimationFrame(update); };
  const go = (page: number) => {
    pages[page - 1].scrollIntoView({block: "start"});
    current = page; onPage(page);
  };
  viewport.replaceChildren(list);
  pages.forEach(page => observer.observe(page));
  viewport.addEventListener("scroll", scroll, {passive: true});
  go(initialPage);
  return {go, close() {
    observer.disconnect();
    viewport.removeEventListener("scroll", scroll);
    cancelAnimationFrame(frame);
    for (const page of pages) {
      if (page.firstChild) {
        onEvict?.(Number(page.dataset.page));
        releasePageDom(page);
      }
    }
    list.remove();
  }};
}
