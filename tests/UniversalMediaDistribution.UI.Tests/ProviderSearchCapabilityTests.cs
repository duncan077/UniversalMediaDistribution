using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.UI.Tests;

/// <summary>
/// Locks in what each provider claims it can filter on. A provider that gains
/// or loses a capability has to change this table, which is the point: the
/// declarations decide whether a filtered search reaches a source at all.
/// </summary>
public sealed class ProviderSearchCapabilityTests
{
    private static readonly HttpClient Client = new();

    public static TheoryData<string, ApiContentFilterSupport,
        ApiContentFilterSupport, bool> Declarations => new()
    {
        {
            "doramasflix",
            ApiContentFilterSupport.Native,
            ApiContentFilterSupport.Native,
            true
        },
        {
            "umd-json-api",
            ApiContentFilterSupport.VerifiedLocal,
            ApiContentFilterSupport.VerifiedLocal,
            true
        },
        {
            "pelisplus",
            ApiContentFilterSupport.Native,
            ApiContentFilterSupport.VerifiedLocal,
            true
        },
        {
            "streamflix",
            ApiContentFilterSupport.Native,
            ApiContentFilterSupport.VerifiedLocal,
            true
        },
        {
            "leercapitulo",
            ApiContentFilterSupport.Native,
            ApiContentFilterSupport.Unsupported,
            true
        },
        {
            "manhwaweb",
            ApiContentFilterSupport.Native,
            ApiContentFilterSupport.Unsupported,
            true
        },
        {
            "zonatmo",
            ApiContentFilterSupport.Native,
            ApiContentFilterSupport.Unsupported,
            true
        },
        {
            "animetmo",
            ApiContentFilterSupport.VerifiedLocal,
            ApiContentFilterSupport.VerifiedLocal,
            true
        },
        {
            "jkanime",
            ApiContentFilterSupport.Native,
            ApiContentFilterSupport.Native,
            true
        },
        {
            "olympusxyz",
            ApiContentFilterSupport.Unsupported,
            ApiContentFilterSupport.Unsupported,
            false
        }
    };

    [Theory]
    [MemberData(nameof(Declarations))]
    public void ProvidersDeclareTheFiltersTheyCanActuallyHonour(
        string providerKey,
        ApiContentFilterSupport genre,
        ApiContentFilterSupport releaseYear,
        bool filterOnly)
    {
        var capabilities = Create(providerKey).SearchCapabilities;

        Assert.Equal(genre, capabilities.Genre);
        Assert.Equal(releaseYear, capabilities.ReleaseYear);
        Assert.Equal(filterOnly, capabilities.SupportsFilterOnlySearch);
    }

    [Fact]
    public void AGenreSearchReachesOnlyTheProvidersThatCanAnswerIt()
    {
        var request = new ApiContentSearchRequest(
            "Akira",
            genre: ApiContentGenre.Action);

        Assert.True(Create("pelisplus").SearchCapabilities
            .CanSatisfy(request));
        Assert.True(Create("streamflix").SearchCapabilities
            .CanSatisfy(request));
        Assert.True(Create("leercapitulo").SearchCapabilities
            .CanSatisfy(request));
    }

    [Fact]
    public void AFilterOnlySearchReachesOnlyTheProvidersThatCanBrowse()
    {
        var request = new ApiContentSearchRequest(
            genre: ApiContentGenre.Action);

        Assert.True(Create("doramasflix").SearchCapabilities
            .CanSatisfy(request));
        Assert.True(Create("streamflix").SearchCapabilities
            .CanSatisfy(request));
        Assert.True(Create("leercapitulo").SearchCapabilities
            .CanSatisfy(request));
        Assert.True(Create("manhwaweb").SearchCapabilities
            .CanSatisfy(request));
        Assert.True(Create("jkanime").SearchCapabilities
            .CanSatisfy(request));
        Assert.True(Create("pelisplus").SearchCapabilities
            .CanSatisfy(request));
    }

    private static IApiContentProvider Create(string providerKey) =>
        providerKey switch
        {
            "doramasflix" => new DoramasflixApiContentProvider(Client),
            "umd-json-api" => new UmdJsonApiContentProvider(Client),
            "pelisplus" => new PelisPlusApiContentProvider(Client),
            "streamflix" => new PelisflixHdApiContentProvider(Client),
            "leercapitulo" => new LeerCapituloApiContentProvider(Client),
            "manhwaweb" => new ManhwaWebApiContentProvider(Client),
            "zonatmo" => new ZonaTmoApiContentProvider(Client),
            "capibaratraductor" =>
                new CapibaraTraductorApiContentProvider(Client),
            "uchuujinmangas" => new UchuujinMangasApiContentProvider(Client),
            "animetmo" => new AnimeTmoApiContentProvider(Client),
            "jkanime" => new JkAnimeApiContentProvider(Client),
            "olympusxyz" => new OlympusXyzApiContentProvider(Client),
            _ => throw new ArgumentOutOfRangeException(nameof(providerKey))
        };
}

public sealed class UmdJsonFilteredSearchTests
{
    private static readonly Uri BaseUri = new("https://umd.example/api/");

    [Fact]
    public async Task FiltersAreSentAsParametersAndVerifiedOnTheResponse()
    {
        var handler = new UmdJsonHandler();
        using var httpClient = new HttpClient(handler);
        IApiContentProvider provider =
            new UmdJsonApiContentProvider(httpClient);

        var page = await provider.SearchAsync(
            BaseUri,
            new ApiContentSearchRequest(
                genre: ApiContentGenre.Action,
                releaseYear: 1988));

        var request = Assert.Single(handler.Requests);
        Assert.Contains("genre=action", request.Query, StringComparison.Ordinal);
        Assert.Contains("year=1988", request.Query, StringComparison.Ordinal);

        // Verification is what makes the result trustworthy: the fixture also
        // returns titles that do not match, exactly as a server ignoring the
        // parameters would.
        var item = Assert.Single(page.Items);
        Assert.Equal("Akira", item.PreviewItem.Name);
    }

    [Fact]
    public async Task AServerThatIgnoresTheFiltersStillYieldsAFilteredResult()
    {
        var handler = new UmdJsonHandler();
        using var httpClient = new HttpClient(handler);
        IApiContentProvider provider =
            new UmdJsonApiContentProvider(httpClient);

        var page = await provider.SearchAsync(
            BaseUri,
            new ApiContentSearchRequest(releaseYear: 2001));

        Assert.Equal(
            "Spirited Away",
            Assert.Single(page.Items).PreviewItem.Name);
    }

    [Fact]
    public async Task TheStoredYearIsPreferredOverTheReleaseDate()
    {
        var handler = new UmdJsonHandler();
        using var httpClient = new HttpClient(handler);
        IApiContentProvider provider =
            new UmdJsonApiContentProvider(httpClient);

        // "Rereleased" carries releaseDate 2019 and releaseYear 1995; the
        // stored year is the original one and is what a year search means.
        var page = await provider.SearchAsync(
            BaseUri,
            new ApiContentSearchRequest(releaseYear: 1995));

        Assert.Equal(
            "Rereleased",
            Assert.Single(page.Items).PreviewItem.Name);
    }

    [Fact]
    public async Task ThePlainTextOverloadStillWorks()
    {
        var handler = new UmdJsonHandler();
        using var httpClient = new HttpClient(handler);
        var provider = new UmdJsonApiContentProvider(httpClient);

        var results = await provider.SearchAsync(BaseUri, "Akira");

        Assert.Equal(4, results.Count);
        var request = Assert.Single(handler.Requests);
        Assert.Contains("q=Akira", request.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("genre=", request.Query, StringComparison.Ordinal);
    }

    private sealed class UmdJsonHandler : HttpMessageHandler
    {
        public ConcurrentBag<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri ??
                throw new InvalidOperationException("Missing request URI.");
            Requests.Add(uri);
            var document = new CatalogTransferDocument
            {
                Items =
                [
                    Item("Akira", 1988, ["Acción"]),
                    Item("Spirited Away", 2001, ["Fantasía"]),
                    Item("Goblin", 2016, ["Romance"]),
                    Item("Rereleased", 1995, ["Drama"], releaseDate: "2019-01-01")
                ]
            };
            var json = JsonSerializer.Serialize(
                document,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(json))
            });
        }

        private static CatalogTransferItem Item(
            string name,
            int year,
            IReadOnlyList<string> genres,
            string? releaseDate = null) =>
            new()
            {
                MediaType = "movie",
                Subtype = "movie",
                Name = name,
                ReleaseYear = year,
                ReleaseDate = releaseDate,
                Genres = genres
            };
    }
}
