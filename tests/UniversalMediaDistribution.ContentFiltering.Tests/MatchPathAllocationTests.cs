namespace UniversalMediaDistribution.ContentFiltering.Tests;

/// <summary>
/// The match path runs on a web view's network thread for every subresource of
/// every page, and on a low-memory device the garbage it produces is the
/// difference between smooth playback and a stutter. Zero is the only budget
/// that stays true as the engine grows, and it is far easier to hold with a
/// test than to recover once lost.
/// </summary>
public sealed class MatchPathAllocationTests
{
    private static readonly string[] Rules =
    [
        "||ads.example.com^",
        "||tracker.example.org^$third-party",
        "||googlesyndication.com^",
        "/pagead/",
        "@@||ads.example.com/needed.js",
        "||example.org/analytics.js$redirect=noopjs",
        "/count.js$domain=allowed.example|~inner.allowed.example",
        "##.generic-ad"
    ];

    private static readonly (string Url, string Document, ResourceType Type)[]
        Requests =
        [
            ("https://ads.example.com/banner.js",
                "https://player.video-host.example/embed", ResourceType.Script),
            ("https://cdn.video-host.example/hls/segment-00042.ts",
                "https://player.video-host.example/embed", ResourceType.Media),
            ("https://player.video-host.example/app.js",
                "https://player.video-host.example/embed", ResourceType.Script),
            ("https://pagead2.googlesyndication.com/pagead/js/x.js",
                "https://player.video-host.example/embed", ResourceType.Script),
            ("https://ads.example.com/needed.js",
                "https://player.video-host.example/embed", ResourceType.Script),
            ("https://example.org/analytics.js",
                "https://player.video-host.example/embed", ResourceType.Script),
            ("https://unrelated.example/logo.png",
                "https://player.video-host.example/embed", ResourceType.Image)
        ];

    [Fact]
    public void MatchingAllocatesNothing()
    {
        var engine = FilterEngineFactory.Build(Rules);

        // Warm up first: the very first call through a code path pays for JIT
        // and for the lazily built lookup tables, neither of which is a
        // per-request cost.
        Measure(engine);

        var allocated = Measure(engine);

        Assert.Equal(0, allocated);
    }

    private static volatile int _sink;

    private static long Measure(ContentFilterEngine engine)
    {
        var sink = 0;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var round = 0; round < 50; round++)
        {
            foreach (var (url, document, type) in Requests)
            {
                sink += (int)engine.Match(new FilterRequest(
                    url.AsSpan(),
                    document.AsSpan(),
                    type)).Action;
            }
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        _sink = sink;
        return allocated;
    }
}
