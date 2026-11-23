import diagnostics from "./locale-errors-en.json" with { type: "json" };
import english from "./locale-en.json" with { type: "json" };

type Language = "en" | "es";
const key = "umd-ui-language";
const savedLanguage = localStorage.getItem(key);
const systemLanguages = navigator.languages.length ? navigator.languages : [navigator.language];
const current: Language = savedLanguage === "en" || savedLanguage === "es" ? savedLanguage :
  (systemLanguages.find(tag => /^(en|es)(-|$)/i.test(tag))?.toLowerCase().startsWith("en") ? "en" : "es");
export const metadataLocale = systemLanguages.find(tag => tag.toLowerCase().startsWith(current + "-")) ??
  (current === "en" ? "en-US" : "es-AR");
const messages: Record<string,string> = english;
export function ui(source: string, ...values: unknown[]): string {
  const message = current === "en" && Object.hasOwn(messages, source) ? messages[source] : source;
  return values.length ? message.replace(/\{(\d+)\}/g, (_, index) => String(values[Number(index)])) : message;
}

const nativeMessages: Record<string,string> = diagnostics;
const templates = Object.entries(nativeMessages).filter(([key]) => key.includes("{")).map(([key, translation]) => {
  const pattern = key.split(/\{\w*\}/g).map(s => s.replace(/[.*+?^$()|[\]\\]/g, "\\$&")).join("(.+?)");
  return {pattern: new RegExp("^" + pattern + "$", "s"), translation};
});
export function localizeDiagnostic(message: string): string {
  if (current !== "en") return message;
  if (Object.hasOwn(nativeMessages,message)) return nativeMessages[message];
  for (const {pattern,translation} of templates) {
    const match = pattern.exec(message);
    if (match) return translation.replace(/\{(\d+)\}/g, (_, i) => match[Number(i)+1]);
  }
  return message;
}

export function setupLanguage(beforeReload?: () => Promise<unknown>) {
  const select = document.getElementById("ui-language") as HTMLSelectElement;
  select.value = current;
  select.onchange = async () => {
    localStorage.setItem(key, select.value);
    if (beforeReload) {
      try { await beforeReload(); } catch { /* Proceed with reload even if pending progress write fails. */ }
    }
    location.reload();
  };
  document.documentElement.lang = current;
  if (current !== "en") return;
  // Only translate the application's static markup, before rendering catalog
  // or account data. Runtime labels use ui() at their point of creation.
  const nodes = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
  for (let node = nodes.nextNode(); node; node = nodes.nextNode()) {
    const raw = node.nodeValue!;
    const label = raw.trim();
    if (Object.hasOwn(messages, label)) node.nodeValue = raw.replace(label, () => messages[label]);
  }
  for (const el of document.querySelectorAll("[aria-label],[placeholder],[title]")) {
    for (const attr of ["aria-label","placeholder","title"]) {
      const value = el.getAttribute(attr);
      if (value && Object.hasOwn(messages, value)) el.setAttribute(attr, messages[value]);
    }
  }
}
export function displayTitle(item: {name: string; alternativeNames?: unknown}): string {
  const aliases = Array.isArray(item.alternativeNames) ? item.alternativeNames : [];
  const locale = (navigator.languages.find(lang => lang.split("-")[0] === current) || current).toLowerCase();
  let title = item.name.trim(), rank = Infinity;
  for (const alias of aliases) {
    if (!alias || typeof alias.name !== "string" || !alias.name.trim() || typeof alias.language !== "string") continue;
    const language = alias.language.trim().toLowerCase();
    if (language.split("-")[0] !== current) continue;
    const kind = String(alias.aliasType || "").trim().toLowerCase();
    const priority = ["translation","translated","localized"].includes(kind) ? 0 : kind === "canonical" ? 1 :
      ["alternative","alias"].includes(kind) ? 2 : kind === "original" ? 3 : 4;
    const score = (language === locale ? 0 : language === current ? 1 : 2) * 5 + priority;
    if (score < rank || (score === rank && alias.name.localeCompare(title, locale, {sensitivity:"accent"}) < 0)) {
      rank = score; title = alias.name.trim();
    }
  }
  return title;
}
