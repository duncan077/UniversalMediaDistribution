import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const source = readFileSync(new URL('../src/UniversalMediaDistribution.Catalog/ContentSources/VideoChallengeCapture.cs', import.meta.url), 'utf8');
const script = source.match(/public const string Script = """([\s\S]*?)""";/)[1];

function player() {
  const posts = [], intervals = [], listeners = {}, requests = [];
  const video = { paused: true, readyState: 4, currentTime: 2, videoWidth: 1920, mediaKeys: null, currentSrc: 'blob:https://player.example/stream' };
  const window = { fetch(...args) { requests.push([this, ...args]); return Promise.resolve('response'); },
    chrome: { webview: { postMessage(value) { posts.push(JSON.parse(value)); } } },
    addEventListener(name, fn) { listeners[name] = fn; } };
  window.parent = window;
  const frame = { contentWindow: {} };
  function Xhr() {}
  Xhr.prototype.open = function (...args) { requests.push([this, ...args]); };
  const context = { window, document: { querySelectorAll(kind) { return kind === 'video' ? [video] : [frame]; } },
    location: { href: 'https://player.example/e/video' }, navigator: { userAgent: 'Browser/Test' },
    URL, XMLHttpRequest: Xhr, performance: { getEntriesByType() { return []; } },
    setInterval(fn) { intervals.push(fn); } };
  vm.runInNewContext(script, context);
  return { ...context, posts, intervals, listeners, requests, video, frame };
}

// No CAPTCHA automation; a fetched ad/manifest with no playing video cannot complete the check.
const p = player();
await p.window.fetch('https://cdn.example/master.m3u8?session=test');
p.intervals[0]();
assert.equal(p.posts.length, 0);
p.video.paused = false;
p.intervals[0]();
assert.equal(p.posts[0].url, 'https://cdn.example/master.m3u8?session=test');
assert.equal(p.posts[0].document, 'https://player.example/e/video');
assert.equal(p.posts[0].userAgent, 'Browser/Test');
assert.equal(p.requests[0][0], p.window, 'fetch preserves receiver');
vm.runInNewContext(script, p);
assert.equal(p.intervals.length, 1, 'reinjection is idempotent');

// Active plain MP4 sources take precedence over unrelated HLS resource observations.
p.video.currentSrc = 'https://cdn.example/actual.mp4';
p.intervals[0]();
assert.equal(p.posts.at(-1).url, p.video.currentSrc);

// Encrypted-media sessions are not exported.
p.video.mediaKeys = {};
const before = p.posts.length;
p.intervals[0]();
assert.equal(p.posts.length, before);

// Frame reports must come from an iframe actually hosted in this document.
const envelope = { umdVideoChallenge: true, url: 'https://cdn.example/frame.mp4' };
p.listeners.message({ source: {}, data: { umdVideoChallengeFrame: envelope } });
assert.equal(p.posts.length, before);
p.listeners.message({ source: p.frame.contentWindow, data: { umdVideoChallengeFrame: envelope } });
assert.equal(p.posts.at(-1).url, envelope.url);
console.log('Video challenge observer checks passed.');
