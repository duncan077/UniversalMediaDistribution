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

export function providerEnabled(profileId: string | null, id: string, storage: KeyValueStore = localStorage): boolean {
  return getProfilePref(profileId, "provider-enabled-" + id, storage) !== "false";
}

function providerPrefIds(profileId: string, keyPrefix: string, storage: KeyValueStore): string[] {
  const scopedPrefix = profilePrefKey(profileId, keyPrefix);
  const providers = new Set<string>();
  for (let i = 0; i < storage.length; i++) {
    const key = storage.key(i);
    if (!key) continue;
    if (key.startsWith(scopedPrefix)) providers.add(key.slice(scopedPrefix.length));
    else if (key.startsWith(keyPrefix)) providers.add(key.slice(keyPrefix.length));
  }
  return [...providers];
}

export function disabledProvidersForProfile(profileId: string, storage: KeyValueStore = localStorage): string[] {
  return providerPrefIds(profileId, "provider-enabled-", storage).filter(id => !providerEnabled(profileId, id, storage));
}

export function preferredProvidersForProfile(profileId: string, storage: KeyValueStore = localStorage): string[] {
  return providerPrefIds(profileId, "provider-favorite-", storage)
    .filter(id => providerEnabled(profileId, id, storage) && getProfilePref(profileId, "provider-favorite-" + id, storage) === "true");
}
