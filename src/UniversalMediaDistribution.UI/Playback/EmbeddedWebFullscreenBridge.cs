namespace UniversalMediaDistribution.UI.Playback;

/// <summary>
/// The state changes reported by the document-side fullscreen observer.
/// </summary>
internal enum EmbeddedWebFullscreenTransition
{
    None,
    Entered,
    Exited,
    ExitRequested
}

/// <summary>
/// Synchronizes an embedded document's HTML fullscreen state with the native
/// application shell. Native web views own their keyboard focus, so Escape
/// pressed inside one does not necessarily reach Avalonia's key routing.
/// </summary>
internal sealed class EmbeddedWebFullscreenBridge
{
    private const string EnteredMessage =
        "umd:embedded-fullscreen:entered";
    private const string ExitedMessage =
        "umd:embedded-fullscreen:exited";
    private const string ExitRequestedMessage =
        "umd:embedded-fullscreen:exit-requested";

    public const string ObserverScript =
        """
        (function () {
          const marker = "__umdEmbeddedFullscreenBridge";
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
          const isFullscreen = function () {
            return !!(
              document.fullscreenElement ||
              document.webkitFullscreenElement
            );
          };
          const report = function () {
            send(
              isFullscreen()
                ? "umd:embedded-fullscreen:entered"
                : "umd:embedded-fullscreen:exited"
            );
          };

          document.addEventListener("fullscreenchange", report, true);
          document.addEventListener("webkitfullscreenchange", report, true);
          document.addEventListener("keydown", function (event) {
            if (event.key === "Escape" || event.key === "Esc") {
              send("umd:embedded-fullscreen:exit-requested");
            }
          }, true);
          report();
        })();
        """;

    public const string ExitScript =
        """
        (function () {
          try {
            const exit =
              document.exitFullscreen ||
              document.webkitExitFullscreen;
            if (typeof exit !== "function") { return; }
            const result = exit.call(document);
            if (result && typeof result.catch === "function") {
              result.catch(function () {});
            }
          } catch (_) {
          }
        })();
        """;

    public bool IsDocumentFullscreen { get; private set; }

    public EmbeddedWebFullscreenTransition Accept(string? message)
    {
        switch (message)
        {
            case EnteredMessage when !IsDocumentFullscreen:
                IsDocumentFullscreen = true;
                return EmbeddedWebFullscreenTransition.Entered;

            case ExitedMessage when IsDocumentFullscreen:
                IsDocumentFullscreen = false;
                return EmbeddedWebFullscreenTransition.Exited;

            case ExitRequestedMessage:
                return EmbeddedWebFullscreenTransition.ExitRequested;

            default:
                return EmbeddedWebFullscreenTransition.None;
        }
    }

    public void Reset() => IsDocumentFullscreen = false;
}
