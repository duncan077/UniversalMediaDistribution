using System.Globalization;

namespace UniversalMediaDistribution.UI.Playback;

/// <summary>
/// What the embedded document last said its own player is doing.
/// </summary>
internal readonly record struct EmbeddedWebPlaybackReading(
    double PositionSeconds,
    double? DurationSeconds,
    bool IsPlaying,
    bool IsEnded,
    bool IsBuffering,
    DateTimeOffset ObservedAt);

/// <summary>
/// Reads and drives the <c>video</c> element inside an embedded third-party
/// page, so a watch party can include a viewer watching on a hoster's own
/// player rather than a direct stream.
/// <para>
/// The document pushes its state rather than being polled: script return
/// values are the least consistent thing across the platform web views, while
/// the page-to-host message channel is the one the fullscreen bridge already
/// depends on. Pushing also means a pause reaches the party at the moment it
/// happens instead of on the next two-second sample.
/// </para>
/// <para>
/// A video inside a cross-origin frame stays invisible to this: the browser
/// forbids reaching into it, and no injection can lift that. Such a page
/// simply never reports, which leaves the player declaring no transport
/// control — the honest answer, and the one the watch-party notice explains.
/// </para>
/// </summary>
internal sealed class EmbeddedWebPlaybackBridge
{
    internal const string MessagePrefix = "umd:embedded-playback:";

    /// <summary>
    /// How long a reading stays trustworthy. The document reports on every
    /// player event and on a one-second heartbeat, so silence for several
    /// seconds means the page navigated, stalled its scripting, or never had
    /// a reachable video.
    /// </summary>
    private static readonly TimeSpan ReadingLifetime =
        TimeSpan.FromSeconds(6);

    private readonly TimeProvider _timeProvider;
    private readonly Lock _gate = new();
    private EmbeddedWebPlaybackReading? _reading;

    public EmbeddedWebPlaybackBridge(TimeProvider? timeProvider = null) =>
        _timeProvider = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// Installs the reporter and keeps it pointed at the page's current video
    /// element. Hoster pages swap their player out during setup, so the
    /// observer re-scans rather than binding once.
    /// </summary>
    public const string ObserverScript =
        """
        (function () {
          const marker = "__umdEmbeddedPlaybackBridge";
          if (window[marker]) { return; }
          window[marker] = true;

          const send = function (message) {
            try {
              if (typeof window.invokeCSharpAction === "function") {
                window.invokeCSharpAction(message);
              }
            } catch (_) {
            }
          };

          // The largest video wins: hoster pages keep hidden preview and
          // advertisement elements alongside the one being watched.
          const findVideo = function () {
            let best = null;
            let bestArea = -1;
            const videos = document.querySelectorAll("video");
            for (let index = 0; index < videos.length; index++) {
              const video = videos[index];
              const area = (video.clientWidth || 0) * (video.clientHeight || 0);
              const usable = area > 0 || (video.duration || 0) > 0;
              if (usable && area > bestArea) {
                best = video;
                bestArea = area;
              }
            }
            return best;
          };

          let bound = null;
          const report = function () {
            const video = bound;
            if (!video) { return; }
            const duration =
              isFinite(video.duration) && video.duration > 0
                ? video.duration
                : -1;
            // readyState below HAVE_FUTURE_DATA while playing is the stall
            // the party has to wait for rather than read as a pause.
            const buffering =
              !video.paused && !video.ended && video.readyState < 3;
            send(
              "umd:embedded-playback:" +
              (video.currentTime || 0).toFixed(3) + "|" +
              duration.toFixed(3) + "|" +
              (video.paused ? "1" : "0") + "|" +
              (video.ended ? "1" : "0") + "|" +
              (buffering ? "1" : "0"));
          };

          const bind = function (video) {
            if (!video || video === bound) { return; }
            bound = video;
            const events = [
              "play",
              "playing",
              "pause",
              "seeked",
              "waiting",
              "stalled",
              "ended",
              "timeupdate",
              "durationchange"
            ];
            for (let index = 0; index < events.length; index++) {
              video.addEventListener(events[index], report, true);
            }
            report();
          };

          // Rebinding covers a player created after load and one replaced
          // when the page switches quality or source.
          window.setInterval(function () {
            const video = findVideo();
            if (video && video !== bound) {
              bind(video);
            } else {
              report();
            }
          }, 1000);
          bind(findVideo());
        })();
        """;

    /// <summary>
    /// Whether the document has recently reported a video this can drive.
    /// </summary>
    public bool CanControl => TryRead() is not null;

    public EmbeddedWebPlaybackReading? TryRead()
    {
        lock (_gate)
        {
            if (_reading is not { } reading)
            {
                return null;
            }

            return _timeProvider.GetUtcNow() - reading.ObservedAt >
                ReadingLifetime
                ? null
                : reading;
        }
    }

    /// <summary>
    /// Takes one message from the document. Returns whether it was one of
    /// ours, so the caller can pass anything else to the other bridges.
    /// </summary>
    public bool Accept(string? message)
    {
        if (message is null ||
            !message.StartsWith(MessagePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var parts = message[MessagePrefix.Length..].Split('|');
        if (parts.Length != 5 ||
            !double.TryParse(
                parts[0],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var position) ||
            !double.TryParse(
                parts[1],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var duration))
        {
            // A malformed report is dropped rather than aging out the last
            // good one: the next heartbeat is a second away.
            return true;
        }

        lock (_gate)
        {
            _reading = new EmbeddedWebPlaybackReading(
                Math.Max(0, position),
                duration > 0 ? duration : null,
                IsPlaying: parts[2] is "0",
                IsEnded: parts[3] is "1",
                IsBuffering: parts[4] is "1",
                _timeProvider.GetUtcNow());
        }

        return true;
    }

    public void Reset()
    {
        lock (_gate)
        {
            _reading = null;
        }
    }

    /// <summary>
    /// Resumes or pauses the page's own player.
    /// </summary>
    public static string CreateSetPlayingScript(bool playing) =>
        BuildCommandScript(
            playing
                ? """
                  const played = video.play();
                  if (played && typeof played.catch === "function") {
                    played.catch(function () { });
                  }
                  """
                : "video.pause();");

    /// <summary>
    /// Moves the page's own player. Seeking past a point the page has not
    /// buffered is the page's problem to solve, exactly as it is when the
    /// viewer drags its own scrub bar.
    /// </summary>
    public static string CreateSeekScript(double positionSeconds) =>
        BuildCommandScript(
            "video.currentTime = " +
            Math.Max(0, positionSeconds).ToString(
                "0.###",
                CultureInfo.InvariantCulture) +
            ";");

    /// <summary>
    /// Wraps a command in the same largest-video search the reporter uses, so
    /// a command and a reading always mean the same element.
    /// </summary>
    private static string BuildCommandScript(string body) =>
        $$"""
        (function () {
          try {
            let best = null;
            let bestArea = -1;
            const videos = document.querySelectorAll("video");
            for (let index = 0; index < videos.length; index++) {
              const video = videos[index];
              const area = (video.clientWidth || 0) * (video.clientHeight || 0);
              const usable = area > 0 || (video.duration || 0) > 0;
              if (usable && area > bestArea) {
                best = video;
                bestArea = area;
              }
            }
            const video = best;
            if (!video) { return; }
            {{body}}
          } catch (_) {
          }
        })();
        """;
}
