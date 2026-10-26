using System.Diagnostics;
using UniversalMediaDistribution.UI.Diagnostics;

namespace UniversalMediaDistribution.UI.Tests;

public sealed class ApplicationDiagnosticLogTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"umd-diagnostics-{Guid.CreateVersion7():N}");

    [Fact]
    public void RepeatedEntriesAreCollapsedUntilTheNextUsefulSummary()
    {
        var timeProvider = new ManualTimeProvider(
            new DateTimeOffset(
                2026,
                7,
                28,
                12,
                0,
                0,
                TimeSpan.Zero));
        var writer = CreateWriter(
            repeatInterval: TimeSpan.FromMinutes(5),
            timeProvider: timeProvider);

        writer.Write(
            DiagnosticLogLevel.Error,
            "providers.daily-fetch",
            "Provider is unavailable.");
        writer.Write(
            DiagnosticLogLevel.Error,
            "providers.daily-fetch",
            "Provider is unavailable.");
        timeProvider.Advance(TimeSpan.FromMinutes(4));
        writer.Write(
            DiagnosticLogLevel.Error,
            "providers.daily-fetch",
            "Provider is unavailable.");

        Assert.Single(File.ReadAllLines(writer.FilePath));

        timeProvider.Advance(TimeSpan.FromMinutes(1));
        writer.Write(
            DiagnosticLogLevel.Error,
            "providers.daily-fetch",
            "Provider is unavailable.");

        var lines = File.ReadAllLines(writer.FilePath);
        Assert.Equal(2, lines.Length);
        Assert.Contains(
            "suppressed 2 identical occurrence(s)",
            lines[1],
            StringComparison.Ordinal);
    }

    [Fact]
    public void RotationKeepsOnlyTheCurrentLogAndTwoArchives()
    {
        const long maximumFileBytes = 220;
        var writer = CreateWriter(
            maximumFileBytes,
            repeatInterval: TimeSpan.Zero);

        for (var index = 0; index < 10; index++)
        {
            writer.Write(
                DiagnosticLogLevel.Warning,
                $"event-{index}",
                new string((char)('a' + index), 100),
                suppressRepeats: false);
        }

        var retainedPaths = new[]
        {
            writer.FilePath,
            writer.GetArchivePath(1),
            writer.GetArchivePath(2)
        };
        Assert.All(retainedPaths, static path => Assert.True(File.Exists(path)));
        Assert.All(
            retainedPaths,
            path => Assert.InRange(
                new FileInfo(path).Length,
                1,
                maximumFileBytes));
        Assert.False(File.Exists(writer.GetArchivePath(3)));

        var retainedText = string.Join(
            Environment.NewLine,
            retainedPaths.Select(File.ReadAllText));
        Assert.DoesNotContain(
            "event-0",
            retainedText,
            StringComparison.Ordinal);
        Assert.Contains(
            "event-9",
            retainedText,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TraceListenerIgnoresInformationAndKeepsWarningsAndErrors()
    {
        var entries =
            new List<(DiagnosticLogLevel Level, string Event, string Message)>();
        var listener = new ApplicationDiagnosticTraceListener(
            (level, eventName, message, _) =>
                entries.Add((level, eventName, message)));

        listener.WriteLine("unstructured noise");
        listener.TraceEvent(
            null,
            "Avalonia",
            TraceEventType.Information,
            0,
            "routine layout information");
        listener.TraceEvent(
            null,
            "Catalog",
            TraceEventType.Warning,
            0,
            "provider degraded");
        listener.TraceEvent(
            null,
            "Catalog",
            TraceEventType.Error,
            0,
            "provider failed");

        Assert.Collection(
            entries,
            warning =>
            {
                Assert.Equal(DiagnosticLogLevel.Warning, warning.Level);
                Assert.Equal("trace.Catalog", warning.Event);
                Assert.Equal("provider degraded", warning.Message);
            },
            error =>
            {
                Assert.Equal(DiagnosticLogLevel.Error, error.Level);
                Assert.Equal("trace.Catalog", error.Event);
                Assert.Equal("provider failed", error.Message);
            });
    }

    [Fact]
    public void EntriesAreSingleLineAndTruncated()
    {
        var writer = CreateWriter(
            maximumFileBytes: 64 * 1024,
            repeatInterval: TimeSpan.Zero);

        writer.Write(
            DiagnosticLogLevel.Error,
            "app.unhandled",
            "first line\r\nsecond line " +
            new string('x', BoundedDiagnosticFileWriter.MaximumEntryCharacters));

        var lines = File.ReadAllLines(writer.FilePath);
        var line = Assert.Single(lines);
        Assert.Contains(
            @"first line\nsecond line",
            line,
            StringComparison.Ordinal);
        Assert.EndsWith("…[truncated]", line, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temporary diagnostics directory is not a test failure.
        }
    }

    private BoundedDiagnosticFileWriter CreateWriter(
        long maximumFileBytes =
            BoundedDiagnosticFileWriter.DefaultMaximumFileBytes,
        TimeSpan? repeatInterval = null,
        TimeProvider? timeProvider = null)
    {
        Directory.CreateDirectory(_directory);
        return new BoundedDiagnosticFileWriter(
            Path.Combine(_directory, "application.log"),
            maximumFileBytes,
            BoundedDiagnosticFileWriter.DefaultArchiveCount,
            repeatInterval,
            timeProvider);
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) :
        TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        internal void Advance(TimeSpan duration) =>
            _utcNow += duration;
    }
}
