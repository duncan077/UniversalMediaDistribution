import type {CatalogItem} from "./main";

export interface CatalogTitle {
  item: CatalogItem;
  local: boolean;
  remoteId?: string;
}

function complement(local: CatalogItem, remote: CatalogItem): CatalogItem {
  const additions: Partial<CatalogItem> = {};
  for (const field of ["description", "thumbnailRef", "backdropRef", "releaseYear", "releaseDate", "publicationStatus"] as const) {
    const value = remote[field];
    const current = local[field];
    if (value && (!current || (field === "publicationStatus" && current === "unknown"))) {
      Object.assign(additions, {[field]:value});
    }
  }
  for (const field of ["genres", "categories"] as const) {
    const existing = local[field] ?? [];
    const values = [...new Set([...existing, ...(remote[field] ?? [])])];
    if (values.length > existing.length) additions[field] = values;
  }
  const links = Object.entries(remote.onlineProviders ?? {}).filter(([site]) => !local.onlineProviders?.[site]);
  if (links.length) additions.onlineProviders = {...Object.fromEntries(links), ...local.onlineProviders};
  return Object.keys(additions).length ? {...local,...additions} : local;
}

export function unifyCatalog(
  library: readonly CatalogItem[],
  online: readonly CatalogItem[],
  localMatches: Readonly<Record<string, CatalogItem>>,
): Map<string, CatalogTitle> {
  const titles = new Map(library.map(item => [item.syncId, {item, local:true} as CatalogTitle]));
  const remoteIds = new Set(online.map(item => item.syncId));
  const matchCounts = new Map<string, number>();
  for (const id of remoteIds) {
    const localId = titles.get(id)?.item.syncId ?? localMatches[id]?.syncId;
    if (localId) matchCounts.set(localId, (matchCounts.get(localId) ?? 0) + 1);
  }
  for (const remote of online) {
    const remoteId = remote.syncId;
    if (!remoteIds.delete(remoteId)) continue;
    const existing = titles.get(remoteId);
    const local = existing?.local ? existing.item : localMatches[remoteId];
    if (!local || (local.syncId !== remoteId && (matchCounts.get(local.syncId) ?? 0) > 1)) {
      titles.set(remoteId, {item:remote, local:false, remoteId});
      continue;
    }
    const item = titles.get(local.syncId)?.item ?? local;
    titles.set(local.syncId, {item:complement(item,remote), local:true, remoteId});
  }
  return titles;
}
