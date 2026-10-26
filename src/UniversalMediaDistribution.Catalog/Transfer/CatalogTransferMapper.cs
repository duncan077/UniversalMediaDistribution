using System.Globalization;
using System.Text;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.Catalog.Transfer;

public static class CatalogTransferMapper
{
    public static CatalogItem ToCatalogItem(
        CatalogTransferItem transferItem,
        Guid id,
        DateTimeOffset? updatedAt = null)
    {
        var mediaType = CatalogSubtypeResolver.ParseMediaType(transferItem.MediaType);
        var subtypeCode = CatalogSubtypeResolver.ResolveCode(mediaType, transferItem.Subtype);
        var publicationStatus = ParsePublicationStatus(transferItem.PublicationStatus);
        var alternativeNames = GetAlternativeNames(transferItem)
            .Select(static alternativeName => new CatalogAlternativeName(
                alternativeName.Name,
                alternativeName.Language,
                alternativeName.AliasType))
            .ToArray();
        var contributors = transferItem.Contributors
            .Select(static contributor => new CatalogContributor(
                contributor.Name,
                contributor.Kind,
                contributor.Url,
                contributor.ThumbnailRef,
                contributor.Credits.Select(static credit =>
                    new CatalogContributorCredit(
                        credit.Role,
                        credit.CharacterName,
                        credit.SortOrder))))
            .ToArray();

        return new CatalogItem(
            id,
            mediaType,
            subtypeCode,
            transferItem.Name,
            transferItem.Description,
            transferItem.ThumbnailRef,
            transferItem.BackdropRef,
            publicationStatus,
            transferItem.Genres,
            transferItem.Categories,
            alternativeNames,
            contributors,
            transferItem.ReleaseDate,
            updatedAt,
            transferItem.ReleaseYear);
    }

    public static IReadOnlyList<CatalogTransferAlias> GetAlternativeNames(
        CatalogTransferItem transferItem)
    {
        ArgumentNullException.ThrowIfNull(transferItem);
        return (transferItem.AlternativeNames ?? [])
            .Concat(transferItem.Aliases ?? [])
            .ToArray();
    }

    public static CatalogTransferItem ToCanonicalTransferItem(CatalogTransferItem transferItem) =>
        transferItem with
        {
            AlternativeNames = GetAlternativeNames(transferItem),
            Aliases = null,
            Contributors = transferItem.Contributors
                .Select(static contributor => contributor with
                {
                    Url = string.IsNullOrWhiteSpace(contributor.Url)
                        ? null
                        : contributor.Url.Trim()
                })
                .ToArray(),
            Sources = transferItem.Sources
                .Select(CanonicalizeSource)
                .ToArray(),
            Units = transferItem.Units
                .Select(CanonicalizeUnit)
                .ToArray(),
            Seasons = transferItem.Seasons
                .Select(static season => season with
                {
                    Units = season.Units
                        .Select(CanonicalizeUnit)
                        .ToArray()
                })
                .ToArray()
        };

    private static CatalogTransferUnit CanonicalizeUnit(CatalogTransferUnit unit) =>
        unit with
        {
            Sources = unit.Sources
                .Select(CanonicalizeSource)
                .ToArray()
        };

    private static CatalogTransferMediaSource CanonicalizeSource(
        CatalogTransferMediaSource source) =>
        source with
        {
            Torrent = source.Torrent with
            {
                Language = LanguageTag.Normalize(source.Torrent.Language)
            }
        };

    public static PublicationStatus ParsePublicationStatus(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return PublicationStatus.Unknown;
        }

        var normalized = RemoveDiacritics(value).Trim().ToLowerInvariant();
        if (normalized.Contains("cancel", StringComparison.Ordinal))
        {
            return PublicationStatus.Cancelled;
        }

        if (normalized.Contains("hiatus", StringComparison.Ordinal) ||
            normalized.Contains("pause", StringComparison.Ordinal) ||
            normalized.Contains("paus", StringComparison.Ordinal) ||
            normalized.Contains("suspend", StringComparison.Ordinal) ||
            normalized.Contains("on hold", StringComparison.Ordinal))
        {
            return PublicationStatus.Paused;
        }

        if (normalized.Contains("announc", StringComparison.Ordinal) ||
            normalized.Contains("upcoming", StringComparison.Ordinal) ||
            normalized.Contains("proxim", StringComparison.Ordinal) ||
            normalized.Contains("not yet aired", StringComparison.Ordinal) ||
            normalized.Contains("planned", StringComparison.Ordinal) ||
            normalized.Equals("pilot", StringComparison.Ordinal))
        {
            return PublicationStatus.Announced;
        }

        // Check completion before airing: providers commonly publish
        // "Finished Airing", which otherwise looks ongoing.
        if (normalized.Contains("finish", StringComparison.Ordinal) ||
            normalized.Contains("complet", StringComparison.Ordinal) ||
            normalized.Contains("finaliz", StringComparison.Ordinal) ||
            normalized.Contains("terminad", StringComparison.Ordinal) ||
            normalized.Contains("concluid", StringComparison.Ordinal) ||
            normalized.Equals("ended", StringComparison.Ordinal))
        {
            return PublicationStatus.Completed;
        }

        if (normalized.Contains("ongoing", StringComparison.Ordinal) ||
            normalized.Contains("airing", StringComparison.Ordinal) ||
            normalized.Contains("emision", StringComparison.Ordinal) ||
            normalized.Contains("emitiendo", StringComparison.Ordinal) ||
            normalized.Contains("public", StringComparison.Ordinal) ||
            normalized.Contains("releasing", StringComparison.Ordinal) ||
            normalized.Contains("activo", StringComparison.Ordinal) ||
            normalized.Contains("active", StringComparison.Ordinal) ||
            normalized.Contains("en curso", StringComparison.Ordinal) ||
            normalized.Contains("returning series", StringComparison.Ordinal) ||
            normalized.Contains("in production", StringComparison.Ordinal))
        {
            return PublicationStatus.Ongoing;
        }

        return Enum.TryParse<PublicationStatus>(
                normalized,
                ignoreCase: true,
                out var status) &&
            Enum.IsDefined(status)
                ? status
                : PublicationStatus.Unknown;
    }

    private static string RemoveDiacritics(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var result = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) is not
                UnicodeCategory.NonSpacingMark)
            {
                result.Append(character);
            }
        }

        return result.ToString().Normalize(NormalizationForm.FormC);
    }

    public static ContentUnitKind ParseUnitKind(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        if (!Enum.TryParse<ContentUnitKind>(value.Replace("_", string.Empty), ignoreCase: true, out var kind) ||
            !Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown content unit kind.");
        }

        return kind;
    }
}
