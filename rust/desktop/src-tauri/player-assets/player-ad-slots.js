// Only installed in the isolated provider player.
(() => {
  function apply() {
    const style = document.createElement("style");
    style.textContent = `
      ins.adsbygoogle, [data-ad-slot], [data-ad-client][data-ad-format],
      iframe[id^="google_ads_iframe_"], div[id^="div-gpt-ad-"],
      .ad-banner, .ad-container[data-ad-unit], .advertisement[data-ad-id] {
        display: none !important;
      }`;
    (document.head || document.documentElement).append(style);
  }
  if (document.documentElement) apply();
  else document.addEventListener("DOMContentLoaded", apply, {once:true});
})();
