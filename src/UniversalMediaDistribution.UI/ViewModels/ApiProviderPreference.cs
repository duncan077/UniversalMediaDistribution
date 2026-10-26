namespace UniversalMediaDistribution.UI.ViewModels;

internal static class ApiProviderPreference
{
    /// <summary>
    /// A lookup explicitly started from "Find on enabled APIs" always stops
    /// for confirmation once it has a candidate, even when there is only one.
    /// Discovery proves that the provider carries the title and episode; it
    /// does not prove that its final video endpoint still works. Auto-opening
    /// a sole candidate used to erase the picker and surface that endpoint
    /// failure as soon as the search completed.
    /// </summary>
    public static bool ShouldShowManualPicker(int candidateCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(candidateCount);
        return candidateCount > 0;
    }

    public static int FindPreferredProviderIndex<T>(
        IReadOnlyList<T> candidates,
        string? preferredProviderId,
        Func<T, string> providerIdSelector)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(providerIdSelector);
        if (string.IsNullOrWhiteSpace(preferredProviderId))
        {
            return -1;
        }

        for (var index = 0; index < candidates.Count; index++)
        {
            if (string.Equals(
                    providerIdSelector(candidates[index]),
                    preferredProviderId,
                    StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    public static int GetAttemptIndex(
        int attempt,
        int candidateCount,
        int preferredIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(candidateCount);
        ArgumentOutOfRangeException.ThrowIfNegative(attempt);
        if (attempt >= candidateCount)
        {
            throw new ArgumentOutOfRangeException(nameof(attempt));
        }

        if (preferredIndex < -1 || preferredIndex >= candidateCount)
        {
            throw new ArgumentOutOfRangeException(nameof(preferredIndex));
        }

        if (preferredIndex <= 0)
        {
            return attempt;
        }

        return attempt == 0
            ? preferredIndex
            : attempt <= preferredIndex
                ? attempt - 1
                : attempt;
    }
}
