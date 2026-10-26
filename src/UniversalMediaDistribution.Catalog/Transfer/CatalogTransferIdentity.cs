using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.Catalog.Transfer;

/// <summary>
/// Resolves one catalog identity across provider primary, original and
/// alternative titles without collapsing different media hierarchies.
/// </summary>
public static class CatalogTransferIdentity
{
    public static string GetKey(CatalogTransferItem item)
    {
        var media = CatalogSubtypeResolver.ParseMediaType(item.MediaType);
        return ContentItemKey.Create(media, item.Name, CatalogSubtypeResolver.ResolveCode(media, item.Subtype));
    }

    /// <summary>
    /// Folds two entries that converging resolved to one identity into a
    /// single item, keeping <paramref name="emitted"/>'s name because that is
    /// the identity already published to the rest of the document. Everything
    /// the later entry carries that the first does not — units above all — is
    /// kept: the two are the same title, so dropping either would lose
    /// chapters the account had recorded.
    /// </summary>
    public static CatalogTransferItem CombineConverged(
        CatalogTransferItem emitted,
        CatalogTransferItem candidate) =>
        ApiCatalogItemMerger.Merge(emitted, candidate);

    public static CatalogTransferItem? FindBestMatch(
        IEnumerable<CatalogTransferItem> existingItems,
        CatalogTransferItem candidate)
    {
        ArgumentNullException.ThrowIfNull(existingItems);
        ArgumentNullException.ThrowIfNull(candidate);
        var candidateKeys = GetNameKeys(candidate);
        var candidatePrimary = NameNormalizer.Normalize(candidate.Name);
        var candidateYear = CatalogReleaseYear.Resolve(candidate.ReleaseYear, candidate.ReleaseDate);
        var matches = existingItems
            .Select(existing => new
            {
                Item = existing,
                Score = Score(existing, candidate, candidateKeys, candidatePrimary, candidateYear)
            })
            .Where(static match => match.Score > 0)
            .OrderByDescending(static match => match.Score)
            .ThenBy(
                static match => match.Item.Name,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (matches.Length == 0) return null;
        var best = matches[0].Item;
        // An incoming list of synonyms must not become a bridge between two
        // existing, unrelated works. An exact primary title is unambiguous;
        // otherwise leave the new entry separate for manual review.
        if (!string.Equals(NameNormalizer.Normalize(best.Name),
                NameNormalizer.Normalize(candidate.Name), StringComparison.Ordinal) &&
            matches.Skip(1).Any(match => GetMatchScore(best, match.Item) == 0))
            return null;
        return best;
    }

    /// <summary>Placeholder metadata is not evidence of a shared identity.</summary>
    public static bool IsUsableName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var normalized = NameNormalizer.Normalize(value);
        Span<char> token = normalized.Length <= 256 ? stackalloc char[normalized.Length] : new char[normalized.Length];
        var length = 0;
        foreach (var character in normalized)
            if (char.IsLetterOrDigit(character)) token[length++] = character;
        ReadOnlySpan<char> significant = token[..length];
        return significant is not ("" or "UNKNOWN" or "DESCONOCIDO" or "DESCONOCIDA" or
            "SINNOMBRE" or "SINTITULO" or "SINTÍTULO" or "NOTAVAILABLE" or
            "NODISPONIBLE" or "NONE" or "NULL" or "NA" or "NO" or "TBA" or "TBD");
    }

    public static IReadOnlySet<string> GetNameKeys(
        CatalogTransferItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        Add(item.Name);
        Add(item.OriginalName);
        foreach (var alternative in
                 CatalogTransferMapper.GetAlternativeNames(item))
        {
            Add(alternative.Name);
        }

        return keys;

        void Add(string? value)
        {
            if (IsUsableName(value))
            {
                keys.Add(NameNormalizer.Normalize(value!));
            }
        }
    }

    public static bool AreCompatible(
        CatalogTransferItem left,
        CatalogTransferItem right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        var leftMedia = CatalogSubtypeResolver.ParseMediaType(left.MediaType);
        var rightMedia = CatalogSubtypeResolver.ParseMediaType(right.MediaType);
        if (leftMedia != rightMedia)
        {
            return false;
        }

        var leftCode = CatalogSubtypeResolver.ResolveCode(
            leftMedia,
            left.Subtype);
        var rightCode = CatalogSubtypeResolver.ResolveCode(
            rightMedia,
            right.Subtype);
        var leftDefinition = MediaSubtypeDefinition.GetRequired(leftCode);
        var rightDefinition = MediaSubtypeDefinition.GetRequired(rightCode);
        if (leftDefinition.HierarchyMode != rightDefinition.HierarchyMode)
        {
            return false;
        }

        return leftMedia is MediaType.Movie
            ? MediaSubtypeGroups.Resolve(leftCode) ==
              MediaSubtypeGroups.Resolve(rightCode)
            : string.Equals(
                leftCode,
                rightCode,
                StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsStandaloneMovie(CatalogTransferItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var mediaType = CatalogSubtypeResolver.ParseMediaType(item.MediaType);
        if (mediaType is not MediaType.Movie)
        {
            return false;
        }

        var subtypeCode = CatalogSubtypeResolver.ResolveCode(
            mediaType,
            item.Subtype);
        return MediaSubtypeDefinition.GetRequired(subtypeCode).HierarchyMode is
            HierarchyMode.Single;
    }

    public static CatalogTransferItem ConvergeIdentity(
        CatalogTransferItem existing,
        CatalogTransferItem candidate)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(candidate);
        if (!AreCompatible(existing, candidate))
        {
            throw new ArgumentException(
                "Catalog identities can converge only for compatible media.",
                nameof(candidate));
        }

        var primaryKey = NameNormalizer.Normalize(existing.Name);
        var aliases = new List<CatalogTransferAlias>();
        var aliasKeys = new HashSet<string>(StringComparer.Ordinal);
        AddRange(CatalogTransferMapper.GetAlternativeNames(existing));
        AddRange(CatalogTransferMapper.GetAlternativeNames(candidate));
        AddTitle(candidate.Name, candidate.OriginalLanguage, "provider");
        AddTitle(existing.OriginalName, existing.OriginalLanguage, "original");
        AddTitle(candidate.OriginalName, candidate.OriginalLanguage, "original");

        return candidate with
        {
            Name = existing.Name,
            OriginalName = string.IsNullOrWhiteSpace(candidate.OriginalName)
                ? existing.OriginalName
                : candidate.OriginalName,
            AlternativeNames = aliases,
            Aliases = null
        };

        void AddRange(IEnumerable<CatalogTransferAlias> values)
        {
            foreach (var alias in values)
            {
                Add(alias);
            }
        }

        void AddTitle(string? name, string? language, string aliasType)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                Add(new CatalogTransferAlias
                {
                    Name = name,
                    Language = language,
                    AliasType = aliasType
                });
            }
        }

        void Add(CatalogTransferAlias alias)
        {
            var nameKey = NameNormalizer.Normalize(alias.Name);
            if (string.Equals(nameKey, primaryKey, StringComparison.Ordinal))
            {
                return;
            }

            var key = $"{nameKey}|{LanguageTag.Normalize(alias.Language)}|" +
                alias.AliasType.Trim().ToLowerInvariant();
            if (aliasKeys.Add(key))
            {
                aliases.Add(alias);
            }
        }
    }
    public static int GetMatchScore(
        CatalogTransferItem existing,
        CatalogTransferItem candidate)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(candidate);
        return Score(existing, candidate, GetNameKeys(candidate), NameNormalizer.Normalize(candidate.Name),
            CatalogReleaseYear.Resolve(candidate.ReleaseYear, candidate.ReleaseDate));
    }

    private static int Score(CatalogTransferItem existing, CatalogTransferItem candidate,
        IReadOnlySet<string> candidateKeys, string candidatePrimary, int? candidateYear)
    {
        if (!AreCompatible(existing, candidate)) return 0;
        var existingYear = CatalogReleaseYear.Resolve(existing.ReleaseYear, existing.ReleaseDate);
        if (existingYear is not null && candidateYear is not null && existingYear != candidateYear) return 0;
        var existingKeys = GetNameKeys(existing);
        var overlap = existingKeys.Count(candidateKeys.Contains);
        if (overlap == 0)
        {
            return 0;
        }

        var existingPrimary = NameNormalizer.Normalize(existing.Name);
        var score = overlap * 100;
        if (string.Equals(
                existingPrimary,
                candidatePrimary,
                StringComparison.Ordinal))
        {
            score += 10_000;
        }
        else if (existingKeys.Contains(candidatePrimary))
        {
            score += 5_000;
        }
        else if (candidateKeys.Contains(existingPrimary))
        {
            score += 4_000;
        }
        else if (existingKeys.Where(candidateKeys.Contains)
            .Select(LevenshteinTitleMatcher.Normalize).Distinct(StringComparer.Ordinal).Take(2).Count() < 2)
        {
            // One secondary synonym shared by otherwise different titles is
            // not enough. Primary-to-alias translations still work above.
            return 0;
        }

        return score;
    }
}
