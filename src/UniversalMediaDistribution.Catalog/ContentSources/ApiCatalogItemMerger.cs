using System.Globalization;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.Catalog.ContentSources;

internal static class ApiCatalogItemMerger
{
    public static CatalogTransferItem MergeMissing(
        CatalogTransferItem localItem,
        CatalogTransferItem providerItem)
    {
        ArgumentNullException.ThrowIfNull(localItem);
        ArgumentNullException.ThrowIfNull(providerItem);

        var local = CatalogTransferMapper.ToCanonicalTransferItem(localItem);
        var provider = CatalogTransferMapper.ToCanonicalTransferItem(providerItem);
        var merged = Merge(local, provider);
        return merged with
        {
            Subtype = local.Subtype,
            Name = local.Name,
            OriginalName = Prefer(local.OriginalName, provider.OriginalName),
            SortName = Prefer(local.SortName, provider.SortName),
            Description = Prefer(local.Description, provider.Description),
            ReleaseDate = Prefer(local.ReleaseDate, provider.ReleaseDate),
            ReleaseYear = local.ReleaseYear ?? provider.ReleaseYear,
            EndDate = Prefer(local.EndDate, provider.EndDate),
            PublicationStatus = PreferPublicationStatus(
                local.PublicationStatus,
                provider.PublicationStatus),
            OriginalLanguage = Prefer(
                local.OriginalLanguage,
                provider.OriginalLanguage),
            CountryCode = Prefer(local.CountryCode, provider.CountryCode),
            ContentRating = Prefer(
                local.ContentRating,
                provider.ContentRating),
            RuntimeSeconds =
                local.RuntimeSeconds ?? provider.RuntimeSeconds,
            PageCount = local.PageCount ?? provider.PageCount,
            ThumbnailRef = Prefer(
                local.ThumbnailRef,
                provider.ThumbnailRef),
            BackdropRef = Prefer(local.BackdropRef, provider.BackdropRef),
            Genres = PreferLocalCollection(local.Genres, provider.Genres),
            Categories = PreferLocalCollection(
                local.Categories,
                provider.Categories),
            AlternativeNames = HasAlternativeNames(local)
                ? CatalogTransferMapper.GetAlternativeNames(local).ToArray()
                : merged.AlternativeNames,
            Aliases = null,
            Contributors = PreferLocalCollection(
                local.Contributors,
                provider.Contributors),
            Seasons = MergeMissingSeasons(
                local.Seasons,
                provider.Seasons),
            Units = MergeMissingUnits(local.Units, provider.Units),
            DistributionRights = PreferLocalCollection(
                local.DistributionRights,
                provider.DistributionRights),
            Sources = PreferLocalCollection(local.Sources, provider.Sources)
        };
    }

    public static CatalogTransferItem Merge(
        CatalogTransferItem localItem,
        CatalogTransferItem providerItem)
    {
        ArgumentNullException.ThrowIfNull(localItem);
        ArgumentNullException.ThrowIfNull(providerItem);

        var local = CatalogTransferMapper.ToCanonicalTransferItem(localItem);
        var provider = CatalogTransferMapper.ToCanonicalTransferItem(providerItem);
        var localMediaType =
            CatalogSubtypeResolver.ParseMediaType(local.MediaType);
        var providerMediaType =
            CatalogSubtypeResolver.ParseMediaType(provider.MediaType);
        if (localMediaType != providerMediaType)
        {
            throw new ArgumentException(
                "A provider item cannot be merged into a different media type.",
                nameof(providerItem));
        }

        if (localMediaType == MediaType.Book && !CatalogTransferIdentity.AreCompatible(local, provider))
        {
            throw new ArgumentException(
                "A provider book cannot be merged into a different book subtype.",
                nameof(providerItem));
        }

        return local with
        {
            Subtype = Prefer(provider.Subtype, local.Subtype)!,
            // Keep the local primary name because media type + normalized name is
            // the catalog identity used by every persistence implementation.
            Name = local.Name,
            OriginalName = Prefer(
                provider.OriginalName,
                local.OriginalName),
            SortName = Prefer(provider.SortName, local.SortName),
            Description = Prefer(
                provider.Description,
                local.Description),
            ReleaseDate = Prefer(
                provider.ReleaseDate,
                local.ReleaseDate),
            ReleaseYear = provider.ReleaseYear ?? local.ReleaseYear,
            EndDate = Prefer(provider.EndDate, local.EndDate),
            PublicationStatus = PreferPublicationStatus(
                provider.PublicationStatus,
                local.PublicationStatus),
            OriginalLanguage = Prefer(
                provider.OriginalLanguage,
                local.OriginalLanguage),
            CountryCode = Prefer(
                provider.CountryCode,
                local.CountryCode),
            ContentRating = Prefer(
                provider.ContentRating,
                local.ContentRating),
            RuntimeSeconds =
                provider.RuntimeSeconds ?? local.RuntimeSeconds,
            PageCount = provider.PageCount ?? local.PageCount,
            ThumbnailRef = Prefer(
                provider.ThumbnailRef,
                local.ThumbnailRef),
            BackdropRef = Prefer(
                provider.BackdropRef,
                local.BackdropRef),
            Genres = MergeNames(local.Genres, provider.Genres),
            Categories = MergeNames(
                local.Categories,
                provider.Categories),
            AlternativeNames = MergeAlternativeNames(local, provider),
            Aliases = null,
            Contributors = MergeContributors(
                local.Contributors,
                provider.Contributors),
            Seasons = MergeSeasons(local.Seasons, provider.Seasons),
            Units = MergeUnits(local.Units, provider.Units),
            DistributionRights = MergeRights(
                local.DistributionRights,
                provider.DistributionRights),
            Sources = MergeSources(local.Sources, provider.Sources)
        };
    }

    private static IReadOnlyList<CatalogTransferAlias>
        MergeAlternativeNames(
            CatalogTransferItem local,
            CatalogTransferItem provider)
    {
        var aliases = new List<CatalogTransferAlias>();
        var keys = new HashSet<string>(StringComparer.Ordinal);

        AddAliases(CatalogTransferMapper.GetAlternativeNames(local));
        AddAliases(CatalogTransferMapper.GetAlternativeNames(provider));
        AddProviderTitle(provider.Name, provider.OriginalLanguage);
        if (!string.IsNullOrWhiteSpace(provider.OriginalName))
        {
            AddProviderTitle(
                provider.OriginalName,
                provider.OriginalLanguage);
        }

        return aliases;

        void AddAliases(IEnumerable<CatalogTransferAlias> values)
        {
            foreach (var alias in values)
            {
                Add(alias);
            }
        }

        void AddProviderTitle(string name, string? language)
        {
            if (string.Equals(
                    NameNormalizer.Normalize(name),
                    NameNormalizer.Normalize(local.Name),
                    StringComparison.Ordinal))
            {
                return;
            }

            Add(new CatalogTransferAlias
            {
                Name = name,
                Language = language,
                AliasType = "provider"
            });
        }

        void Add(CatalogTransferAlias alias)
        {
            var key = $"{NameNormalizer.Normalize(alias.Name)}|" +
                $"{LanguageTag.Normalize(alias.Language)}|" +
                $"{alias.AliasType.Trim().ToLowerInvariant()}";
            if (keys.Add(key))
            {
                aliases.Add(alias);
            }
        }
    }

    private static IReadOnlyList<CatalogTransferSeason> MergeSeasons(
        IReadOnlyList<CatalogTransferSeason> localSeasons,
        IReadOnlyList<CatalogTransferSeason> providerSeasons)
    {
        var seasons = localSeasons.ToList();
        foreach (var providerSeason in providerSeasons)
        {
            var index = seasons.FindIndex(
                season => season.Number == providerSeason.Number);
            if (index < 0)
            {
                seasons.Add(providerSeason);
                continue;
            }

            var localSeason = seasons[index];
            seasons[index] = localSeason with
            {
                SortOrder = providerSeason.SortOrder,
                DisplayName = Prefer(
                    providerSeason.DisplayName,
                    localSeason.DisplayName),
                ThumbnailRef = Prefer(
                    providerSeason.ThumbnailRef,
                    localSeason.ThumbnailRef),
                ReleaseDate = Prefer(
                    providerSeason.ReleaseDate,
                    localSeason.ReleaseDate),
                Units = MergeUnits(
                    localSeason.Units,
                    providerSeason.Units)
            };
        }

        return seasons
            .OrderBy(static season => season.SortOrder)
            .ThenBy(static season => season.Number)
            .ToArray();
    }

    private static IReadOnlyList<CatalogTransferSeason> MergeMissingSeasons(
        IReadOnlyList<CatalogTransferSeason> localSeasons,
        IReadOnlyList<CatalogTransferSeason> providerSeasons)
    {
        var seasons = localSeasons.ToList();
        foreach (var providerSeason in providerSeasons)
        {
            var index = seasons.FindIndex(
                season => season.Number == providerSeason.Number);
            if (index < 0)
            {
                seasons.Add(providerSeason);
                continue;
            }

            var localSeason = seasons[index];
            seasons[index] = localSeason with
            {
                DisplayName = Prefer(
                    localSeason.DisplayName,
                    providerSeason.DisplayName),
                ThumbnailRef = Prefer(
                    localSeason.ThumbnailRef,
                    providerSeason.ThumbnailRef),
                ReleaseDate = Prefer(
                    localSeason.ReleaseDate,
                    providerSeason.ReleaseDate),
                Units = MergeMissingUnits(
                    localSeason.Units,
                    providerSeason.Units)
            };
        }

        return seasons
            .OrderBy(static season => season.SortOrder)
            .ThenBy(static season => season.Number)
            .ToArray();
    }

    private static IReadOnlyList<CatalogTransferUnit> MergeUnits(
        IReadOnlyList<CatalogTransferUnit> localUnits,
        IReadOnlyList<CatalogTransferUnit> providerUnits)
    {
        var units = localUnits.ToList();
        var indexByKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < units.Count; index++)
        {
            if (!string.IsNullOrWhiteSpace(units[index].DisplayNumber))
                indexByKey.TryAdd(UnitKey(units[index]), index);
        }
        foreach (var providerUnit in providerUnits)
        {
            var key = UnitKey(providerUnit);
            if (!indexByKey.TryGetValue(key, out var index))
            {
                if (!string.IsNullOrWhiteSpace(providerUnit.DisplayNumber))
                    indexByKey.TryAdd(key, units.Count);
                units.Add(providerUnit);
                continue;
            }

            var localUnit = units[index];
            units[index] = localUnit with
            {
                // Preserve the local representation so a provider chapter such
                // as "01" updates chapter "1" without changing its progress key.
                Kind = localUnit.Kind,
                DisplayNumber = localUnit.DisplayNumber,
                SortOrder = providerUnit.SortOrder,
                Name = Prefer(providerUnit.Name, localUnit.Name),
                ThumbnailRef = Prefer(
                    providerUnit.ThumbnailRef,
                    localUnit.ThumbnailRef),
                DurationSeconds =
                    providerUnit.DurationSeconds ??
                    localUnit.DurationSeconds,
                PageCount =
                    providerUnit.PageCount ?? localUnit.PageCount,
                ReleaseDate = Prefer(
                    providerUnit.ReleaseDate,
                    localUnit.ReleaseDate),
                IsSpecial = providerUnit.IsSpecial,
                Sources = MergeSources(
                    localUnit.Sources,
                    providerUnit.Sources)
            };
        }

        return units
            .OrderBy(static unit => unit.SortOrder)
            .ThenBy(static unit => unit.DisplayNumber, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<CatalogTransferUnit> MergeMissingUnits(
        IReadOnlyList<CatalogTransferUnit> localUnits,
        IReadOnlyList<CatalogTransferUnit> providerUnits)
    {
        var units = localUnits.ToList();
        var indexByKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < units.Count; index++)
        {
            if (!string.IsNullOrWhiteSpace(units[index].DisplayNumber))
                indexByKey.TryAdd(UnitKey(units[index]), index);
        }
        foreach (var providerUnit in providerUnits)
        {
            var key = UnitKey(providerUnit);
            if (!indexByKey.TryGetValue(key, out var index))
            {
                if (!string.IsNullOrWhiteSpace(providerUnit.DisplayNumber))
                    indexByKey.TryAdd(key, units.Count);
                units.Add(providerUnit);
                continue;
            }

            var localUnit = units[index];
            units[index] = localUnit with
            {
                Name = Prefer(localUnit.Name, providerUnit.Name),
                ThumbnailRef = Prefer(
                    localUnit.ThumbnailRef,
                    providerUnit.ThumbnailRef),
                DurationSeconds =
                    localUnit.DurationSeconds ??
                    providerUnit.DurationSeconds,
                PageCount = localUnit.PageCount ?? providerUnit.PageCount,
                ReleaseDate = Prefer(
                    localUnit.ReleaseDate,
                    providerUnit.ReleaseDate),
                Sources = PreferLocalCollection(
                    localUnit.Sources,
                    providerUnit.Sources)
            };
        }

        return units
            .OrderBy(static unit => unit.SortOrder)
            .ThenBy(static unit => unit.DisplayNumber, StringComparer.Ordinal)
            .ToArray();
    }

    // Match the same numeric equivalence as DisplayNumbersMatch (01 == 1.0),
    // while preserving labels verbatim and keeping different unit kinds apart.
    // TryAdd retains the first matching unit, as the previous list scan did.
    private static string UnitKey(CatalogTransferUnit unit)
    {
        var kind = CatalogTransferMapper.ParseUnitKind(unit.Kind);
        return decimal.TryParse(unit.DisplayNumber, NumberStyles.Number, CultureInfo.InvariantCulture, out var number)
            ? $"{kind}:number:{number.ToString("G29", CultureInfo.InvariantCulture)}"
            : $"{kind}:label:{unit.DisplayNumber}";
    }

    private static IReadOnlyList<CatalogTransferContributor>
        MergeContributors(
            IReadOnlyList<CatalogTransferContributor> localContributors,
            IReadOnlyList<CatalogTransferContributor> providerContributors)
    {
        var contributors = localContributors.ToList();
        foreach (var providerContributor in providerContributors)
        {
            var index = contributors.FindIndex(
                localContributor =>
                    ContributorKey(localContributor) ==
                    ContributorKey(providerContributor));
            if (index < 0)
            {
                contributors.Add(providerContributor);
                continue;
            }

            var localContributor = contributors[index];
            contributors[index] = localContributor with
            {
                Url = Prefer(
                    providerContributor.Url,
                    localContributor.Url),
                ThumbnailRef = Prefer(
                    providerContributor.ThumbnailRef,
                    localContributor.ThumbnailRef),
                Credits = MergeCredits(
                    localContributor.Credits,
                    providerContributor.Credits)
            };
        }

        return contributors;
    }

    private static string ContributorKey(
        CatalogTransferContributor contributor) =>
        $"{contributor.Kind.Trim().ToLowerInvariant()}|" +
        NameNormalizer.Normalize(contributor.Name);

    private static IReadOnlyList<CatalogTransferCredit> MergeCredits(
        IReadOnlyList<CatalogTransferCredit> localCredits,
        IReadOnlyList<CatalogTransferCredit> providerCredits)
    {
        var credits = localCredits.ToList();
        foreach (var providerCredit in providerCredits)
        {
            var index = credits.FindIndex(localCredit =>
                string.Equals(
                    localCredit.Role.Trim(),
                    providerCredit.Role.Trim(),
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    localCredit.CharacterName?.Trim(),
                    providerCredit.CharacterName?.Trim(),
                    StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                credits.Add(providerCredit);
            }
            else
            {
                credits[index] = providerCredit;
            }
        }

        return credits
            .OrderBy(static credit => credit.SortOrder)
            .ToArray();
    }

    private static IReadOnlyList<CatalogTransferRight> MergeRights(
        IReadOnlyList<CatalogTransferRight> localRights,
        IReadOnlyList<CatalogTransferRight> providerRights)
    {
        var rights = localRights.ToList();
        foreach (var providerRight in providerRights)
        {
            var index = rights.FindIndex(localRight =>
                string.Equals(
                    localRight.SourceUri.Trim(),
                    providerRight.SourceUri.Trim(),
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    NameNormalizer.Normalize(localRight.LicenseName),
                    NameNormalizer.Normalize(providerRight.LicenseName),
                    StringComparison.Ordinal));
            if (index < 0)
            {
                rights.Add(providerRight);
            }
            else
            {
                rights[index] = providerRight;
            }
        }

        return rights;
    }

    private static IReadOnlyList<CatalogTransferMediaSource> MergeSources(
        IReadOnlyList<CatalogTransferMediaSource> localSources,
        IReadOnlyList<CatalogTransferMediaSource> providerSources)
    {
        var sources = localSources.ToList();
        var keys = localSources
            .Select(SourceKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var providerSource in providerSources)
        {
            if (keys.Add(SourceKey(providerSource)))
            {
                sources.Add(providerSource);
            }
        }

        return sources;
    }

    private static string SourceKey(CatalogTransferMediaSource source)
    {
        var torrent = source.Torrent;
        var torrentKey = Prefer(
            torrent.InfoHashV2,
            torrent.InfoHashV1,
            torrent.MagnetUri,
            torrent.SourceUri,
            torrent.TorrentMetadataBase64,
            torrent.DisplayName) ?? string.Empty;
        return $"{torrentKey.Trim()}|" +
            $"{source.AssetRole.Trim()}|" +
            $"{source.FilePath?.Trim()}|" +
            $"{source.FileIndex?.ToString() ?? string.Empty}";
    }

    private static IReadOnlyList<string> MergeNames(
        IReadOnlyList<string> localNames,
        IReadOnlyList<string> providerNames)
    {
        var names = new List<string>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in localNames.Concat(providerNames))
        {
            if (keys.Add(NameNormalizer.Normalize(name)))
            {
                names.Add(name.Trim());
            }
        }

        return names;
    }

    private static bool HasAlternativeNames(CatalogTransferItem item) =>
        CatalogTransferMapper.GetAlternativeNames(item).Any();

    private static IReadOnlyList<T> PreferLocalCollection<T>(
        IReadOnlyList<T> localValues,
        IReadOnlyList<T> providerValues) =>
        localValues.Count > 0 ? localValues : providerValues;

    private static string? Prefer(params string?[] values) =>
        values.FirstOrDefault(
            static value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string? PreferPublicationStatus(
        string? preferred,
        string? fallback)
    {
        if (CatalogTransferMapper.ParsePublicationStatus(preferred) is not
            PublicationStatus.Unknown)
        {
            return Prefer(preferred);
        }

        if (CatalogTransferMapper.ParsePublicationStatus(fallback) is not
            PublicationStatus.Unknown)
        {
            return Prefer(fallback);
        }

        return Prefer(preferred, fallback);
    }
}
