namespace UniversalMediaDistribution.UI.Playback;

/// <summary>
/// Shared timing and document script for the native Windows and Android
/// background browser hosts.
/// </summary>
public static class BackgroundWebVideoPlayback
{
    public static readonly TimeSpan PlaybackDuration = TimeSpan.FromMinutes(3);

    public static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);

    public const string PlayingMessage = "umd:background-video:playing";

    /// <summary>
    /// Starts the largest video in the embed. A direct video is clicked before
    /// <c>play()</c>; if the player has not created one yet, the common play
    /// overlays are clicked until it does. Muting prevents an invisible
    /// background session from producing sound and lets the stock engines
    /// apply their ordinary muted-media autoplay policy.
    /// </summary>
    public const string StartScript =
        """
        (function () {
          const marker = "__umdBackgroundVideoPlayback";
          if (window[marker]) { return; }
          window[marker] = true;

          const send = function (message) {
            try {
              if (window.umdBackgroundVideo &&
                  typeof window.umdBackgroundVideo.postMessage === "function") {
                window.umdBackgroundVideo.postMessage(message);
                return;
              }
              if (typeof window.invokeCSharpAction === "function") {
                window.invokeCSharpAction(message);
                return;
              }
              if (window.chrome && window.chrome.webview &&
                  typeof window.chrome.webview.postMessage === "function") {
                window.chrome.webview.postMessage(message);
              }
            } catch (_) {
            }
          };

          const findVideo = function () {
            let best = null;
            let bestArea = -1;
            const videos = document.querySelectorAll("video");
            for (let index = 0; index < videos.length; index++) {
              const video = videos[index];
              const area = (video.clientWidth || 0) * (video.clientHeight || 0);
              const usable = area > 0 || video.readyState > 0 ||
                (video.currentSrc || video.src);
              if (usable && area > bestArea) {
                best = video;
                bestArea = area;
              }
            }
            return best;
          };

          const overlays = [
            ".vjs-big-play-button",
            ".jw-icon-display",
            ".plyr__control--overlaid",
            "button[aria-label*='play' i]",
            "[role='button'][aria-label*='play' i]",
            "button.play",
            ".play-button",
            "#play-button"
          ];

          let reported = false;
          let bound = null;
          const reportPlaying = function () {
            if (reported) { return; }
            reported = true;
            send("umd:background-video:playing");
          };

          const bindAndStart = function (video) {
            if (!video) { return false; }
            if (video !== bound) {
              bound = video;
              video.addEventListener("playing", reportPlaying, true);
            }
            video.muted = true;
            if (!video.paused && !video.ended && video.readyState >= 2) {
              reportPlaying();
              return true;
            }
            try { video.click(); } catch (_) { }
            try {
              const played = video.play();
              if (played && typeof played.catch === "function") {
                played.catch(function () { });
              }
            } catch (_) {
            }
            return false;
          };

          let attempts = 0;
          const start = function () {
            if (reported) { return; }
            attempts++;
            const video = findVideo();
            bindAndStart(video);
            if (!video || (video.paused && !video.ended)) {
              for (let index = 0; index < overlays.length; index++) {
                const candidate = document.querySelector(overlays[index]);
                if (!candidate) { continue; }
                try { candidate.click(); } catch (_) { }
                break;
              }
            }
            if (attempts < 60) { window.setTimeout(start, 500); }
          };

          start();
        })();
        """;

    public static void ValidateSource(Uri source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.IsAbsoluteUri ||
            source.Scheme is not ("http" or "https") ||
            source.UserInfo.Length > 0)
        {
            throw new ArgumentException(
                "A background video source must be an absolute HTTP or HTTPS URL without embedded credentials.",
                nameof(source));
        }
    }
}
