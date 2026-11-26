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
  const seenRemote = new Set<string>();
  for (const remote of online) {
    const remoteId = remote.syncId;
    if (seenRemote.has(remoteId)) continue;
    seenRemote.add(remoteId);
    const local = titles.get(remoteId)?.item ?? localMatches[remoteId];
    if (!local) {
      titles.set(remoteId, {item:remote, local:false, remoteId});
      continue;
    }
    const id = local.syncId;
    const previous = titles.get(id);
    const preferredRemote = remoteId === id ? remoteId : previous?.remoteId ?? remoteId;
    titles.set(id, {item:complement(previous?.item ?? local,remote), local:true, remoteId:preferredRemote});
  }
  return titles;
}
