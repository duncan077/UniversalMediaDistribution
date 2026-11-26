export const adultFilterKey = "umd-hide-adult";

const adultLabels = new Set([
  "adult", "adulto", "adultos", "porn", "porno", "pornography", "pornografia",
  "+18", "18+", "hentai", "erotica", "erotico", "nsfw", "xxx", "smut",
]);
const adultRatings = new Set([
  "rx", "nc-17", "nc17", "x", "xxx", "+18", "18+", "adult", "adulto", "adultos",
  "nsfw", "ao", "adults only", "solo adultos", "mayores de 18",
]);

type ContentLabels = {genres?: string[] | null; categories?: string[] | null; contentRating?: string | null};

export function hideAdult(storage: Pick<Storage, "getItem"> = localStorage): boolean {
  return storage.getItem(adultFilterKey) !== "false";
}

function normalize(value: string): string {
  return value.normalize("NFKD").replace(/\p{M}/gu, "").trim().replace(/\s+/g, " ").toLowerCase();
}

export function isAdultTitle(item?: ContentLabels | null): boolean {
  if (!item) return false;
  return [...(item.categories ?? []), ...(item.genres ?? [])]
    .some(label => typeof label === "string" && adultLabels.has(normalize(label)))
    || (typeof item.contentRating === "string" && adultRatings.has(normalize(item.contentRating)));
}

export function hiddenTitle(item?: ContentLabels | null): boolean {
  return hideAdult() && isAdultTitle(item);
}
