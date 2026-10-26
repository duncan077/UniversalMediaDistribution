using System.Text.Json.Serialization;

namespace UniversalMediaDistribution.Metadata;

/// <summary>
/// The slice of the TMDB v3 responses this provider reads. These replace
/// TMDbLib, whose Newtonsoft.Json dependency cannot be trimmed or AOT compiled:
/// both produced trim and AOT analysis warnings, and Newtonsoft reached the
/// application only through that package.
/// </summary>
internal sealed record TmdbMovieSearchResponse
{
    [JsonPropertyName("results")]
    public IReadOnlyList<TmdbMovieSearchResult>? Results { get; init; }
}

internal sealed record TmdbMovieSearchResult
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("original_title")]
    public string? OriginalTitle { get; init; }

    [JsonPropertyName("release_date")]
    public string? ReleaseDate { get; init; }
}

internal sealed record TmdbTvSearchResponse
{
    [JsonPropertyName("results")]
    public IReadOnlyList<TmdbTvSearchResult>? Results { get; init; }
}

internal sealed record TmdbTvSearchResult
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("original_name")]
    public string? OriginalName { get; init; }

    [JsonPropertyName("first_air_date")]
    public string? FirstAirDate { get; init; }
}

internal sealed record TmdbMovieDetails
{
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("original_title")]
    public string? OriginalTitle { get; init; }

    [JsonPropertyName("original_language")]
    public string? OriginalLanguage { get; init; }

    [JsonPropertyName("overview")]
    public string? Overview { get; init; }

    [JsonPropertyName("poster_path")]
    public string? PosterPath { get; init; }

    // A movie's alternative titles arrive under "titles" while a series uses
    // "results"; the two shapes are otherwise identical.
    [JsonPropertyName("alternative_titles")]
    public TmdbMovieAlternativeTitles? AlternativeTitles { get; init; }

    [JsonPropertyName("translations")]
    public TmdbTranslations? Translations { get; init; }
}

internal sealed record TmdbTvDetails
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("original_name")]
    public string? OriginalName { get; init; }

    [JsonPropertyName("original_language")]
    public string? OriginalLanguage { get; init; }

    [JsonPropertyName("overview")]
    public string? Overview { get; init; }

    [JsonPropertyName("poster_path")]
    public string? PosterPath { get; init; }

    [JsonPropertyName("alternative_titles")]
    public TmdbTvAlternativeTitles? AlternativeTitles { get; init; }

    [JsonPropertyName("translations")]
    public TmdbTranslations? Translations { get; init; }
}

internal sealed record TmdbMovieAlternativeTitles
{
    [JsonPropertyName("titles")]
    public IReadOnlyList<TmdbAlternativeTitle>? Titles { get; init; }
}

internal sealed record TmdbTvAlternativeTitles
{
    [JsonPropertyName("results")]
    public IReadOnlyList<TmdbAlternativeTitle>? Results { get; init; }
}

internal sealed record TmdbAlternativeTitle
{
    [JsonPropertyName("title")]
    public string? Title { get; init; }
}

internal sealed record TmdbTranslations
{
    [JsonPropertyName("translations")]
    public IReadOnlyList<TmdbTranslation>? Translations { get; init; }
}

internal sealed record TmdbTranslation
{
    [JsonPropertyName("iso_639_1")]
    public string? Iso6391 { get; init; }

    [JsonPropertyName("data")]
    public TmdbTranslationData? Data { get; init; }
}

internal sealed record TmdbTranslationData
{
    // A translated movie carries "title" and a translated series carries
    // "name". Both are read so one mapping serves either endpoint.
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(TmdbMovieSearchResponse))]
[JsonSerializable(typeof(TmdbTvSearchResponse))]
[JsonSerializable(typeof(TmdbMovieDetails))]
[JsonSerializable(typeof(TmdbTvDetails))]
internal sealed partial class TmdbJsonSerializerContext : JsonSerializerContext;
