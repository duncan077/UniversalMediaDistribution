using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using UniversalMediaDistribution.ContentFiltering.Compilation;
using UniversalMediaDistribution.ContentFiltering.Matching;

namespace UniversalMediaDistribution.ContentFiltering;

/// <summary>
/// A compiled ruleset, read directly out of one contiguous buffer.
///
/// Nothing here is materialized into per-rule objects: the sections are spans
/// over the buffer the caller supplied, so a ruleset of a hundred thousand
/// rules is a handful of large allocations and the garbage collector never
/// walks it. The buffer may equally be a file read or a memory-mapped view,
/// which is what lets a memory-constrained device keep the pages evictable.
/// </summary>
public sealed class CompiledRuleset
{
    private readonly ReadOnlyMemory<byte> _blob;
    private readonly int[] _offsets;
    private readonly int[] _lengths;
    private readonly RegexRuleCache _regexes;
    private readonly Dictionary<int, string> _redirectNames;

    private CompiledRuleset(
        ReadOnlyMemory<byte> blob,
        int[] offsets,
        int[] lengths,
        bool isMemoryMapped)
    {
        _blob = blob;
        _offsets = offsets;
        _lengths = lengths;
        IsMemoryMapped = isMemoryMapped;
        _regexes = new RegexRuleCache(index =>
        {
            var rule = Rules[index];
            return (Decode(rule.PatternOffset, rule.PatternLength), rule.Has(RecordFlags.MatchCase));
        });
        _redirectNames = BuildRedirectNames();
    }

    /// <summary>
    /// True when the bytes are a file-backed mapping rather than a managed
    /// array — which is what keeps them off a constrained device's heap.
    /// </summary>
    public bool IsMemoryMapped { get; }

    /// <summary>A ruleset that matches nothing. The safe starting state.</summary>
    public static CompiledRuleset Empty { get; } =
        Load(new RulesetBlob.Writer().Build());

    /// <summary>
    /// The blob layout this build understands. A cached ruleset carrying any
    /// other version has to be recompiled from its source lists.
    /// </summary>
    public static int FormatVersion => RulesetBlob.FormatVersion;

    public int ByteLength => _blob.Length;

    public int NetworkRuleCount => Rules.Length;

    public int CosmeticRuleCount => CosmeticRecords.Length;

    /// <summary>
    /// Rules no token could index. Every request pays for these, so the
    /// compiler keeps the count small on purpose.
    /// </summary>
    public int AlwaysEvaluatedRuleCount => AlwaysRuleIndices.Length;

    public static CompiledRuleset Load(ReadOnlyMemory<byte> blob)
    {
        if (!TryLoad(blob, out var ruleset))
        {
            throw new InvalidDataException(
                "The compiled ruleset is not in a readable format.");
        }

        return ruleset;
    }

    /// <summary>
    /// Reads a blob's header. Returns false for a truncated buffer or a format
    /// version this build does not know, both of which mean "recompile from
    /// the cached lists" rather than "fail".
    /// </summary>
    public static bool TryLoad(
        ReadOnlyMemory<byte> blob,
        out CompiledRuleset ruleset) =>
        TryLoad(blob, out ruleset, isMapped: false);

    internal static bool TryLoad(
        ReadOnlyMemory<byte> blob,
        out CompiledRuleset ruleset,
        bool isMapped)
    {
        ruleset = null!;
        var span = blob.Span;
        if (span.Length < RulesetBlob.DataStart ||
            BinaryPrimitives.ReadInt32LittleEndian(span) != RulesetBlob.Magic ||
            BinaryPrimitives.ReadInt32LittleEndian(span[4..]) !=
                RulesetBlob.FormatVersion ||
            BinaryPrimitives.ReadInt32LittleEndian(span[8..]) !=
                RulesetBlob.SectionCount)
        {
            return false;
        }

        var offsets = new int[RulesetBlob.SectionCount];
        var lengths = new int[RulesetBlob.SectionCount];
        for (var index = 0; index < RulesetBlob.SectionCount; index++)
        {
            var entry = span[(RulesetBlob.HeaderLength + index * 8)..];
            var offset = BinaryPrimitives.ReadInt32LittleEndian(entry);
            var length = BinaryPrimitives.ReadInt32LittleEndian(entry[4..]);
            if (offset < 0 ||
                length < 0 ||
                (long)offset + length > span.Length)
            {
                return false;
            }

            if (length % ElementSize((RulesetSection)index) != 0)
            {
                // A section that is not a whole number of records cannot be
                // reinterpreted, and a partial record is not something to
                // guess at.
                return false;
            }

            offsets[index] = offset;
            lengths[index] = length;
        }

        ruleset = new CompiledRuleset(blob, offsets, lengths, isMapped);
        return true;
    }

    private static int ElementSize(RulesetSection section) =>
        section switch
        {
            RulesetSection.Pool => 1,
            RulesetSection.Rules =>
                Marshal.SizeOf<NetworkRuleRecord>(),
            RulesetSection.Aux => Marshal.SizeOf<NetworkRuleAux>(),
            RulesetSection.CosmeticRecords =>
                Marshal.SizeOf<CosmeticRecord>(),
            RulesetSection.TokenBitset => sizeof(ulong),
            _ => sizeof(int)
        };

    internal ReadOnlySpan<byte> Pool => Bytes(RulesetSection.Pool);

    internal ReadOnlySpan<NetworkRuleRecord> Rules =>
        Cast<NetworkRuleRecord>(RulesetSection.Rules);

    internal ReadOnlySpan<NetworkRuleAux> Aux =>
        Cast<NetworkRuleAux>(RulesetSection.Aux);

    internal ReadOnlySpan<int> TokenHashes =>
        Cast<int>(RulesetSection.TokenHashes);

    internal ReadOnlySpan<int> TokenBucketStarts =>
        Cast<int>(RulesetSection.TokenBucketStarts);

    internal ReadOnlySpan<int> TokenRuleIndices =>
        Cast<int>(RulesetSection.TokenRuleIndices);

    internal ReadOnlySpan<int> AlwaysRuleIndices =>
        Cast<int>(RulesetSection.AlwaysRuleIndices);

    internal ReadOnlySpan<ulong> TokenBitset =>
        Cast<ulong>(RulesetSection.TokenBitset);

    internal ReadOnlySpan<CosmeticRecord> CosmeticRecords =>
        Cast<CosmeticRecord>(RulesetSection.CosmeticRecords);

    internal ReadOnlySpan<int> CosmeticHostHashes =>
        Cast<int>(RulesetSection.CosmeticHostHashes);

    internal ReadOnlySpan<int> CosmeticHostStarts =>
        Cast<int>(RulesetSection.CosmeticHostStarts);

    internal ReadOnlySpan<int> CosmeticHostIndices =>
        Cast<int>(RulesetSection.CosmeticHostIndices);

    internal ReadOnlySpan<int> CosmeticGenericIndices =>
        Cast<int>(RulesetSection.CosmeticGenericIndices);

    /// <summary>
    /// True when no rule anywhere uses this token. One array probe, and it is
    /// the answer for the overwhelming majority of a URL's tokens.
    /// </summary>
    internal bool IsUnusedToken(int tokenHash)
    {
        var bitset = TokenBitset;
        if (bitset.IsEmpty)
        {
            return true;
        }

        var bit = (uint)tokenHash & (uint)(bitset.Length * 64 - 1);
        return (bitset[(int)(bit >> 6)] & 1UL << (int)(bit & 63)) == 0;
    }

    internal ReadOnlySpan<byte> Text(int offset, int length) =>
        length <= 0 ? [] : Pool.Slice(offset, length);

    internal bool RegexMatches(int ruleIndex, ReadOnlySpan<char> input) =>
        _regexes.IsMatch(ruleIndex, input);

    internal string? RedirectName(int offset) =>
        _redirectNames.GetValueOrDefault(offset);

    internal string Decode(int offset, int length) =>
        length <= 0 ? string.Empty : Encoding.UTF8.GetString(Text(offset, length));

    private ReadOnlySpan<byte> Bytes(RulesetSection section) =>
        _blob.Span.Slice(_offsets[(int)section], _lengths[(int)section]);

    private ReadOnlySpan<T> Cast<T>(RulesetSection section)
        where T : unmanaged =>
        MemoryMarshal.Cast<byte, T>(Bytes(section));

    private Dictionary<int, string> BuildRedirectNames()
    {
        var aux = Aux;
        if (aux.IsEmpty)
        {
            return [];
        }

        var result = new Dictionary<int, string>();
        foreach (var entry in aux)
        {
            if (entry.RedirectLength > 0 &&
                !result.ContainsKey(entry.RedirectOffset))
            {
                result[entry.RedirectOffset] =
                    Decode(entry.RedirectOffset, entry.RedirectLength);
            }
        }

        return result;
    }
}
