using UniversalMediaDistribution.ContentFiltering.Compilation;
using UniversalMediaDistribution.ContentFiltering.Injection;

namespace UniversalMediaDistribution.ContentFiltering.Subscriptions;

/// <summary>
/// How much filtering the viewer asked for.
/// </summary>
public enum FilteringMode
{
    /// <summary>No filtering at all.</summary>
    Off = 0,

    /// <summary>
    /// The rules built into the application only. Nothing is downloaded, so
    /// this is also what an offline install runs on.
    /// </summary>
    Basic = 1,

    /// <summary>The default subscriptions.</summary>
    Standard = 2,

    /// <summary>The default subscriptions plus annoyance lists.</summary>
    Complete = 3
}

/// <summary>
/// What the device can actually carry. Decided from the hardware, not from a
/// preference: a Chromecast is a television with two gigabytes of memory and
/// four small cores, and it is already decoding video and verifying torrent
/// pieces while this runs.
/// </summary>
public enum DeviceTier
{
    Standard = 0,

    /// <summary>
    /// A low-memory device. Generic element hiding and procedural selectors
    /// are declined here — the first costs a match against every element on
    /// every style recalculation, the second costs DOM work on every mutation,
    /// and both are paid continuously while a video plays. Network blocking,
    /// which is what actually stops the popunders, is unaffected.
    /// </summary>
    Constrained = 1
}

public static class DeviceTiers
{
    public static RulesetCompilerOptions CompilerOptions(this DeviceTier tier) =>
        tier == DeviceTier.Constrained
            ? RulesetCompilerOptions.Constrained
            : RulesetCompilerOptions.Standard;

    public static InjectionOptions InjectionOptions(this DeviceTier tier) =>
        tier == DeviceTier.Constrained
            ? Injection.InjectionOptions.Constrained
            : Injection.InjectionOptions.Standard;

    /// <summary>
    /// The tier is a ceiling, not a suggestion. A viewer may ask for less
    /// filtering than the device could carry, never for more.
    /// </summary>
    public static FilteringMode Cap(this DeviceTier tier, FilteringMode mode) =>
        tier == DeviceTier.Constrained && mode == FilteringMode.Complete
            ? FilteringMode.Standard
            : mode;
}
