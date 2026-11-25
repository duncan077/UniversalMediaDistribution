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
