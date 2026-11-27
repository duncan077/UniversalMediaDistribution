import assert from "node:assert/strict";
import {readFileSync} from "node:fs";
import {runInNewContext} from "node:vm";
import test from "node:test";

const script = readFileSync(new URL("../src-tauri/player-assets/provider-media.js",import.meta.url),"utf8");
test("a same-origin video reports transport changes between samples and clamps seeks", async () => {
  const video = Object.assign(new EventTarget(), {
    currentTime: 3, duration: 60, paused:true, ended:false, seeking:false, readyState:4,
    getBoundingClientRect: () => ({width:640,height:360}),
    seekable:{length:1,start:()=>0,end:()=>60},
    async play(){this.paused=false;this.dispatchEvent(new Event("play"));},
    pause(){this.paused=true;this.dispatchEvent(new Event("pause"));}
  });
  const doc = (videos, frames) => ({querySelectorAll:s=>s==="iframe"?frames:videos});
  const inaccessible={get contentDocument(){throw new Error("cross-origin");}};
  const sample=runInNewContext(script,{document:doc([], [inaccessible,{contentDocument:doc([video],[])}])});
  assert.equal(sample("read").position,3);
  await video.play();
  video.pause();
  const result=sample("read");
  assert.equal(result.plays,1);
  assert.equal(result.pauses,1);
  assert.equal(result.paused,true);
  assert.equal(sample("seek",200).position,60);
  assert.equal(runInNewContext(script,{document:doc([], [inaccessible])})("read"),null);
});

test("public handoff only offers ordinary HTTPS media", () => {
  const video = {readyState: 4, paused: false, currentTime: 12, getBoundingClientRect: () => ({width:640,height:360})};
  const doc = {
    URL: "https://player.example.org/embed?tracking=example",
    defaultView: {location: new URL("https://player.example.org/embed"), performance: {getEntriesByType: () => []}},
    querySelectorAll: selector => selector === "iframe" ? [] : [video],
  };
  video.ownerDocument = doc;
  const window = {};
  const sample = runInNewContext(script, {document:doc, URL, self:window, top:window});
  video.currentSrc = "https://media.example.org/movie.mp4";
  const source = sample("public-source");
  assert.deepEqual([...source.addresses], [video.currentSrc]);
  assert.equal(source.page, "https://player.example.org/embed");
  assert.equal(source.position, 12);
  video.mediaKeys = {};
  assert.equal(sample("public-source"),null);
  video.mediaKeys = null;
  for (const url of ["blob:https://example.org/id","http://example.org/movie.mp4","https://u:p@example.org/movie.mp4"]) {
    video.currentSrc = url;
    assert.equal(sample("public-source"),null);
  }
});

test("handoff recognizes the active JW Player HLS source without a file extension", () => {
  const file = "https://media.example.org/?s=fixture";
  const fallback = "https://media.example.org/master.m3u8";
  const selected = {file, type: "hls"};
  const owner = {
    location: new URL("https://nupload.my/watch/fixture"),
    jwplayer(id) {
      assert.equal(id, "player");
      return {getPlaylistItem: () => ({sources: [selected], file: fallback})};
    },
    performance: {getEntriesByType: () => [
      {initiatorType: "xmlhttprequest", name: file},
      {initiatorType: "xmlhttprequest", name: fallback},
    ]},
  };
  const video = {
    currentSrc: "blob:https://nupload.my/fixture", currentTime: 12, readyState: 4,
    paused: false, closest: () => ({id: "player"}),
    getBoundingClientRect: () => ({width: 640, height: 360}),
  };
  const doc = {
    URL: owner.location.href, defaultView: owner,
    querySelectorAll: selector => selector === "iframe" ? [] : [video],
  };
  video.ownerDocument = doc;
  const window = {};
  const sample = runInNewContext(script, {document: doc, URL, self: window, top: window});
  assert.deepEqual([...sample("public-source").addresses], [file, fallback]);
  assert.deepEqual([...sample("frame-source").addresses], [file, fallback]);
  selected.file = "/stream?id=x";
  assert.equal(sample("public-source").addresses[0], "https://nupload.my/stream?id=x");
  doc.baseURI = "https://media.example.org/playlist/";
  selected.file = "stream?id=x";
  assert.equal(sample("frame-source").addresses[0], "https://media.example.org/playlist/stream?id=x");
  selected.file = "//user:password@media.example.org/file";
  assert.deepEqual([...sample("public-source").addresses], [fallback]);
  selected.drm = {widevine: {url: "https://license.example.org"}};
  assert.equal(sample("public-source"), null);
});
