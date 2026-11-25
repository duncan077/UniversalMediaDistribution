(function (action, position) {
  // Cross-origin frames are intentionally left to the browser's origin policy.
  const documents = [document];
  let best = null;
  let score = -1;
  for (let i = 0; i < documents.length && i < 24; i++) {
    const doc = documents[i];
    for (const video of doc.querySelectorAll("video, audio")) {
      const rect = video.getBoundingClientRect();
      const rank = (video.readyState ? 1e12 : 0) + (!video.paused ? 1e11 : 0) + rect.width * rect.height;
      if (rank > score) { best = video; score = rank; }
    }
    for (const frame of doc.querySelectorAll("iframe")) {
      try { if (frame.contentDocument && documents.length < 24) documents.push(frame.contentDocument); } catch {}
    }
  }
  const frameSample = () => self === top ? self[Symbol.for("umd:provider-media:v1")]?.() ?? null : null;
  if (!best || best.readyState < 1) return action === "public-source" ? frameSample() : null;
  function publicCandidates() {
    if (!best.currentSrc || best.mediaKeys) return [];
    const owner = best.ownerDocument.defaultView;
    const host = owner?.location?.hostname ?? "";
    if (/(^|\.)mega\.(nz|co\.nz)$/i.test(host)) return [];
    const secure = value => {
      if (typeof value !== "string" || value.length > 16_384) return null;
      try {
        const url = new URL(value);
        if (url.protocol === "https:" && !url.username && !url.password) return url.href;
      } catch {}
      return null;
    };
    const direct = secure(best.currentSrc);
    if (direct) return [direct];
    if (!best.currentSrc.startsWith("blob:")) return [];
    try {
      const entries = owner.performance.getEntriesByType("resource");
      const manifests = entries.slice(-160)
        .filter(entry => ["fetch", "xmlhttprequest", "video"].includes(entry.initiatorType))
        .map(entry => secure(entry.name))
        .filter(url => {
          if (!url) return false;
          const path = new URL(url).pathname;
          return /\.(?:m3u8|mpd)$/i.test(path) && !/(?:^|[/_-])(?:audio|subtitles?|captions?)(?:[/_.-]|$)/i.test(path);
        });
      const rank = url => /(?:^|[/_-])(?:master|manifest|index)(?:[/_.-]|$)/i.test(new URL(url).pathname) ? 0 : 1;
      return [...new Set(manifests)].sort((a, b) => rank(a) - rank(b)).slice(0, 4);
    } catch { return []; }
  }
  if (action === "public-source" || action === "frame-source") {
    if (action === "frame-source" && (best.paused || best.ended || best.readyState < 2)) return null;
    const page = new URL(best.ownerDocument.URL);
    page.hash = page.search = "";
    const addresses = publicCandidates();
    if (addresses.length && page.href.length <= 16_384) {
      return { page: page.href, addresses, position: best.currentTime, paused: best.paused };
    }
    return action === "public-source" && !best.mediaKeys ? frameSample() : null;
  }
  const key = Symbol.for("umd.provider.transport.v1");
  if (!best[key]) {
    const counts = {play:0,pause:0,seeked:0};
    Object.defineProperty(best,key,{value:counts});
    for (const event of Object.keys(counts)) best.addEventListener(event,()=>{counts[event]=(counts[event]+1)>>>0;});
  }
  if (action === "seek" && best.seekable.length) {
    const start = best.seekable.start(0), end = best.seekable.end(best.seekable.length - 1);
    best.currentTime = Math.min(end, Math.max(start, position));
  } else if (action === "play") best.play().catch(() => {});
  else if (action === "pause") best.pause();
  return { position: best.currentTime, duration: Number.isFinite(best.duration) ? best.duration : null,
    paused: best.paused, ended: best.ended, ready: best.readyState, seeking: best.seeking,
    plays:best[key].play, pauses:best[key].pause, seeks:best[key].seeked, seekable: best.seekable.length > 0 };
})
