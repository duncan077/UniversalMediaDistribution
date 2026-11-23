import {ui} from "./locale";
import {invokeProfile} from "./profile-context";
import type {CatalogItem} from "./main";
import {isPageActive, navigateTo, onPageEnter} from "./router";

interface Person { accountId: string; username: string; displayName: string }
interface Friend extends Person { shareProgress: boolean }
interface Pending extends Person { direction: "incoming" | "outgoing" }
interface State {
  friends: Friend[];
  requests: Pending[];
  visibility: { shareProgress: boolean; discoverableByUsername: boolean; hiddenItemKeys: string[] };
}
interface Progress {
  unitKind?: string; displayNumber?: string; seasonNumber?: number; isCompleted: boolean;
}
interface Title extends Progress { title: string; viewerFavorite: boolean; friendFavorite: boolean }
interface Comparison { inCommon: Title[]; friendOnly: Title[]; viewerOnly: Title[] }
interface Activity { key: string; hidden: boolean; matches: (Person & Progress)[] }
type Action = { kind: string } & Record<string, unknown>;

function call<T>(action: Action) { return invokeProfile<T>("friend_action", { action }); }
function element<K extends keyof HTMLElementTagNameMap>(tag: K, text = "", className?: string) {
  const el = document.createElement(tag);
  if (text) el.textContent = text;
  if (className) el.className = className;
  return el;
}
function button(text: string, click: () => void, className?: string) {
  const el = element("button", text, className);
  el.type = "button";
  el.onclick = click;
  return el;
}
function toggle(text: string, checked: boolean, change: (checked: boolean) => void) {
  const label = element("label", "", "toggle-row");
  const input = element("input") as HTMLInputElement;
  input.type = "checkbox";
  input.checked = checked;
  input.onchange = () => { const value = input.checked; input.checked = checked; change(value); };
  label.append(input, " " + text);
  return label;
}
function name(p: Person) { return p.displayName === p.username ? "@" + p.username : p.displayName + " (@" + p.username + ")"; }
function position(row: Progress) {
  const parts: string[] = [];
  if (row.seasonNumber != null) parts.push(ui("Temporada {0}", row.seasonNumber));
  if (row.displayNumber != null) {
    const label = row.unitKind === "chapter"
      ? ui("Capítulo ")
      : row.unitKind === "track"
        ? ui("Pista ")
        : row.unitKind === "audio_chapter"
          ? ui("Capítulo de audio ")
          : ui("Episodio ");
    parts.push(label + row.displayNumber);
  }
  if (row.isCompleted) parts.push(ui("Completado"));
  return parts.join(" · ") || ui("En progreso");
}

/**
 * Inline Friend Activity widget for the Item Detail page (Avalonia parity).
 * Stays hidden if the user is not signed in or has no activity to display/configure.
 */
export function inlineFriendActivity(item: CatalogItem, signal?: AbortSignal): HTMLElement {
  const container = element("section", "", "surface-panel detail-friends-panel");
  container.hidden = true;

  async function refresh() {
    if (signal?.aborted) return;
    try {
      const data = await call<Activity>({ kind: "activity", mediaType: item.mediaType, title: item.name });
      if (signal?.aborted || !container.isConnected) return;
      container.replaceChildren();
      container.hidden = false;
      const header = element("div", "", "detail-friends-header");
      header.append(element("h3", ui("Actividad de amigos")));
      header.append(toggle(ui("Ocultar mi actividad en este título"), data.hidden, (hidden) => {
        container.inert = true;
        void call({ kind: "hide", key: data.key, hidden })
          .then(() => refresh())
          .catch(() => {})
          .finally(() => { container.inert = false; });
      }));
      container.append(header);
      if (data.matches.length) {
        const list = element("ul", "", "friend-activity-list");
        for (const row of data.matches) {
          const li = element("li");
          li.append(element("strong", name(row)), element("span", position(row), "entry-badge progress"));
          list.append(li);
        }
        container.append(list);
      } else {
        container.append(element("p", ui("Ningún amigo comparte progreso en este título."), "muted"));
      }
    } catch {
      /* Not signed in or server unreachable: keep hidden so the detail page stays clean. */
      container.hidden = true;
    }
  }

  void refresh();
  return container;
}

export function setupFriends(onSearchTitle?: (query: string) => void) {
  const content = document.querySelector<HTMLElement>("#friends-content")!;
  const heading = document.querySelector<HTMLElement>("#friends-title")!;
  const status = document.querySelector<HTMLElement>("#friends-status")!;
  let generation = 0;
  let reload = () => home();

  async function load<T>(title: string, fetch: () => Promise<T>, render: (data: T) => void) {
    const current = ++generation;
    heading.textContent = title;
    content.replaceChildren();
    content.inert = false;
    status.textContent = ui("Cargando…");
    try {
      const data = await fetch();
      if (current !== generation || !isPageActive("friends")) return;
      status.textContent = "";
      render(data);
    } catch (error) {
      if (current !== generation || !isPageActive("friends")) return;
      status.textContent = String(error);
      const actions = element("div", "", "button-row");
      actions.append(
        button(ui("Reintentar"), reload, "primary"),
        button(ui("Cuenta y respaldo"), () => document.querySelector<HTMLButtonElement>("#account")?.click(), "quiet-btn"),
      );
      content.append(actions);
    }
  }

  async function mutate(action: Action) {
    const current = ++generation;
    content.inert = true;
    status.textContent = ui("Guardando…");
    try {
      await call(action);
      if (current === generation && isPageActive("friends")) reload();
    } catch (error) {
      if (current === generation && isPageActive("friends")) {
        content.inert = false;
        status.textContent = String(error);
      }
    }
  }

  function home() {
    reload = home;
    void load(ui("Amigos"), () => call<State>({ kind: "state" }), state => {
      const addPanel = element("div", "", "surface-panel");
      const form = element("form", "", "friends-add-form");
      const label = element("label", ui("Agregar amigo por nombre de usuario"), "field");
      const input = element("input") as HTMLInputElement;
      input.required = true;
      input.maxLength = 64;
      input.placeholder = "usuario";
      input.autocapitalize = "none";
      input.spellcheck = false;
      label.append(input);
      const send = element("button", ui("Enviar solicitud"), "primary");
      const refreshBtn = button(ui("Actualizar"), home, "quiet-btn");
      form.append(label, send, refreshBtn);
      form.onsubmit = event => {
        event.preventDefault();
        if (input.value.trim()) void mutate({ kind: "send", username: input.value.trim() });
      };
      addPanel.append(form);
      content.append(addPanel);

      if (state.requests.length) {
        const reqSection = element("section", "", "friends-section");
        reqSection.append(element("h2", ui("Solicitudes")));
        for (const p of state.requests) {
          const row = element("div", "", "surface-panel friend-card");
          const incoming = p.direction === "incoming";
          const info = element("div");
          info.append(
            element("strong", name(p)),
            element("p", incoming ? ui("Solicitud recibida") : ui("Solicitud enviada"), "muted"),
          );
          const actions = element("div", "", "button-row");
          if (incoming) actions.append(button(ui("Aceptar"), () => void mutate({ kind: "accept", id: p.accountId }), "primary"));
          actions.append(button(incoming ? ui("Rechazar") : ui("Cancelar solicitud"), () => void mutate({ kind: "dismiss", id: p.accountId }), "quiet-btn"));
          row.append(info, actions);
          reqSection.append(row);
        }
        content.append(reqSection);
      }

      const listSection = element("section", "", "friends-section");
      listSection.append(element("h2", ui("Mis amigos")));
      if (!state.friends.length) {
        const empty = element("div", "", "surface-panel");
        empty.append(element("p", ui("Todavía no tenés amigos en este servidor."), "muted"));
        listSection.append(empty);
      }
      for (const p of state.friends) {
        const row = element("div", "", "surface-panel friend-card");
        const info = element("div");
        info.append(
          element("h3", name(p)),
          toggle(ui("Compartir mi actividad con {0}", name(p)), p.shareProgress, enabled => void mutate({ kind: "sharing", id: p.accountId, enabled })),
        );
        const actions = element("div", "", "button-row");
        actions.append(
          button(ui("Comparar títulos"), () => compare(p), "primary"),
          button(ui("Quitar amigo"), () => {
            actions.replaceChildren(
              element("span", ui("¿Quitar a {0} de tus amigos?", name(p))),
              button(ui("Quitar"), () => void mutate({ kind: "remove", id: p.accountId }), "danger-btn"),
              button(ui("Cancelar"), home, "quiet-btn"),
            );
          }, "danger-btn"),
        );
        row.append(info, actions);
        listSection.append(row);
      }
      content.append(listSection);

      const v = state.visibility;
      const privacy = element("section", "", "surface-panel friends-privacy");
      privacy.append(
        element("h2", ui("Privacidad")),
        toggle(ui("Permitir solicitudes por nombre de usuario"), v.discoverableByUsername, enabled => void mutate({ kind: "setDiscovery", enabled })),
        toggle(ui("Compartir mi progreso con amigos"), v.shareProgress, enabled => void mutate({ kind: "setGlobal", enabled })),
        element("p", ui("Tus amigos pueden ver hasta dónde llegaste en los títulos de tu último respaldo y cuáles marcaste como favoritos."), "muted"),
      );
      if (v.hiddenItemKeys.length) {
        const hidden = element("details");
        hidden.append(element("summary", ui("Títulos ocultos ({0})", v.hiddenItemKeys.length)));
        for (const key of v.hiddenItemKeys) {
          const row = element("div", "", "hidden-key-row");
          row.append(element("span", key), button(ui("Volver a mostrar"), () => void mutate({ kind: "hide", key, hidden: false }), "quiet-btn"));
          hidden.append(row);
        }
        privacy.append(hidden);
      }
      content.append(privacy);
    });
  }

  function compare(friend: Friend) {
    reload = () => compare(friend);
    void load(ui("Comparación con {0}", name(friend)), () => call<Comparison>({ kind: "compare", id: friend.accountId }), result => {
      content.append(button("← " + ui("Volver a Amigos"), home, "quiet-btn"));
      if (!(result.inCommon.length || result.friendOnly.length || result.viewerOnly.length)) {
        const empty = element("div", "", "surface-panel");
        empty.append(element("p", ui("No hay actividad para comparar. La comparación usa tu respaldo y el progreso que comparte tu amigo."), "muted"));
        content.append(empty);
        return;
      }
      for (const [title, rows, searchable] of [
        [ui("En común"), result.inCommon, false],
        [ui("Solo de {0}", name(friend)), result.friendOnly, true],
        [ui("Solo tuyos"), result.viewerOnly, false],
      ] as const) {
        if (!rows.length) continue;
        const sec = element("section", "", "surface-panel friends-compare-section");
        sec.append(element("h2", `${title} (${rows.length})`));
        const list = element("ul", "", "compare-list");
        for (const row of rows) {
          const li = element("li");
          const titleEl = searchable && onSearchTitle
            ? button(row.title, () => onSearchTitle(row.title), "link-btn")
            : element("strong", row.title);
          const badges = element("div", "", "compare-badges");
          badges.append(element("span", position(row), "entry-badge progress"));
          if (row.viewerFavorite) badges.append(element("span", "♥ " + ui("Favorito tuyo"), "entry-badge seen"));
          if (row.friendFavorite) badges.append(element("span", "★ " + ui("Favorito de {0}", name(friend)), "entry-badge"));
          li.append(titleEl, badges);
          list.append(li);
        }
        sec.append(list);
        content.append(sec);
      }
    });
  }

  onPageEnter("friends", home);
  document.querySelector<HTMLButtonElement>("#friends-open")!.onclick = () => {
    navigateTo("friends", { resetStack: true });
    home();
  };
}
