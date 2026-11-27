export const subtitleLanguageKey = "subtitle-language";

type SubtitleTrack = {language?: string | null; label?: string | null; url?: string};
export type SubtitleSelection = {manual?: SubtitleTrack | null};

export const subtitleLanguages = [
  "ar", "bg", "bn", "ca", "cs", "da", "de", "el", "en", "en-GB", "en-US",
  "es", "es-419", "es-AR", "es-ES", "es-MX", "fa", "fi", "fr", "he", "hi", "hu",
  "id", "it", "ja", "ko", "nl", "no", "pl", "pt", "pt-BR", "pt-PT", "ro", "ru",
  "sv", "ta", "th", "tr", "uk", "vi", "zh", "zh-Hans", "zh-Hant",
];

const languageNames = ["en", "es"].map(locale => new Intl.DisplayNames(locale, {type: "language", fallback: "none"}));
const ambiguous = new Set(["und", "mul", "mis", "zxx"]);
const aliases = new Map([["pob", "pt-BR"], ["latino", "es-419"], ["latin american spanish", "es-419"]]);
for (const tag of subtitleLanguages) {
  for (const names of languageNames) {
    const name = names.of(tag);
    if (name) aliases.set(name.toLowerCase(), tag);
  }
}

export function languageTag(value?: string | null): string | undefined {
  if (!value) return;
  const tag = value.trim().replaceAll("_", "-");
  const alias = aliases.get(tag.toLowerCase());
  if (alias) return alias;
  if (!/^[a-z]{2,3}(?:-[a-z0-9]{2,8})*$/i.test(tag) || tag.length > 48) return;
  try {
    const locale = new Intl.Locale(tag);
    if (!ambiguous.has(locale.language) && languageNames[0].of(locale.language)) return locale.baseName;
  } catch {
    // Metadata from a provider can contain invalid language tags.
  }
}

export function trackLanguage(language?: string | null, label?: string | null) {
  // VLC can provide a language in the track description instead of a language tag.
  return languageTag(language) ?? languageTag(label) ?? languageTag(label?.match(/\[([^\]]+)\]\s*$/)?.[1]);
}

export function preferredSubtitleLanguage(preference: string | null, deviceLanguage: string) {
  if (preference === "off") return;
  return languageTag(preference) ?? languageTag(deviceLanguage);
}

export function automaticSubtitle<T extends SubtitleTrack>(
  tracks: readonly T[], audio: string | undefined, preferred: string | undefined,
): T | undefined {
  const from = languageTag(audio);
  const to = languageTag(preferred);
  if (!from || !to) return;
  const target = new Intl.Locale(to);
  if (new Intl.Locale(from).language === target.language) return;
  let compatible: T | undefined;
  for (const track of tracks) {
    const tag = trackLanguage(track.language, track.label);
    if (!tag) continue;
    if (tag === to) return track;
    if (compatible !== undefined) continue;
    const locale = new Intl.Locale(tag);
    if (locale.language === target.language && (!target.script || locale.maximize().script === target.script)) compatible = track;
  }
  return compatible;
}

export function manualSubtitle<T extends SubtitleTrack>(tracks: readonly T[], picked: SubtitleTrack | null): T | undefined {
  if (!picked) return;
  const language = trackLanguage(picked.language, picked.label);
  return (picked.url ? tracks.find(track => track.url === picked.url) : undefined)
    ?? tracks.find(track => picked.label && track.label === picked.label && trackLanguage(track.language, track.label) === language)
    ?? (language ? tracks.find(track => trackLanguage(track.language, track.label) === language) : undefined);
}
