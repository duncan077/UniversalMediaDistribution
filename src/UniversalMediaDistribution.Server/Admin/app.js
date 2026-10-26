"use strict";
const byId = id => document.getElementById(id);
const number = new Intl.NumberFormat("es-AR");
const date = value => value ? new Date(value).toLocaleString("es-AR") : "—";
const names = {
  ok: "Correcto", partial: "Parcial", completed: "Completado", running: "En curso", queued: "En cola",
  not_run: "Sin ejecutar", pending: "Pendiente", attention: "Revisar", backoff: "En espera",
  disabled: "Deshabilitado", configuration_required: "Sin configurar", interrupted: "Interrumpido",
  failed: "Falló", cancelled: "Cancelado", budget_exhausted: "Tiempo agotado", already_running: "Ya en curso",
  timeout: "Tiempo de espera", throttled: "Límite del proveedor", network: "Conexión / HTTP",
  not_found: "No encontrado", challenge_or_access_denied: "Acceso bloqueado / CAPTCHA",
  parse_or_storage_error: "Lectura / almacenamiento", unsupported: "No compatible",
  info: "Información", warning: "Advertencia", error: "Error", started: "Iniciado", stopping: "Deteniendo",
  updates: "Novedades", discovery: "Catálogo", detail: "Detalle", provider: "Proveedor", run: "Ejecución",
  lifecycle: "Servicio", request: "Solicitud", configuration: "Configuración", updated: "Actualizado", http_error: "Error HTTP"
};
let token = "", timer = null, refreshing = null, generation = 0, logGeneration = 0, before = null;
let providerNames = new Map();
let crawlerRunning = false, submittingCrawl = false, followedRun = null;
const intervalEdits = new Map(), savingIntervals = new Set();
function text(id, value) { byId(id).textContent = value; }
function showError(message) { text("error", message); byId("error").hidden = !message; }
function label(value) { return names[value] || value || "—"; }
function badge(value) {
  const node = document.createElement("span");
  node.className = "badge";
  node.textContent = label(value);
  node.dataset.tone = ["ok", "completed", "started"].includes(value) ? "good" :
    ["error", "failed", "attention", "http_error"].includes(value) ? "error" :
      ["warning", "partial", "backoff", "interrupted", "budget_exhausted", "configuration_required"].includes(value) ? "warning" : "neutral";
  return node;
}
function cell(row, value) {
  const node = document.createElement("td");
  if (value instanceof Node) node.append(value); else node.textContent = value;
  row.append(node);
  return node;
}
function bytes(value) {
  const units = ["B", "KiB", "MiB", "GiB"];
  let index = 0;
  while (value >= 1024 && index < units.length - 1) { value /= 1024; index++; }
  return `${number.format(Math.round(value * 10) / 10)} ${units[index]}`;
}
function duration(start, end) {
  const seconds = Math.max(0, Math.floor(((end ? new Date(end) : new Date()) - new Date(start)) / 1000));
  if (seconds < 60) return `${seconds} s`;
  if (seconds < 3600) return `${Math.floor(seconds / 60)} min`;
  if (seconds < 86400) return `${Math.floor(seconds / 3600)} h ${Math.floor(seconds % 3600 / 60)} min`;
  return `${Math.floor(seconds / 86400)} d ${Math.floor(seconds % 86400 / 3600)} h`;
}
async function api(path, { method = "GET", body } = {}) {
  const headers = { Authorization: `Bearer ${token}`, Accept: "application/json" };
  if (body !== undefined) headers["Content-Type"] = "application/json";
  const response = await fetch(`/api/v1/admin/diagnostics${path}`, {
    method, headers, body: body === undefined ? undefined : JSON.stringify(body),
    cache: "no-store", credentials: "omit", signal: AbortSignal.timeout(15000)
  });
  if (response.status === 401) throw new Error("La clave administrativa no es válida.");
  if (!response.ok) {
    const failure = await response.json().catch(() => null);
    throw new Error(failure?.message || (response.status === 503 ?
      "El acceso administrativo no está configurado en el servidor, o el servicio no está disponible." : `El servidor respondió HTTP ${response.status}.`));
  }
  return response.json();
}
function crawlControls() {
  for (const id of ["crawl-start", "crawl-provider", "crawl-retry"]) byId(id).disabled = crawlerRunning || submittingCrawl;
  text("crawl-start", submittingCrawl ? "Iniciando…" : crawlerRunning ? "Scraper en curso" : "Ejecutar scraper");
}
function renderSnapshot(data) {
  const changed = crawlerRunning !== data.crawlerRunning;
  crawlerRunning = data.crawlerRunning;
  if (changed) poll();
  const selectedCrawl = byId("crawl-provider").value;
  byId("crawl-provider").replaceChildren(new Option("Todos los habilitados", ""), ...data.providers.map(provider => {
    const option = new Option(provider.name, provider.id);
    option.disabled = ["disabled", "configuration_required"].includes(provider.status);
    return option;
  }));
  byId("crawl-provider").value = selectedCrawl;
  crawlControls();
  const followed = data.runs.find(run => run.id === followedRun);
  if (followed && !["queued", "running"].includes(followed.status)) {
    text("crawl-message", `Ejecución ${label(followed.status).toLowerCase()}: ${number.format(followed.titlesUpdated)} títulos actualizados, ${number.format(followed.failures)} fallos. Revisa los logs para más detalles.`);
    followedRun = null;
  } else if (crawlerRunning) text("crawl-message", "Scraper en curso. El historial y los logs muestran su actividad.");
  providerNames = new Map(data.providers.map(provider => [provider.id, provider.name]));
  text("catalog-count", number.format(data.catalog.titles));
  text("catalog-date", `Revisión ${number.format(data.catalog.revision)} · ${date(data.catalog.updatedAt)}`);
  text("crawl-state", data.crawlerRunning ? "En curso" : "En reposo");
  const lastRun = data.runs[0];
  text("crawl-date", lastRun ? `Última ejecución: ${date(lastRun.startedAt)}` : "Sin ejecuciones registradas");
  text("failure-count", number.format(data.providers.filter(provider => ["attention", "backoff"].includes(provider.status)).length));
  text("failed-titles", `${number.format(data.providers.reduce((total, provider) => total + provider.failedTitles, 0))} títulos con fallos pendientes`);
  text("due-count", number.format(data.providers.filter(provider => provider.status !== "disabled").reduce((total, provider) => total + provider.due, 0)));
  text("uptime", duration(data.runtime.startedAt));
  text("memory", `Memoria del proceso: ${bytes(data.runtime.workingSetBytes)}`);
  text("database-size", bytes(data.runtime.databaseBytes));
  text("requests", `${number.format(data.runtime.requests)} solicitudes · ${number.format(data.runtime.failedRequests)} errores HTTP 5xx desde el inicio`);
  const stale = !lastRun || Date.now() - new Date(lastRun.startedAt).getTime() > 2 * 3600000;
  byId("schedule").hidden = !stale || data.crawlerRunning;
  text("schedule", lastRun ? "No hay ejecuciones del scraper en las últimas dos horas. Puedes iniciarlo desde este panel o revisar el cron del servidor." :
    "El scraper todavía no registró ejecuciones. Puedes iniciarlo desde este panel; para actualizar automáticamente, configura el cron o timer del servidor.");
  renderProviderRows(data.providers);
  const selectedProvider = byId("provider-filter").value;
  const options = [new Option("Todos / servidor", ""), ...data.providers.map(provider => new Option(provider.name, provider.id))];
  byId("provider-filter").replaceChildren(...options);
  byId("provider-filter").value = selectedProvider;
  const runs = document.createDocumentFragment();
  for (const run of data.runs) {
    const row = document.createElement("tr");
    cell(row, date(run.startedAt)); cell(row, badge(run.status));
    cell(row, run.finishedAt || ["running", "queued"].includes(run.status) ? duration(run.startedAt, run.finishedAt) : "—");
    cell(row, number.format(run.titlesUpdated)); cell(row, number.format(run.failures)); runs.append(row);
  }
  byId("runs").replaceChildren(runs);
  byId("no-runs").hidden = data.runs.length > 0;
  text("updated", `Actualizado: ${date(data.generatedAt)}`);
}
function renderProviderRows(data) {
  // Polling must not replace a focused number field or its pending save.
  if (document.activeElement?.dataset.intervalProvider || savingIntervals.size > 0) return;
  const providers = document.createDocumentFragment();
  for (const provider of data) {
    const row = document.createElement("tr");
    const link = document.createElement("button");
    link.type = "button"; link.className = "provider-link"; link.textContent = provider.name;
    link.addEventListener("click", () => {
      byId("provider-filter").value = provider.id;
      byId("level-filter").value = "";
      loadLogs().catch(error => showError(error.message));
      byId("log-section").scrollIntoView({ behavior: "smooth" });
    });
    cell(row, link);
    const status = cell(row, badge(provider.status));
    if (provider.failureKind) {
      const detail = document.createElement("span"); detail.className = "sub";
      detail.textContent = label(provider.failureKind); status.append(detail);
    }
    cell(row, `${number.format(provider.discovered)} / ${number.format(provider.loaded)}`);
    cell(row, `${number.format(provider.due)} / ${number.format(provider.failedTitles)}`);
    cell(row, provider.backfillComplete ? "Recorrido" : "Pendiente / en progreso");
    const controls = document.createElement("div"); controls.className = "interval-controls";
    const input = document.createElement("input"); input.type = "number"; input.min = "1"; input.max = "60"; input.step = "1";
    input.ariaLabel = `Intervalo en segundos de ${provider.name}`;
    input.dataset.intervalProvider = provider.id;
    input.value = intervalEdits.get(provider.id) ?? String(provider.intervalSeconds ?? 2);
    input.addEventListener("input", () => intervalEdits.set(provider.id, input.value));
    const save = document.createElement("button"); save.type = "button"; save.textContent = "Guardar";
    save.ariaLabel = `Guardar intervalo de ${provider.name}`;
    save.dataset.intervalProvider = provider.id;
    save.addEventListener("click", () => saveProviderInterval(provider, input, save));
    controls.append(input, save); cell(row, controls);
    cell(row, date(provider.lastAttempt)); cell(row, date(provider.lastSuccess)); cell(row, date(provider.retryAt));
    providers.append(row);
  }
  byId("providers").replaceChildren(providers);
}
async function saveProviderInterval(provider, input, button) {
  if (!token || savingIntervals.has(provider.id)) return;
  const intervalSeconds = Number(input.value);
  if (!Number.isInteger(intervalSeconds) || intervalSeconds < 1 || intervalSeconds > 60) {
    text("interval-message", "El intervalo debe ser un número entero entre 1 y 60 segundos."); return;
  }
  const session = generation;
  let saved = false;
  savingIntervals.add(provider.id); input.disabled = true; button.disabled = true; button.textContent = "Guardando…";
  try {
    const result = await api("/provider-interval", { method: "PUT", body: { providerId: provider.id, intervalSeconds } });
    if (session !== generation) return;
    intervalEdits.delete(provider.id);
    text("interval-message", `${provider.name}: ${result.intervalSeconds} s. ${result.message}`);
    saved = true;
  } catch (error) {
    if (session === generation) text("interval-message", error.message);
  } finally {
    if (session === generation) {
      savingIntervals.delete(provider.id); input.disabled = false; button.disabled = false; button.textContent = "Guardar";
    }
  }
  if (saved) await refresh().catch(() => {});
}
async function loadLogs(append = false) {
  const current = ++logGeneration, session = generation;
  const query = new URLSearchParams({ limit: "100" });
  if (byId("provider-filter").value) query.set("provider", byId("provider-filter").value);
  if (byId("level-filter").value) query.set("level", byId("level-filter").value);
  if (append && before) query.set("before", before);
  const data = await api(`/logs?${query}`);
  if (current !== logGeneration || session !== generation) return;
  const rows = document.createDocumentFragment();
  for (const event of data.items) {
    const row = document.createElement("tr");
    cell(row, date(event.occurredAt)); cell(row, badge(event.level));
    cell(row, providerNames.get(event.providerId) || event.providerId || (event.source === "crawler" ? "Scraper" : "Servidor"));
    cell(row, label(event.stage)); cell(row, `${label(event.status)}${event.httpStatus ? ` · HTTP ${event.httpStatus}` : ""}`);
    cell(row, event.message); rows.append(row);
  }
  if (append) byId("logs").append(rows); else byId("logs").replaceChildren(rows);
  before = data.before;
  byId("older").hidden = !before;
  byId("no-logs").hidden = byId("logs").childElementCount > 0;
}
async function refresh() {
  if (refreshing === generation || !token) return;
  const session = generation;
  refreshing = session; byId("refresh").disabled = true;
  try {
    const data = await api("");
    if (session !== generation) return;
    renderSnapshot(data);
    await loadLogs();
    if (session !== generation) return;
    byId("signin").hidden = true; byId("dashboard").hidden = false;
    text("connection", "Conectado"); byId("connection").dataset.tone = "good"; showError("");
  } catch (error) {
    if (session === generation) {
      showError(error.message);
      text("connection", byId("dashboard").hidden ? "Sin conectar" : "Sin actualizar");
      byId("connection").dataset.tone = "warning";
    }
    throw error;
  } finally {
    if (refreshing === session) { refreshing = null; byId("refresh").disabled = false; }
  }
}
function poll() {
  clearInterval(timer);
  timer = byId("automatic").checked && token ? setInterval(() => {
    if (!document.hidden) refresh().catch(() => {});
  }, crawlerRunning ? 5000 : 30000) : null;
}
byId("crawl-form").addEventListener("submit", async event => {
  event.preventDefault();
  if (submittingCrawl || crawlerRunning || !token) return;
  const session = generation;
  submittingCrawl = true; crawlControls(); text("crawl-message", "Solicitando ejecución…");
  try {
    const providerId = byId("crawl-provider").value || null;
    const result = await api("/crawl", { method: "POST", body: { providerId, retryFailures: byId("crawl-retry").checked } });
    if (session !== generation) return;
    followedRun = result.runId;
    crawlerRunning = true; poll();
    text("crawl-message", result.message);
    byId("provider-filter").value = providerId || "";
    byId("level-filter").value = "";
    await refresh();
  } catch (error) {
    if (session === generation) {
      // A lost HTTP response may still have started the background run.
      await refresh().catch(() => {});
      if (session === generation) text("crawl-message", error.message);
    }
  } finally {
    if (session === generation) { submittingCrawl = false; crawlControls(); }
  }
});
byId("login").addEventListener("submit", async event => {
  event.preventDefault(); token = byId("token").value.trim(); byId("token").value = ""; generation++;
  byId("connect").disabled = true;
  try { await refresh(); poll(); } catch { token = ""; }
  finally { byId("connect").disabled = false; }
});
byId("logout").addEventListener("click", () => {
  token = ""; generation++; logGeneration++; clearInterval(timer); before = null;
  submittingCrawl = false; followedRun = null; text("crawl-message", "");
  intervalEdits.clear(); savingIntervals.clear(); text("interval-message", "");
  byId("signin").hidden = false; byId("dashboard").hidden = true;
  byId("logs").replaceChildren(); byId("providers").replaceChildren(); byId("runs").replaceChildren();
  text("connection", "Sin conectar"); byId("connection").dataset.tone = "neutral"; showError(""); byId("token").focus();
});
byId("refresh").addEventListener("click", () => refresh().catch(() => {}));
byId("automatic").addEventListener("change", poll);
for (const id of ["provider-filter", "level-filter"]) byId(id).addEventListener("change", () => loadLogs().catch(error => showError(error.message)));
byId("older").addEventListener("click", async () => {
  byId("older").disabled = true;
  try { await loadLogs(true); } catch (error) { showError(error.message); }
  finally { byId("older").disabled = false; }
});
