using UniversalMediaDistribution.ContentFiltering.Subscriptions;

namespace UniversalMediaDistribution.ContentFiltering.Tests;

/// <summary>
/// The compiled ruleset is loaded through a memory-mapped view so its pages
/// stay file-backed and evictable rather than counting against a constrained
/// device's heap. The hazards that buys are all lifetime hazards, and they are
/// what these tests are about.
/// </summary>
public sealed class MappedRulesetTests : IDisposable
{
    private const string Document = "https://player.video-host.example/embed";

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"umd-mapped-{Guid.NewGuid():N}");

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
            // A mapped file may still be held open; the operating system
            // reclaims it when the test process exits.
        }
    }

    private FilterRulesetStore Store() => new(_directory);

    private static byte[] Compile(params string[] lines) =>
        FilterEngineFactory.Compile(out _, null, lines);

    [Fact]
    public void ASavedRulesetIsLoadedThroughAMapping()
    {
        var store = Store();
        store.SaveRuleset(Compile("||ads.example^"));

        Assert.True(store.TryLoadRuleset(out var ruleset));
        Assert.True(ruleset.IsMemoryMapped);
        Assert.Equal(
            FilterAction.Block,
            new ContentFilterEngine(ruleset)
                .Act("https://ads.example/x.js", Document));
    }

    [Fact]
    public void ARulesetCanBeReplacedWhileAnEarlierOneIsStillMapped()
    {
        // The refresh path writes a temporary file and renames it over the
        // live one. On Windows that rename fails against an open mapping
        // unless the file was opened to allow it, and a filtering engine that
        // could never be updated after its first load would be worse than one
        // that was never mapped.
        var store = Store();
        store.SaveRuleset(Compile("||first.example^"));
        Assert.True(store.TryLoadRuleset(out var first));

        store.SaveRuleset(Compile("||second.example^"));

        Assert.True(store.TryLoadRuleset(out var second));
        Assert.Equal(
            FilterAction.Block,
            new ContentFilterEngine(second)
                .Act("https://second.example/x.js", Document));
        Assert.Equal(
            FilterAction.Allow,
            new ContentFilterEngine(second)
                .Act("https://first.example/x.js", Document));
    }

    [Fact]
    public void AMappedRulesetStaysReadableAfterItsFileIsReplaced()
    {
        // A request already in flight keeps matching against the ruleset it
        // started with. If replacing the file could pull that memory away, the
        // result would not be an exception — it would be a corrupted read.
        var store = Store();
        store.SaveRuleset(Compile("||first.example^"));
        Assert.True(store.TryLoadRuleset(out var first));
        var engine = new ContentFilterEngine(first);

        store.SaveRuleset(Compile("||second.example^"));
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.Equal(
            FilterAction.Block,
            engine.Act("https://first.example/x.js", Document));
        Assert.Equal(first.NetworkRuleCount, engine.Ruleset.NetworkRuleCount);
    }

    [Fact]
    public void AnAbsentRulesetIsNotAFailure()
    {
        Assert.False(Store().TryLoadRuleset(out _));
    }

    [Fact]
    public void ATruncatedRulesetFileIsRefused()
    {
        var store = Store();
        store.SaveRuleset(Compile("||ads.example^"));
        var path = Directory.GetFiles(_directory, "ruleset-*.bin").Single();
        File.WriteAllBytes(path, File.ReadAllBytes(path)[..16]);

        Assert.False(store.TryLoadRuleset(out _));
    }

    [Fact]
    public void AnEmptyRulesetFileIsRefusedRatherThanMapped()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(Path.Combine(_directory, "ruleset-1.bin"), []);

        Assert.False(Store().TryLoadRuleset(out _));
    }

    [Fact]
    public void SupersededGenerationsAreCleanedUp()
    {
        var store = Store();
        for (var round = 0; round < 4; round++)
        {
            store.SaveRuleset(Compile($"||ads{round}.example^"));
        }

        // Nothing has been mapped here, so every superseded generation goes.
        Assert.Single(Directory.GetFiles(_directory, "ruleset-*.bin"));
        Assert.True(store.TryLoadRuleset(out var ruleset));
        Assert.Equal(
            FilterAction.Block,
            new ContentFilterEngine(ruleset)
                .Act("https://ads3.example/x.js", Document));
    }

    [Fact]
    public void AnUnreadableNewestGenerationFallsBackToTheOneBeforeIt()
    {
        var store = Store();
        store.SaveRuleset(Compile("||first.example^"));
        Assert.True(store.TryLoadRuleset(out _));

        // The mapping above keeps generation 1 on disk, so writing a corrupt
        // generation 2 leaves something to fall back to.
        File.WriteAllBytes(
            Path.Combine(_directory, "ruleset-2.bin"),
            [1, 2, 3, 4, 5, 6, 7, 8]);

        Assert.True(store.TryLoadRuleset(out var ruleset));
        Assert.Equal(
            FilterAction.Block,
            new ContentFilterEngine(ruleset)
                .Act("https://first.example/x.js", Document));
    }
}
