using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using UniversalMediaDistribution.Application.Catalog;
using UniversalMediaDistribution.Application.Metadata;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.Metadata;

public sealed class MyAnimeListMetadataProvider : ICatalogMetadataProvider
{
    private const int MaximumResponseBytes = 2 * 1024 * 1024;
    private const int SearchLimit = 10;
    private static readonly Uri ApiRoot =
        new("https://api.myanimelist.net/v2/", UriKind.Absolute);
    private readonly HttpClient _httpClient;

    public MyAnimeListMetadataProvider(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public string Name => "MyAnimeList";

    public bool CanRefresh(CatalogItemDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        return (details.Item.MediaType is MediaType.Movie &&
                IsAnimeSubtype(details.Item.SubtypeCode)) ||
               (details.Item.MediaType is MediaType.Book &&
                details.Item.SubtypeCode is
                    "book.manga" or "book.web_novel");
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
                "MyAnimeList metadata applies only to anime, manga and web novels.");
        }

        var normalizedSettings = settings.Normalize();
        if (string.IsNullOrWhiteSpace(normalizedSettings.MyAnimeListClientId))
        {
            throw new InvalidOperationException(
                "Enter and save a MyAnimeList client ID before refreshing anime or manga metadata.");
        }

        var resource = IsAnimeSubtype(details.Item.SubtypeCode)
            ? "anime"
            : "manga";
        var candidates = new List<MetadataSearchCandidate>();
        foreach (var query in MetadataMatchSelector.BuildSearchQueries(details))
        {
            var relativeUri =
                $"{resource}?q={Uri.EscapeDataString(query)}&limit={SearchLimit}" +
                "&fields=id,title,main_picture,alternative_titles,start_date,media_type";
            var response = await GetJsonAsync(
                relativeUri,
                normalizedSettings.MyAnimeListClientId,
                MyAnimeListJsonContext.Default.MyAnimeListSearchResponse,
                cancellationToken);
            candidates.AddRange(response.Data
                .Select(static entry => entry.Node)
                .Where(node => IsCompatibleMediaType(
                    details.Item.SubtypeCode,
                    node.MediaType))
                .Select(MapSearchResult));
        }

        var match = MetadataMatchSelector.SelectBest(
            details,
            candidates,
            "MyAnimeList");
        var result = await GetJsonAsync(
            $"{resource}/{match.Id}" +
            "?fields=id,title,main_picture,alternative_titles,synopsis,start_date,media_type",
            normalizedSettings.MyAnimeListClientId,
            MyAnimeListJsonContext.Default.MyAnimeListManga,
            cancellationToken);
        if (!IsCompatibleMediaType(details.Item.SubtypeCode, result.MediaType))
        {
            throw new InvalidOperationException(
                "MyAnimeList returned a result with an incompatible media type.");
        }

        var primaryTitle = SelectPrimaryTitle(
            result,
            normalizedSettings.PreferredLanguage);
        var names = new List<CatalogAlternativeName>();
        AddName(names, result.Title, language: null, "canonical");
        AddName(names, result.AlternativeTitles?.English, "en", "translation");
        AddName(names, result.AlternativeTitles?.Japanese, "ja", "original");
        foreach (var synonym in result.AlternativeTitles?.Synonyms ?? [])
        {
            AddName(names, synonym, language: null, "synonym");
        }

        return new CatalogMetadataSnapshot(
            "MyAnimeList",
            primaryTitle,
            result.Synopsis,
            SelectPicture(result.MainPicture),
            names);
    }

    private async ValueTask<T> GetJsonAsync<T>(
        string relativeUri,
        string clientId,
        JsonTypeInfo<T> jsonTypeInfo,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(ApiRoot, relativeUri));
        request.Headers.TryAddWithoutValidation("X-MAL-CLIENT-ID", clientId);
        request.Headers.Accept.ParseAdd("application/json");

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException(
                "MyAnimeList rejected the client ID.");
        }

        if (response.StatusCode is HttpStatusCode.TooManyRequests)
        {
            throw new InvalidOperationException(
                "MyAnimeList rate-limited the request. Try again later.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"MyAnimeList returned HTTP {(int)response.StatusCode}.");
        }

        if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
        {
            throw new InvalidDataException(
                "MyAnimeList returned an oversized metadata response.");
        }

        await using var source =
            await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var content = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (content.Length + read > MaximumResponseBytes)
            {
                throw new InvalidDataException(
                    "MyAnimeList returned an oversized metadata response.");
            }

            await content.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        content.Position = 0;
        try
        {
            return await JsonSerializer.DeserializeAsync(
                    content,
                    jsonTypeInfo,
                    cancellationToken) ??
                throw new InvalidDataException(
                    "MyAnimeList returned an empty metadata response.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "MyAnimeList returned malformed metadata.",
                exception);
        }
    }

    private static MetadataSearchCandidate MapSearchResult(
        MyAnimeListManga result) =>
        new(
            result.Id,
            result.Title,
            result.AlternativeTitles?.Japanese,
            result.StartDate,
            new[] { result.AlternativeTitles?.English }
                .Concat(result.AlternativeTitles?.Synonyms ?? [])
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value!)
                .ToArray());

    private static string SelectPrimaryTitle(
        MyAnimeListManga result,
        string preferredLanguage)
    {
        if (preferredLanguage.StartsWith("en", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(result.AlternativeTitles?.English))
        {
            return result.AlternativeTitles.English;
        }

        if (preferredLanguage.StartsWith("ja", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(result.AlternativeTitles?.Japanese))
        {
            return result.AlternativeTitles.Japanese;
        }

        return result.Title;
    }

    private static string? SelectPicture(MyAnimeListPicture? picture)
    {
        var value = string.IsNullOrWhiteSpace(picture?.Large)
            ? picture?.Medium
            : picture.Large;
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
               uri.Scheme == Uri.UriSchemeHttps &&
               string.Equals(
                   uri.Host,
                   "cdn.myanimelist.net",
                   StringComparison.OrdinalIgnoreCase)
            ? uri.AbsoluteUri
            : null;
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

        names.Add(new CatalogAlternativeName(name, language, kind));
    }

    private static bool IsCompatibleMediaType(
        string subtypeCode,
        string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType))
        {
            return false;
        }

        return subtypeCode switch
        {
            "movie.anime_movie" => mediaType.Equals(
                "movie",
                StringComparison.OrdinalIgnoreCase),
            "movie.anime_series" =>
                mediaType.Equals("tv", StringComparison.OrdinalIgnoreCase) ||
                mediaType.Equals("ova", StringComparison.OrdinalIgnoreCase) ||
                mediaType.Equals("ona", StringComparison.OrdinalIgnoreCase) ||
                mediaType.Equals(
                    "special",
                    StringComparison.OrdinalIgnoreCase),
            "book.web_novel" => mediaType.Equals(
                "novel",
                StringComparison.OrdinalIgnoreCase),
            "book.manga" => !mediaType.Equals(
                "novel",
                StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static bool IsAnimeSubtype(string subtypeCode) =>
        subtypeCode is "movie.anime_movie" or "movie.anime_series";
}

internal sealed record MyAnimeListSearchResponse
{
    [JsonPropertyName("data")]
    public IReadOnlyList<MyAnimeListSearchEntry> Data { get; init; } = [];
}

internal sealed record MyAnimeListSearchEntry
{
    [JsonPropertyName("node")]
    public required MyAnimeListManga Node { get; init; }
}

internal sealed record MyAnimeListManga
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("main_picture")]
    public MyAnimeListPicture? MainPicture { get; init; }

    [JsonPropertyName("alternative_titles")]
    public MyAnimeListAlternativeTitles? AlternativeTitles { get; init; }

    [JsonPropertyName("synopsis")]
    public string? Synopsis { get; init; }

    [JsonPropertyName("start_date")]
    public string? StartDate { get; init; }

    [JsonPropertyName("media_type")]
    public string? MediaType { get; init; }
}

internal sealed record MyAnimeListPicture
{
    [JsonPropertyName("medium")]
    public string? Medium { get; init; }

    [JsonPropertyName("large")]
    public string? Large { get; init; }
}

internal sealed record MyAnimeListAlternativeTitles
{
    [JsonPropertyName("synonyms")]
    public IReadOnlyList<string> Synonyms { get; init; } = [];

    [JsonPropertyName("en")]
    public string? English { get; init; }

    [JsonPropertyName("ja")]
    public string? Japanese { get; init; }
}

[JsonSerializable(typeof(MyAnimeListSearchResponse))]
[JsonSerializable(typeof(MyAnimeListManga))]
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
internal sealed partial class MyAnimeListJsonContext : JsonSerializerContext;
