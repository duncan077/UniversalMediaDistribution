using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using UniversalMediaDistribution.Application.Catalog;
using UniversalMediaDistribution.Application.Metadata;
using UniversalMediaDistribution.Application.UserState;
using UniversalMediaDistribution.Catalog;
using UniversalMediaDistribution.UI.Composition;
using UniversalMediaDistribution.UI.Playback;
using UniversalMediaDistribution.UI.Remote;
using UniversalMediaDistribution.UI.ViewModels;
using UniversalMediaDistribution.UI.Views;

namespace UniversalMediaDistribution.UI.Tests;

[Collection("Player surface transitions")]
public sealed class CatalogToolPanelNavigationTests
{
    [Theory]
    [InlineData(1280, false)]
    [InlineData(640, false)]
    [InlineData(640, true)]
    public async Task ReturningToCatalogRestoresToolsWithoutAnotherNavigationOrResize(double width, bool expanded)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(PlayerFullscreenLayoutTests.TestApp));
        await session.Dispatch(() =>
        {
            var repository = new InMemoryCatalogRepository();
            var settings = new InMemoryMetadataProviderSettingsStore();
            var model = new MainViewModel(
                new CatalogBrowseService(repository), null!, null,
                new CatalogMetadataRefreshService(repository, settings, []), settings,
                null!, new InMemoryUserMediaStateStore(), null!, null!,
                new UnavailablePlatformMediaPlayer(), [], RuntimeProfile.Desktop,
                null!, null!, null!, null!, null!, null!, null, new HttpRemoteMediaServerClient());
            // Measure the real view without attaching it to a window: this
            // exercises responsive layout/bindings without InitializeAsync,
            // background services, network, or native playback.
            var view = new MainView { DataContext = model };
            view.Measure(new Size(width, 800));
            view.Arrange(new Rect(0, 0, width, 800));
            var search = view.FindControl<Border>("CatalogSearchPanel")!;
            var filters = view.FindControl<Border>("CatalogFilterPanel")!;
            var toggle = view.FindControl<Button>("CatalogToolsToggleButton")!;
            if (expanded) toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var narrow = width == 640;
            Assert.Equal(!narrow || expanded, filters.IsVisible);

            Action<bool>[] destinations =
            [
                value => model.IsSettingsOpen = value,
                value => model.IsDownloadsOpen = value,
                value => model.IsFriendsOpen = value,
                value => model.IsUserListsOpen = value,
                value => model.IsEmbeddedMediaOpen = value,
                value => model.IsLiveTvOpen = value
            ];
            // Start away from the catalog, as when navigating while a layout
            // pass has hidden the tools. Going back must re-evaluate visibility.
            model.IsSettingsOpen = true;
            view.DataContext = null;
            view.DataContext = model;
            model.IsSettingsOpen = false;
            Assert.Equal(!narrow || expanded, filters.IsVisible);
            Assert.Equal(narrow, toggle.IsVisible);

            for (var cycle = 0; cycle < 3; cycle++)
            {
                foreach (var navigate in destinations)
                {
                    navigate(true);
                    Assert.False(model.ShowCatalogOverviewTools);
                    Assert.False(search.IsVisible);
                    Assert.False(filters.IsVisible);
                    Assert.False(toggle.IsVisible);
                    navigate(false);
                    Assert.True(model.ShowCatalogOverviewTools);
                    Assert.Equal(!narrow || expanded, search.IsVisible);
                    Assert.Equal(!narrow || expanded, filters.IsVisible);
                    Assert.Equal(narrow, toggle.IsVisible);
                }
            }
            view.DataContext = null;
        }, CancellationToken.None);
    }
}
