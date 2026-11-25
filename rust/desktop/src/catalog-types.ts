export function catalogGroup(media?: string | null, subtype?: string | null): string {
  const type = media?.toLowerCase();
  const code = subtype?.split(".").pop()?.replace(/[- ]/g, "_").toLowerCase();
  if (type === "book") {
    if (code === "manga" || code === "manhwa") return "manga";
    if (code === "comic") return "comic";
    if (code && ["book", "novel", "light_novel", "web_novel"].includes(code)) return "prose";
  }
  if (type === "movie" && code) return code.startsWith("anime_") ? "anime" : "liveAction";
  return type === "audio" ? "audio" : "";
}

export function groupMedia(group: string): string {
  if (group === "audio") return "audio";
  if (group === "anime" || group === "liveAction") return "movie";
  return group ? "book" : "";
}

export function bookGroupLabel(group: string): string {
  switch (group) {
    case "manga": return "Manga y manhwa";
    case "prose": return "Libros y novelas";
    case "comic": return "Cómics";
    default: return "";
  }
}
