// Runs the packaged admin script against an in-memory DOM and HTTP fixture.
// No browser, credentials, or upstream provider traffic is required.
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import vm from "node:vm";

const html = readFileSync(new URL("../src/UniversalMediaDistribution.Server/Admin/index.html", import.meta.url), "utf8");
const script = readFileSync(new URL("../src/UniversalMediaDistribution.Server/Admin/app.js", import.meta.url), "utf8");

class Element {
  children = []; listeners = new Map(); dataset = {}; value = ""; checked = false; disabled = false; hidden = false;
  textContent = "";
  get childElementCount() { return this.children.length; }
  append(...children) { this.children.push(...children); }
  replaceChildren(...children) { this.children = children; }
  addEventListener(type, listener) { this.listeners.set(type, listener); }
  scrollIntoView() {}
  focus() {}
}
class Option extends Element {
  constructor(text, value) { super(); this.textContent = text; this.value = value; }
}
const response = (body, status = 200) => ({ status, ok: status >= 200 && status < 300, json: async () => body });
function setup() {
  const elements = new Map([...html.matchAll(/id="([^"]+)"/g)].map(match => [match[1], new Element()]));
  const get = id => { assert.ok(elements.has(id), `Missing HTML element: ${id}`); return elements.get(id); };
  get("automatic").checked = true;
  get("dashboard").hidden = true;
  let snapshot = {
    generatedAt: "2026-10-17T00:00:00Z", crawlerRunning: false,
    catalog: { titles: 2, revision: 1, updatedAt: null },
    runtime: { startedAt: "2026-10-17T00:00:00Z", workingSetBytes: 1024, databaseBytes: 2048, requests: 1, failedRequests: 0 },
    providers: ["one", "two"].map(id => ({ id, name: id, status: "ok", discovered: 1, loaded: 1, due: 0, failedTitles: 0, intervalSeconds: 2 })),
    runs: []
  };
  const requests = [], intervals = new Map();
  let nextTimer = 0, post = async () => response({ status: "accepted", runId: "manual-run", message: "Scraper iniciado." }, 202);
  let put = async body => {
    snapshot.providers.find(provider => provider.id === body.providerId).intervalSeconds = body.intervalSeconds;
    return response({ ...body, message: "Intervalo guardado. Se aplicará desde la próxima ejecución manual o automática." });
  };
  const document = { getElementById: get, createElement: () => new Element(), createDocumentFragment: () => new Element(), hidden: false, activeElement: null };
  vm.runInNewContext(script, {
    document,
    Node: Element, Option, Intl, Date, URLSearchParams, AbortSignal,
    setInterval: (_, delay) => { intervals.set(++nextTimer, delay); return nextTimer; },
    clearInterval: id => intervals.delete(id),
    fetch: async (url, options) => {
      requests.push({ url, ...options });
      if (options.method === "POST") return post();
      if (options.method === "PUT") return put(JSON.parse(options.body));
      return response(url.includes("/logs?") ? { items: [], before: null } : snapshot);
    }
  });
  const event = (id, name) => get(id).listeners.get(name)({ preventDefault() {} });
  return {
    get, event, requests, intervals, snapshot, document,
    setPost: callback => { post = callback; },
    setPut: callback => { put = callback; },
    login: async () => { get("token").value = "fixture-admin-token"; await event("login", "submit"); }
  };
}

test("selected provider executes once, follows activity and unlocks on completion", async () => {
  const app = setup();
  await app.login();
  assert.equal(app.get("dashboard").hidden, false);
  assert.deepEqual([...app.intervals.values()], [30000]);
  app.get("crawl-provider").value = "one";
  app.snapshot.crawlerRunning = true;
  app.snapshot.runs = [{ id: "manual-run", startedAt: app.snapshot.generatedAt, status: "running", titlesUpdated: 0, failures: 0 }];
  await app.event("crawl-form", "submit");
  const posts = app.requests.filter(request => request.method === "POST");
  assert.equal(posts.length, 1);
  assert.deepEqual(JSON.parse(posts[0].body), { providerId: "one", retryFailures: false });
  assert.equal(posts[0].headers.Authorization, "Bearer fixture-admin-token");
  assert.equal(app.get("crawl-start").disabled, true);
  assert.deepEqual([...app.intervals.values()], [5000]);
  await app.event("crawl-form", "submit");
  assert.equal(app.requests.filter(request => request.method === "POST").length, 1);
  app.snapshot.crawlerRunning = false;
  Object.assign(app.snapshot.runs[0], { status: "completed", finishedAt: app.snapshot.generatedAt, titlesUpdated: 2 });
  await app.event("refresh", "click");
  assert.equal(app.get("crawl-start").disabled, false);
  assert.match(app.get("crawl-message").textContent, /2 títulos actualizados/);
  assert.deepEqual([...app.intervals.values()], [30000]);
});

function intervalControls(app, providerId) {
  const descendants = node => [node, ...node.children.flatMap(descendants)];
  const controls = descendants(app.get("providers")).filter(node => node.dataset.intervalProvider === providerId);
  return { input: controls.find(node => node.type === "number"), save: controls.find(node => node.type === "button") };
}

test("interval editor preserves typing through polling and saves only the chosen provider", async () => {
  const app = setup();
  await app.login();
  let { input } = intervalControls(app, "one");
  assert.equal(input.value, "2");
  input.value = "5";
  input.listeners.get("input")();
  app.document.activeElement = input;
  await app.event("refresh", "click");
  assert.equal(intervalControls(app, "one").input, input);
  app.document.activeElement = null;
  await app.event("refresh", "click");
  const controls = intervalControls(app, "one");
  assert.equal(controls.input.value, "5");
  await controls.save.listeners.get("click")();
  const request = app.requests.find(request => request.method === "PUT");
  assert.equal(request.url, "/api/v1/admin/diagnostics/provider-interval");
  assert.deepEqual(JSON.parse(request.body), { providerId: "one", intervalSeconds: 5 });
  assert.equal(request.headers.Authorization, "Bearer fixture-admin-token");
  assert.equal(intervalControls(app, "one").input.value, "5");
  assert.equal(intervalControls(app, "two").input.value, "2");
  assert.match(app.get("interval-message").textContent, /próxima ejecución/);
});

test("invalid intervals are rejected locally and failed saves preserve the draft", async () => {
  const app = setup();
  await app.login();
  let { input, save } = intervalControls(app, "one");
  for (const value of ["", "0", "61", "1.5"]) {
    input.value = value;
    await save.listeners.get("click")();
    assert.match(app.get("interval-message").textContent, /entre 1 y 60/);
  }
  assert.equal(app.requests.filter(request => request.method === "PUT").length, 0);
  input.value = "4"; input.listeners.get("input")();
  app.setPut(async () => response({ message: "No se pudo guardar." }, 400));
  await save.listeners.get("click")();
  assert.equal(save.disabled, false);
  assert.match(app.get("interval-message").textContent, /No se pudo guardar/);
  await app.event("refresh", "click");
  assert.equal(intervalControls(app, "one").input.value, "4");
});

test("interval changes can be saved for the next run while a crawler is active", async () => {
  const app = setup();
  app.snapshot.crawlerRunning = true;
  await app.login();
  const { input, save } = intervalControls(app, "two");
  assert.equal(input.disabled, false);
  input.value = "1"; input.listeners.get("input")();
  await save.listeners.get("click")();
  assert.equal(app.requests.filter(request => request.method === "PUT").length, 1);
  assert.equal(app.get("crawl-start").disabled, true);
});

test("all providers and retry option are sent, server conflict is shown", async () => {
  const app = setup();
  await app.login();
  app.get("crawl-retry").checked = true;
  app.setPost(async () => response({ message: "Ya hay un scraper ejecutándose." }, 409));
  app.snapshot.crawlerRunning = true;
  await app.event("crawl-form", "submit");
  assert.deepEqual(JSON.parse(app.requests.find(request => request.method === "POST").body), { providerId: null, retryFailures: true });
  assert.match(app.get("crawl-message").textContent, /Ya hay un scraper/);
  assert.equal(app.get("crawl-start").disabled, true);
});

test("logging out while a start request is pending does not resurrect the session", async () => {
  const app = setup();
  await app.login();
  let finish;
  app.setPost(() => new Promise(resolve => { finish = resolve; }));
  const starting = app.event("crawl-form", "submit");
  assert.equal(app.get("crawl-start").disabled, true);
  app.event("logout", "click");
  finish(response({ status: "accepted", runId: "manual-run", message: "Started" }, 202));
  await starting;
  assert.equal(app.get("dashboard").hidden, true);
  assert.equal(app.get("signin").hidden, false);
  assert.equal(app.get("crawl-message").textContent, "");
  assert.equal(app.intervals.size, 0);
});
