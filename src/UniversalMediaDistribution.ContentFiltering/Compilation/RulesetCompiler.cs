using System.Runtime.InteropServices;
using System.Text;
using UniversalMediaDistribution.ContentFiltering.Matching;
using UniversalMediaDistribution.ContentFiltering.Parsing;

namespace UniversalMediaDistribution.ContentFiltering.Compilation;

public sealed record RulesetCompilerOptions
{
    /// <summary>
    /// How many rules may sit in the bucket every request has to evaluate.
    /// This is the one cost that scales with nothing, so it is capped rather
    /// than trusted.
    /// </summary>
    public int MaxAlwaysEvaluatedRules { get; init; } = 2048;

    /// <summary>
    /// Regular-expression rules are linear-time but still far more expensive
    /// than a literal compare, and they cannot be indexed by token.
    /// </summary>
    public int MaxRegexRules { get; init; } = 1024;

    /// <summary>
    /// Generic element hiding is tens of thousands of selectors evaluated
    /// against every element on every style recalculation. A constrained
    /// device declines it here rather than paying for it on every frame.
    /// </summary>
    public bool IncludeGenericCosmetics { get; init; } = true;

    /// <summary>
    /// Procedural selectors need DOM work on every mutation, so they are the
    /// second thing a constrained device gives up.
    /// </summary>
    public bool IncludeProceduralCosmetics { get; init; } = true;

    public static RulesetCompilerOptions Standard { get; } = new();

    public static RulesetCompilerOptions Constrained { get; } = new()
    {
        IncludeGenericCosmetics = false,
        IncludeProceduralCosmetics = false,
        MaxAlwaysEvaluatedRules = 512,
        MaxRegexRules = 128
    };
}

public sealed record RulesetCompilationReport
{
    public int NetworkRules { get; init; }

    public int CosmeticRules { get; init; }

    public int AlwaysEvaluatedRules { get; init; }

    public int RegexRules { get; init; }

    public int TokenBuckets { get; init; }

    public int ByteLength { get; init; }

    /// <summary>Rules removed by a matching <c>$badfilter</c>.</summary>
    public int RemovedByBadFilter { get; init; }

    /// <summary>
    /// Rules dropped because a cap was reached. A non-zero value here is worth
    /// looking at: it means coverage was traded for predictable cost.
    /// </summary>
    public int DroppedForCapacity { get; init; }

    public int DuplicateRules { get; init; }
}

/// <summary>
/// Turns parsed filter lists into the contiguous ruleset the engine matches
/// against. Everything expensive happens here, once, off the request path.
/// </summary>
public static class RulesetCompiler
{
    /// <summary>
    /// A pattern longer than this is not a filter anyone wrote; it is a
    /// malformed line. The cap also keeps a pattern reference inside the
    /// sixteen-bit length a rule record carries.
    /// </summary>
    private const int MaxPatternLength = 2048;

    /// <summary>
    /// Each wildcard multiplies the positions the matcher may have to try.
    /// Real filters use one or two.
    /// </summary>
    private const int MaxWildcards = 4;

    public static byte[] Compile(
        FilterListParseResult list,
        out RulesetCompilationReport report,
        RulesetCompilerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(list);
        return Compile([list], out report, options);
    }

    public static byte[] Compile(
        IReadOnlyList<FilterListParseResult> lists,
        out RulesetCompilationReport report,
        RulesetCompilerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(lists);
        var settings = options ?? RulesetCompilerOptions.Standard;
        var pool = new StringPool();
        var writer = new RulesetBlob.Writer();

        var network = SelectNetworkRules(
            lists,
            out var removedByBadFilter,
            out var duplicates);

        var records = new List<NetworkRuleRecord>(network.Count);
        var aux = new List<NetworkRuleAux>();
        var buckets = new Dictionary<int, List<int>>();
        var alwaysExceptions = new List<int>();
        var alwaysBlocks = new List<int>();
        var regexIndices = new List<int>();
        var dropped = 0;

        foreach (var rule in network)
        {
            var body = PatternAnalyzer.Normalize(
                rule.Pattern.AsSpan(),
                out var anchors);
            if (body.IsEmpty || rule.Pattern.Length > MaxPatternLength)
            {
                dropped++;
                continue;
            }

            var isRegex = (rule.Flags & NetworkRuleFlags.Regex) != 0;
            if (isRegex)
            {
                if (regexIndices.Count >= settings.MaxRegexRules)
                {
                    dropped++;
                    continue;
                }

                // A regular expression has no literal to index, and its body
                // is the whole written pattern rather than an anchored one.
                body = rule.Pattern.AsSpan();
                anchors = RecordFlags.None;
            }
            else if (!IsMatchable(body))
            {
                dropped++;
                continue;
            }

            var index = records.Count;
            var patternRef = pool.Add(body);
            records.Add(new NetworkRuleRecord(
                patternRef.Offset,
                (ushort)patternRef.Length,
                (ushort)rule.Types,
                (ushort)rule.Methods,
                (ushort)(ToRecordFlags(rule.Flags) | anchors),
                AddAux(pool, aux, rule)));

            if (isRegex)
            {
                regexIndices.Add(index);
                AddAlways(rule, index, alwaysExceptions, alwaysBlocks);
                continue;
            }

            if (PatternAnalyzer.TrySelectToken(body, anchors, out var token))
            {
                if (!buckets.TryGetValue(token, out var bucket))
                {
                    bucket = [];
                    buckets[token] = bucket;
                }

                bucket.Add(index);
                continue;
            }

            AddAlways(rule, index, alwaysExceptions, alwaysBlocks);
        }

        var always = BuildAlways(
            alwaysExceptions,
            alwaysBlocks,
            settings.MaxAlwaysEvaluatedRules,
            ref dropped);

        var tokenHashes = new int[buckets.Count];
        buckets.Keys.CopyTo(tokenHashes, 0);
        Array.Sort(tokenHashes);

        var bucketStarts = new int[tokenHashes.Length + 1];
        var ruleIndices = new List<int>();
        for (var index = 0; index < tokenHashes.Length; index++)
        {
            bucketStarts[index] = ruleIndices.Count;
            ruleIndices.AddRange(buckets[tokenHashes[index]]);
        }

        bucketStarts[^1] = ruleIndices.Count;

        var cosmetics = CompileCosmetics(lists, settings, pool, writer);

        writer.Set<byte>(RulesetSection.Pool, pool.Bytes);
        writer.Set<NetworkRuleRecord>(
            RulesetSection.Rules,
            CollectionsMarshal.AsSpan(records));
        writer.Set<NetworkRuleAux>(
            RulesetSection.Aux,
            CollectionsMarshal.AsSpan(aux));
        writer.Set<int>(RulesetSection.TokenHashes, tokenHashes);
        writer.Set<int>(RulesetSection.TokenBucketStarts, bucketStarts);
        writer.Set<int>(
            RulesetSection.TokenRuleIndices,
            CollectionsMarshal.AsSpan(ruleIndices));
        writer.Set<int>(
            RulesetSection.AlwaysRuleIndices,
            CollectionsMarshal.AsSpan(always));
        writer.Set<int>(
            RulesetSection.RegexRuleIndices,
            CollectionsMarshal.AsSpan(regexIndices));
        writer.Set<ulong>(
            RulesetSection.TokenBitset,
            BuildBitset(tokenHashes));

        var blob = writer.Build();
        report = new RulesetCompilationReport
        {
            NetworkRules = records.Count,
            CosmeticRules = cosmetics,
            AlwaysEvaluatedRules = always.Count,
            RegexRules = regexIndices.Count,
            TokenBuckets = tokenHashes.Length,
            ByteLength = blob.Length,
            RemovedByBadFilter = removedByBadFilter,
            DroppedForCapacity = dropped,
            DuplicateRules = duplicates
        };
        return blob;
    }

    /// <summary>
    /// Rejects patterns the literal matcher cannot handle: non-ASCII text,
    /// which is compared byte-to-character and would mis-compare, and patterns
    /// so full of wildcards that matching them could backtrack for a long
    /// time on the network thread.
    /// </summary>
    private static bool IsMatchable(ReadOnlySpan<char> pattern)
    {
        var wildcards = 0;
        foreach (var current in pattern)
        {
            if (current > 127)
            {
                return false;
            }

            if (current == '*' && ++wildcards > MaxWildcards)
            {
                return false;
            }
        }

        return true;
    }

    private static void AddAlways(
        ParsedNetworkRule rule,
        int index,
        List<int> exceptions,
        List<int> blocks)
    {
        if (rule.IsException)
        {
            exceptions.Add(index);
        }
        else
        {
            blocks.Add(index);
        }
    }

    /// <summary>
    /// Exceptions are never dropped for capacity. Losing a blocking rule costs
    /// coverage; losing the exception that keeps a page working costs the page.
    /// </summary>
    private static List<int> BuildAlways(
        List<int> exceptions,
        List<int> blocks,
        int cap,
        ref int dropped)
    {
        var always = new List<int>(exceptions);
        var room = Math.Max(0, cap - always.Count);
        if (blocks.Count > room)
        {
            dropped += blocks.Count - room;
        }

        always.AddRange(blocks.Take(room));
        always.Sort();
        return always;
    }

    private static List<ParsedNetworkRule> SelectNetworkRules(
        IReadOnlyList<FilterListParseResult> lists,
        out int removedByBadFilter,
        out int duplicates)
    {
        var badFilters = new HashSet<RuleSignature>();
        foreach (var list in lists)
        {
            foreach (var rule in list.NetworkRules)
            {
                if ((rule.Flags & NetworkRuleFlags.BadFilter) != 0)
                {
                    badFilters.Add(Signature(rule));
                }
            }
        }

        removedByBadFilter = 0;
        duplicates = 0;
        var seen = new HashSet<RuleSignature>();
        var selected = new List<ParsedNetworkRule>();
        foreach (var list in lists)
        {
            foreach (var rule in list.NetworkRules)
            {
                if ((rule.Flags & NetworkRuleFlags.BadFilter) != 0)
                {
                    continue;
                }

                var signature = Signature(rule);
                if (badFilters.Contains(signature))
                {
                    removedByBadFilter++;
                    continue;
                }

                if (!seen.Add(signature))
                {
                    duplicates++;
                    continue;
                }

                selected.Add(rule);
            }
        }

        return selected;
    }

    /// <summary>
    /// The identity a <c>$badfilter</c> rule names: everything about a rule
    /// except the <c>$badfilter</c> option itself.
    /// </summary>
    private static RuleSignature Signature(ParsedNetworkRule rule) =>
        new(
            rule.Pattern,
            rule.Types,
            rule.Flags & ~NetworkRuleFlags.BadFilter,
            rule.Methods,
            rule.DocumentDomains,
            rule.RequestDomains,
            rule.DenyAllowDomains,
            rule.RedirectResource);

    private readonly record struct RuleSignature(
        string Pattern,
        ResourceType Types,
        NetworkRuleFlags Flags,
        RequestMethod Methods,
        string? DocumentDomains,
        string? RequestDomains,
        string? DenyAllowDomains,
        string? RedirectResource);

    private static int AddAux(
        StringPool pool,
        List<NetworkRuleAux> aux,
        ParsedNetworkRule rule)
    {
        if (rule.DocumentDomains is null &&
            rule.RequestDomains is null &&
            rule.DenyAllowDomains is null &&
            rule.RedirectResource is null)
        {
            return -1;
        }

        var document = pool.Add(rule.DocumentDomains);
        var request = pool.Add(rule.RequestDomains);
        var denyAllow = pool.Add(rule.DenyAllowDomains);
        var redirect = pool.Add(rule.RedirectResource);
        aux.Add(new NetworkRuleAux(
            document.Offset,
            document.Length,
            request.Offset,
            request.Length,
            denyAllow.Offset,
            denyAllow.Length,
            redirect.Offset,
            redirect.Length));
        return aux.Count - 1;
    }

    private static RecordFlags ToRecordFlags(NetworkRuleFlags flags)
    {
        var result = RecordFlags.None;
        if ((flags & NetworkRuleFlags.Exception) != 0)
        {
            result |= RecordFlags.Exception;
        }

        if ((flags & NetworkRuleFlags.Important) != 0)
        {
            result |= RecordFlags.Important;
        }

        if ((flags & NetworkRuleFlags.ThirdPartyOnly) != 0)
        {
            result |= RecordFlags.ThirdPartyOnly;
        }

        if ((flags & NetworkRuleFlags.FirstPartyOnly) != 0)
        {
            result |= RecordFlags.FirstPartyOnly;
        }

        if ((flags & NetworkRuleFlags.MatchCase) != 0)
        {
            result |= RecordFlags.MatchCase;
        }

        if ((flags & NetworkRuleFlags.Regex) != 0)
        {
            result |= RecordFlags.Regex;
        }

        if ((flags & NetworkRuleFlags.Redirect) != 0)
        {
            result |= RecordFlags.Redirect;
        }

        if ((flags & NetworkRuleFlags.RedirectRule) != 0)
        {
            result |= RecordFlags.RedirectRule;
        }

        return result;
    }

    private static ulong[] BuildBitset(int[] tokenHashes)
    {
        if (tokenHashes.Length == 0)
        {
            return [];
        }

        // Roughly eight bits per token keeps the false-positive rate low
        // enough that a miss almost always ends at the bit test.
        var bits = 4096;
        while (bits < tokenHashes.Length * 8 && bits < 1 << 21)
        {
            bits <<= 1;
        }

        var words = new ulong[bits / 64];
        foreach (var hash in tokenHashes)
        {
            var bit = (uint)hash & (uint)(bits - 1);
            words[bit >> 6] |= 1UL << (int)(bit & 63);
        }

        return words;
    }

    private static int CompileCosmetics(
        IReadOnlyList<FilterListParseResult> lists,
        RulesetCompilerOptions settings,
        StringPool pool,
        RulesetBlob.Writer writer)
    {
        var records = new List<CosmeticRecord>();
        var byHost = new Dictionary<int, List<int>>();
        var generic = new List<int>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var list in lists)
        {
            foreach (var rule in list.CosmeticRules)
            {
                if (rule.IsProcedural && !settings.IncludeProceduralCosmetics)
                {
                    continue;
                }

                var domains = rule.Domains;
                var hosts = PositiveDomains(domains);
                if (hosts.Count == 0 &&
                    !settings.IncludeGenericCosmetics &&
                    rule.Kind == CosmeticRuleKind.Selector &&
                    !rule.IsException)
                {
                    continue;
                }

                if (!seen.Add(
                    $"{domains}{rule.Kind}{rule.IsException}" +
                    $"{rule.Body}"))
                {
                    continue;
                }

                var body = pool.Add(rule.Body);
                var scope = pool.Add(domains);
                var flags = CosmeticFlags.None;
                if (rule.Kind == CosmeticRuleKind.Scriptlet)
                {
                    flags |= CosmeticFlags.Scriptlet;
                }

                if (rule.IsException)
                {
                    flags |= CosmeticFlags.Exception;
                }

                if (rule.IsProcedural)
                {
                    flags |= CosmeticFlags.Procedural;
                }

                var index = records.Count;
                records.Add(new CosmeticRecord(
                    body.Offset,
                    body.Length,
                    scope.Offset,
                    scope.Length,
                    (int)flags));

                if (hosts.Count == 0)
                {
                    generic.Add(index);
                    continue;
                }

                foreach (var host in hosts)
                {
                    var hash = FilterTokenizer.Hash(host);
                    if (!byHost.TryGetValue(hash, out var bucket))
                    {
                        bucket = [];
                        byHost[hash] = bucket;
                    }

                    bucket.Add(index);
                }
            }
        }

        var hostHashes = new int[byHost.Count];
        byHost.Keys.CopyTo(hostHashes, 0);
        Array.Sort(hostHashes);

        var starts = new int[hostHashes.Length + 1];
        var indices = new List<int>();
        for (var index = 0; index < hostHashes.Length; index++)
        {
            starts[index] = indices.Count;
            indices.AddRange(byHost[hostHashes[index]]);
        }

        starts[^1] = indices.Count;

        writer.Set<CosmeticRecord>(
            RulesetSection.CosmeticRecords,
            CollectionsMarshal.AsSpan(records));
        writer.Set<int>(RulesetSection.CosmeticHostHashes, hostHashes);
        writer.Set<int>(RulesetSection.CosmeticHostStarts, starts);
        writer.Set<int>(
            RulesetSection.CosmeticHostIndices,
            CollectionsMarshal.AsSpan(indices));
        writer.Set<int>(
            RulesetSection.CosmeticGenericIndices,
            CollectionsMarshal.AsSpan(generic));
        return records.Count;
    }

    private static List<string> PositiveDomains(string? domains)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(domains))
        {
            return result;
        }

        foreach (var range in domains.AsSpan().Split(','))
        {
            var entry = domains.AsSpan()[range].Trim();
            if (entry.IsEmpty || entry[0] == '~' || entry.SequenceEqual("*"))
            {
                continue;
            }

            result.Add(entry.ToString().ToLowerInvariant());
        }

        return result;
    }

    /// <summary>
    /// Interns every string the ruleset needs into one UTF-8 buffer. Rules
    /// then reference text by offset, which is what keeps them free of
    /// references and the whole ruleset free of per-rule allocations.
    /// </summary>
    private sealed class StringPool
    {
        private readonly Dictionary<string, PoolReference> _known =
            new(StringComparer.Ordinal);
        private readonly List<byte> _bytes = [];

        public ReadOnlySpan<byte> Bytes => CollectionsMarshal.AsSpan(_bytes);

        public PoolReference Add(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return new PoolReference(0, 0);
            }

            if (_known.TryGetValue(value, out var existing))
            {
                return existing;
            }

            var encoded = Encoding.UTF8.GetBytes(value);
            var reference = new PoolReference(_bytes.Count, encoded.Length);
            _bytes.AddRange(encoded);
            _known[value] = reference;
            return reference;
        }

        public PoolReference Add(ReadOnlySpan<char> value) =>
            value.IsEmpty ? new PoolReference(0, 0) : Add(value.ToString());
    }

    private readonly record struct PoolReference(int Offset, int Length);
}
