using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.UI.Tests;

public sealed class ApiContentSearchContractTests
{
    private static readonly Uri BaseUri = new("https://provider.example/");

    [Fact]
    public void RequestRequiresAtLeastOneCriterion()
    {
        Assert.Throws<ArgumentException>(() => new ApiContentSearchRequest());
        Assert.Throws<ArgumentException>(() =>
            new ApiContentSearchRequest(mediaType: MediaType.Movie));
        Assert.Throws<ArgumentException>(() =>
            new ApiContentSearchRequest("   "));
    }

    [Fact]
    public void RequestAcceptsAnySingleCriterion()
    {
        Assert.Equal("Akira", new ApiContentSearchRequest("  Akira  ").Query);
        Assert.Equal(
            ApiContentGenre.Action,
            new ApiContentSearchRequest(genre: ApiContentGenre.Action).Genre);
        Assert.Equal(1988, new ApiContentSearchRequest(releaseYear: 1988).ReleaseYear);
    }

    [Fact]
    public void RequestReportsWhetherFiltersAreSet()
    {
        Assert.False(new ApiContentSearchRequest("Akira").HasFilters);
        Assert.True(new ApiContentSearchRequest("Akira", releaseYear: 1988).HasFilters);
        Assert.True(
            new ApiContentSearchRequest(genre: ApiContentGenre.Mecha).HasFilters);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1799)]
    [InlineData(2201)]
    [InlineData(20_250)]
    public void RequestRejectsImplausibleYears(int year)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ApiContentSearchRequest(releaseYear: year));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(ApiContentSearchRequest.MaximumPageSize + 1)]
    public void RequestRejectsOutOfRangePageSize(int pageSize)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ApiContentSearchRequest("Akira", pageSize: pageSize));
    }

    [Fact]
    public void TextOnlyCapabilitiesRejectEveryFilter()
    {
        var capabilities = ApiContentSearchCapabilities.TextOnly;

        Assert.True(capabilities.CanSatisfy(new ApiContentSearchRequest("Akira")));
        Assert.False(capabilities.CanSatisfy(
            new ApiContentSearchRequest("Akira", genre: ApiContentGenre.Mecha)));
        Assert.False(capabilities.CanSatisfy(
            new ApiContentSearchRequest("Akira", releaseYear: 1988)));
        Assert.False(capabilities.CanSatisfy(
            new ApiContentSearchRequest(genre: ApiContentGenre.Mecha)));
    }

    [Fact]
    public void CapabilitiesRequireEveryCriterionNotJustOne()
    {
        // Genre-native, year-blind: the combined request must be refused rather
        // than answered on genre alone.
        var genreOnly = new ApiContentSearchCapabilities(
            genre: ApiContentFilterSupport.Native);

        Assert.True(genreOnly.CanSatisfy(
            new ApiContentSearchRequest("Akira", genre: ApiContentGenre.Mecha)));
        Assert.False(genreOnly.CanSatisfy(new ApiContentSearchRequest(
            "Akira",
            genre: ApiContentGenre.Mecha,
            releaseYear: 1988)));
    }

    [Fact]
    public void VerifiedLocalCountsAsSupport()
    {
        var capabilities = new ApiContentSearchCapabilities(
            genre: ApiContentFilterSupport.Native,
            releaseYear: ApiContentFilterSupport.VerifiedLocal);

        Assert.True(capabilities.CanSatisfy(new ApiContentSearchRequest(
            "Akira",
            genre: ApiContentGenre.Mecha,
            releaseYear: 1988)));
    }

    [Fact]
    public void FilterOnlySearchGatesTextlessRequests()
    {
        var request = new ApiContentSearchRequest(genre: ApiContentGenre.Mecha);

        Assert.False(
            new ApiContentSearchCapabilities(
                genre: ApiContentFilterSupport.Native).CanSatisfy(request));
        Assert.True(
            new ApiContentSearchCapabilities(
                genre: ApiContentFilterSupport.Native,
                supportsFilterOnlySearch: true).CanSatisfy(request));
    }

    [Fact]
    public void FilterOnlySearchNeedsAFilterToSearchOn()
    {
        Assert.Throws<ArgumentException>(() =>
            new ApiContentSearchCapabilities(supportsFilterOnlySearch: true));
    }

    [Fact]
    public void TruncatedPageCannotOfferAContinuationToken()
    {
        Assert.Throws<ArgumentException>(() => new ApiContentSearchPage(
            [],
            continuationToken: "page-2",
            isTruncated: true));
    }

    [Fact]
    public void PageNormalizesAndValidatesItsContents()
    {
        Assert.Empty(ApiContentSearchPage.Empty.Items);
        Assert.Null(ApiContentSearchPage.Empty.ContinuationToken);
        Assert.False(ApiContentSearchPage.Empty.IsTruncated);
        Assert.Null(new ApiContentSearchPage([], "   ").ContinuationToken);
        Assert.Equal("page-2", new ApiContentSearchPage([], " page-2 ").ContinuationToken);
        Assert.Throws<ArgumentNullException>(() => new ApiContentSearchPage(null!));
        Assert.Throws<ArgumentException>(() => new ApiContentSearchPage([null!]));
    }

    [Fact]
    public void GenreKeysAreNormalizedAndValidated()
    {
        Assert.Equal("science-fiction", ApiContentGenre.Parse("  Science-Fiction ").Key);
        Assert.Equal("slice-of-life", ApiContentGenre.SliceOfLife.ToString());
        Assert.Throws<ArgumentException>(() => ApiContentGenre.Parse("sci fi"));
        Assert.Throws<ArgumentException>(() => ApiContentGenre.Parse("-action"));
        Assert.Throws<ArgumentException>(() => ApiContentGenre.Parse("action-"));
        Assert.Throws<ArgumentException>(() => ApiContentGenre.Parse("action--rpg"));
        Assert.Throws<ArgumentException>(() => ApiContentGenre.Parse("a"));
        Assert.Throws<ArgumentException>(() => ApiContentGenre.Parse("acción"));
    }

    [Fact]
    public void KnownGenreKeysResolveToTheCatalogInstance()
    {
        Assert.True(ApiContentGenre.TryParse("ACTION", out var known));
        Assert.Same(ApiContentGenre.Action, known);

        // Well-formed but not yet catalogued: allowed, so a provider map can run
        // ahead of the shared list.
        Assert.True(ApiContentGenre.TryParse("mahou-shoujo", out var unlisted));
        Assert.Equal("mahou-shoujo", unlisted.Key);
        Assert.DoesNotContain(unlisted, ApiContentGenre.All);
        Assert.Equal(ApiContentGenre.Parse("mahou-shoujo"), unlisted);
    }

    [Fact]
    public void GenreCatalogKeysAreUniqueAndWellFormed()
    {
        Assert.NotEmpty(ApiContentGenre.All);
        Assert.Equal(
            ApiContentGenre.All.Count,
            ApiContentGenre.All.Select(static genre => genre.Key).Distinct(
                StringComparer.Ordinal).Count());
        foreach (var genre in ApiContentGenre.All)
        {
            Assert.Same(genre, ApiContentGenre.Parse(genre.Key));
        }
    }

    [Fact]
    public async Task DefaultBridgeForwardsATextOnlyRequest()
    {
        var provider = new TextOnlyProvider();
        IApiContentProvider contract = provider;

        var page = await contract.SearchAsync(
            BaseUri,
            new ApiContentSearchRequest("  Akira  ", MediaType.Movie));

        Assert.Equal("Akira", provider.LastQuery);
        Assert.Equal(MediaType.Movie, provider.LastMediaType);
        Assert.Equal("movie:akira", Assert.Single(page.Items).ExternalId);
        Assert.Null(page.ContinuationToken);
        Assert.False(page.IsTruncated);
    }

    [Fact]
    public async Task DefaultBridgeSkipsRatherThanBroadening()
    {
        var provider = new TextOnlyProvider();
        IApiContentProvider contract = provider;

        var page = await contract.SearchAsync(
            BaseUri,
            new ApiContentSearchRequest("Akira", releaseYear: 1988));

        Assert.Empty(page.Items);
        Assert.Null(provider.LastQuery);
    }

    [Fact]
    public async Task DefaultBridgeRefusesToCoverForADeclaredCapability()
    {
        // Declares genre support but never overrode the request-based overload,
        // so honouring the filter is impossible. Failing loudly beats returning
        // an unfiltered list that looks filtered.
        var provider = new LyingProvider();
        IApiContentProvider contract = provider;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await contract.SearchAsync(
                BaseUri,
                new ApiContentSearchRequest(genre: ApiContentGenre.Mecha)));

        Assert.Contains("lying-provider", exception.Message, StringComparison.Ordinal);
        Assert.Null(provider.LastQuery);
    }

    private static readonly CatalogTransferItem Preview = new()
    {
        MediaType = "movie",
        Subtype = "anime_movie",
        Name = "Akira"
    };

    private sealed class TextOnlyProvider : IApiContentProvider
    {
        public ApiContentProviderDescriptor Descriptor { get; } = new(
            "text-only-provider",
            "Text only provider");

        public string? LastQuery { get; private set; }

        public MediaType? LastMediaType { get; private set; }

        public ValueTask<IReadOnlyList<ApiContentSearchItem>> SearchAsync(
            Uri baseUri,
            string query,
            MediaType? mediaType = null,
            CancellationToken cancellationToken = default)
        {
            LastQuery = query;
            LastMediaType = mediaType;
            IReadOnlyList<ApiContentSearchItem> results =
            [
                new("movie:akira", Preview)
            ];
            return ValueTask.FromResult(results);
        }

        public ValueTask<ApiContentItem> LoadItemAsync(
            Uri baseUri,
            ApiContentSearchItem searchItem,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ApiContentItem(Preview));
    }

    private sealed class LyingProvider : IApiContentProvider
    {
        public ApiContentProviderDescriptor Descriptor { get; } = new(
            "lying-provider",
            "Lying provider");

        public ApiContentSearchCapabilities SearchCapabilities { get; } = new(
            genre: ApiContentFilterSupport.Native,
            supportsFilterOnlySearch: true);

        public string? LastQuery { get; private set; }

        public ValueTask<IReadOnlyList<ApiContentSearchItem>> SearchAsync(
            Uri baseUri,
            string query,
            MediaType? mediaType = null,
            CancellationToken cancellationToken = default)
        {
            LastQuery = query;
            return ValueTask.FromResult<IReadOnlyList<ApiContentSearchItem>>(
                [new("movie:akira", Preview)]);
        }

        public ValueTask<ApiContentItem> LoadItemAsync(
            Uri baseUri,
            ApiContentSearchItem searchItem,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ApiContentItem(Preview));
    }
}
