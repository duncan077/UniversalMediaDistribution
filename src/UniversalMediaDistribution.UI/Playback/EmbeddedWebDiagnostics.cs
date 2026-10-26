namespace UniversalMediaDistribution.UI.Playback;

/// <summary>
/// Opt-in diagnostics for the embedded player surface.
/// </summary>
public static class EmbeddedWebDiagnostics
{
    /// <summary>
    /// Set <c>UMD_EMBEDDED_WEB_DEVTOOLS=1</c> to inspect the embedded page
    /// with the web engine's own developer tools.
    ///
    /// This is the only practical way to see what a hoster page is really
    /// doing: which host drew an advertisement, whether it was a request at
    /// all, and what the markup is. It is an environment variable rather than
    /// a setting because it is a developer affordance, and off by default
    /// because a developer-tools window on top of a video is not something a
    /// viewer should ever meet by accident.
    /// </summary>
    public static bool IsDevToolsRequested =>
        Environment.GetEnvironmentVariable("UMD_EMBEDDED_WEB_DEVTOOLS")
            is "1" or "true" or "TRUE";

    /// <summary>
    /// The embedded player uses a private web-view profile so that nothing
    /// cached survives the session to be served behind the filter's back.
    ///
    /// Set <c>UMD_EMBEDDED_WEB_PRIVATE=0</c> to turn that off. It exists as an
    /// escape hatch because the private profile changes how the web engine's
    /// environment is created, and an environment is rebuilt every time the
    /// player control is reparented — so if switching servers misbehaves, this
    /// is the first thing to rule in or out.
    /// </summary>
    public static bool IsPrivateProfileEnabled =>
        Environment.GetEnvironmentVariable("UMD_EMBEDDED_WEB_PRIVATE")
            is not ("0" or "false" or "FALSE");

    /// <summary>
    /// Set <c>UMD_EMBEDDED_WEB_REQUEST_LOG</c> to a file path to record every
    /// request the filter is asked about, with its verdict.
    ///
    /// This exists because the counts alone cannot settle the one question
    /// that matters when advertisements survive: whether a request reached the
    /// filter at all. A capture from the web engine says what the page loaded;
    /// this says what the filter was offered. Comparing the two is the only
    /// way to tell a rule that did not match from a request that was never
    /// seen — and those have completely different fixes.
    /// </summary>
    public static string? RequestLogPath { get; } =
        Environment.GetEnvironmentVariable("UMD_EMBEDDED_WEB_REQUEST_LOG")
            is { Length: > 0 } path
            ? path
            : null;

    private static readonly Lock LogGate = new();
    private static StreamWriter? _writer;

    /// <summary>
    /// Writes through one open handle rather than opening and closing the file
    /// per line. This runs on the thread the page is loading on, once per
    /// request, and a page playing video makes a request every few seconds for
    /// as long as it plays — a file open per line is not something to put
    /// there, even for a diagnostic.
    /// </summary>
    public static void LogRequest(
        string verdict,
        string reason,
        ReadOnlySpan<char> url)
    {
        if (RequestLogPath is not { } path)
        {
            return;
        }

        var line = $"{DateTime.Now:HH:mm:ss.fff} {verdict,-8} {reason,-18} {url}";
        try
        {
            lock (LogGate)
            {
                _writer ??= new StreamWriter(path, append: true)
                {
                    AutoFlush = true
                };
                _writer.WriteLine(line);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // A diagnostic that cannot write is not a reason to fail a request.
        }
    }

    public static void LogNote(string note)
    {
        if (RequestLogPath is not null)
        {
            LogRequest("--", "note", note);
        }
    }

    private static int _watchdogStarted;

    /// <summary>
    /// Watches the user-interface thread and records when it stops answering.
    ///
    /// Windows reports a stalled thread as AppHangB1 and kills the process,
    /// which leaves nothing behind to look at. This posts a ping from a
    /// background thread and notes how long the answer took, so the log's last
    /// lines say what the thread was doing when it stopped — the request or
    /// the navigation immediately before the stall is the operation that
    /// caused it.
    ///
    /// Only runs when request logging is switched on.
    /// </summary>
    public static void StartUiWatchdog(Func<bool> isUiResponsive)
    {
        ArgumentNullException.ThrowIfNull(isUiResponsive);
        if (RequestLogPath is null ||
            Interlocked.Exchange(ref _watchdogStarted, 1) == 1)
        {
            return;
        }

        var thread = new Thread(() =>
        {
            var stalledSince = 0;
            while (true)
            {
                Thread.Sleep(1000);
                if (isUiResponsive())
                {
                    if (stalledSince > 0)
                    {
                        LogNote(
                            "UI THREAD RESPONSIVE AGAIN after " +
                            $"{stalledSince} s");
                        stalledSince = 0;
                    }

                    continue;
                }

                stalledSince++;
                if (stalledSince is 2 or 5 or 10 or 20 or 40)
                {
                    LogNote($"UI THREAD UNRESPONSIVE for {stalledSince} s");
                }
            }
        })
        {
            IsBackground = true,
            Name = "umd-ui-watchdog",
            Priority = ThreadPriority.AboveNormal
        };
        thread.Start();
    }
}
