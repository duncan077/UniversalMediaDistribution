namespace UniversalMediaDistribution.ContentFiltering.Parsing;

/// <summary>
/// Everything one filter list contributed, plus what had to be left behind.
/// The counters matter: a list whose skipped share suddenly jumps is a list
/// whose syntax has moved on, and that is worth noticing before coverage
/// quietly degrades.
/// </summary>
public sealed class FilterListParseResult
{
    public List<ParsedNetworkRule> NetworkRules { get; } = [];

    public List<ParsedCosmeticRule> CosmeticRules { get; } = [];

    /// <summary>
    /// <c>!#include</c> targets. Resolving them needs another fetch, so the
    /// parser only reports them.
    /// </summary>
    public List<string> Includes { get; } = [];

    public string? Title { get; internal set; }

    /// <summary>The list's own <c>! Expires:</c> refresh hint, when it gives one.</summary>
    public TimeSpan? Expires { get; internal set; }

    public int TotalLines { get; internal set; }

    /// <summary>Lines that were syntactically unusable.</summary>
    public int MalformedLines { get; internal set; }

    /// <summary>
    /// Lines that parsed but named something this engine does not enforce.
    /// Dropped on purpose: a half-applied rule is worse than an absent one.
    /// </summary>
    public int UnsupportedLines { get; internal set; }

    public int SkippedLines => MalformedLines + UnsupportedLines;

    internal void Add(FilterListParseResult other)
    {
        NetworkRules.AddRange(other.NetworkRules);
        CosmeticRules.AddRange(other.CosmeticRules);
        Includes.AddRange(other.Includes);
        TotalLines += other.TotalLines;
        MalformedLines += other.MalformedLines;
        UnsupportedLines += other.UnsupportedLines;
        Title ??= other.Title;
        Expires ??= other.Expires;
    }
}

/// <summary>
/// What the <c>!#if</c> preprocessor should believe about this host. Unknown
/// conditions are treated as true so an unfamiliar capability flag costs
/// coverage rather than silently discarding a list's whole body.
/// </summary>
public sealed record FilterListEnvironment(bool IsMobile = false)
{
    public static FilterListEnvironment Desktop { get; } = new();

    public static FilterListEnvironment Mobile { get; } = new(IsMobile: true);
}
