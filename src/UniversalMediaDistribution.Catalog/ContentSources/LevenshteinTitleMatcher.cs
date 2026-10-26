using System.Buffers;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.Catalog.ContentSources;

public static class LevenshteinTitleMatcher
{
    public const double DefaultMinimumSimilarity = 0.72;

    /// <summary>
    /// How much of the longer title the shorter one must cover before
    /// containment is treated as the same work rather than a title that merely
    /// starts the same way.
    /// </summary>
    public const double ContainmentMajority = 0.5;

    public static double Similarity(string left, string right)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(left);
        ArgumentException.ThrowIfNullOrWhiteSpace(right);
        var normalizedLeft = Normalize(left);
        var normalizedRight = Normalize(right);
        if (normalizedLeft.Length == 0 || normalizedRight.Length == 0)
        {
            return 0;
        }

        return SimilarityNormalized(normalizedLeft, normalizedRight);
    }

    internal static double SimilarityNormalized(
        ReadOnlySpan<char> normalizedLeft,
        ReadOnlySpan<char> normalizedRight)
    {
        if (normalizedLeft.Length == 0 || normalizedRight.Length == 0)
        {
            return 0;
        }

        if (normalizedLeft.Contains(
                normalizedRight,
                StringComparison.Ordinal) ||
            normalizedRight.Contains(
                normalizedLeft,
                StringComparison.Ordinal))
        {
            // One title containing the other is only evidence of the same work
            // when the shorter one is most of the longer one — "Hidden
            // Logistics Center" inside "Hidden Logistics Center in the
            // Apocalypse" is the same series written more fully. Scoring every
            // containment a flat 1 also made "Avatar" a perfect match for
            // "Avatar: The Legend of Korra", and "Naruto" for "Naruto
            // Shippuden", which are separate works that merely start the same
            // way. Below the majority mark the plain edit-distance score is
            // used, which for a contained title is exactly that ratio.
            var shorter = Math.Min(normalizedLeft.Length, normalizedRight.Length);
            var longer = Math.Max(normalizedLeft.Length, normalizedRight.Length);
            var ratio = (double)shorter / longer;
            return ratio >= ContainmentMajority ? 1 : ratio;
        }

        var distance = Distance(normalizedLeft, normalizedRight);
        return 1d -
            (double)distance / Math.Max(normalizedLeft.Length, normalizedRight.Length);
    }

    /// <summary>
    /// How much longer one title is than the other once normalized, for
    /// separating titles that <see cref="SimilarityNormalized"/> cannot.
    /// <para>
    /// Containment still scores a flat 1 whenever the shorter title covers
    /// most of the longer one, so against "All American" both the exact series
    /// and its "All American: Homecoming" spin-off can tie. Ranking the gap
    /// puts the title that is actually the requested one first; ordering by
    /// provider id instead picked whichever route happened to sort first.
    /// </para>
    /// </summary>
    public static int NormalizedLengthGap(string left, string right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return Math.Abs(Normalize(left).Length - Normalize(right).Length);
    }

    internal static int NormalizedLengthGap(
        ReadOnlySpan<char> normalizedLeft,
        ReadOnlySpan<char> normalizedRight) =>
        Math.Abs(normalizedLeft.Length - normalizedRight.Length);

    public static int Distance(string left, string right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return Distance(left.AsSpan(), right.AsSpan());
    }

    internal static int Distance(
        ReadOnlySpan<char> left,
        ReadOnlySpan<char> right)
    {
        if (left.Length > right.Length)
        {
            var swap = left;
            left = right;
            right = swap;
        }

        const int maximumStackLength = 256;
        var rowLength = left.Length + 1;
        int[]? rented = null;
        Span<int> row = rowLength <= maximumStackLength
            ? stackalloc int[rowLength]
            : (rented = ArrayPool<int>.Shared.Rent(rowLength))
                .AsSpan(0, rowLength);
        try
        {
            for (var index = 0; index < rowLength; index++)
            {
                row[index] = index;
            }

            for (var rightIndex = 1;
                 rightIndex <= right.Length;
                 rightIndex++)
            {
                var previousDiagonal = row[0];
                row[0] = rightIndex;
                for (var leftIndex = 1;
                     leftIndex <= left.Length;
                     leftIndex++)
                {
                    var previousRowValue = row[leftIndex];
                    var substitutionCost =
                        left[leftIndex - 1] == right[rightIndex - 1]
                            ? 0
                            : 1;
                    row[leftIndex] = Math.Min(
                        Math.Min(
                            row[leftIndex - 1] + 1,
                            previousRowValue + 1),
                        previousDiagonal + substitutionCost);
                    previousDiagonal = previousRowValue;
                }
            }

            return row[left.Length];
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<int>.Shared.Return(rented);
            }
        }
    }

    internal static string Normalize(string value)
    {
        if (!CatalogTransferIdentity.IsUsableName(value)) return string.Empty;
        var normalized = NameNormalizer.Normalize(value);
        var length = 0;
        foreach (var character in normalized)
        {
            if (char.IsLetterOrDigit(character))
            {
                length++;
            }
        }

        return string.Create(
            length,
            normalized,
            static (destination, source) =>
            {
                var index = 0;
                foreach (var character in source)
                {
                    if (char.IsLetterOrDigit(character))
                    {
                        destination[index++] = character;
                    }
                }
            });
    }
}
