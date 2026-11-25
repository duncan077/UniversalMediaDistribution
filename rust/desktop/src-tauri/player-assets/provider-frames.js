(function (readMedia) {
  const channel = "umd:provider-media:v1";
  if (self !== top) {
    function report() {
      // Read each frame inside its own origin. No cookies or request headers are collected.
      const sample = readMedia("frame-source", 0);
      if (sample?.addresses.length) top.postMessage({channel, sample}, "*");
    }
    addEventListener("playing", report, true);
    setInterval(report, 1500);
    return;
  }

  const samples = new Map();
  function descendants() {
    const found = [self];
    for (let i = 0; i < found.length && found.length < 24; i++) {
      try {
        const parent = found[i];
        for (let j = 0; j < parent.length && found.length < 24; j++) found.push(parent[j]);
      } catch {}
    }
    return found.slice(1);
  }
  addEventListener("message", event => {
    if (event.data?.channel !== channel || !event.origin.startsWith("https://")) return;
    const {sample} = event.data;
    if (!sample || typeof sample.page !== "string" || sample.page.length > 16_384
        || !Array.isArray(sample.addresses) || sample.addresses.length > 4
        || !descendants().includes(event.source)) return;
    try { if (new URL(sample.page).origin !== event.origin) return; } catch { return; }
    const now = Date.now();
    for (const [source, value] of samples) if (now - value.time > 4000) samples.delete(source);
    samples.delete(event.source);
    if (samples.size < 24) samples.set(event.source, {time: now, sample});
  });
  Object.defineProperty(self, Symbol.for(channel), {value() {
    const frames = descendants(), now = Date.now();
    return [...samples].reverse().find(([frame, value]) => now - value.time <= 4000 && frames.includes(frame))?.[1].sample ?? null;
  }});
})
