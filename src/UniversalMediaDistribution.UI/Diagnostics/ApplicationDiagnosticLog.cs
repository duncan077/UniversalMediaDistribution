using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using UniversalMediaDistribution.UI.Composition;

namespace UniversalMediaDistribution.UI.Diagnostics;

/// <summary>
/// A small, bounded support log for information needed to diagnose failures.
/// Successful requests, catalog items and routine status changes are
/// deliberately excluded.
/// </summary>
public static class ApplicationDiagnosticLog
{
    private const string LogDirectoryName = "logs";
    private const string LogFileName = "application.log";

    private static readonly ApplicationDiagnosticTraceListener TraceListener =
        new(Write);
    private static BoundedDiagnosticFileWriter? _writer;
    private static int _infrastructureAttached;
    private static int _sessionEnded;

    public static string? FilePath =>
        Volatile.Read(ref _writer)?.FilePath;

    public static void Initialize(
        string applicationDataDirectory,
        RuntimeProfile runtimeProfile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(
            applicationDataDirectory);
        ArgumentNullException.ThrowIfNull(runtimeProfile);

        try
        {
            var logPath = Path.Combine(
                Path.GetFullPath(applicationDataDirectory),
                LogDirectoryName,
                LogFileName);
            var writer = new BoundedDiagnosticFileWriter(logPath);
            Volatile.Write(ref _writer, writer);
            Interlocked.Exchange(ref _sessionEnded, 0);
            AttachProcessInfrastructure();
            writer.Write(
                DiagnosticLogLevel.Information,
                "app.start",
                BuildApplicationInformation(runtimeProfile),
                suppressRepeats: false);
        }
        catch (Exception exception)
            when (exception is not OutOfMemoryException)
        {
            // Diagnostics must never prevent the application from starting.
        }
    }

    public static void Information(string eventName, string message) =>
        Write(DiagnosticLogLevel.Information, eventName, message);

    public static void Warning(string eventName, string message) =>
        Write(DiagnosticLogLevel.Warning, eventName, message);

    public static void Error(string eventName, string message) =>
        Write(DiagnosticLogLevel.Error, eventName, message);

    public static void Error(
        string eventName,
        string context,
        Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Write(
            DiagnosticLogLevel.Error,
            eventName,
            $"{context}{Environment.NewLine}{exception}");
    }

    public static void EndSession(string outcome = "clean")
    {
        if (Interlocked.Exchange(ref _sessionEnded, 1) != 0)
        {
            return;
        }

        Write(
            DiagnosticLogLevel.Information,
            "app.stop",
            $"outcome={outcome}",
            suppressRepeats: false);
    }

    private static void AttachProcessInfrastructure()
    {
        if (Interlocked.Exchange(ref _infrastructureAttached, 1) != 0)
        {
            return;
        }

        Trace.Listeners.Add(TraceListener);
        AppDomain.CurrentDomain.UnhandledException +=
            static (_, eventArgs) =>
            {
                var message = eventArgs.ExceptionObject is Exception exception
                    ? exception.ToString()
                    : Convert.ToString(
                        eventArgs.ExceptionObject,
                        CultureInfo.InvariantCulture) ??
                      "Unknown exception object.";
                Write(
                    DiagnosticLogLevel.Error,
                    "app.unhandled",
                    $"terminating={eventArgs.IsTerminating}; {message}",
                    suppressRepeats: false);
            };
        TaskScheduler.UnobservedTaskException +=
            static (_, eventArgs) =>
                Write(
                    DiagnosticLogLevel.Error,
                    "task.unobserved",
                    eventArgs.Exception.ToString());
    }

    private static string BuildApplicationInformation(
        RuntimeProfile runtimeProfile)
    {
        var entryAssembly = Assembly.GetEntryAssembly() ??
            typeof(ApplicationDiagnosticLog).Assembly;
        var version = entryAssembly.GetCustomAttribute<
                AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ??
            entryAssembly.GetName().Version?.ToString() ??
            "unknown";
        return string.Join(
            "; ",
            $"version={version}",
            $"profile={runtimeProfile.PlatformName}",
            $"television={runtimeProfile.IsTelevision}",
            $"touch={runtimeProfile.IsTouchPrimary}",
            $"framework={RuntimeInformation.FrameworkDescription}",
            $"os={RuntimeInformation.OSDescription}",
            $"osArchitecture={RuntimeInformation.OSArchitecture}",
            $"processArchitecture={RuntimeInformation.ProcessArchitecture}",
            $"culture={CultureInfo.CurrentUICulture.Name}",
            $"processId={Environment.ProcessId}");
    }

    private static void Write(
        DiagnosticLogLevel level,
        string eventName,
        string message,
        bool suppressRepeats = true)
    {
        try
        {
            Volatile.Read(ref _writer)?.Write(
                level,
                eventName,
                message,
                suppressRepeats);
        }
        catch (Exception exception)
            when (exception is not OutOfMemoryException)
        {
            // A full, read-only or unavailable diagnostics directory must not
            // turn a recoverable application error into a second failure.
        }
    }
}

internal enum DiagnosticLogLevel
{
    Information,
    Warning,
    Error
}

internal sealed class ApplicationDiagnosticTraceListener(
    Action<DiagnosticLogLevel, string, string, bool> write) : TraceListener
{
    private readonly Action<DiagnosticLogLevel, string, string, bool> _write =
        write ?? throw new ArgumentNullException(nameof(write));

    public override bool IsThreadSafe => true;

    public override void Write(string? message)
    {
        // Trace.Write and Avalonia informational chatter are intentionally
        // excluded. Structured warning/error events enter through TraceEvent.
    }

    public override void WriteLine(string? message)
    {
        // See Write.
    }

    public override void Fail(string? message, string? detailMessage)
    {
        _write(
            DiagnosticLogLevel.Error,
            "trace.assert",
            string.IsNullOrWhiteSpace(detailMessage)
                ? message ?? "Assertion failed."
                : $"{message} {detailMessage}",
            true);
    }

    public override void TraceEvent(
        TraceEventCache? eventCache,
        string source,
        TraceEventType eventType,
        int id,
        string? message)
    {
        if (TryMapLevel(eventType, out var level))
        {
            _write(
                level,
                BuildEventName(source),
                message ?? string.Empty,
                true);
        }
    }

    public override void TraceEvent(
        TraceEventCache? eventCache,
        string source,
        TraceEventType eventType,
        int id,
        string? format,
        params object?[]? args)
    {
        if (!TryMapLevel(eventType, out var level))
        {
            return;
        }

        string message;
        try
        {
            message = args is { Length: > 0 }
                ? string.Format(
                    CultureInfo.InvariantCulture,
                    format ?? string.Empty,
                    args)
                : format ?? string.Empty;
        }
        catch (FormatException)
        {
            message = format ?? string.Empty;
        }

        _write(level, BuildEventName(source), message, true);
    }

    private static bool TryMapLevel(
        TraceEventType eventType,
        out DiagnosticLogLevel level)
    {
        if ((eventType &
             (TraceEventType.Critical | TraceEventType.Error)) != 0)
        {
            level = DiagnosticLogLevel.Error;
            return true;
        }

        if ((eventType & TraceEventType.Warning) != 0)
        {
            level = DiagnosticLogLevel.Warning;
            return true;
        }

        level = default;
        return false;
    }

    private static string BuildEventName(string source) =>
        string.IsNullOrWhiteSpace(source)
            ? "trace"
            : $"trace.{source}";
}

internal sealed class BoundedDiagnosticFileWriter
{
    internal const long DefaultMaximumFileBytes = 1024 * 1024;
    internal const int DefaultArchiveCount = 2;
    internal const int MaximumEntryCharacters = 16 * 1024;
    internal static readonly TimeSpan DefaultRepeatInterval =
        TimeSpan.FromMinutes(5);

    private const int MaximumRememberedEntries = 256;
    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    private readonly Lock _gate = new();
    private readonly long _maximumFileBytes;
    private readonly int _archiveCount;
    private readonly TimeSpan _repeatInterval;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<string, RepetitionState> _repetitions =
        new(StringComparer.Ordinal);

    internal BoundedDiagnosticFileWriter(
        string filePath,
        long maximumFileBytes = DefaultMaximumFileBytes,
        int archiveCount = DefaultArchiveCount,
        TimeSpan? repeatInterval = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (maximumFileBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumFileBytes));
        }

        if (archiveCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(archiveCount));
        }

        var effectiveRepeatInterval =
            repeatInterval ?? DefaultRepeatInterval;
        if (effectiveRepeatInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(repeatInterval));
        }

        FilePath = Path.GetFullPath(filePath);
        _maximumFileBytes = maximumFileBytes;
        _archiveCount = archiveCount;
        _repeatInterval = effectiveRepeatInterval;
        _timeProvider = timeProvider ?? TimeProvider.System;
        Directory.CreateDirectory(
            Path.GetDirectoryName(FilePath) ??
            throw new ArgumentException(
                "The diagnostic log requires a parent directory.",
                nameof(filePath)));
    }

    internal string FilePath { get; }

    internal void Write(
        DiagnosticLogLevel level,
        string eventName,
        string message,
        bool suppressRepeats = true)
    {
        var timestamp = _timeProvider.GetUtcNow();
        var normalizedEvent = Normalize(eventName, maximumLength: 96);
        if (normalizedEvent.Length == 0)
        {
            normalizedEvent = "app";
        }

        var normalizedMessage = Normalize(
            message,
            MaximumEntryCharacters);
        var repetitionKey = string.Concat(
            level.ToString(),
            "\n",
            normalizedEvent,
            "\n",
            normalizedMessage);

        lock (_gate)
        {
            _repetitions.TryGetValue(
                repetitionKey,
                out var repetition);
            if (suppressRepeats &&
                repetition is not null &&
                timestamp >= repetition.LastWrittenAt &&
                timestamp - repetition.LastWrittenAt < _repeatInterval)
            {
                repetition.SuppressedCount++;
                repetition.LastSeenAt = timestamp;
                return;
            }

            var repetitionSuffix =
                repetition?.SuppressedCount > 0
                    ? $" [suppressed {repetition.SuppressedCount} identical occurrence(s)]"
                    : string.Empty;
            var line =
                $"{timestamp:O} [{ToLabel(level)}] {normalizedEvent}: " +
                normalizedMessage +
                repetitionSuffix;
            AppendLine(line);

            if (repetition is null)
            {
                TrimRepetitionCacheIfNeeded();
                _repetitions[repetitionKey] = new RepetitionState(
                    timestamp);
            }
            else
            {
                repetition.LastWrittenAt = timestamp;
                repetition.LastSeenAt = timestamp;
                repetition.SuppressedCount = 0;
            }
        }
    }

    internal string GetArchivePath(int archiveNumber)
    {
        if (archiveNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(archiveNumber));
        }

        var directory = Path.GetDirectoryName(FilePath)!;
        var stem = Path.GetFileNameWithoutExtension(FilePath);
        var extension = Path.GetExtension(FilePath);
        return Path.Combine(
            directory,
            $"{stem}.{archiveNumber}{extension}");
    }

    private void AppendLine(string line)
    {
        var content = line + Environment.NewLine;
        var addedBytes = Utf8.GetByteCount(content);
        // One filesystem lookup rather than three. File.Exists was a stat, and
        // each of the two FileInfo instances was another - the second one asked
        // the very question the first had just answered. A FileInfo caches its
        // metadata after the first property read, so a single instance settles
        // both existence and length. This runs under the writer lock on every
        // logged line, including from the WebView's network thread.
        var current = new FileInfo(FilePath);
        var currentLength = current.Exists ? current.Length : 0L;
        if (currentLength > 0 &&
            currentLength + addedBytes > _maximumFileBytes)
        {
            Rotate();
        }

        File.AppendAllText(FilePath, content, Utf8);
    }

    private void Rotate()
    {
        if (_archiveCount == 0)
        {
            File.Delete(FilePath);
            return;
        }

        for (var archive = _archiveCount; archive >= 1; archive--)
        {
            var source = archive == 1
                ? FilePath
                : GetArchivePath(archive - 1);
            if (File.Exists(source))
            {
                File.Move(
                    source,
                    GetArchivePath(archive),
                    overwrite: true);
            }
        }
    }

    private void TrimRepetitionCacheIfNeeded()
    {
        if (_repetitions.Count < MaximumRememberedEntries)
        {
            return;
        }

        var oldest = _repetitions.MinBy(
            static pair => pair.Value.LastSeenAt);
        if (!string.IsNullOrEmpty(oldest.Key))
        {
            _repetitions.Remove(oldest.Key);
        }
    }

    private static string Normalize(string? value, int maximumLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var normalized = value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace('\t', ' ')
            .Trim();
        return normalized.Length <= maximumLength
            ? normalized
            : string.Concat(
                normalized.AsSpan(
                    0,
                    maximumLength - " …[truncated]".Length),
                " …[truncated]");
    }

    private static string ToLabel(DiagnosticLogLevel level) =>
        level switch
        {
            DiagnosticLogLevel.Information => "INFO",
            DiagnosticLogLevel.Warning => "WARN",
            DiagnosticLogLevel.Error => "ERROR",
            _ => "UNKNOWN"
        };

    private sealed class RepetitionState(DateTimeOffset timestamp)
    {
        internal DateTimeOffset LastWrittenAt { get; set; } = timestamp;

        internal DateTimeOffset LastSeenAt { get; set; } = timestamp;

        internal int SuppressedCount { get; set; }
    }
}
