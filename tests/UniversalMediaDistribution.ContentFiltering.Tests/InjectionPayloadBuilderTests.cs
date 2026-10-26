using UniversalMediaDistribution.ContentFiltering.Injection;

namespace UniversalMediaDistribution.ContentFiltering.Tests;

public sealed class InjectionPayloadBuilderTests
{
    private static CosmeticPayload Payload(
        IReadOnlyList<string>? selectors = null,
        IReadOnlyList<string>? procedural = null,
        IReadOnlyList<string>? scriptlets = null,
        IReadOnlyList<string>? generic = null) =>
        new(selectors ?? [], procedural ?? [], scriptlets ?? [], generic ?? []);

    [Fact]
    public void EveryScriptletTheBuilderShipsIsImplementedByTheLibrary()
    {
        // The C# list and the JavaScript registry are two halves of one
        // contract; a name in one and not the other is a silently dead rule.
        var payload = InjectionPayloadBuilder.Build(Payload());

        foreach (var name in InjectionPayloadBuilder.SupportedScriptlets)
        {
            Assert.Contains($"\"{name}\":", payload, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SelectorsReachTheConfiguration()
    {
        var payload = InjectionPayloadBuilder.Build(
            Payload(selectors: [".ad-banner", "#promo"]));

        Assert.Contains("\".ad-banner\"", payload, StringComparison.Ordinal);
        Assert.Contains("\"#promo\"", payload, StringComparison.Ordinal);
    }

    [Fact]
    public void ScriptletArgumentsAreParsedAndPassedThrough()
    {
        var payload = InjectionPayloadBuilder.Build(
            Payload(scriptlets: ["set-constant, adsEnabled, false"]));

        Assert.Contains(
            "\"name\":\"set-constant\"",
            payload,
            StringComparison.Ordinal);
        Assert.Contains(
            "[\"adsEnabled\",\"false\"]",
            payload,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnimplementedScriptletIsDroppedRatherThanShipped()
    {
        var payload = InjectionPayloadBuilder.Build(
            Payload(scriptlets: ["not-a-real-scriptlet, x"]));

        Assert.Contains("\"scriptlets\":[]", payload, StringComparison.Ordinal);
    }

    [Fact]
    public void TheConstrainedProfileShipsNoProceduralSelectors()
    {
        var payload = InjectionPayloadBuilder.Build(
            Payload(procedural: ["div:has-text(Ad)"]),
            InjectionOptions.Constrained);

        Assert.Contains("\"procedural\":[]", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("has-text(Ad)", payload, StringComparison.Ordinal);
    }

    [Fact]
    public void GenericHidingIsKeptApartSoItCanBeScopedToTheTopDocument()
    {
        // A document-start script runs in every frame. Tens of thousands of
        // generic selectors parsed once per frame is enough work to stall a
        // renderer, and it buys nothing inside an advertisement frame.
        var payload = InjectionPayloadBuilder.Build(
            Payload(selectors: [".host-ad"], generic: [".generic-ad"]));

        Assert.Contains(
            "\"selectors\":[\".host-ad\"]",
            payload,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"genericSelectors\":[\".generic-ad\"]",
            payload,
            StringComparison.Ordinal);
        Assert.Contains(
            "window.top === window.self",
            payload,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheDefaultCeilingDoesNotTruncateARealisticList()
    {
        // EasyList currently offers about fourteen thousand generic hiding
        // selectors, and neither embedded host has any host-specific rules —
        // so generic hiding is the whole cosmetic defence for them. A ceiling
        // that trims it takes an arbitrary prefix, and the advertisement boxes
        // whose selectors fell off the end simply stay on screen.
        var selectors = new List<string>();
        for (var index = 0; index < 20_000; index++)
        {
            selectors.Add($".ad-{index}");
        }

        var payload = InjectionPayloadBuilder.Build(
            Payload(selectors: selectors),
            InjectionOptions.Standard);

        Assert.Contains("\".ad-0\"", payload, StringComparison.Ordinal);
        Assert.Contains("\".ad-19999\"", payload, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFrameAroundARefusedPictureIsRemovedToo()
    {
        // A host that redirects to a throwaway domain and injects its
        // advertisement card from that same origin puts the markup out of
        // every rule's reach. Only the pictures it pulls in are blockable, and
        // blocking those leaves the empty box behind — so a failed image has
        // to take its frame with it.
        var payload = InjectionPayloadBuilder.Build(Payload());

        Assert.Contains("removeCardAround", payload, StringComparison.Ordinal);
        Assert.Contains(
            "addEventListener(\"error\"",
            payload,
            StringComparison.Ordinal);
        // And never at the cost of the player itself.
        Assert.Contains(
            "querySelector(\"video, audio, canvas\")",
            payload,
            StringComparison.Ordinal);
    }

    [Fact]
    public void HidingRulesApplyBeforeTheDocumentIsParsed()
    {
        // Injecting the stylesheet at DOMContentLoaded is too late: the box has
        // already been laid out and painted, so hiding it then is a flash of
        // exactly the content we were asked to remove.
        var payload = InjectionPayloadBuilder.Build(Payload());

        var injectStyle = payload.IndexOf(
            "injectStyle();",
            StringComparison.Ordinal);
        var domContentLoaded = payload.IndexOf(
            "document.addEventListener(\"DOMContentLoaded\", start",
            StringComparison.Ordinal);

        Assert.True(injectStyle > 0 && domContentLoaded > 0);
        Assert.True(
            injectStyle < domContentLoaded,
            "the stylesheet must be injected before the DOM-ready handler is " +
            "registered, not from inside it.");
    }

    [Fact]
    public void SelectorCountsAreCapped()
    {
        var selectors = new List<string>();
        for (var index = 0; index < 100; index++)
        {
            selectors.Add($".ad-{index}");
        }

        var payload = InjectionPayloadBuilder.Build(
            Payload(selectors: selectors),
            InjectionOptions.Standard with { MaxSelectors = 10 });

        Assert.Contains("\".ad-9\"", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("\".ad-10\"", payload, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBudgetsReachTheRuntime()
    {
        var payload = InjectionPayloadBuilder.Build(
            Payload(),
            InjectionOptions.Constrained);

        Assert.Contains(
            "\"proceduralBudgetMs\":6",
            payload,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"maxElementsPerPass\":500",
            payload,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"builtInDefenses\":true",
            payload,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheRuntimeAndLibraryAreBothPresentAndGuardedAgainstReinjection()
    {
        var payload = InjectionPayloadBuilder.Build(Payload());

        Assert.Contains("__umdScriptlets", payload, StringComparison.Ordinal);
        Assert.Contains("__umdFilterRuntime", payload, StringComparison.Ordinal);
        Assert.Contains(
            "if (globalThis[marker]) { return; }",
            payload,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("set-constant, a, b", "set-constant", 2)]
    [InlineData("prevent-setTimeout, 'a, b'", "prevent-setTimeout", 1)]
    [InlineData("remove-attr, onclick\\, onload, div", "remove-attr", 2)]
    [InlineData("nowebrtc", "nowebrtc", 0)]
    public void ScriptletBodiesSplitOnRealArgumentBoundaries(
        string body,
        string name,
        int argumentCount)
    {
        Assert.True(ScriptletInvocation.TryParse(body, out var invocation));
        Assert.Equal(name, invocation.Name);
        Assert.Equal(argumentCount, invocation.Arguments.Count);
    }
}
