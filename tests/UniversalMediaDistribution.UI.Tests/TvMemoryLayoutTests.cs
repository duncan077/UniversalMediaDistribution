using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using UniversalMediaDistribution.Torrenting.Ingestion;
using UniversalMediaDistribution.UI.ViewModels;
using UniversalMediaDistribution.UI.Views;

namespace UniversalMediaDistribution.UI.Tests;

[Collection("Player surface transitions")]
public sealed class TvMemoryLayoutTests
{
    [Fact]
    public async Task DetailsListKeepsLongSeriesVirtualized()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(PlayerFullscreenLayoutTests.TestApp));
        await session.Dispatch(() =>
        {
            var view = new MainView();
            var scroll = view.FindControl<ScrollViewer>("DetailsScroll")!;
            foreach (var child in view.FindControl<Grid>("MainContent")!.Children)
                child.IsVisible = ReferenceEquals(child, scroll);
            var list = view.FindControl<ListBox>("DetailsEntriesList")!;
            list.ItemTemplate = new FuncDataTemplate<int>((value, _) => new TextBlock { Text = value.ToString(), Height = 64 });
            list.ItemsSource = Enumerable.Range(0, 1000).ToArray();
            var window = new Window { Content = view, Width = 1280, Height = 720 };
            window.Show();
            try
            {
                window.UpdateLayout();
                scroll.Offset = new Vector(0, scroll.Extent.Height);
                window.UpdateLayout();
                Assert.InRange(list.Bounds.Height, 1, scroll.Bounds.Height);
                Assert.InRange(list.GetRealizedContainers().Count(), 1, 40);
                list.ScrollIntoView(999);
                window.UpdateLayout();
                Assert.NotNull(list.ContainerFromIndex(999));
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ArtworkOnlyLoadsInVisibleViewportAndReleasesWhenHidden()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(PlayerFullscreenLayoutTests.TestApp));
        await session.Dispatch(() =>
        {
            using var first = new CatalogArtworkViewModel("first", null, new TorrentMediaSessionService(null));
            using var last = new CatalogArtworkViewModel("last", null, new TorrentMediaSessionService(null));
            var panel = new StackPanel
            {
                Children =
                {
                    new CatalogArtworkView { Height = 100, DataContext = first },
                    new Border { Height = 2000 },
                    new CatalogArtworkView { Height = 100, DataContext = last }
                }
            };
            var scroll = new ScrollViewer { Content = panel };
            var window = new Window { Content = scroll, Width = 400, Height = 300 };
            window.Show();
            try
            {
                window.UpdateLayout();
                // Unsupported references fail synchronously: HasError proves
                // whether a load was attempted, without network or decoders.
                Assert.True(first.HasError);
                Assert.False(last.HasError);
                scroll.Offset = new Vector(0, 2200);
                window.UpdateLayout();
                Assert.False(first.HasError);
                Assert.True(last.HasError);
                panel.IsVisible = false;
                window.UpdateLayout();
                Assert.False(last.HasError);
                panel.IsVisible = true;
                window.UpdateLayout();
                Assert.True(last.HasError);
                ImageMemoryPolicy.SetArtworkActive(false);
                Assert.False(last.HasError);
                Assert.Equal(0, DecodedArtworkCache.ByteCount);
                ImageMemoryPolicy.SetArtworkActive(true);
                Assert.True(last.HasError);
            }
            finally
            {
                window.Close();
                ImageMemoryPolicy.SetArtworkActive(true);
            }
        }, CancellationToken.None);
    }
}
