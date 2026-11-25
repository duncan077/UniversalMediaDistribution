export function virtualCatalog<T>(host: HTMLElement, key: (item: T) => string, card: (item: T) => HTMLElement) {
  let items: readonly T[] = [];
  let active = false;
  let frame = 0;
  const mounted = new Map<string, {item: T; element: HTMLElement}>();

  function layout() {
    frame = 0;
    if (!active || !host.clientWidth) return;
    const style = getComputedStyle(host);
    const list = host.classList.contains("list-view");
    const columns = list ? 1 : Math.max(1, style.gridTemplateColumns.trim().split(/\s+/).length);
    host.dataset.virtualColumns = String(columns);
    const columnGap = parseFloat(style.columnGap) || 0;
    const rowGap = parseFloat(style.rowGap) || 0;
    const cell = (host.clientWidth - columnGap * (columns - 1)) / columns;
    const height = list ? 88 : Math.ceil(cell * 1.5) + 106;
    const stride = height + rowGap;
    const rows = Math.ceil(items.length / columns);
    const total = Math.max(0, rows * stride - rowGap);
    if (host.style.height !== `${total}px`) host.style.height = `${total}px`;
    const top = host.getBoundingClientRect().top;
    const first = Math.min(rows, Math.max(0, Math.floor(-top / stride) - 2));
    const last = Math.max(0, Math.min(rows, Math.ceil((innerHeight - top) / stride) + 2));
    const selected = document.activeElement?.closest<HTMLElement>("[data-virtual-key]");
    const focused = selected && host.contains(selected) ? selected.dataset.virtualKey : undefined;
    const indexes = new Set<number>();
    for (let i = first * columns; i < Math.min(items.length, last * columns); i++) indexes.add(i);
    if (focused !== undefined) {
      const index = items.findIndex(item => key(item) === focused);
      if (index >= 0) indexes.add(index);
    }
    const keep = new Set<string>();
    for (const i of indexes) {
      const item = items[i];
      const id = key(item);
      keep.add(id);
      let mountedCard = mounted.get(id);
      if (!mountedCard || (mountedCard.item !== item && id !== focused) || !host.contains(mountedCard.element)) {
        mountedCard?.element.remove();
        const element = card(item);
        element.dataset.virtualKey = id;
        mountedCard = {item, element};
        mounted.set(id, mountedCard);
        host.append(element);
      }
      const node = mountedCard.element;
      node.dataset.virtualIndex = String(i);
      node.style.width = `${cell}px`;
      node.style.left = `${(i % columns) * (cell + columnGap)}px`;
      node.style.top = `${Math.floor(i / columns) * stride}px`;
    }
    for (const [id, entry] of mounted) {
      if (keep.has(id)) continue;
      entry.element.remove();
      mounted.delete(id);
    }
  }

  const schedule = () => { if (active && !frame) frame = requestAnimationFrame(layout); };
  const resize = new ResizeObserver(schedule);
  resize.observe(host);
  window.addEventListener("scroll", schedule, {passive: true});
  window.addEventListener("resize", schedule, {passive: true});
  host.addEventListener("focusout", schedule);
  return {
    set(next: readonly T[]) {
      items = next;
      if (!active) {
        host.replaceChildren();
        host.classList.add("virtual-catalog");
        active = true;
      }
      schedule();
    },
    disable() {
      active = false;
      if (frame) cancelAnimationFrame(frame);
      frame = 0;
      items = [];
      host.classList.remove("virtual-catalog");
      host.style.removeProperty("height");
      delete host.dataset.virtualColumns;
      for (const entry of mounted.values()) entry.element.remove();
      mounted.clear();
    },
  };
}
