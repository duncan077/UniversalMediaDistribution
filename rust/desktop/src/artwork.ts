import {convertFileSrc} from "@tauri-apps/api/core";

const television = navigator.userAgent.includes("UMD-TV");

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
  const query = new URLSearchParams({u: url, w: String(width)});
  for (const provider of [...providers].sort()) query.append("p", provider);
  const proxy = () => {
    if (!img.isConnected) return;
    img.addEventListener("error", fail, {once: true});
    img.src = `${convertFileSrc("artwork", "umdstream")}?${query}`;
  };
  if (television) {
    img.loading = "lazy";
    img.addEventListener("error", fail, {once: true});
    img.src = `${convertFileSrc("artwork", "umdstream")}?${query}`;
  } else {
    img.addEventListener("error", proxy, {once: true});
    img.src = url;
  }
}
