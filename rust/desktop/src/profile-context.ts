import {invoke} from "./bridge";
import {
  clearProfilePrefs,
  getProfilePref,
  preferredProvidersForProfile,
  profilePrefKey,
  profilePrefPrefix,
  setProfilePref,
} from "./profile-prefs";

export {clearProfilePrefs, getProfilePref, preferredProvidersForProfile, profilePrefKey, profilePrefPrefix, setProfilePref};

export interface Profiles {
  activeId: string;
  profiles: {id: string; name: string}[];
}

// A window keeps its profile until it reloads. Late writes still belong to
// the person who started playback or a backup in that window.
let context: Promise<Profiles> | undefined;
let cachedActiveId: string | null = null;

export function activeProfileId(): string | null {
  return cachedActiveId;
}

export function windowProfile(): Promise<Profiles> {
  return context ??= invoke<Profiles>("profiles").then(state => {
    cachedActiveId = state.activeId;
    return state;
  }).catch(error => {
    context = undefined;
    throw error;
  });
}

export function writeProfilePref(key: string, value: string): Promise<void> {
  if (cachedActiveId) {
    setProfilePref(cachedActiveId, key, value);
    return Promise.resolve();
  }
  return windowProfile().then(({activeId}) => {
    setProfilePref(activeId, key, value);
  });
}

export async function invokeProfile<T>(command: string, args: Record<string,unknown> = {}): Promise<T> {
  const {activeId} = await windowProfile();
  const view = command === "library"
    ? {
        hideAdult: getProfilePref(activeId, "umd-hide-adult") === "true",
        preferredProviders: preferredProvidersForProfile(activeId),
      }
    : {};
  return invoke<T>(command, {...args, ...view, profileId: activeId});
}
