using UniversalMediaDistribution.Application.Catalog;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.Metadata;

internal sealed record MetadataSearchCandidate(
    int Id,
    string Title,
    string? OriginalTitle,
    string? ReleaseDate,
    IReadOnlyList<string> AlternativeTitles);

internal static class MetadataMatchSelector
{
    public static MetadataSearchCandidate SelectBest(
        CatalogItemDetails details,
        IEnumerable<MetadataSearchCandidate> candidates,
        string providerName)
    {
        ArgumentNullException.ThrowIfNull(details);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);

        var knownNames = BuildKnownNames(details);
        var releaseYear = ParseYear(details.ReleaseDate);
        var scored = candidates
            .DistinctBy(static candidate => candidate.Id)
            .Select(candidate => new
            {
                Candidate = candidate,
                Score = Score(details.Item.NameKey, knownNames, releaseYear, candidate)
            })
            .Where(static value => value.Score >= 0)
            .OrderByDescending(static value => value.Score)
            .ToArray();

        if (scored.Length == 0)
        {
            throw new InvalidOperationException(
                $"{providerName} returned no exact title or alternative-title match.");
        }

        var bestScore = scored[0].Score;
        var best = scored
            .Where(value => value.Score == bestScore)
            .Select(static value => value.Candidate)
            .ToArray();
        if (best.Length != 1)
        {
            throw new InvalidOperationException(
                $"{providerName} returned multiple equally likely matches. " +
                "Add a release year or a more specific local title before refreshing.");
        }

        return best[0];
    }

    public static IReadOnlyList<string> BuildSearchQueries(
        CatalogItemDetails details) =>
        new[] { details.Item.Name, details.OriginalName }
            .Concat(details.Item.AlternativeNames.Select(static name => name.Name))
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!.Trim())
            .DistinctBy(NameNormalizer.Normalize, StringComparer.Ordinal)
            .Take(4)
            .ToArray();

    private static HashSet<string> BuildKnownNames(CatalogItemDetails details)
    {
        var values = BuildSearchQueries(details)
            .Select(NameNormalizer.Normalize)
            .ToHashSet(StringComparer.Ordinal);
        values.Add(details.Item.NameKey);
        return values;
    }

    private static int Score(
        string primaryNameKey,
        IReadOnlySet<string> knownNames,
        int? expectedYear,
        MetadataSearchCandidate candidate)
    {
        var candidateNames = new[] { candidate.Title, candidate.OriginalTitle }
            .Concat(candidate.AlternativeTitles)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => NameNormalizer.Normalize(value!))
            .ToHashSet(StringComparer.Ordinal);
        if (!candidateNames.Overlaps(knownNames))
        {
            return -1;
        }

        var score = candidateNames.Contains(primaryNameKey) ? 120 : 100;
        var candidateYear = ParseYear(candidate.ReleaseDate);
        if (expectedYear is not null && candidateYear is not null)
        {
            score += expectedYear == candidateYear ? 30 : -20;
        }

        return score;
    }

    private static int? ParseYear(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < 4)
        {
            return null;
        }

        return int.TryParse(value.AsSpan(0, 4), out var year) &&
               year is >= 1800 and <= 3000
            ? year
            : null;
    }
}
