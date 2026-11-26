import {convertFileSrc} from "@tauri-apps/api/core";

const television = navigator.userAgent.includes("UMD-TV");

export function artworkSource(url: string, width = 0, providers: string[] = []): string {
  const query = new URLSearchParams({u: url, w: String(width)});
  for (const provider of [...providers].sort()) query.append("p", provider);
  return `${convertFileSrc("artwork", "umdstream")}?${query}`;
}

export function attachArtworkImage(
  img: HTMLImageElement, url: string, providers: string[], onExhausted: () => void, size: "card" | "wide" | "backdrop" | boolean = "card",
) {
  img.decoding = "async";
  img.referrerPolicy = "no-referrer";
  const fail = () => { if (img.isConnected) onExhausted(); };
  if (url.startsWith("data:")) {
    img.addEventListener("error", fail, {once: true});
    img.src = url;
    return;
  }
  const width = television ? size === "backdrop" ? 64 : size === "wide" || size === true ? 640 : 240 : 0;
  const source = artworkSource(url, width, providers);
  const proxy = () => {
    if (!img.isConnected) return;
    img.addEventListener("error", fail, {once: true});
    img.src = source;
  };
  if (television) {
    img.loading = "lazy";
    img.addEventListener("error", fail, {once: true});
    img.src = source;
  } else {
    img.addEventListener("error", proxy, {once: true});
    img.src = url;
  }
}
