const assert = require('node:assert/strict');
const { test } = require('node:test');
const vm = require('node:vm');
const payloads = JSON.parse(require('node:fs').readFileSync(0, 'utf8'));

function page(withRoot = true) {
  const events = [];
  const observers = [];
  const calls = [];
  class Element {
    constructor(tagName = 'DIV') {
      this.tagName = tagName;
      this.children = [];
      this.style = { setProperty() {} };
      this.attributes = {};
    }
    appendChild(child) { this.children.push(child); }
    querySelectorAll() { return []; }
    setAttribute(name, value) { this.attributes[name] = value; }
    setAttributeNS(namespace, name, value) { this.attributes[name] = value; }
  }
  const elementType = tagName => class extends Element {
    constructor() { super(tagName); }
    get src() { return this.attributes.src; }
    set src(value) { this.attributes.src = value; }
  };
  class Observer {
    constructor(callback) { this.callback = callback; observers.push(this); }
    observe(target) { assert.ok(target, 'cannot observe a missing root'); this.target = target; }
    disconnect() { this.disconnected = true; }
  }
  class Xhr {
    open(method, url) { this.url = url; }
    send() { calls.push(['xhr', this.url]); }
    abort() { this.aborted = true; }
  }
  const document = {
    head: null,
    documentElement: withRoot ? new Element('HTML') : null,
    readyState: 'loading',
    baseURI: 'https://player.example/embed',
    createElement: tag => new Element(tag.toUpperCase()),
    querySelectorAll: () => [],
    addEventListener: (type, callback) => events.push({ type, callback, document: true })
  };
  const context = vm.createContext({
    document, Element, HTMLElement: Element, MutationObserver: Observer,
    HTMLScriptElement: elementType('SCRIPT'), HTMLImageElement: elementType('IMG'),
    HTMLIFrameElement: elementType('IFRAME'), XMLHttpRequest: Xhr,
    navigator: { sendBeacon(url) { calls.push(['beacon', url]); return true; } },
    location: new URL(document.baseURI), URL, performance: { now: () => 0 },
    requestAnimationFrame: callback => callback(),
    fetch: async input => { calls.push(['fetch', input]); return 'loaded'; },
    Worker: class { constructor(url) { calls.push(['worker', url]); } },
    WebSocket: class { constructor(url) { calls.push(['websocket', url]); } },
    addEventListener: (type, callback) => events.push({ type, callback }),
    open: () => 'popup'
  });
  vm.runInContext('window = globalThis; self = globalThis; top = globalThis;', context);
  return { context, events, observers, calls, document, Element };
}

test('document-start payload installs defenses before the root exists', () => {
  const p = page(false);
  vm.runInContext(payloads.cosmetic, p.context);
  assert.ok(p.context.__umdFilterRuntime);
  assert.equal(p.context.open(), null);
  assert.ok(p.events.some(e => e.type === 'click' && !e.document));
  assert.ok(p.events.some(e => e.type === 'error'));
  const watcher = p.observers.find(o => o.target === p.document);
  assert.ok(watcher);
  p.document.documentElement = new p.Element('HTML');
  watcher.callback();
  assert.equal(watcher.disconnected, true);
  assert.match(p.document.documentElement.children[0].textContent, /\.advertisement/);
  p.events.find(e => e.type === 'DOMContentLoaded').callback();
  assert.equal(p.document.documentElement.children.length, 1);
});

test('an existing root gets styles immediately and reinjection is idempotent', () => {
  const p = page();
  vm.runInContext(payloads.cosmetic, p.context);
  const count = p.events.length;
  vm.runInContext(payloads.cosmetic, p.context);
  assert.equal(p.events.length, count);
  assert.equal(p.document.documentElement.children.length, 1);
});

test('request-only fallback does not install cosmetic or popup defenses', () => {
  const p = page(false);
  vm.runInContext(payloads.requests, p.context);
  assert.ok(p.context.__umdEmbeddedRequestBlockerV1);
  assert.equal(p.observers.length, 0);
  assert.equal(p.events.length, 0);
  assert.equal(p.context.open(), 'popup');
  const wrapped = p.context.fetch;
  vm.runInContext(payloads.requests, p.context);
  assert.equal(p.context.fetch, wrapped);
  vm.runInContext(payloads.cosmetic, p.context);
  assert.equal(p.context.open(), null);
});

test('fetch blocks ads, including same-site paths and misleading queries', async () => {
  const p = page();
  vm.runInContext(payloads.requests, p.context);
  for (const url of [
    'https://doubleclick.net/tag.js',
    'https://doubleclick.net/tag.js?return=movie.mp4',
    'https://doubleclick.net/tag.js?x=.ts',
    'https://doubleclick.net/tag.js#movie.m3u8',
    'https://doubleclick.net/movie.mp4.js',
    'https://doubleclick.net/hls/advert.js',
    'https://doubleclick.net/playlist-advert',
    'https://ads.player.example/tag.js',
    '/pagead/ad.js'
  ]) {
    await assert.rejects(p.context.fetch(url), /Blocked/);
  }
  assert.equal(p.calls.length, 0);
  for (const url of [
    '/player.js',
    'https://doubleclick.net/hls/master.m3u8?token=abc',
    'https://doubleclick.net/segments/00042',
    'https://doubleclick.net/VIDEO.MP4'
  ]) {
    assert.equal(await p.context.fetch(url), 'loaded');
  }
});

test('XHR and beacons block requests before sending', () => {
  const p = page();
  vm.runInContext(payloads.requests, p.context);
  const blocked = new p.context.XMLHttpRequest();
  blocked.open('GET', 'https://doubleclick.net/tag.js?return=video.mp4');
  blocked.send();
  assert.equal(blocked.aborted, true);
  p.context.navigator.sendBeacon('https://doubleclick.net/beacon.mp4');
  assert.equal(p.calls.length, 0);
  const media = new p.context.XMLHttpRequest();
  media.open('GET', 'https://doubleclick.net/video.m3u8');
  media.send();
  assert.equal(p.calls.length, 1);
});

test('DOM setters and workers cannot claim a media resource type', () => {
  const p = page();
  vm.runInContext(payloads.requests, p.context);
  const script = new p.context.HTMLScriptElement();
  script.src = 'https://doubleclick.net/advert.mp4';
  script.setAttribute('src', 'https://doubleclick.net/tag.js?return=video.mp4');
  script.setAttributeNS(null, 'src', 'https://doubleclick.net/advert.mp4');
  assert.equal(script.src, undefined);
  script.src = 'https://player.example/player.js';
  assert.equal(script.src, 'https://player.example/player.js');
  assert.throws(() => new p.context.Worker('https://doubleclick.net/advert.mp4'), /Blocked/);
  assert.throws(() => new p.context.WebSocket('wss://doubleclick.net/collect'), /Blocked/);
  assert.throws(() => new p.context.WebSocket('wss://ads.player.example/collect'), /Blocked/);
  assert.throws(() => new p.context.WebSocket('wss://player.example/pagead/collect'), /Blocked/);
});
