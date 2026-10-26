using UniversalMediaDistribution.ContentFiltering.Compilation;
using UniversalMediaDistribution.ContentFiltering.Matching;

namespace UniversalMediaDistribution.ContentFiltering;

/// <summary>
/// Decides what a web view is allowed to load, and what to inject into the
/// pages it shows.
///
/// The ruleset behind a single volatile reference is the whole of the
/// thread-safety story: matching only ever reads an immutable compiled blob,
/// and a refresh swaps the reference. That matters because Android calls
/// <c>shouldInterceptRequest</c> from its network thread while the UI thread is
/// doing something else, and a lock there would stall page loads.
///
/// The match path allocates nothing.
/// </summary>
public sealed class ContentFilterEngine
{
    private readonly PublicSuffixIndex _suffixes;
    private volatile CompiledRuleset _ruleset;

    public ContentFilterEngine(
        CompiledRuleset? ruleset = null,
        PublicSuffixIndex? publicSuffixes = null)
    {
        _ruleset = ruleset ?? CompiledRuleset.Empty;
        _suffixes = publicSuffixes ?? PublicSuffixIndex.Default;
    }

    public CompiledRuleset Ruleset => _ruleset;

    /// <summary>
    /// Swaps in a freshly compiled ruleset. Requests already in flight finish
    /// against the ruleset they started with, which is the correct outcome and
    /// the reason no lock is needed.
    /// </summary>
    public void Update(CompiledRuleset ruleset)
    {
        ArgumentNullException.ThrowIfNull(ruleset);
        _ruleset = ruleset;
    }

    /// <summary>
    /// Convenience over <see cref="Match"/> for callers that already hold
    /// <see cref="Uri"/> values, such as navigation events.
    /// </summary>
    public bool ShouldBlock(
        Uri requestUri,
        Uri? documentUri = null,
        ResourceType type = ResourceType.Other)
    {
        ArgumentNullException.ThrowIfNull(requestUri);
        if (!requestUri.IsAbsoluteUri)
        {
            return false;
        }

        var document = documentUri is { IsAbsoluteUri: true }
            ? documentUri.AbsoluteUri.AsSpan()
            : default;
        return Match(new FilterRequest(
            requestUri.AbsoluteUri.AsSpan(),
            document,
            type)).IsBlocking;
    }

    public FilterDecision Match(in FilterRequest request)
    {
        var ruleset = _ruleset;
        var url = request.Url;
        var layout = UrlLayout.Parse(url);
        if (!layout.IsSupported)
        {
            // Schemes filter lists do not describe. Guessing at data:, blob:,
            // and about: would break more than it protects.
            return FilterDecision.Allowed(FilterReason.UnsupportedScheme);
        }

        if (url[layout.HostEnd - 1] == '.')
        {
            // A trailing dot is a legal but non-canonical way to write a host,
            // and it must not become a way around a rule. Rewriting the URL
            // needs a string, which is why it happens only in this rare case
            // and never on the path a page's own requests take.
            return Match(new FilterRequest(
                string.Concat(url[..(layout.HostEnd - 1)], url[layout.HostEnd..])
                    .AsSpan(),
                request.DocumentUrl,
                request.Type,
                request.Method));
        }

        var host = layout.Host(url);
        var documentUrl = request.DocumentUrl;
        var documentLayout = UrlLayout.Parse(documentUrl);
        var documentHost = documentLayout.IsSupported
            ? documentLayout.Host(documentUrl).TrimEnd('.')
            : default;

        var sameSite = DomainMatcher.IsSameSiteWithDocument(host, documentHost);
        // Preserve the selected document, including native adapters that
        // report top-level loads as SubDocument or Other. Its subresources
        // must still reach the rules, even when served by the same host.
        if (sameSite &&
            request.Type is (ResourceType.Document or ResourceType.SubDocument or ResourceType.Other) &&
            url.Equals(documentUrl, StringComparison.Ordinal))
        {
            return FilterDecision.Allowed(FilterReason.SameSite);
        }

        if (MediaSafety.IsMediaTarget(url[layout.AuthorityEnd..], request.Type))
        {
            return FilterDecision.Allowed(FilterReason.Media);
        }

        var thirdParty = documentHost.IsEmpty ||
            !_suffixes.SameSite(host, documentHost);

        var verdict = default(Verdict);
        Evaluate(
            ruleset,
            ruleset.AlwaysRuleIndices,
            request,
            layout,
            host,
            documentHost,
            thirdParty,
            ref verdict);

        if (!verdict.ImportantException)
        {
            var tokens = FilterTokenizer.EnumerateUrlTokens(url);
            while (tokens.MoveNext())
            {
                var token = tokens.Current;
                if (ruleset.IsUnusedToken(token))
                {
                    continue;
                }

                var bucket = Bucket(ruleset, token);
                if (bucket.IsEmpty)
                {
                    continue;
                }

                Evaluate(
                    ruleset,
                    bucket,
                    request,
                    layout,
                    host,
                    documentHost,
                    thirdParty,
                    ref verdict);
                if (verdict.ImportantException)
                {
                    break;
                }
            }
        }

        var decision = verdict.Resolve();
        return sameSite && decision.Reason == FilterReason.NoRule
            ? FilterDecision.Allowed(FilterReason.SameSite)
            : decision;
    }

    /// <summary>
    /// The selectors and scriptlets a page on this host needs. Resolved once
    /// per navigation rather than per request, so this is allowed to allocate.
    /// </summary>
    public CosmeticPayload GetCosmetics(string hostname)
    {
        ArgumentNullException.ThrowIfNull(hostname);
        var ruleset = _ruleset;
        var host = hostname.Trim().TrimEnd('.').ToLowerInvariant();
        if (host.Length == 0 || ruleset.CosmeticRuleCount == 0)
        {
            return CosmeticPayload.Empty;
        }

        // Generic and host-specific rules are kept apart because they are
        // injected into different places. A host's own rules are a handful and
        // belong wherever its page runs; the generic set is tens of thousands
        // of selectors, and putting that into every frame of a frame-heavy
        // page is enough work to stall the renderer.
        var generic = new List<int>();
        generic.AddRange(ruleset.CosmeticGenericIndices);
        var specific = new List<int>();
        CollectHostCandidates(ruleset, host, specific);

        var candidates = new List<int>(generic.Count + specific.Count);
        candidates.AddRange(generic);
        candidates.AddRange(specific);
        if (candidates.Count == 0)
        {
            return CosmeticPayload.Empty;
        }

        var genericCount = generic.Count;

        var excluded = new HashSet<string>(StringComparer.Ordinal);
        var records = ruleset.CosmeticRecords;
        foreach (var index in candidates)
        {
            var record = records[index];
            if (record.IsException &&
                DomainMatcher.ScopeApplies(
                    ruleset.Text(record.DomainsOffset, record.DomainsLength),
                    host))
            {
                excluded.Add(
                    ruleset.Decode(record.BodyOffset, record.BodyLength));
            }
        }

        var selectors = new List<string>();
        var genericSelectors = new List<string>();
        var procedural = new List<string>();
        var scriptlets = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var position = 0; position < candidates.Count; position++)
        {
            var record = records[candidates[position]];
            if (record.IsException ||
                !DomainMatcher.ScopeApplies(
                    ruleset.Text(record.DomainsOffset, record.DomainsLength),
                    host))
            {
                continue;
            }

            var body = ruleset.Decode(record.BodyOffset, record.BodyLength);
            if (excluded.Contains(body) || !seen.Add(body))
            {
                continue;
            }

            if (record.IsScriptlet)
            {
                scriptlets.Add(body);
            }
            else if (record.IsProcedural)
            {
                procedural.Add(body);
            }
            else if (position < genericCount)
            {
                genericSelectors.Add(body);
            }
            else
            {
                selectors.Add(body);
            }
        }

        return new CosmeticPayload(
            selectors,
            procedural,
            scriptlets,
            genericSelectors);
    }

    private static void CollectHostCandidates(
        CompiledRuleset ruleset,
        string host,
        List<int> candidates)
    {
        var hashes = ruleset.CosmeticHostHashes;
        if (hashes.IsEmpty)
        {
            return;
        }

        var starts = ruleset.CosmeticHostStarts;
        var indices = ruleset.CosmeticHostIndices;
        var remaining = host.AsSpan();
        while (!remaining.IsEmpty)
        {
            var position = hashes.BinarySearch(FilterTokenizer.Hash(remaining));
            if (position >= 0)
            {
                for (var index = starts[position];
                    index < starts[position + 1];
                    index++)
                {
                    candidates.Add(indices[index]);
                }
            }

            var dot = remaining.IndexOf('.');
            remaining = dot < 0 ? default : remaining[(dot + 1)..];
        }
    }

    private static ReadOnlySpan<int> Bucket(
        CompiledRuleset ruleset,
        int tokenHash)
    {
        var position = ruleset.TokenHashes.BinarySearch(tokenHash);
        if (position < 0)
        {
            return [];
        }

        var starts = ruleset.TokenBucketStarts;
        return ruleset.TokenRuleIndices[starts[position]..starts[position + 1]];
    }

    private static void Evaluate(
        CompiledRuleset ruleset,
        ReadOnlySpan<int> candidates,
        in FilterRequest request,
        in UrlLayout layout,
        ReadOnlySpan<char> host,
        ReadOnlySpan<char> documentHost,
        bool thirdParty,
        ref Verdict verdict)
    {
        var rules = ruleset.Rules;
        foreach (var index in candidates)
        {
            if ((uint)index >= (uint)rules.Length)
            {
                continue;
            }

            var rule = rules[index];
            if (!Applies(
                    ruleset,
                    rule,
                    index,
                    request,
                    layout,
                    host,
                    documentHost,
                    thirdParty))
            {
                continue;
            }

            var important = rule.Has(RecordFlags.Important);
            if (rule.Has(RecordFlags.Exception))
            {
                if (important)
                {
                    verdict.ImportantException = true;
                    return;
                }

                verdict.Exception = true;
                continue;
            }

            var resource = rule.Has(RecordFlags.Redirect) && rule.AuxRef >= 0
                ? ruleset.RedirectName(
                    ruleset.Aux[rule.AuxRef].RedirectOffset)
                : null;

            if (rule.Has(RecordFlags.RedirectRule))
            {
                // "$redirect-rule" only supplies a stand-in for a block some
                // other rule made; on its own it decides nothing.
                verdict.RedirectRuleResource ??= resource;
                continue;
            }

            if (important)
            {
                verdict.ImportantBlock = true;
                verdict.ImportantRedirect ??= resource;
                continue;
            }

            verdict.Block = true;
            verdict.RedirectResource ??= resource;
        }
    }

    private static bool Applies(
        CompiledRuleset ruleset,
        in NetworkRuleRecord rule,
        int ruleIndex,
        in FilterRequest request,
        in UrlLayout layout,
        ReadOnlySpan<char> host,
        ReadOnlySpan<char> documentHost,
        bool thirdParty)
    {
        if ((rule.TypeMask & (ushort)request.Type) == 0)
        {
            return false;
        }

        if (rule.MethodMask != 0 &&
            request.Method != RequestMethod.None &&
            (rule.MethodMask & (ushort)request.Method) == 0)
        {
            return false;
        }

        if (rule.Has(RecordFlags.ThirdPartyOnly) && !thirdParty ||
            rule.Has(RecordFlags.FirstPartyOnly) && thirdParty)
        {
            return false;
        }

        if (rule.AuxRef >= 0)
        {
            var aux = ruleset.Aux[rule.AuxRef];
            if (aux.DocumentDomainsLength > 0 &&
                !DomainMatcher.ScopeApplies(
                    ruleset.Text(
                        aux.DocumentDomainsOffset,
                        aux.DocumentDomainsLength),
                    documentHost))
            {
                return false;
            }

            if (aux.RequestDomainsLength > 0 &&
                !DomainMatcher.ScopeApplies(
                    ruleset.Text(
                        aux.RequestDomainsOffset,
                        aux.RequestDomainsLength),
                    host))
            {
                return false;
            }

            if (aux.DenyAllowLength > 0 &&
                DomainMatcher.ContainsHost(
                    ruleset.Text(aux.DenyAllowOffset, aux.DenyAllowLength),
                    host))
            {
                return false;
            }
        }

        if (rule.Has(RecordFlags.Regex))
        {
            return ruleset.RegexMatches(ruleIndex, request.Url);
        }

        return PatternMatcher.Matches(
            ruleset.Text(rule.PatternOffset, rule.PatternLength),
            request.Url,
            layout,
            rule.TypedFlags);
    }

    private struct Verdict
    {
        public bool ImportantException;
        public bool ImportantBlock;
        public bool Exception;
        public bool Block;
        public string? ImportantRedirect;
        public string? RedirectResource;
        public string? RedirectRuleResource;

        /// <summary>
        /// Precedence, strongest first: an important exception, an important
        /// block, an ordinary exception, an ordinary block.
        /// </summary>
        public readonly FilterDecision Resolve()
        {
            if (ImportantException)
            {
                return FilterDecision.Allowed(FilterReason.Exception);
            }

            if (ImportantBlock)
            {
                return Decide(ImportantRedirect ?? RedirectRuleResource);
            }

            if (Exception)
            {
                return FilterDecision.Allowed(FilterReason.Exception);
            }

            return Block
                ? Decide(RedirectResource ?? RedirectRuleResource)
                : FilterDecision.Allow;
        }

        private static FilterDecision Decide(string? resource) =>
            string.IsNullOrEmpty(resource)
                ? FilterDecision.Block
                : FilterDecision.Redirect(resource);
    }
}

/// <summary>
/// What a page on a given host should have injected. Resolved per navigation.
/// </summary>
/// <param name="Selectors">Rules written for this host. Few, and injected
/// wherever the host's page runs.</param>
/// <param name="GenericSelectors">Rules written for no host in particular.
/// Tens of thousands of them, so they go into the top document only — a
/// frame-heavy page would otherwise parse the whole set once per frame.</param>
public sealed record CosmeticPayload(
    IReadOnlyList<string> Selectors,
    IReadOnlyList<string> ProceduralSelectors,
    IReadOnlyList<string> Scriptlets,
    IReadOnlyList<string> GenericSelectors)
{
    public static CosmeticPayload Empty { get; } = new([], [], [], []);

    public bool IsEmpty =>
        Selectors.Count == 0 &&
        GenericSelectors.Count == 0 &&
        ProceduralSelectors.Count == 0 &&
        Scriptlets.Count == 0;
}
