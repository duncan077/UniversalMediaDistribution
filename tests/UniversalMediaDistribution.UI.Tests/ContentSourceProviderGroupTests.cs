using UniversalMediaDistribution.Application.ContentSources;
using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.UI.ViewModels;

namespace UniversalMediaDistribution.UI.Tests;

/// <summary>
/// Sixteen online sources listed flat made turning a whole category on a
/// sixteen-tap job, so the settings page groups them by kind.
/// </summary>
public sealed class ContentSourceProviderGroupTests
{
    [Fact]
    public void TheSummaryCountsWhatIsSwitchedOn()
    {
        var group = Build(
            ("one", true),
            ("two", false),
            ("three", true));

        Assert.Equal(2, group.EnabledCount);
        Assert.False(group.AreAllEnabled);
    }

    [Fact]
    public void EnablingTheGroupSwitchesEveryProviderOn()
    {
        var group = Build(
            ("one", true),
            ("two", false));

        group.ToggleAllCommand.Execute(null);

        Assert.All(group.Providers, provider => Assert.True(provider.IsEnabled));
        Assert.True(group.AreAllEnabled);
    }

    [Fact]
    public void AGroupAlreadyFullyOnSwitchesEverythingOff()
    {
        var group = Build(
            ("one", true),
            ("two", true));

        group.ToggleAllCommand.Execute(null);

        Assert.All(group.Providers, provider => Assert.False(provider.IsEnabled));
    }

    [Fact]
    public void TheSummaryFollowsAProviderSwitchedOnItsOwn()
    {
        var group = Build(
            ("one", false),
            ("two", false));
        var changed = new List<string?>();
        group.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        group.Providers[0].IsEnabled = true;

        Assert.Equal(1, group.EnabledCount);
        Assert.Contains(nameof(group.SummaryText), changed);
    }

    [Fact]
    public void AGroupOfOneOffersNoBulkSwitch()
    {
        // The single row is already the switch; a second one beside it would
        // only be a wider target for the same thing.
        Assert.False(Build(("only", true)).CanToggleAll);
        Assert.True(Build(("one", true), ("two", true)).CanToggleAll);
    }

    [Fact]
    public void ApiProvidersAreSubgroupedByMediaCategory()
    {
        var group = new ContentSourceProviderGroupViewModel(
            ContentSourceProviderKind.Api,
            [
                Provider("movies", ApiContentProviderScope.LiveAction),
                Provider("anime", ApiContentProviderScope.Anime),
                Provider("manga", ApiContentProviderScope.Manga)
            ]);

        Assert.Equal(3, group.ApiCategories.Count);
        Assert.Equal("Movies & series", group.ApiCategories[0].Title);
        Assert.Equal("Anime", group.ApiCategories[1].Title);
        Assert.Equal("Books & manga", group.ApiCategories[2].Title);
        Assert.All(group.ApiCategories, category =>
            Assert.Single(category.Providers));
    }

    [Fact]
    public void GeneralApiProvidersRemainAvailableInAnAllMediaCategory()
    {
        var group = new ContentSourceProviderGroupViewModel(
            ContentSourceProviderKind.Api,
            [Provider("general", ApiContentProviderScope.All)]);

        var category = Assert.Single(group.ApiCategories);
        Assert.Equal("All media", category.Title);
        Assert.Equal("general", Assert.Single(category.Providers).Id);
    }

    private static ContentSourceProviderGroupViewModel Build(
        params (string Id, bool IsEnabled)[] providers) =>
        new(
            ContentSourceProviderKind.Api,
            providers.Select(provider =>
                new ContentSourceProviderRowViewModel(
                    new ContentSourceProviderConfiguration
                    {
                        Id = provider.Id,
                        DisplayName = provider.Id,
                        Kind = ContentSourceProviderKind.Api,
                        IsEnabled = provider.IsEnabled,
                        BaseUrl = $"https://{provider.Id}.example/api"
                    })));

    private static ContentSourceProviderRowViewModel Provider(
        string id,
        ApiContentProviderScope scope) => new(
        new ContentSourceProviderConfiguration
        {
            Id = id,
            DisplayName = id,
            Kind = ContentSourceProviderKind.Api,
            BaseUrl = $"https://{id}.example/api"
        },
        apiScope: scope);
}
