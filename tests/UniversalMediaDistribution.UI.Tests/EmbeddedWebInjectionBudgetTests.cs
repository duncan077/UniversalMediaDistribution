using UniversalMediaDistribution.ContentFiltering.Injection;
using UniversalMediaDistribution.ContentFiltering.Subscriptions;
using UniversalMediaDistribution.UI.Playback;

namespace UniversalMediaDistribution.UI.Tests;

/// <summary>
/// The embedded player sizes its injected payload from the device tier.
/// <para>
/// The tier was already detected correctly on Android and the constrained
/// budget already existed; what was missing was anything carrying the one to
/// the other, so the low-memory televisions were served the full
/// fifty-thousand-selector payload plus the procedural engine on every
/// navigation. These cover the two links in that chain.
/// </para>
/// </summary>
public sealed class EmbeddedWebInjectionBudgetTests
{
    private static readonly Uri PlayerPage =
        new("https://player.video-host.example/embed/123");

    [Fact]
    public void AConstrainedDeviceMapsToTheSmallerInjectionBudget()
    {
        var constrained = DeviceTier.Constrained.InjectionOptions();
        var standard = DeviceTier.Standard.InjectionOptions();

        Assert.Same(InjectionOptions.Standard, standard);
        Assert.False(constrained.IncludeProcedural);
        Assert.True(constrained.MaxSelectors < standard.MaxSelectors);
        Assert.True(
            constrained.MaxElementsPerPass < standard.MaxElementsPerPass);
        Assert.True(
            constrained.ProceduralBudgetMilliseconds <
            standard.ProceduralBudgetMilliseconds);
    }

    /// <summary>
    /// The budget has to reach the payload, not merely be selected. A session
    /// handed the constrained options must actually emit less script.
    /// </summary>
    [Fact]
    public void TheConstrainedBudgetProducesASmallerPayload()
    {
        var constrained = BuildPayload(DeviceTier.Constrained);
        var standard = BuildPayload(DeviceTier.Standard);

        Assert.True(
            constrained.Length < standard.Length,
            $"Constrained payload was {constrained.Length} bytes against " +
            $"{standard.Length} for standard - the options are not reaching " +
            "the injected script.");
    }

    private static string BuildPayload(DeviceTier tier)
    {
        var session = new EmbeddedWebFilterSession(
            EmbeddedWebAdBlocker.Engine,
            () => tier.InjectionOptions());
        session.Open(PlayerPage);
        return session.BuildInjectionPayload(PlayerPage.Host);
    }
}
