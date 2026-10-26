using System.Net.Http.Json;
using System.Web;
using UniversalMediaDistribution.Application.Catalog;
using UniversalMediaDistribution.Application.Metadata;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.Metadata;

public sealed class TmdbMetadataProvider : ICatalogMetadataProvider
{
    private const int MaximumSearchResults = 80;

    public string Name => "TMDB";

    public bool CanRefresh(CatalogItemDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        return details.Item.MediaType is MediaType.Movie &&
               details.Item.SubtypeCode is not
                   ("movie.anime_movie" or "movie.anime_series");
    }

    public async ValueTask<CatalogMetadataSnapshot> FetchAsync(
        CatalogItemDetails details,
        MetadataProviderSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(details);
        ArgumentNullException.ThrowIfNull(settings);
        if (!CanRefresh(details))
        {
            throw new NotSupportedException(
                "TMDB metadata applies only to movies and series.");
        }

        var normalizedSettings = settings.Normalize();
        if (string.IsNullOrWhiteSpace(normalizedSettings.TmdbApiKey))
        {
            throw new InvalidOperationException(
                "Enter and save a TMDB v3 API key before refreshing movie metadata.");
        }

        try
        {
            using var client = new HttpClient
            {
                BaseAddress = new Uri("https://api.themoviedb.org/3/"),
                Timeout = TimeSpan.FromSeconds(20)
            };
            var credentials = new TmdbRequestContext(
                normalizedSettings.TmdbApiKey,
                normalizedSettings.PreferredLanguage);

            return IsSeries(details.Item.SubtypeCode)
                ? await FetchSeriesAsync(
                    client,
                    credentials,
                    details,
                    cancellationToken)
                : await FetchMovieAsync(
                    client,
                    credentials,
                    details,
                    cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                "TMDB metadata lookup failed. Check the API key and network connection.",
                exception);
        }
    }

    private static async ValueTask<CatalogMetadataSnapshot> FetchMovieAsync(
        HttpClient client,
        TmdbRequestContext context,
        CatalogItemDetails details,
        CancellationToken cancellationToken)
    {
        var candidates = new List<MetadataSearchCandidate>();
        foreach (var query in MetadataMatchSelector.BuildSearchQueries(details))
        {
            var response = await GetAsync(
                client,
                context.BuildSearchPath("search/movie", query),
                TmdbJsonSerializerContext.Default.TmdbMovieSearchResponse,
                cancellationToken);
            candidates.AddRange((response?.Results ?? [])
                .Take(MaximumSearchResults - candidates.Count)
                .Select(MapMovieSearchResult));
            if (candidates.Count >= MaximumSearchResults)
            {
                break;
            }
        }

        var match = MetadataMatchSelector.SelectBest(details, candidates, "TMDB");
        var movie = await GetAsync(
                client,
                context.BuildDetailsPath("movie", match.Id),
                TmdbJsonSerializerContext.Default.TmdbMovieDetails,
                cancellationToken) ??
            throw new InvalidOperationException(
                "TMDB returned no movie details for the selected match.");
        var names = new List<CatalogAlternativeName>();
        AddName(
            names,
            movie.OriginalTitle,
            movie.OriginalLanguage,
            "original");
        foreach (var alternative in movie.AlternativeTitles?.Titles ?? [])
        {
            AddName(names, alternative.Title, language: null, "alternative");
        }

        foreach (var translation in movie.Translations?.Translations ?? [])
        {
            AddName(
                names,
                TranslatedName(translation),
                translation.Iso6391,
                "translation");
        }

        return CreateSnapshot(
            movie.Title,
            movie.OriginalTitle,
            movie.Overview,
            movie.PosterPath,
            names);
    }

    private static async ValueTask<CatalogMetadataSnapshot> FetchSeriesAsync(
        HttpClient client,
        TmdbRequestContext context,
        CatalogItemDetails details,
        CancellationToken cancellationToken)
    {
        var candidates = new List<MetadataSearchCandidate>();
        foreach (var query in MetadataMatchSelector.BuildSearchQueries(details))
        {
            var response = await GetAsync(
                client,
                context.BuildSearchPath("search/tv", query),
                TmdbJsonSerializerContext.Default.TmdbTvSearchResponse,
                cancellationToken);
            candidates.AddRange((response?.Results ?? [])
                .Take(MaximumSearchResults - candidates.Count)
                .Select(MapTvSearchResult));
            if (candidates.Count >= MaximumSearchResults)
            {
                break;
            }
        }

        var match = MetadataMatchSelector.SelectBest(details, candidates, "TMDB");
        var show = await GetAsync(
                client,
                context.BuildDetailsPath("tv", match.Id),
                TmdbJsonSerializerContext.Default.TmdbTvDetails,
                cancellationToken) ??
            throw new InvalidOperationException(
                "TMDB returned no series details for the selected match.");
        var names = new List<CatalogAlternativeName>();
        AddName(names, show.OriginalName, show.OriginalLanguage, "original");
        foreach (var alternative in show.AlternativeTitles?.Results ?? [])
        {
            AddName(names, alternative.Title, language: null, "alternative");
        }

        foreach (var translation in show.Translations?.Translations ?? [])
        {
            AddName(
                names,
                TranslatedName(translation),
                translation.Iso6391,
                "translation");
        }

        return CreateSnapshot(
            show.Name,
            show.OriginalName,
            show.Overview,
            show.PosterPath,
            names);
    }

    private static MetadataSearchCandidate MapMovieSearchResult(
        TmdbMovieSearchResult result) =>
        new(
            result.Id,
            result.Title ?? string.Empty,
            result.OriginalTitle,
            // TMDB already sends these as yyyy-MM-dd, so the value is passed
            // through rather than parsed and reformatted. An empty string is
            // sent for unknown dates and must read as absent.
            NullIfBlank(result.ReleaseDate),
            []);

    private static MetadataSearchCandidate MapTvSearchResult(
        TmdbTvSearchResult result) =>
        new(
            result.Id,
            result.Name ?? string.Empty,
            result.OriginalName,
            NullIfBlank(result.FirstAirDate),
            []);

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static string? TranslatedName(TmdbTranslation translation) =>
        NullIfBlank(translation.Data?.Name) ??
        NullIfBlank(translation.Data?.Title);

    /// <summary>
    /// Reads one TMDB endpoint. A failed status is raised rather than returned
    /// so it surfaces as the provider's wrapped lookup failure, matching how
    /// TMDbLib was configured with ThrowApiExceptions.
    /// </summary>
    private static async ValueTask<T?> GetAsync<T>(
        HttpClient client,
        string path,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(
            path,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync(
            typeInfo,
            cancellationToken);
    }

    /// <summary>
    /// Carries the v3 key and language across the request builders. The key is
    /// a query parameter for this API version, so it is escaped like any other.
    /// </summary>
    private sealed record TmdbRequestContext(string ApiKey, string Language)
    {
        public string BuildSearchPath(string endpoint, string query) =>
            $"{endpoint}?api_key={Escape(ApiKey)}" +
            $"&language={Escape(Language)}" +
            $"&query={Escape(query)}" +
            "&page=1&include_adult=false";

        public string BuildDetailsPath(string endpoint, int id) =>
            $"{endpoint}/{id}?api_key={Escape(ApiKey)}" +
            $"&language={Escape(Language)}" +
            "&append_to_response=alternative_titles,translations";

        private static string Escape(string value) =>
            HttpUtility.UrlEncode(value) ?? string.Empty;
    }

    private static CatalogMetadataSnapshot CreateSnapshot(
        string? localizedTitle,
        string? originalTitle,
        string? overview,
        string? posterPath,
        IReadOnlyList<CatalogAlternativeName> names)
    {
        var title = string.IsNullOrWhiteSpace(localizedTitle)
            ? originalTitle
            : localizedTitle;
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new InvalidOperationException(
                "TMDB returned a title with no usable name.");
        }

        return new CatalogMetadataSnapshot(
            "TMDB",
            title,
            overview,
            BuildPosterUrl(posterPath),
            names);
    }

    private static void AddName(
        ICollection<CatalogAlternativeName> names,
        string? name,
        string? language,
        string kind)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        try
        {
            names.Add(new CatalogAlternativeName(name, language, kind));
        }
        catch (ArgumentException)
        {
            names.Add(new CatalogAlternativeName(name, kind: kind));
        }
    }

    private static string? BuildPosterUrl(string? posterPath)
    {
        if (string.IsNullOrWhiteSpace(posterPath) ||
            !posterPath.StartsWith('/') ||
            posterPath.Contains('\\') ||
            posterPath.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }

        return $"https://image.tmdb.org/t/p/w500{posterPath}";
    }

    private static bool IsSeries(string subtypeCode) =>
        subtypeCode is "movie.series" or "movie.anime_series";
}
