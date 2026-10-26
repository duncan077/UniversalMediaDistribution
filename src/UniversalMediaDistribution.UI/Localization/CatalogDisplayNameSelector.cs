using System.Globalization;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.UI.Localization;

public static class CatalogDisplayNameSelector
{
    public static string Select(
        string primaryName,
        IEnumerable<CatalogAlternativeName>? alternativeNames,
        CultureInfo? culture = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(primaryName);
        culture ??= UiTextCatalog.CurrentCulture;
        var languageTag = culture.Name.ToLowerInvariant();
        var neutralLanguage = culture.TwoLetterISOLanguageName.ToLowerInvariant();

        // A linear scan for the single best candidate, not a sort: this runs
        // once per catalog card on every refresh, and only the winner is ever
        // read. Ties keep the first in source order, which is what the stable
        // OrderBy chain this replaced did.
        string? localized = null;
        var bestLanguageRank = int.MaxValue;
        var bestKindRank = int.MaxValue;
        if (alternativeNames is not null)
        {
            foreach (var name in alternativeNames)
            {
                if (string.IsNullOrWhiteSpace(name.Name) ||
                    string.Equals(name.Language, "und", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var languageRank = GetLanguageRank(
                    name.Language,
                    languageTag,
                    neutralLanguage);
                if (languageRank == int.MaxValue)
                {
                    continue;
                }

                var kindRank = GetKindRank(name.Kind);
                if (localized is not null &&
                    !IsBetterCandidate(
                        languageRank,
                        kindRank,
                        name.Name,
                        bestLanguageRank,
                        bestKindRank,
                        localized))
                {
                    continue;
                }

                localized = name.Name;
                bestLanguageRank = languageRank;
                bestKindRank = kindRank;
            }
        }

        return string.IsNullOrWhiteSpace(localized)
            ? primaryName.Trim()
            : localized;
    }

    private static bool IsBetterCandidate(
        int languageRank,
        int kindRank,
        string name,
        int bestLanguageRank,
        int bestKindRank,
        string bestName)
    {
        if (languageRank != bestLanguageRank)
        {
            return languageRank < bestLanguageRank;
        }

        if (kindRank != bestKindRank)
        {
            return kindRank < bestKindRank;
        }

        return StringComparer.CurrentCultureIgnoreCase.Compare(name, bestName) < 0;
    }

    // Both ranks are compared over a span of the original string. The Trim +
    // ToLowerInvariant these replaced allocated on every comparison, and this
    // is called for every alternative name of every card.
    private static int GetLanguageRank(
        string candidate,
        string languageTag,
        string neutralLanguage)
    {
        var normalized = candidate.AsSpan().Trim();
        if (normalized.Equals(languageTag, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (normalized.Equals(neutralLanguage, StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        // "es-AR" ranks under "es"; the separator is tested in place rather
        // than by building a "<language>-" prefix string per comparison.
        return normalized.Length > neutralLanguage.Length &&
               normalized[neutralLanguage.Length] == '-' &&
               normalized[..neutralLanguage.Length].Equals(
                   neutralLanguage,
                   StringComparison.OrdinalIgnoreCase)
            ? 2
            : int.MaxValue;
    }

    private static int GetKindRank(string kind)
    {
        var normalized = kind.AsSpan().Trim();
        if (normalized.Equals("translation", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("translated", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("localized", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (normalized.Equals("canonical", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (normalized.Equals("alternative", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("alias", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        return normalized.Equals("original", StringComparison.OrdinalIgnoreCase)
            ? 3
            : 4;
    }
}
