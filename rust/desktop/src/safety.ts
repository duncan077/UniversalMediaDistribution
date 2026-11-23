export function remoteUrl(value: unknown): string | null {
  if (typeof value !== "string") return null;
  try {
    const url = new URL(value);
    return (url.protocol === "http:" || url.protocol === "https:") && !url.username && !url.password ? url.href : null;
  } catch {
    return null;
  }
}

export function mediaUrl(value: unknown): string | null {
  if (typeof value !== "string") return null;
  try {
    const url = new URL(value);
    if (url.username || url.password) return null;
    if (url.protocol === "http:" || url.protocol === "https:" || url.protocol === "asset:" || url.protocol === "umdstream:") {
      return url.href;
    }
    return null;
  } catch {
    return null;
  }
}

export function safeEpubImage(value: unknown): string | null {
  if (typeof value !== "string") return null;
  const trimmed = value.trim();
  if (/^data:image\/svg/i.test(trimmed)) return null;
  return /^data:image\/[a-z0-9.+-]+[;,]/i.test(trimmed) ? trimmed : null;
}

export function isHttpsUrl(value: string): boolean {
  return /^https:\/\//i.test(value.trim());
}

export function safeMediaClass(mediaType: unknown): "movie" | "book" | "audio" | "other" {
  const normalized = typeof mediaType === "string" ? mediaType.trim().toLowerCase() : "";
  return normalized === "movie" || normalized === "book" || normalized === "audio" ? normalized : "other";
}

export function nativeSourceUrl(address: string): string {
  const url = new URL(address);
  if (url.hostname === "asset.localhost" || url.protocol === "asset:") {
    let path = decodeURIComponent(url.pathname.slice(1)).replaceAll("\\", "/");
    if (/^\/\/\?\/UNC\//i.test(path)) {
      path = "//" + path.slice(8);
    } else if (path.startsWith("//?/")) {
      path = path.slice(4);
    }
    if (path.startsWith("//")) {
      const parts = path.slice(2).split("/");
      const host = parts.shift() ?? "";
      const rest = parts.map(s => encodeURIComponent(s)).join("/");
      return new URL(`file://${host}/${rest}`).href;
    }
    return new URL(
      "file:///" + path.replace(/^\/+/, "").split("/").map(s => encodeURIComponent(s).replace("%3A", ":")).join("/"),
    ).href;
  }
  return address;
}
