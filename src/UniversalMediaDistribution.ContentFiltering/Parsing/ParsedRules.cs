namespace UniversalMediaDistribution.ContentFiltering.Parsing;

[Flags]
public enum NetworkRuleFlags
{
    None = 0,

    /// <summary>An <c>@@</c> rule: a match means the request is allowed.</summary>
    Exception = 1 << 0,

    /// <summary><c>$important</c>: outranks exceptions.</summary>
    Important = 1 << 1,

    /// <summary><c>$third-party</c>.</summary>
    ThirdPartyOnly = 1 << 2,

    /// <summary><c>$~third-party</c>.</summary>
    FirstPartyOnly = 1 << 3,

    /// <summary><c>$match-case</c>: compare the pattern case-sensitively.</summary>
    MatchCase = 1 << 4,

    /// <summary>The pattern is a <c>/regular expression/</c>.</summary>
    Regex = 1 << 5,

    /// <summary>
    /// <c>$badfilter</c>: removes an identical rule from every other list
    /// rather than adding one. Resolved away at compile time.
    /// </summary>
    BadFilter = 1 << 6,

    /// <summary><c>$redirect=</c> or <c>$redirect-rule=</c>.</summary>
    Redirect = 1 << 7,

    /// <summary>
    /// <c>$redirect-rule=</c>: only redirects when some other rule already
    /// blocked the request, so on its own it is not a blocking rule.
    /// </summary>
    RedirectRule = 1 << 8
}

/// <summary>
/// One network filter as written, before compilation. The pattern still
/// carries its <c>||</c>, <c>|</c>, and <c>^</c> markers; the compiler is what
/// turns those into anchors.
/// </summary>
public sealed record ParsedNetworkRule
{
    public required string Pattern { get; init; }

    public ResourceType Types { get; init; } = ResourceType.Default;

    public NetworkRuleFlags Flags { get; init; }

    public RequestMethod Methods { get; init; }

    /// <summary>
    /// <c>$domain=</c> in its written form, for example
    /// <c>example.com|~ads.example.com</c>. Matched against the document, not
    /// the request.
    /// </summary>
    public string? DocumentDomains { get; init; }

    /// <summary><c>$to=</c>, matched against the request host.</summary>
    public string? RequestDomains { get; init; }

    /// <summary><c>$denyallow=</c>: request hosts this rule steps aside for.</summary>
    public string? DenyAllowDomains { get; init; }

    /// <summary>The <c>$redirect=</c> resource name, when one is present.</summary>
    public string? RedirectResource { get; init; }

    public bool IsException =>
        (Flags & NetworkRuleFlags.Exception) != 0;
}

public enum CosmeticRuleKind
{
    /// <summary>An element-hiding selector.</summary>
    Selector,

    /// <summary>A <c>+js(...)</c> scriptlet invocation.</summary>
    Scriptlet
}

/// <summary>
/// One cosmetic filter as written. <see cref="Domains"/> is the scope in its
/// written form (<c>a.com,~b.a.com</c>); null means the rule is generic and
/// applies everywhere.
/// </summary>
public sealed record ParsedCosmeticRule
{
    public required string Body { get; init; }

    public CosmeticRuleKind Kind { get; init; }

    public string? Domains { get; init; }

    public bool IsException { get; init; }

    /// <summary>
    /// True for <c>#?#</c> rules and for any selector using a procedural
    /// pseudo-class. Those cost DOM work on every mutation, so a device tier
    /// can decline them without losing plain selectors.
    /// </summary>
    public bool IsProcedural { get; init; }
}
