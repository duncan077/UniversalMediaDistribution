namespace UniversalMediaDistribution.ContentFiltering;

public enum FilterAction
{
    /// <summary>Load the request normally.</summary>
    Allow,

    /// <summary>Refuse the request at the network layer.</summary>
    Block,

    /// <summary>
    /// Serve a neutered stand-in instead of the real resource, so a page that
    /// waits on the script it requested still makes progress.
    /// </summary>
    Redirect
}

/// <summary>
/// Why a request ended up allowed or blocked.
///
/// This exists because "advertisements are still showing" has several causes
/// that look identical from outside, and the counts by reason separate them:
/// a page whose advertisements come from its own origin looks nothing like a
/// ruleset that has not downloaded yet.
/// </summary>
public enum FilterReason
{
    /// <summary>No rule matched.</summary>
    NoRule,

    /// <summary>A scheme filter lists do not describe.</summary>
    UnsupportedScheme,

    /// <summary>The request went to the page's own site.</summary>
    SameSite,

    /// <summary>The request is part of playing the video.</summary>
    Media,

    /// <summary>An <c>@@</c> rule allowed it.</summary>
    Exception,

    /// <summary>A rule matched and blocked it.</summary>
    Rule
}

/// <summary>
/// The result of matching one request. A struct with no owned state, so the
/// match path can return a verdict without allocating.
/// </summary>
public readonly struct FilterDecision : IEquatable<FilterDecision>
{
    private FilterDecision(
        FilterAction action,
        string? redirectResource,
        FilterReason reason)
    {
        Action = action;
        RedirectResource = redirectResource;
        Reason = reason;
    }

    public FilterAction Action { get; }

    public FilterReason Reason { get; }

    /// <summary>
    /// The surrogate resource name for <see cref="FilterAction.Redirect"/>,
    /// otherwise <see langword="null"/>. Names come from a table built when the
    /// ruleset loads, so reading one costs nothing here.
    /// </summary>
    public string? RedirectResource { get; }

    public static FilterDecision Allow { get; } =
        new(FilterAction.Allow, null, FilterReason.NoRule);

    public static FilterDecision Block { get; } =
        new(FilterAction.Block, null, FilterReason.Rule);

    public static FilterDecision Allowed(FilterReason reason) =>
        new(FilterAction.Allow, null, reason);

    public static FilterDecision Redirect(string resource)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resource);
        return new FilterDecision(
            FilterAction.Redirect,
            resource,
            FilterReason.Rule);
    }

    public bool IsBlocking => Action is not FilterAction.Allow;

    /// <summary>
    /// Compares the verdict, not the explanation: two allows are the same
    /// outcome however they were reached.
    /// </summary>
    public bool Equals(FilterDecision other) =>
        Action == other.Action &&
        string.Equals(
            RedirectResource,
            other.RedirectResource,
            StringComparison.Ordinal);

    public override bool Equals(object? obj) =>
        obj is FilterDecision other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(Action, RedirectResource);

    public static bool operator ==(FilterDecision left, FilterDecision right) =>
        left.Equals(right);

    public static bool operator !=(FilterDecision left, FilterDecision right) =>
        !left.Equals(right);
}
