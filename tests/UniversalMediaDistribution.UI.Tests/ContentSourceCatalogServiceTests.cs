using System.Net;
using System.Text;
using System.Text.Json;
using UniversalMediaDistribution.Application.Catalog;
using UniversalMediaDistribution.Application.ContentSources;
using UniversalMediaDistribution.Catalog;
using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.UI.Tests;

public sealed class ContentSourceCatalogServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnlineCatalogIsSearchableMetadataButIsNotAReaderOrVideoProvider(bool video)
    {
        var item = video ? CreateVideo("Copper Skies", "series") : CreateManga("Copper Skies", "Metadata", 3);
        var handler = new CatalogJsonHandler(new CatalogTransferDocument { Items = [item] });
        using var http = new HttpClient(handler);
        var provider = new UmdJsonApiContentProvider(http);
        var configuration = CreateApiConfiguration(provider);
        var repository = new InMemoryCatalogRepository(new CatalogTransferDocument { Items = [item] });
        var service = CreateService(repository, new InMemoryContentSourceProviderSettingsStore(new()
        {
            Providers = [configuration]
        }), http, provider);
        var media = video ? MediaType.Movie : MediaType.Book;
        Assert.False(service.ProvidesUnitContent(configuration.Id));
        Assert.True(service.IsProviderAvailableForSearch(configuration, media, null));
        var response = await service.SearchAsync("Copper", media);
        var online = Assert.Single(response.Results, result => result.IsOnline);
        Assert.Empty(online.Card.Distributors);
        var loaded = await service.LoadOnlineItemAsync(online);
        Assert.DoesNotContain(loaded.Details.Item.Contributors, contributor => contributor.Name == configuration.DisplayName);
        var requests = handler.RequestCount;
        var unit = loaded.Details.Units.First();
        if (video)
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.FindApiVideoCandidatesForUnitAsync(loaded.Details, unit).AsTask());
        else
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.FindApiImageCandidatesForUnitAsync(loaded.Details, unit).AsTask());
        Assert.Equal(requests, handler.RequestCount);
        // The remote catalog setting is retained for metadata search and sync.
        var saved = Assert.Single((await service.LoadSettingsAsync()).Providers, entry => entry.Id == configuration.Id);
        Assert.True(saved.IsEnabled);
        Assert.Equal(configuration.BaseUrl, saved.BaseUrl);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderUpdateTargetsOnlyTheSelectedBookFormat(bool refreshTitle)
    {
        var manga = CreateManga("Copper Skies", "Saved manga", 2);
        var novel = manga with { Subtype = "novel", Description = "Saved novel" };
        var repository = new InMemoryCatalogRepository(new CatalogTransferDocument { Items = [novel, manga] });
        var provider = new DynamicChapterApiProvider();
        using var httpClient = new HttpClient(new EmptyHandler());
        var service = CreateService(repository,
            new InMemoryContentSourceProviderSettingsStore(new ContentSourceProviderSettings
            {
                Providers = [CreateApiConfiguration(provider)]
            }), httpClient, provider);
        var browse = new CatalogBrowseService(repository);
        var cards = (await browse.GetOverviewAsync(MediaType.Book)).Items;
        var mangaCard = Assert.Single(cards, card => card.SubtypeCode == "book.manga");
        var novelCard = Assert.Single(cards, card => card.SubtypeCode == "book.novel");
        var details = Assert.IsType<CatalogItemDetails>(await browse.GetDetailsAsync(mangaCard.Id));
        if (refreshTitle)
            Assert.NotNull(await service.RefreshApiItemAsync(details));
        else
            await service.ResolveApiImagesForUnitAsync(details, details.Units.First());
        var savedNovel = Assert.IsType<CatalogItemDetails>(await browse.GetDetailsAsync(novelCard.Id));
        Assert.Equal("Saved novel", savedNovel.Item.Description);
        Assert.Equal(2, savedNovel.Units.Count);
        var savedManga = Assert.IsType<CatalogItemDetails>(await browse.GetDetailsAsync(mangaCard.Id));
        Assert.Equal(3, savedManga.Units.Count);
    }

    [Fact]
    public async Task MangaProviderCannotRefreshOrSupplyChaptersForSameNamedNovel()
    {
        var novel = CreateManga("Copper Skies", "Saved novel", 2) with { Subtype = "novel" };
        var repository = new InMemoryCatalogRepository(new CatalogTransferDocument { Items = [novel] });
        var provider = new DynamicChapterApiProvider();
        using var httpClient = new HttpClient(new EmptyHandler());
        var service = CreateService(repository,
            new InMemoryContentSourceProviderSettingsStore(new ContentSourceProviderSettings
            {
                Providers = [CreateApiConfiguration(provider)]
            }), httpClient, provider);
        var browse = new CatalogBrowseService(repository);
        var card = Assert.Single((await browse.GetOverviewAsync(MediaType.Book)).Items);
        var details = Assert.IsType<CatalogItemDetails>(await browse.GetDetailsAsync(card.Id));
        Assert.Null(await service.RefreshApiItemAsync(details));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await service.FindApiImageCandidatesForUnitAsync(details, details.Units.First()));
        var saved = Assert.IsType<CatalogItemDetails>(await browse.GetDetailsAsync(card.Id));
        Assert.Equal("book.novel", saved.Item.SubtypeCode);
        Assert.Equal(2, saved.Units.Count);
    }

    [Fact]
    public async Task InMemoryCatalogStartsWithoutDemoTitles()
    {
        var repository = new InMemoryCatalogRepository();

        Assert.Empty(await repository.ListAsync());
    }

    [Fact]
    public async Task SearchKeepsMatchingLocalAndTorrentCatalogResultsSeparate()
    {
        var remoteItem = CreateManga(
            "Copper Skies",
            "Provider version",
            chapterCount: 3);
        var handler = new CatalogJsonHandler(new CatalogTransferDocument
        {
            SourceLabel = "Community catalog",
            Items = [remoteItem]
        });
        using var httpClient = new HttpClient(handler);
        var repository = CreateLocalMangaRepository();
        var service = CreateService(
            repository,
            new InMemoryContentSourceProviderSettingsStore(
                new ContentSourceProviderSettings
                {
                    Providers =
                    [
                        new ContentSourceProviderConfiguration
                        {
                            Id = "torrent.community",
                            DisplayName = "Community torrents",
                            Kind = ContentSourceProviderKind.TorrentCatalog,
                            IsEnabled = true,
                            BaseUrl = "https://catalog.example/catalog.json"
                        }
                    ]
                }),
            httpClient);

        var response = await service.SearchAsync("Copper", MediaType.Book);

        Assert.Empty(response.Failures);
        Assert.Equal(2, response.Results.Count);
        Assert.Single(response.Results, static result => !result.IsOnline);
        var online = Assert.Single(
            response.Results,
            static result => result.IsOnline);
        Assert.Equal(
            ContentSourceProviderKind.TorrentCatalog,
            online.Provider!.Kind);
        Assert.Equal("Community torrents", online.Provider.DisplayName);
        Assert.Equal(
            "Community torrents",
            Assert.Single(online.Card.Distributors).Name);

        var loaded = await service.LoadOnlineItemAsync(online);

        Assert.Equal("Community torrents", loaded.ProviderName);
        Assert.Equal(
            "Community torrents",
            Assert.Single(
                loaded.Details.Item.Contributors,
                static contributor =>
                    contributor.IsDistributor).Name);
        Assert.Equal(3, loaded.Details.Units.Count);
        Assert.Equal("Provider version", loaded.Details.Item.Description);
        Assert.Equal(1, loaded.UpdatedCount);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task StreamingSearchYieldsLocalThenEachProviderAsItCompletes()
    {
        var slowRelease = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var fast = new ControlledApiProvider(
            "fast-reader",
            "Fast reader",
            release: null);
        var slow = new ControlledApiProvider(
            "slow-reader",
            "Slow reader",
            slowRelease.Task);
        using var httpClient = new HttpClient(new EmptyHandler());
        var settings = new InMemoryContentSourceProviderSettingsStore(
            new ContentSourceProviderSettings
            {
                Providers =
                [
                    CreateApiConfiguration(fast),
                    CreateApiConfiguration(slow)
                ]
            });
        var service = CreateService(
            CreateLocalMangaRepository(),
            settings,
            httpClient,
            fast,
            slow);

        await using var batches = service.SearchStreamingAsync(
                "Copper",
                MediaType.Book)
            .GetAsyncEnumerator();

        Assert.True(await batches.MoveNextAsync());
        Assert.True(batches.Current.IsLocal);
        Assert.Single(batches.Current.Results);

        Assert.True(await batches.MoveNextAsync());
        Assert.Equal("fast-reader", batches.Current.ProviderId);
        Assert.False(batches.Current.IsLocal);

        slowRelease.SetResult();
        Assert.True(await batches.MoveNextAsync());
        Assert.Equal("slow-reader", batches.Current.ProviderId);
        Assert.False(await batches.MoveNextAsync());
    }

    [Fact]
    public async Task StreamingSearchUsesSharedThreeProviderLimit()
    {
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thirdEntry = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var fourthEntry = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        Action onStarted = () =>
        {
            var count = Interlocked.Increment(ref entered);
            if (count >= 3)
            {
                thirdEntry.TrySetResult();
            }

            if (count >= 4)
            {
                fourthEntry.TrySetResult();
            }
        };
        var providers = Enumerable.Range(0, 6)
            .Select(index => new ControlledApiProvider(
                $"controlled-{index}",
                $"Controlled {index}",
                release.Task,
                onStarted))
            .ToArray();
        using var httpClient = new HttpClient(new EmptyHandler());
        var settings = new InMemoryContentSourceProviderSettingsStore(
            new ContentSourceProviderSettings
            {
                Providers = providers
                    .Select(CreateApiConfiguration)
                    .ToArray()
            });
        var service = CreateService(
            new InMemoryCatalogRepository(),
            settings,
            httpClient,
            providers);
        var consumeTask = ConsumeAsync();

        await thirdEntry.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.Equal(3, Volatile.Read(ref entered));
            Assert.False(fourthEntry.Task.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
        }

        await consumeTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(6, Volatile.Read(ref entered));

        async Task ConsumeAsync()
        {
            await foreach (var _ in service.SearchStreamingAsync(
                "Copper",
                MediaType.Book))
            {
            }
        }
    }

    [Fact]
    public async Task ProviderRegistrationDoesNotConstructDisabledProvider()
    {
        var descriptor = new ApiContentProviderDescriptor(
            "lazy-reader",
            "Lazy reader",
            "https://lazy-reader.example/api");
        var creationCount = 0;
        var registration = new ApiContentProviderRegistration(
            descriptor,
            () =>
            {
                creationCount++;
                return new TestApiProvider(
                    name: "Lazy Copper",
                    id: descriptor.Id);
            });
        using var httpClient = new HttpClient(new EmptyHandler());
        var repository = new InMemoryCatalogRepository(
            new CatalogTransferDocument());
        var settingsStore =
            new InMemoryContentSourceProviderSettingsStore();
        var service = new ContentSourceCatalogService(
            new CatalogBrowseService(repository),
            new CatalogJsonTransferService(repository),
            settingsStore,
            httpClient,
            [registration]);

        var settings = await service.LoadSettingsAsync();
        Assert.Equal(0, creationCount);

        var configuration = Assert.Single(
            settings.Providers,
            provider => provider.Id == descriptor.Id);
        await service.SaveSettingsAsync(new ContentSourceProviderSettings
        {
            Providers = settings.Providers
                .Select(provider => provider.Id == descriptor.Id
                    ? provider with { IsEnabled = true }
                    : provider)
                .ToArray()
        });
        Assert.Equal(0, creationCount);

        var response = await service.SearchAsync(
            "Lazy",
            MediaType.Book);

        Assert.Single(response.Results);
        Assert.Equal(1, creationCount);
        Assert.True(registration.IsValueCreated);
        Assert.False(configuration.IsEnabled);
    }

    [Fact]
    public async Task AdultApiResultsAreExcludedWhenThePreferenceIsEnabled()
    {
        var provider = new ControlledApiProvider(
            "adult-reader",
            "Adult reader",
            release: null,
            adultContent: true);
        using var httpClient = new HttpClient(new EmptyHandler());
        var settings = new InMemoryContentSourceProviderSettingsStore(
            new ContentSourceProviderSettings
            {
                Providers = [CreateApiConfiguration(provider)]
            });
        var service = CreateService(
            CreateLocalMangaRepository(),
            settings,
            httpClient,
            hideAdultContent: () => true,
            providers: [provider]);

        var response = await service.SearchAsync("Copper", MediaType.Book);

        Assert.Single(response.Results);
        Assert.False(response.Results[0].IsOnline);
    }

    [Fact]
    public async Task AdultApiGenresAreExcludedWhenThePreferenceIsEnabled()
    {
        var provider = new ControlledApiProvider(
            "adult-genre-reader",
            "Adult genre reader",
            release: null,
            adultGenre: true);
        using var httpClient = new HttpClient(new EmptyHandler());
        var settings = new InMemoryContentSourceProviderSettingsStore(
            new ContentSourceProviderSettings
            {
                Providers = [CreateApiConfiguration(provider)]
            });
        var service = CreateService(
            CreateLocalMangaRepository(),
            settings,
            httpClient,
            hideAdultContent: () => true,
            providers: [provider]);

        var response = await service.SearchAsync("Copper", MediaType.Book);

        Assert.Single(response.Results);
        Assert.False(response.Results[0].IsOnline);
    }

    [Fact]
    public async Task TorrentItemIsRecheckedBeforeItIsImported()
    {
        var safePreview = CreateManga(
            "Changing Skies",
            "Initially safe",
            chapterCount: 1);
        var adultItem = safePreview with { Genres = ["Adult"] };
        var handler = new CatalogJsonHandler(
            new CatalogTransferDocument { Items = [safePreview] },
            new CatalogTransferDocument { Items = [adultItem] });
        using var httpClient = new HttpClient(handler);
        var repository = new InMemoryCatalogRepository();
        var settings = new InMemoryContentSourceProviderSettingsStore(
            new ContentSourceProviderSettings
            {
                Providers =
                [
                    new ContentSourceProviderConfiguration
                    {
                        Id = "changing.catalog",
                        DisplayName = "Changing catalog",
                        Kind = ContentSourceProviderKind.TorrentCatalog,
                        IsEnabled = true,
                        BaseUrl = "https://catalog.example/catalog.json"
                    }
                ]
            });
        var service = CreateService(
            repository,
            settings,
            httpClient,
            hideAdultContent: () => true);
        var online = Assert.Single(
            (await service.SearchAsync("Changing", MediaType.Book)).Results,
            static result => result.IsOnline);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await service.LoadOnlineItemAsync(online));

        Assert.Contains(
            "adult-content filter",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await repository.ListAsync());
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task ApiItemIsRecheckedAfterLoadingFullDetails()
    {
        var provider = new ControlledApiProvider(
            "changing-api",
            "Changing API",
            release: null,
            adultOnLoad: true);
        using var httpClient = new HttpClient(new EmptyHandler());
        var repository = new InMemoryCatalogRepository();
        var settings = new InMemoryContentSourceProviderSettingsStore(
            new ContentSourceProviderSettings
            {
                Providers = [CreateApiConfiguration(provider)]
            });
        var service = CreateService(
            repository,
            settings,
            httpClient,
            hideAdultContent: () => true,
            providers: [provider]);
        var online = Assert.Single(
            (await service.SearchAsync("Copper", MediaType.Book)).Results,
            static result => result.IsOnline);

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await service.LoadOnlineItemAsync(online));

        Assert.Empty(await repository.ListAsync());
    }

    [Fact]
    public async Task CodeProviderCanReturnDirectOrderedChapterImages()
    {
        var provider = new TestApiProvider();
        using var httpClient = new HttpClient(new EmptyHandler());
        var repository = new InMemoryCatalogRepository(
            new CatalogTransferDocument());
        var service = CreateService(
            repository,
            new InMemoryContentSourceProviderSettingsStore(
                new ContentSourceProviderSettings
                {
                    Providers =
                    [
                        new ContentSourceProviderConfiguration
                        {
                            Id = provider.Descriptor.Id,
                            DisplayName = provider.Descriptor.DisplayName,
                            Kind = ContentSourceProviderKind.Api,
                            IsEnabled = true,
                            BaseUrl = "https://reader.example/api"
                        }
                    ]
                }),
            httpClient,
            provider);

        var response = await service.SearchAsync("Cloud", MediaType.Book);
        var online = Assert.Single(response.Results);
        Assert.True(online.IsOnline);
        Assert.Equal(ContentSourceProviderKind.Api, online.Provider!.Kind);

        var loaded = await service.LoadOnlineItemAsync(online);

        var imageSet = Assert.Single(loaded.ImageSets);
        Assert.Equal(ContentUnitKind.Chapter, imageSet.UnitKind);
        Assert.Equal("1", imageSet.DisplayNumber);
        Assert.Equal(
            [0, 1],
            imageSet.Pages.Select(static page => page.Position));
        Assert.Single(loaded.Details.Units);
        Assert.Equal(1, loaded.CreatedCount);
        Assert.Equal(1, provider.LoadCount);
        Assert.Contains(
            loaded.TrustedArtworkOrigins,
            static origin => origin.Host == "covers.example");
    }

    [Fact]
    public async Task ApiAlternativeTitleKeepsCatalogIdAndUsesSearchThumbnail()
    {
        var localItem = new CatalogTransferItem
        {
            MediaType = "movie",
            Subtype = "anime_movie",
            Name = "Spirited Away",
            AlternativeNames =
            [
                new CatalogTransferAlias
                {
                    Name = "El viaje de Chihiro",
                    AliasType = "localized"
                }
            ],
            Units =
            [
                new CatalogTransferUnit
                {
                    Kind = "episode",
                    DisplayNumber = "movie",
                    SortOrder = 0
                }
            ]
        };
        var repository = new InMemoryCatalogRepository(
            new CatalogTransferDocument { Items = [localItem] });
        var browse = new CatalogBrowseService(repository);
        var originalCard = Assert.Single(
            (await browse.GetOverviewAsync(MediaType.Movie)).Items);
        var provider = new MovieArtworkProvider();
        using var httpClient = new HttpClient(new EmptyHandler());
        var service = CreateService(
            repository,
            new InMemoryContentSourceProviderSettingsStore(
                new ContentSourceProviderSettings
                {
                    Providers = [CreateApiConfiguration(provider)]
                }),
            httpClient,
            provider);

        var response = await service.SearchAsync(
            "Spirited Away",
            MediaType.Movie);
        var online = Assert.Single(
            response.Results,
            static result => result.IsOnline);
        var loaded = await service.LoadOnlineItemAsync(online);

        Assert.Equal(originalCard.Id, loaded.Details.Item.Id);
        Assert.Equal("Spirited Away", loaded.Details.Item.Name);
        Assert.Equal(
            "https://images.example/spirited-away-new.jpg",
            loaded.Details.Item.ThumbnailRef);
        var overviewCard = Assert.Single(
            (await browse.GetOverviewAsync(MediaType.Movie)).Items);
        Assert.Equal(originalCard.Id, overviewCard.Id);
        Assert.Equal(
            "https://images.example/spirited-away-new.jpg",
            overviewCard.ThumbnailRef);
        Assert.Contains(
            loaded.Details.Item.AlternativeNames,
            static alias => alias.Name == "El viaje de Chihiro");
    }
    [Fact]
    public async Task DefaultApiProviderSearchUsesAccentInsensitiveNameContains()
    {
        var provider = new TestApiProvider(
            "Oculto el centro de logística en el apocalipsis",
            isEnabledByDefault: true);
        using var httpClient = new HttpClient(new EmptyHandler());
        var service = CreateService(
            new InMemoryCatalogRepository(
                new CatalogTransferDocument()),
            new InMemoryContentSourceProviderSettingsStore(),
            httpClient,
            provider);

        var settings = await service.LoadSettingsAsync();
        Assert.True(Assert.Single(
            settings.Providers,
            configuration =>
                configuration.Id == provider.Descriptor.Id).IsEnabled);

        var response = await service.SearchAsync(
            "logistica",
            MediaType.Book);

        Assert.Empty(response.Failures);
        var result = Assert.Single(response.Results);
        Assert.True(result.IsOnline);
        Assert.Equal(
            "Oculto el centro de logística en el apocalipsis",
            result.Card.Name);
    }

    [Fact]
    public async Task UsersCannotCreateAnUnregisteredApiProvider()
    {
        using var httpClient = new HttpClient(new EmptyHandler());
        var repository = new InMemoryCatalogRepository(
            new CatalogTransferDocument());
        var service = CreateService(
            repository,
            new InMemoryContentSourceProviderSettingsStore(),
            httpClient);

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            async () => await service.SaveSettingsAsync(
                new ContentSourceProviderSettings
                {
                    Providers =
                    [
                        new ContentSourceProviderConfiguration
                        {
                            Id = "not-installed",
                            DisplayName = "Not installed",
                            Kind = ContentSourceProviderKind.Api,
                            IsEnabled = false
                        }
                    ]
                }));

        Assert.Contains(
            "not registered",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DynamicChapterMatchMergesMangaAndChaptersWithoutChangingLocalIdentity()
    {
        var provider = new DynamicChapterApiProvider();
        using var httpClient = new HttpClient(new EmptyHandler());
        var repository = CreateLocalMangaRepository();
        var service = CreateService(
            repository,
            new InMemoryContentSourceProviderSettingsStore(
                new ContentSourceProviderSettings
                {
                    Providers =
                    [
                        new ContentSourceProviderConfiguration
                        {
                            Id = provider.Descriptor.Id,
                            DisplayName = provider.Descriptor.DisplayName,
                            Kind = ContentSourceProviderKind.Api,
                            IsEnabled = true,
                            BaseUrl = "https://dynamic-reader.example/api"
                        }
                    ]
                }),
            httpClient,
            provider);
        var browse = new CatalogBrowseService(repository);
        var card = Assert.Single(
            (await browse.GetOverviewAsync(MediaType.Book)).Items,
            static item => item.Name == "Copper Skies");
        var details = Assert.IsType<CatalogItemDetails>(
            await browse.GetDetailsAsync(card.Id));
        var chapter = Assert.Single(
            details.Units,
            static unit => unit.DisplayNumber == "1");
        var originalChapterKey = chapter.Identity.StableKey;

        var resolution = await service.ResolveApiImagesForUnitAsync(
            details,
            chapter);

        Assert.Equal("Copper Skies", provider.Queries[0]);
        Assert.Equal("Dynamic manga reader", resolution.ProviderName);
        Assert.InRange(resolution.TitleSimilarity, 0.90, 0.99);
        Assert.Equal(2, resolution.ImageSet.Pages.Count);
        Assert.Equal("01", resolution.ImageSet.DisplayNumber);
        Assert.Equal(0, resolution.CreatedCount);
        Assert.Equal(1, resolution.UpdatedCount);
        Assert.Equal(details.Item.Id, resolution.Details.Item.Id);
        Assert.Equal("Copper Skies", resolution.Details.Item.Name);
        Assert.Equal(
            "Dynamic provider result",
            resolution.Details.Item.Description);
        Assert.Equal("1", resolution.Unit.DisplayNumber);
        Assert.Equal(originalChapterKey, resolution.Unit.Identity.StableKey);
        Assert.Equal("Provider Horizon", resolution.Unit.Name);
        Assert.Equal(30, resolution.Unit.PageCount);
        Assert.Contains(
            resolution.Details.Item.AlternativeNames,
            static name => name.Name == "Coper Skies");
        Assert.Contains(
            resolution.Details.Units,
            static unit => unit.DisplayNumber == "2");
        Assert.Contains(
            resolution.Details.Units,
            static unit => unit.DisplayNumber == "3");
        Assert.Equal(3, resolution.Details.Units.Count);
        Assert.Equal(1, provider.LoadItemCount);
        Assert.Equal(1, provider.UnitImageLoadCount);
        var updated = Assert.IsType<CatalogItemDetails>(
            await browse.GetDetailsAsync(card.Id));
        Assert.Equal(
            resolution.Details.Item.Description,
            updated.Item.Description);
        Assert.DoesNotContain(
            (await browse.GetOverviewAsync(MediaType.Book)).Items,
            static item => item.Name == "Coper Skies");
    }

    [Fact]
    public async Task DynamicChapterLookupReturnsEveryMatchingProviderAndUsesSelectedOne()
    {
        var first = new DynamicChapterApiProvider(
            "dynamic-reader-first",
            "First reader");
        var second = new DynamicChapterApiProvider(
            "dynamic-reader-second",
            "Second reader");
        using var httpClient = new HttpClient(new EmptyHandler());
        var repository = CreateLocalMangaRepository();
        var bindings = new InMemoryApiTitleBindingStore();
        var service = CreateService(
            repository,
            new InMemoryContentSourceProviderSettingsStore(
                new ContentSourceProviderSettings
                {
                    Providers =
                    [
                        CreateApiConfiguration(first),
                        CreateApiConfiguration(second)
                    ]
                }),
            httpClient,
            bindings,
            first,
            second);
        var browse = new CatalogBrowseService(repository);
        var card = Assert.Single(
            (await browse.GetOverviewAsync(MediaType.Book)).Items,
            static item => item.Name == "Copper Skies");
        var details = Assert.IsType<CatalogItemDetails>(
            await browse.GetDetailsAsync(card.Id));
        var chapter = Assert.Single(
            details.Units,
            static unit => unit.DisplayNumber == "1");

        var candidates =
            await service.FindApiImageCandidatesForUnitAsync(
                details,
                chapter);

        Assert.Equal(
            ["First reader", "Second reader"],
            candidates.Select(static candidate =>
                candidate.ProviderName));
        Assert.Equal(0, first.UnitImageLoadCount);
        Assert.Equal(0, second.UnitImageLoadCount);
        var selected = Assert.Single(
            candidates,
            static candidate =>
                candidate.ProviderName == "Second reader");
        var resolution = await service.ResolveApiImagesForUnitAsync(
            details,
            chapter,
            selected);
        Assert.Equal("Second reader", resolution.ProviderName);
        Assert.Equal(0, first.UnitImageLoadCount);
        Assert.Equal(1, second.UnitImageLoadCount);
        Assert.Contains(
            resolution.Details.Item.Contributors,
            static contributor =>
                contributor.Name == "Second reader" &&
                contributor.Credits.Any(
                    static credit =>
                        credit.Role == "Distributor"));

        var firstSearches = first.Queries.Count;
        var secondSearches = second.Queries.Count;
        var remembered = await service.FindApiImageCandidatesForUnitAsync(
            details,
            chapter);

        Assert.Equal(
            ["First reader", "Second reader"],
            remembered.Select(static candidate => candidate.ProviderName));
        Assert.True(first.Queries.Count > firstSearches);
        Assert.Equal(secondSearches, second.Queries.Count);
    }

    [Fact]
    public async Task EnteringSavedMangaCanRefreshNewProviderChapters()
    {
        var provider = new DynamicChapterApiProvider();
        using var httpClient = new HttpClient(new EmptyHandler());
        var repository = CreateLocalMangaRepository();
        var service = CreateService(
            repository,
            new InMemoryContentSourceProviderSettingsStore(
                new ContentSourceProviderSettings
                {
                    Providers =
                    [
                        new ContentSourceProviderConfiguration
                        {
                            Id = provider.Descriptor.Id,
                            DisplayName = provider.Descriptor.DisplayName,
                            Kind = ContentSourceProviderKind.Api,
                            IsEnabled = true,
                            BaseUrl = "https://dynamic-reader.example/api"
                        }
                    ]
                }),
            httpClient,
            provider);
        var browse = new CatalogBrowseService(repository);
        var card = Assert.Single(
            (await browse.GetOverviewAsync(MediaType.Book)).Items,
            static item => item.Name == "Copper Skies");
        var details = Assert.IsType<CatalogItemDetails>(
            await browse.GetDetailsAsync(card.Id));

        var refreshed = await service.RefreshApiItemAsync(details);

        Assert.NotNull(refreshed);
        Assert.Equal(details.Item.Id, refreshed.Details.Item.Id);
        Assert.Equal("Dynamic manga reader", refreshed.ProviderName);
        Assert.Equal(1, refreshed.UpdatedCount);
        Assert.Equal(3, refreshed.Details.Units.Count);
        Assert.Contains(
            refreshed.Details.Units,
            static unit => unit.DisplayNumber == "3");
        Assert.Equal("Saved manga", refreshed.Details.Item.Description);
        Assert.Equal(
            "Chapter 1",
            refreshed.Details.Units.Single(
                static unit => unit.DisplayNumber == "1").Name);
        Assert.Equal(1, provider.LoadItemCount);
        Assert.Equal(0, provider.UnitImageLoadCount);
    }

    [Fact]
    public async Task CompleteSavedCatalogDoesNotResearchApiProviders()
    {
        var provider = new DynamicChapterApiProvider();
        var repository = new InMemoryCatalogRepository(
            new CatalogTransferDocument
            {
                Items =
                [
                    CreateManga(
                        "Copper Skies",
                        "Complete local description",
                        chapterCount: 2) with
                    {
                        ReleaseDate = "2026-01-01",
                        ThumbnailRef =
                            "https://local.example/copper.webp"
                    }
                ]
            });
        using var httpClient = new HttpClient(new EmptyHandler());
        var service = CreateService(
            repository,
            new InMemoryContentSourceProviderSettingsStore(
                new ContentSourceProviderSettings
                {
                    Providers = [CreateApiConfiguration(provider)]
                }),
            httpClient,
            provider);
        var browse = new CatalogBrowseService(repository);
        var details = Assert.IsType<CatalogItemDetails>(
            await browse.GetDetailsAsync(
                Assert.Single(
                    (await browse.GetOverviewAsync(MediaType.Book)).Items).Id));

        var refreshed = await service.RefreshApiItemAsync(details);

        Assert.Null(refreshed);
        Assert.Empty(provider.Queries);
        Assert.Equal(0, provider.LoadItemCount);
    }

    [Fact]
    public async Task HourlyUpdatesAreSessionScopedAndAppendNewestUnits()
    {
        var provider = new DailyApiProvider();
        var timeProvider = new AdjustableTimeProvider(
            new DateTimeOffset(
                2026,
                7,
                23,
                12,
                0,
                0,
                TimeSpan.Zero));
        var repository = new InMemoryCatalogRepository(
            new CatalogTransferDocument
            {
                Items =
                [
                    CreateManga(
                        "Copper Skies",
                        "Local description",
                        chapterCount: 2)
                ]
            });
        using var httpClient = new HttpClient(new EmptyHandler());
        var settings = new InMemoryContentSourceProviderSettingsStore(
            new ContentSourceProviderSettings
            {
                Providers =
                [
                    new ContentSourceProviderConfiguration
                    {
                        Id = provider.Descriptor.Id,
                        DisplayName = provider.Descriptor.DisplayName,
                        Kind = ContentSourceProviderKind.Api,
                        IsEnabled = true,
                        BaseUrl = "https://daily.example/"
                    }
                ]
            });
        var legacyCheckpointStore =
            new InMemoryContentSourceDailyUpdateStore();
        var service = new ContentSourceCatalogService(
            new CatalogBrowseService(repository),
            new CatalogJsonTransferService(repository),
            settings,
            httpClient,
            [provider],
            legacyCheckpointStore,
            timeProvider);

        var first = await service.RefreshDailyUpdatesAsync();
        var sameHour = await service.RefreshDailyUpdatesAsync();
        timeProvider.Advance(TimeSpan.FromMinutes(59));
        var beforeNextHour = await service.RefreshDailyUpdatesAsync();
        timeProvider.Advance(TimeSpan.FromMinutes(1));
        var nextHour = await service.RefreshDailyUpdatesAsync();
        var restartedService = new ContentSourceCatalogService(
            new CatalogBrowseService(repository),
            new CatalogJsonTransferService(repository),
            settings,
            httpClient,
            [provider],
            legacyCheckpointStore,
            timeProvider);
        var afterRestart = await restartedService.RefreshDailyUpdatesAsync();

        Assert.Equal(1, first.CheckedProviderCount);
        Assert.Equal(1, first.UpdatedCount);
        Assert.False(sameHour.WasRefreshAttempted);
        Assert.Equal(1, sameHour.SkippedProviderCount);
        Assert.False(beforeNextHour.WasRefreshAttempted);
        Assert.Equal(1, beforeNextHour.SkippedProviderCount);
        Assert.Equal(1, nextHour.CheckedProviderCount);
        Assert.Equal(1, afterRestart.CheckedProviderCount);
        Assert.Equal(3, provider.DailyLoadCount);
        var card = Assert.Single(
            (await new CatalogBrowseService(repository)
                .GetOverviewAsync(MediaType.Book)).Items);
        var details = Assert.IsType<CatalogItemDetails>(
            await new CatalogBrowseService(repository)
                .GetDetailsAsync(card.Id));
        Assert.Equal(
            ["1", "2", "3", "4", "5"],
            details.Units.Select(static unit => unit.DisplayNumber));
        Assert.Equal(
            [0, 1, 2, 3, 4],
            details.Units.Select(static unit => unit.SortOrder));
        Assert.Equal("Local description", details.Item.Description);
        Assert.Equal(
            "https://daily.example/copper.webp",
            details.Item.ThumbnailRef);
        Assert.Equal(
            "Daily reader",
            Assert.Single(
                details.Item.Contributors,
                static contributor =>
                    contributor.IsDistributor).Name);
    }

    [Fact]
    public async Task FailedDailyProviderIsRetriedOnNextHomeEntry()
    {
        var provider = new DailyApiProvider
        {
            FailuresRemaining = 1
        };
        var repository = new InMemoryCatalogRepository(
            new CatalogTransferDocument());
        using var httpClient = new HttpClient(new EmptyHandler());
        var settings = new InMemoryContentSourceProviderSettingsStore(
            new ContentSourceProviderSettings
            {
                Providers =
                [
                    new ContentSourceProviderConfiguration
                    {
                        Id = provider.Descriptor.Id,
                        DisplayName = provider.Descriptor.DisplayName,
                        Kind = ContentSourceProviderKind.Api,
                        IsEnabled = true,
                        BaseUrl = "https://daily.example/"
                    }
                ]
            });
        var service = new ContentSourceCatalogService(
            new CatalogBrowseService(repository),
            new CatalogJsonTransferService(repository),
            settings,
            httpClient,
            [provider],
            new InMemoryContentSourceDailyUpdateStore());

        var failed = await service.RefreshDailyUpdatesAsync();
        var retried = await service.RefreshDailyUpdatesAsync();

        Assert.Single(failed.Failures);
        Assert.Empty(retried.Failures);
        Assert.Equal(1, retried.CreatedCount);
        Assert.Equal(2, provider.DailyLoadCount);
    }

    [Fact]
    public async Task DailyUpdatesFillOnlyAMissingThumbnailFromSearchPreview()
    {
        var provider = new DailyApiProvider
        {
            OmitDailyThumbnail = true
        };
        var repository = new InMemoryCatalogRepository(
            new CatalogTransferDocument());
        using var httpClient = new HttpClient(new EmptyHandler());
        var service = CreateService(
            repository,
            new InMemoryContentSourceProviderSettingsStore(
                new ContentSourceProviderSettings
                {
                    Providers = [CreateApiConfiguration(provider)]
                }),
            httpClient,
            provider);

        var result = await service.RefreshDailyUpdatesAsync();

        Assert.Equal(1, result.CreatedCount);
        Assert.Equal(1, provider.SearchCount);
        var details = Assert.IsType<CatalogItemDetails>(
            await new CatalogBrowseService(repository).GetDetailsAsync(
                Assert.Single(
                    (await new CatalogBrowseService(repository)
                        .GetOverviewAsync(MediaType.Book)).Items).Id));
        Assert.Equal(
            "https://daily.example/copper.webp",
            details.Item.ThumbnailRef);
    }

    [Fact]
    public async Task SearchScopedToAnimeLeavesLiveActionTitlesOut()
    {
        var repository = new InMemoryCatalogRepository(
            new CatalogTransferDocument
            {
                SourceLabel = "Content-source tests",
                Items =
                [
                    CreateVideo("Shadow Runner", "anime_series"),
                    CreateVideo("Shadow Detective", "series")
                ]
            });
        var service = CreateService(
            repository,
            new InMemoryContentSourceProviderSettingsStore(
                new ContentSourceProviderSettings()),
            new HttpClient());

        var unscoped = await service.SearchAsync("Shadow", MediaType.Movie);
        var animeOnly = await service.SearchAsync(
            "Shadow",
            MediaType.Movie,
            MediaSubtypeGroup.Anime);
        var liveActionOnly = await service.SearchAsync(
            "Shadow",
            MediaType.Movie,
            MediaSubtypeGroup.LiveAction);

        Assert.Equal(2, unscoped.Results.Count);
        Assert.Equal(
            "Shadow Runner",
            Assert.Single(animeOnly.Results).Card.Name);
        Assert.Equal(
            "Shadow Detective",
            Assert.Single(liveActionOnly.Results).Card.Name);
    }

    [Fact]
    public async Task SearchOnlyCallsProvidersForTheRequestedContentScope()
    {
        var mangaProvider = new TestApiProvider(
            id: "manga-reader",
            scope: ApiContentProviderScope.Manga);
        var animeProvider = new TestApiProvider(
            id: "anime-reader",
            scope: ApiContentProviderScope.Anime);
        using var httpClient = new HttpClient(new EmptyHandler());
        var service = CreateService(
            new InMemoryCatalogRepository(new CatalogTransferDocument()),
            new InMemoryContentSourceProviderSettingsStore(
                new ContentSourceProviderSettings
                {
                    Providers =
                    [
                        CreateApiConfiguration(mangaProvider),
                        CreateApiConfiguration(animeProvider)
                    ]
                }),
            httpClient,
            mangaProvider,
            animeProvider);

        await service.SearchAsync("Cloud", MediaType.Book);

        Assert.Equal(1, mangaProvider.SearchCount);
        Assert.Equal(0, animeProvider.SearchCount);

        await service.SearchAsync(
            "Cloud",
            MediaType.Movie,
            MediaSubtypeGroup.Anime);

        Assert.Equal(1, mangaProvider.SearchCount);
        Assert.Equal(1, animeProvider.SearchCount);
    }

    [Fact]
    public void ProviderAvailabilityRequiresEnabledMatchingCategory()
    {
        var mangaProvider = new TestApiProvider(
            id: "manga-reader",
            scope: ApiContentProviderScope.Manga);
        var animeProvider = new TestApiProvider(
            id: "anime-reader",
            scope: ApiContentProviderScope.Anime);
        using var httpClient = new HttpClient(new EmptyHandler());
        var service = CreateService(
            new InMemoryCatalogRepository(
                new CatalogTransferDocument()),
            new InMemoryContentSourceProviderSettingsStore(
                new ContentSourceProviderSettings()),
            httpClient,
            mangaProvider,
            animeProvider);
        var mangaConfiguration =
            CreateApiConfiguration(mangaProvider);
        var animeConfiguration =
            CreateApiConfiguration(animeProvider);
        var disabledMangaConfiguration =
            new ContentSourceProviderConfiguration
            {
                Id = mangaConfiguration.Id,
                DisplayName = mangaConfiguration.DisplayName,
                Kind = mangaConfiguration.Kind,
                IsEnabled = false,
                BaseUrl = mangaConfiguration.BaseUrl
            };

        Assert.True(service.IsProviderAvailableForSearch(
            mangaConfiguration,
            MediaType.Book,
            subtypeGroup: null));
        Assert.False(service.IsProviderAvailableForSearch(
            animeConfiguration,
            MediaType.Book,
            subtypeGroup: null));
        Assert.True(service.IsProviderAvailableForSearch(
            animeConfiguration,
            MediaType.Movie,
            MediaSubtypeGroup.Anime));
        Assert.False(service.IsProviderAvailableForSearch(
            mangaConfiguration,
            MediaType.Movie,
            MediaSubtypeGroup.LiveAction));
        Assert.False(service.IsProviderAvailableForSearch(
            disabledMangaConfiguration,
            mediaType: null,
            subtypeGroup: null));
        Assert.True(service.IsProviderAvailableForSearch(
            mangaConfiguration,
            mediaType: null,
            subtypeGroup: null));
        Assert.True(service.IsProviderAvailableForSearch(
            animeConfiguration,
            mediaType: null,
            subtypeGroup: null));
    }

    [Fact]
    public async Task AConfirmedChapterProviderTitleIsReusedWithoutSearchingAgain()
    {
        var provider = new DynamicChapterApiProvider(
            "dynamic-reader",
            "Dynamic reader");
        using var httpClient = new HttpClient(new EmptyHandler());
        var repository = CreateLocalMangaRepository();
        var bindings = new InMemoryApiTitleBindingStore();
        var service = CreateService(
            repository,
            new InMemoryContentSourceProviderSettingsStore(
                new ContentSourceProviderSettings
                {
                    Providers = [CreateApiConfiguration(provider)]
                }),
            httpClient,
            bindings,
            provider);
        var browse = new CatalogBrowseService(repository);
        var card = Assert.Single(
            (await browse.GetOverviewAsync(MediaType.Book)).Items,
            static item => item.Name == "Copper Skies");
        var details = Assert.IsType<CatalogItemDetails>(
            await browse.GetDetailsAsync(card.Id));
        var chapter = Assert.Single(
            details.Units,
            static unit => unit.DisplayNumber == "1");

        var candidates = await service.FindApiImageCandidatesForUnitAsync(
            details,
            chapter);
        await service.ResolveApiImagesForUnitAsync(
            details,
            chapter,
            candidates[0]);
        var searchesBefore = provider.Queries.Count;
        Assert.NotEmpty(provider.Queries);

        // The second chapter goes straight to the remembered title.
        var reused = await service.FindApiImageCandidatesForUnitAsync(
            details,
            chapter);

        Assert.Equal("Dynamic reader", Assert.Single(reused).ProviderName);
        Assert.Equal(searchesBefore, provider.Queries.Count);
        var binding = Assert.Single(await bindings.LoadAsync(card.Id)).Value;
        Assert.Equal("manga:coper-skies", binding.ExternalId);
        Assert.Equal(
            "chapter/1",
            Assert.Single(binding.UnitRoutes).Route);
        Assert.Equal(
            "chapter/1",
            Assert.Single(provider.LastLoadUnitRoutes).Route);
    }

    [Fact]
    public async Task ADeadRememberedChapterRouteIsSearchedAndResolvedAgain()
    {
        var provider = new DynamicChapterApiProvider(
            "dynamic-reader",
            "Dynamic reader");
        using var httpClient = new HttpClient(new EmptyHandler());
        var repository = CreateLocalMangaRepository();
        var bindings = new InMemoryApiTitleBindingStore();
        var service = CreateService(
            repository,
            new InMemoryContentSourceProviderSettingsStore(
                new ContentSourceProviderSettings
                {
                    Providers = [CreateApiConfiguration(provider)]
                }),
            httpClient,
            bindings,
            provider);
        var browse = new CatalogBrowseService(repository);
        var card = Assert.Single(
            (await browse.GetOverviewAsync(MediaType.Book)).Items,
            static item => item.Name == "Copper Skies");
        var details = Assert.IsType<CatalogItemDetails>(
            await browse.GetDetailsAsync(card.Id));
        var chapter = Assert.Single(
            details.Units,
            static unit => unit.DisplayNumber == "1");
        var initial = await service.FindApiImageCandidatesForUnitAsync(
            details,
            chapter);
        _ = await service.ResolveApiImagesForUnitAsync(
            details,
            chapter,
            initial[0]);
        var searchesBefore = provider.Queries.Count;
        provider.RejectRememberedUnitRoutes = true;

        var remembered = await service.FindApiImageCandidatesForUnitAsync(
            details,
            chapter);
        Assert.Equal(searchesBefore, provider.Queries.Count);
        var resolution = await service.ResolveApiImagesForUnitAsync(
            details,
            chapter,
            remembered[0]);

        Assert.Equal("Dynamic reader", resolution.ProviderName);
        Assert.True(provider.Queries.Count > searchesBefore);
        Assert.Equal(
            "chapter/1",
            Assert.Single(
                Assert.Single(await bindings.LoadAsync(card.Id))
                    .Value.UnitRoutes).Route);
    }

    [Fact]
    public async Task AStaleChapterBindingFallsBackToTheNormalSearch()
    {
        var provider = new DynamicChapterApiProvider(
            "dynamic-reader",
            "Dynamic reader");
        var backup = new DynamicChapterApiProvider(
            "backup-reader",
            "Backup reader");
        using var httpClient = new HttpClient(new EmptyHandler());
        var repository = CreateLocalMangaRepository();
        var browse = new CatalogBrowseService(repository);
        var card = Assert.Single(
            (await browse.GetOverviewAsync(MediaType.Book)).Items,
            static item => item.Name == "Copper Skies");
        var bindings = new InMemoryApiTitleBindingStore();
        // A pairing that no longer resolves, as if the provider dropped it.
        await bindings.SaveAsync(
            new ApiTitleBinding(
                card.Id,
                CreateApiConfiguration(provider).Id,
                "manga:gone",
                "Gone"));
        var service = CreateService(
            repository,
            new InMemoryContentSourceProviderSettingsStore(
                new ContentSourceProviderSettings
                {
                    Providers =
                    [
                        CreateApiConfiguration(provider),
                        CreateApiConfiguration(backup)
                    ]
                }),
            httpClient,
            bindings,
            provider,
            backup);
        var details = Assert.IsType<CatalogItemDetails>(
            await browse.GetDetailsAsync(card.Id));
        var chapter = Assert.Single(
            details.Units,
            static unit => unit.DisplayNumber == "1");

        var candidates = await service.FindApiImageCandidatesForUnitAsync(
            details,
            chapter);

        Assert.Equal(
            ["Backup reader", "Dynamic reader"],
            candidates.Select(static candidate => candidate.ProviderName));
        // It searched by name after the stale pairing failed, and forgot it.
        Assert.NotEmpty(provider.Queries);
        Assert.NotEmpty(backup.Queries);
        Assert.Empty(await bindings.LoadAsync(card.Id));
    }

    [Fact]
    public async Task ASoft404RememberedTitlePageFallsBackToSearch()
    {
        var provider = new DynamicChapterApiProvider(
            "dynamic-reader",
            "Dynamic reader")
        {
            ReturnSoft404ForRememberedTitle = true
        };
        using var httpClient = new HttpClient(new EmptyHandler());
        var repository = CreateLocalMangaRepository();
        var browse = new CatalogBrowseService(repository);
        var card = Assert.Single(
            (await browse.GetOverviewAsync(MediaType.Book)).Items,
            static item => item.Name == "Copper Skies");
        var bindings = new InMemoryApiTitleBindingStore();
        await bindings.SaveAsync(
            new ApiTitleBinding(
                card.Id,
                CreateApiConfiguration(provider).Id,
                "manga:coper-skies",
                "Coper Skies"));
        var service = CreateService(
            repository,
            new InMemoryContentSourceProviderSettingsStore(
                new ContentSourceProviderSettings
                {
                    Providers = [CreateApiConfiguration(provider)]
                }),
            httpClient,
            bindings,
            provider);
        var details = Assert.IsType<CatalogItemDetails>(
            await browse.GetDetailsAsync(card.Id));
        var chapter = Assert.Single(
            details.Units,
            static unit => unit.DisplayNumber == "1");

        var candidates = await service.FindApiImageCandidatesForUnitAsync(
            details,
            chapter);

        Assert.Equal("Dynamic reader", Assert.Single(candidates).ProviderName);
        Assert.NotEmpty(provider.Queries);
        Assert.Empty(await bindings.LoadAsync(card.Id));
    }

    private static CatalogTransferItem CreateVideo(
        string name,
        string subtype) =>
        new()
        {
            MediaType = "movie",
            Subtype = subtype,
            Name = name,
            Description = "A video title",
            PublicationStatus = "ongoing",
            Genres = ["Action"],
            Categories = ["Video"],
            Units =
            [
                new CatalogTransferUnit
                {
                    Kind = "episode",
                    DisplayNumber = "1",
                    SortOrder = 0,
                    Name = "Episode 1"
                }
            ]
        };

    /// <summary>
    /// A saved base URL outranks the built-in default, which is what lets a
    /// source be pointed somewhere else. Once the site abandons that domain the
    /// saved value stops being a preference, and leaving it in place kept every
    /// existing install fetching a dead address while only new ones moved.
    /// </summary>
    [Fact]
    public async Task ASavedUrlOnARetiredHostFallsBackToTheCurrentDefault()
    {
        var provider = new TestApiProvider(
            retiredHosts: ["old.example"]);
        using var httpClient = new HttpClient(new EmptyHandler());
        var service = CreateService(
            new InMemoryCatalogRepository(new CatalogTransferDocument()),
            new InMemoryContentSourceProviderSettingsStore(
                new ContentSourceProviderSettings
                {
                    Providers =
                    [
                        CreateApiConfiguration(provider) with
                        {
                            BaseUrl = "https://old.example/api/"
                        }
                    ]
                }),
            httpClient,
            provider);

        var settings = await service.LoadSettingsAsync();

        Assert.Equal(
            provider.Descriptor.DefaultBaseUrl,
            Assert.Single(
                settings.Providers,
                configuration => configuration.Id ==
                    provider.Descriptor.Id).BaseUrl);
    }

    /// <summary>
    /// Retiring a host must not reach any further than that host, or pointing a
    /// source somewhere deliberately would stop working.
    /// </summary>
    [Fact]
    public async Task ASavedUrlOnALiveHostIsStillHonoured()
    {
        var provider = new TestApiProvider(
            retiredHosts: ["old.example"]);
        using var httpClient = new HttpClient(new EmptyHandler());
        var service = CreateService(
            new InMemoryCatalogRepository(new CatalogTransferDocument()),
            new InMemoryContentSourceProviderSettingsStore(
                new ContentSourceProviderSettings
                {
                    Providers =
                    [
                        CreateApiConfiguration(provider) with
                        {
                            BaseUrl = "https://chosen.example/api/"
                        }
                    ]
                }),
            httpClient,
            provider);

        var settings = await service.LoadSettingsAsync();

        Assert.Equal(
            "https://chosen.example/api/",
            Assert.Single(
                settings.Providers,
                configuration => configuration.Id ==
                    provider.Descriptor.Id).BaseUrl);
    }

    /// <summary>
    /// Saving must not write the dead address back, or the move would be undone
    /// the next time the settings page is used.
    /// </summary>
    [Fact]
    public async Task SavingARetiredHostPersistsTheCurrentDefaultInstead()
    {
        var provider = new TestApiProvider(
            retiredHosts: ["old.example"]);
        using var httpClient = new HttpClient(new EmptyHandler());
        var store = new InMemoryContentSourceProviderSettingsStore(
            new ContentSourceProviderSettings());
        var service = CreateService(
            new InMemoryCatalogRepository(new CatalogTransferDocument()),
            store,
            httpClient,
            provider);

        await service.SaveSettingsAsync(new ContentSourceProviderSettings
        {
            Providers =
            [
                CreateApiConfiguration(provider) with
                {
                    BaseUrl = "https://old.example/api/"
                }
            ]
        });

        Assert.Equal(
            provider.Descriptor.DefaultBaseUrl,
            Assert.Single(
                (await store.LoadAsync()).Providers,
                configuration => configuration.Id ==
                    provider.Descriptor.Id).BaseUrl);
    }

    /// <summary>
    /// The live domain of the source that prompted this.
    /// </summary>
    [Fact]
    public void PelisPlusToPointsAtTheDomainThatAnswers()
    {
        var descriptor = BuiltInApiContentProviderDescriptors.PelisPlusTo;

        Assert.Equal("https://tioplus.app/", descriptor.DefaultBaseUrl);
        Assert.True(
            descriptor.IsRetiredBaseUrl("https://pelisplus.to/"));
        Assert.True(
            descriptor.IsRetiredBaseUrl("https://ww3.tioplus.net/"));
        Assert.False(
            descriptor.IsRetiredBaseUrl(descriptor.DefaultBaseUrl));
    }

    private static ContentSourceCatalogService CreateService(
        InMemoryCatalogRepository repository,
        IContentSourceProviderSettingsStore settingsStore,
        HttpClient httpClient,
        params IApiContentProvider[] providers) =>
        CreateService(
            repository,
            settingsStore,
            httpClient,
            titleBindingStore: null,
            providers);

    private static ContentSourceCatalogService CreateService(
        InMemoryCatalogRepository repository,
        IContentSourceProviderSettingsStore settingsStore,
        HttpClient httpClient,
        Func<bool> hideAdultContent,
        params IApiContentProvider[] providers)
    {
        var browse = new CatalogBrowseService(repository);
        var transfer = new CatalogJsonTransferService(repository);
        return new ContentSourceCatalogService(
            browse,
            transfer,
            settingsStore,
            httpClient,
            providers,
            hideAdultContent: hideAdultContent);
    }

    private static ContentSourceCatalogService CreateService(
        InMemoryCatalogRepository repository,
        IContentSourceProviderSettingsStore settingsStore,
        HttpClient httpClient,
        IApiTitleBindingStore? titleBindingStore,
        params IApiContentProvider[] providers)
    {
        var browse = new CatalogBrowseService(repository);
        var transfer = new CatalogJsonTransferService(repository);
        return new ContentSourceCatalogService(
            browse,
            transfer,
            settingsStore,
            httpClient,
            providers,
            dailyUpdateStore: null,
            timeProvider: null,
            titleBindingStore);
    }

    private static InMemoryCatalogRepository
        CreateLocalMangaRepository() =>
        new(new CatalogTransferDocument
        {
            SourceLabel = "Content-source tests",
            Items =
            [
                CreateManga(
                    "Copper Skies",
                    "Saved manga",
                    chapterCount: 2)
            ]
        });

    private static CatalogTransferItem CreateManga(
        string name,
        string description,
        int chapterCount) =>
        new()
        {
            MediaType = "book",
            Subtype = "manga",
            Name = name,
            Description = description,
            PublicationStatus = "ongoing",
            Genres = ["Fantasy"],
            Categories = ["Manga"],
            Units = Enumerable.Range(1, chapterCount)
                .Select(number => new CatalogTransferUnit
                {
                    Kind = "chapter",
                    DisplayNumber = number.ToString(),
                    SortOrder = number - 1,
                    Name = $"Chapter {number}"
                })
                .ToArray()
        };

    private static ContentSourceProviderConfiguration
        CreateApiConfiguration(IApiContentProvider provider) =>
        new()
        {
            Id = provider.Descriptor.Id,
            DisplayName = provider.Descriptor.DisplayName,
            Kind = ContentSourceProviderKind.Api,
            IsEnabled = true,
            BaseUrl =
                $"https://{provider.Descriptor.Id}.example/api"
        };

    private sealed class TestApiProvider : IApiContentProvider
    {
        private readonly CatalogTransferItem _item;

        public TestApiProvider(
            string name = "Cloud Reader",
            bool isEnabledByDefault = false,
            string id = "test-reader",
            ApiContentProviderScope scope = ApiContentProviderScope.All,
            IReadOnlyList<string>? retiredHosts = null)
        {
            _item = CreateManga(
                name,
                "Online manga",
                chapterCount: 1);
            Descriptor = new ApiContentProviderDescriptor(
                id,
                "Test reader",
                "https://reader.example/api/",
                isEnabledByDefault,
                scope,
                retiredHosts);
        }

        public ApiContentProviderDescriptor Descriptor { get; }

        public int LoadCount { get; private set; }

        public int SearchCount { get; private set; }

        public IReadOnlyList<Uri> GetTrustedArtworkOrigins(Uri baseUri) =>
            [baseUri, new Uri("https://covers.example/")];

        public ValueTask<IReadOnlyList<ApiContentSearchItem>> SearchAsync(
            Uri baseUri,
            string query,
            MediaType? mediaType = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SearchCount++;
            IReadOnlyList<ApiContentSearchItem> result =
            [
                new("manga:cloud-reader", _item)
            ];
            return ValueTask.FromResult(result);
        }

        public ValueTask<ApiContentItem> LoadItemAsync(
            Uri baseUri,
            ApiContentSearchItem searchItem,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LoadCount++;
            return ValueTask.FromResult(new ApiContentItem(
                _item,
                [
                    new ApiContentImageSet(
                        ContentUnitKind.Chapter,
                        "1",
                        seasonNumber: null,
                        [
                            new ApiContentImagePage(
                                0,
                                new Uri("https://cdn.example/chapter/1_0.webp")),
                            new ApiContentImagePage(
                                1,
                                new Uri("https://cdn.example/chapter/1_1.png"))
                        ])
                ]));
        }
    }

    private sealed class MovieArtworkProvider : IApiContentProvider
    {
        private readonly CatalogTransferItem _preview = new()
        {
            MediaType = "movie",
            Subtype = "anime_movie",
            Name = "El viaje de Chihiro",
            ThumbnailRef = "https://images.example/spirited-away-new.jpg",
            AlternativeNames =
            [
                new CatalogTransferAlias
                {
                    Name = "Spirited Away",
                    AliasType = "localized"
                }
            ],
            Units =
            [
                new CatalogTransferUnit
                {
                    Kind = "episode",
                    DisplayNumber = "1",
                    SortOrder = 0
                }
            ]
        };

        public ApiContentProviderDescriptor Descriptor { get; } = new(
            "movie-artwork-test",
            "Movie artwork test");

        public ValueTask<IReadOnlyList<ApiContentSearchItem>> SearchAsync(
            Uri baseUri,
            string query,
            MediaType? mediaType = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<ApiContentSearchItem> results =
            [
                new("movie:spirited-away", _preview)
            ];
            return ValueTask.FromResult(results);
        }

        public ValueTask<ApiContentItem> LoadItemAsync(
            Uri baseUri,
            ApiContentSearchItem searchItem,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new ApiContentItem(
                _preview with { ThumbnailRef = null }));
        }
    }
    private sealed class ControlledApiProvider : IApiContentProvider
    {
        private readonly Action? _onStarted;
        private readonly Task? _release;
        private readonly CatalogTransferItem _item;
        private readonly CatalogTransferItem _loadedItem;

        public ControlledApiProvider(
            string id,
            string displayName,
            Task? release,
            Action? onStarted = null,
            bool adultContent = false,
            bool adultGenre = false,
            bool adultOnLoad = false)
        {
            Descriptor = new ApiContentProviderDescriptor(
                id,
                displayName,
                $"https://{id}.example/api");
            _release = release;
            _onStarted = onStarted;
            _item = CreateManga(
                $"Copper {displayName}",
                "Streaming search result",
                chapterCount: 1) with
            {
                Categories = adultContent ? ["adult"] : ["Manga"],
                Genres = adultGenre ? ["adult"] : ["Fantasy"]
            };
            _loadedItem = adultOnLoad
                ? _item with { Genres = ["adult"] }
                : _item;
        }

        public ApiContentProviderDescriptor Descriptor { get; }

        public async ValueTask<IReadOnlyList<ApiContentSearchItem>> SearchAsync(
            Uri baseUri,
            string query,
            MediaType? mediaType = null,
            CancellationToken cancellationToken = default)
        {
            _onStarted?.Invoke();
            if (_release is not null)
            {
                await _release.WaitAsync(cancellationToken);
            }

            return [new ApiContentSearchItem(Descriptor.Id, _item)];
        }

        public ValueTask<ApiContentItem> LoadItemAsync(
            Uri baseUri,
            ApiContentSearchItem searchItem,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ApiContentItem(_loadedItem, []));
    }

    private sealed class DynamicChapterApiProvider : IApiContentProvider
    {
        private readonly CatalogTransferItem _item =
            CreateManga("Coper Skies", "Dynamic provider result", chapterCount: 1)
                with
            {
                Units =
                    [
                        new CatalogTransferUnit
                        {
                            Kind = "chapter",
                            DisplayNumber = "01",
                            SortOrder = 0,
                            Name = "Provider Horizon",
                            PageCount = 30
                        },
                        new CatalogTransferUnit
                        {
                            Kind = "chapter",
                            DisplayNumber = "3",
                            SortOrder = 2,
                            Name = "The Third Sky",
                            PageCount = 32
                        }
                    ]
            };

        public DynamicChapterApiProvider(
            string id = "dynamic-reader",
            string displayName = "Dynamic manga reader")
        {
            Descriptor = new ApiContentProviderDescriptor(
                id,
                displayName);
        }

        public ApiContentProviderDescriptor Descriptor { get; }

        public List<string> Queries { get; } = [];

        public int LoadItemCount { get; private set; }

        public int UnitImageLoadCount { get; private set; }

        public bool RejectRememberedUnitRoutes { get; set; }

        public bool ReturnSoft404ForRememberedTitle { get; set; }

        public IReadOnlyList<ApiProviderUnitRoute> LastLoadUnitRoutes
        {
            get;
            private set;
        } = [];

        public ValueTask<IReadOnlyList<ApiContentSearchItem>> SearchAsync(
            Uri baseUri,
            string query,
            MediaType? mediaType = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Queries.Add(query);
            IReadOnlyList<ApiContentSearchItem> result =
            [
                new("manga:coper-skies", _item)
            ];
            return ValueTask.FromResult(result);
        }

        public ValueTask<ApiContentItem> LoadItemAsync(
            Uri baseUri,
            ApiContentSearchItem searchItem,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // A real provider only knows its own identifiers; anything else is
            // a dead link, which is what a stale binding looks like.
            if (!string.Equals(
                    searchItem.ExternalId,
                    "manga:coper-skies",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The requested title does not exist on this provider.");
            }

            LoadItemCount++;
            LastLoadUnitRoutes = searchItem.UnitRoutes;
            var item = ReturnSoft404ForRememberedTitle &&
                       searchItem.RequireProviderTitle
                ? _item with { Name = "404 Not Found" }
                : _item;
            return ValueTask.FromResult(new ApiContentItem(item));
        }

        public IReadOnlyList<ApiProviderUnitRoute> GetKnownUnitRoutes(
            Uri baseUri,
            ApiContentSearchItem searchItem,
            ApiContentUnitRequest unit) =>
            unit.Kind is ContentUnitKind.Chapter
                ?
                [
                    new ApiProviderUnitRoute(
                        ContentUnitKind.Chapter,
                        unit.DisplayNumber,
                        unit.SeasonNumber,
                        $"chapter/{unit.DisplayNumber}")
                ]
                : [];

        public ValueTask<ApiContentImageSet?> LoadUnitImagesAsync(
            Uri baseUri,
            ApiContentSearchItem searchItem,
            ApiContentUnitRequest unit,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UnitImageLoadCount++;
            if (RejectRememberedUnitRoutes &&
                searchItem.UnitRoutes.Count > 0)
            {
                throw new HttpRequestException(
                    "The remembered chapter page returned 404.");
            }

            ApiContentImageSet imageSet = new(
                ContentUnitKind.Chapter,
                "01",
                seasonNumber: null,
                [
                    new ApiContentImagePage(
                        0,
                        new Uri("https://images.example/copper_0.jpg")),
                    new ApiContentImagePage(
                        1,
                        new Uri("https://images.example/copper_1.webp"))
                ]);
            return ValueTask.FromResult<ApiContentImageSet?>(
                unit.Matches(imageSet) ? imageSet : null);
        }
    }

    private sealed class DailyApiProvider : IApiContentProvider
    {
        public ApiContentProviderDescriptor Descriptor { get; } =
            new(
                "daily-reader",
                "Daily reader",
                "https://daily.example/",
                isEnabledByDefault: true);

        public bool SupportsDailyUpdates => true;

        public int DailyLoadCount { get; private set; }

        public int SearchCount { get; private set; }

        public int FailuresRemaining { get; set; }

        public bool OmitDailyThumbnail { get; set; }

        public ValueTask<IReadOnlyList<ApiContentSearchItem>>
            LoadDailyUpdatesAsync(
                Uri baseUri,
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DailyLoadCount++;
            if (FailuresRemaining > 0)
            {
                FailuresRemaining--;
                throw new HttpRequestException(
                    "Daily reader is temporarily unavailable.");
            }

            var chapter = DailyLoadCount + 2;
            IReadOnlyList<ApiContentSearchItem> updates =
            [
                new(
                    "copper-skies",
                    new CatalogTransferItem
                    {
                        MediaType = "book",
                        Subtype = "manga",
                        Name = "Copper Skies",
                        ThumbnailRef = OmitDailyThumbnail
                            ? null
                            : "https://daily.example/copper.webp",
                        Categories = ["Manga"],
                        Units =
                        [
                            new CatalogTransferUnit
                            {
                                Kind = "chapter",
                                DisplayNumber =
                                    chapter.ToString(),
                                SortOrder = 0
                            }
                        ]
                    })
            ];
            return ValueTask.FromResult(updates);
        }

        public ValueTask<IReadOnlyList<ApiContentSearchItem>> SearchAsync(
            Uri baseUri,
            string query,
            MediaType? mediaType = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SearchCount++;
            IReadOnlyList<ApiContentSearchItem> results =
            [
                new(
                    "copper-skies",
                    new CatalogTransferItem
                    {
                        MediaType = "book",
                        Subtype = "manga",
                        Name = "Copper Skies",
                        ThumbnailRef =
                            "https://daily.example/copper.webp"
                    })
            ];
            return ValueTask.FromResult(results);
        }

        public ValueTask<ApiContentItem> LoadItemAsync(
            Uri baseUri,
            ApiContentSearchItem searchItem,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "Daily preview imports do not load full items.");
    }

    private sealed class AdjustableTimeProvider(
        DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public override TimeZoneInfo LocalTimeZone =>
            TimeZoneInfo.Utc;

        public void Advance(TimeSpan duration) =>
            _utcNow += duration;
    }

    private sealed class CatalogJsonHandler : HttpMessageHandler
    {
        private readonly IReadOnlyList<string> _documents;

        public CatalogJsonHandler(params CatalogTransferDocument[] documents)
        {
            ArgumentOutOfRangeException.ThrowIfZero(documents.Length);
            _documents = documents
                .Select(document => JsonSerializer.Serialize(
                    document,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)))
                .ToArray();
        }

        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestCount++;
            var json = _documents[Math.Min(
                RequestCount - 1,
                _documents.Count - 1)];
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(
                    json,
                    Encoding.UTF8,
                    "application/json")
            });
        }
    }

    private sealed class EmptyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "This test does not expect an HTTP request.");
    }
}
