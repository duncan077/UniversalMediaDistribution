import {convertFileSrc} from "@tauri-apps/api/core";
import {invoke} from "./bridge";

interface Layout {id: string; width: number; height: number; rows: number}

export async function drawImageTiles(address: string, viewport: HTMLElement, container: HTMLElement, label: string, signal: AbortSignal) {
  const layout = await invoke<Layout | null>("prepare_reader_tiles", {address}).catch(() => null);
  if (!layout) return false;
  const release = () => void invoke("release_reader_tiles", {id: layout.id}).catch(() => {});
  if (signal.aborted || !container.isConnected) {
    release();
    return false;
  }
  const strip = document.createElement("div");
  strip.className = "reader-image-tiles";
  strip.setAttribute("role", "img");
  strip.setAttribute("aria-label", label);
  const path = `${convertFileSrc("tiles", "umdstream")}/${layout.id}`;
  const observer = new IntersectionObserver(entries => {
    for (const entry of entries) {
      const tile = entry.target as HTMLElement;
      if (!entry.isIntersecting || signal.aborted) {
        tile.querySelector("img")?.removeAttribute("src");
        tile.replaceChildren();
      } else if (!tile.firstChild) {
        const image = document.createElement("img");
        image.alt = "";
        image.decoding = "async";
        image.src = `${path}/${tile.dataset.tile}`;
        tile.append(image);
      }
    }
  }, {root: viewport, rootMargin: "192px 0px"});
  signal.addEventListener("abort", () => {
    observer.disconnect();
    for (const image of strip.querySelectorAll("img")) image.removeAttribute("src");
    strip.remove();
    release();
  }, {once: true});
  for (let top = 0, index = 0; top < layout.height; top += layout.rows, index++) {
    const tile = document.createElement("div");
    tile.dataset.tile = String(index);
    tile.style.aspectRatio = `${layout.width} / ${Math.min(layout.rows, layout.height - top)}`;
    strip.append(tile);
    observer.observe(tile);
  }
  container.append(strip);
  return true;
}
