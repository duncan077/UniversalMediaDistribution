export interface KeyValueStore {
  readonly length: number;
  key(index: number): string | null;
  getItem(key: string): string | null;
  setItem(key: string, value: string): void;
  removeItem(key: string): void;
}

const PROFILE_PREF_PREFIX = "umd-profile:";

export function profilePrefPrefix(profileId: string): string {
  return `${PROFILE_PREF_PREFIX}${profileId}:`;
}

export function profilePrefKey(profileId: string, key: string): string {
  return `${profilePrefPrefix(profileId)}${key}`;
}

export function getProfilePref(profileId: string | null, key: string, storage: KeyValueStore = localStorage): string | null {
  if (profileId) {
    const scoped = storage.getItem(profilePrefKey(profileId, key));
    if (scoped !== null) return scoped;
  }
  return storage.getItem(key);
}

export function setProfilePref(profileId: string | null, key: string, value: string, storage: KeyValueStore = localStorage): void {
  storage.setItem(profileId ? profilePrefKey(profileId, key) : key, value);
}

export function clearProfilePrefs(profileId: string, storage: KeyValueStore = localStorage): void {
  const prefix = profilePrefPrefix(profileId);
  const keys: string[] = [];
  for (let i = 0; i < storage.length; i++) {
    const key = storage.key(i);
    if (key && key.startsWith(prefix)) keys.push(key);
  }
  for (const key of keys) storage.removeItem(key);
}

export function preferredProvidersForProfile(profileId: string, storage: KeyValueStore = localStorage): string[] {
  const scopedPrefix = profilePrefKey(profileId, "provider-favorite-");
  const legacyPrefix = "provider-favorite-";
  const providers = new Set<string>();
  for (let i = 0; i < storage.length; i++) {
    const key = storage.key(i);
    if (!key) continue;
    if (key.startsWith(scopedPrefix)) providers.add(key.slice(scopedPrefix.length));
    else if (key.startsWith(legacyPrefix)) providers.add(key.slice(legacyPrefix.length));
  }
  return [...providers].filter(id => getProfilePref(profileId, legacyPrefix + id, storage) === "true");
}
