using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace UniversalMediaDistribution.ContentFiltering.Compilation;

internal enum RulesetSection
{
    /// <summary>Every pattern, domain list, selector, and name, as UTF-8.</summary>
    Pool = 0,

    Rules = 1,
    Aux = 2,
    TokenHashes = 3,
    TokenBucketStarts = 4,
    TokenRuleIndices = 5,

    /// <summary>Rules no token could index, evaluated for every request.</summary>
    AlwaysRuleIndices = 6,

    /// <summary>
    /// A bit per token hash. One array probe answers "no rule uses this
    /// token", which is the answer for almost every token in a URL.
    /// </summary>
    TokenBitset = 7,

    RegexRuleIndices = 8,
    CosmeticRecords = 9,
    CosmeticHostHashes = 10,
    CosmeticHostStarts = 11,
    CosmeticHostIndices = 12,
    CosmeticGenericIndices = 13
}

/// <summary>
/// The on-disk and in-memory shape of a compiled ruleset: one contiguous
/// buffer of aligned sections. Loading is a header read, not a parse, and the
/// same buffer can come from a file read or a memory-mapped view.
/// </summary>
internal static class RulesetBlob
{
    public const int SectionCount = 14;

    /// <summary>
    /// Bumped whenever a section's meaning changes. A reader that sees an
    /// unfamiliar version recompiles from the cached lists rather than
    /// misreading bytes.
    /// </summary>
    public const int FormatVersion = 1;

    public const int Magic = 0x46444D55; // "UMDF"

    private const int Alignment = 8;

    public const int HeaderLength = 16;

    public const int TableLength = SectionCount * 8;

    public static int DataStart => Align(HeaderLength + TableLength);

    public static int Align(int value) =>
        (value + Alignment - 1) / Alignment * Alignment;

    public sealed class Writer
    {
        private readonly byte[][] _sections =
            new byte[SectionCount][];

        public Writer()
        {
            for (var index = 0; index < SectionCount; index++)
            {
                _sections[index] = [];
            }
        }

        public void Set<T>(RulesetSection section, ReadOnlySpan<T> values)
            where T : unmanaged =>
            _sections[(int)section] = MemoryMarshal.AsBytes(values).ToArray();

        public byte[] Build()
        {
            var offsets = new int[SectionCount];
            var lengths = new int[SectionCount];
            var cursor = DataStart;
            for (var index = 0; index < SectionCount; index++)
            {
                offsets[index] = cursor;
                lengths[index] = _sections[index].Length;
                cursor = Align(cursor + lengths[index]);
            }

            var blob = new byte[cursor];
            var span = blob.AsSpan();
            BinaryPrimitives.WriteInt32LittleEndian(span, Magic);
            BinaryPrimitives.WriteInt32LittleEndian(span[4..], FormatVersion);
            BinaryPrimitives.WriteInt32LittleEndian(span[8..], SectionCount);
            BinaryPrimitives.WriteInt32LittleEndian(span[12..], 0);

            for (var index = 0; index < SectionCount; index++)
            {
                var entry = span[(HeaderLength + index * 8)..];
                BinaryPrimitives.WriteInt32LittleEndian(entry, offsets[index]);
                BinaryPrimitives.WriteInt32LittleEndian(
                    entry[4..],
                    lengths[index]);
                _sections[index].CopyTo(span[offsets[index]..]);
            }

            return blob;
        }
    }
}
