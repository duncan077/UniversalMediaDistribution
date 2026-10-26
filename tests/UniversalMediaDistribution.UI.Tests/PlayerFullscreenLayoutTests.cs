using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using UniversalMediaDistribution.UI.Views;

namespace UniversalMediaDistribution.UI.Tests;

[Collection("Player surface transitions")]
public sealed class PlayerFullscreenLayoutTests
{
    [Theory]
    [InlineData(WindowState.Normal, 1280)]
    [InlineData(WindowState.Maximized, 1280)]
    [InlineData(WindowState.Normal, 900)]
    [InlineData(WindowState.Normal, 640)]
    public async Task FullscreenPreservesPlayerRowsAndRestoresWindow(WindowState initialState, double width)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(TestApp));
        await session.Dispatch(() =>
        {
            // Load the real XAML without a view model: no providers, database,
            // network requests or native media engine are needed for layout.
            var view = new MainView();
            var player = view.FindControl<Grid>("EmbeddedPlayerSurface")!;
            var video = view.FindControl<ContentControl>("EmbeddedPlayerContent")!;
            var mediaSurface = new Border();
            video.Content = mediaSurface;
            var content = view.FindControl<Grid>("MainContent")!;
            foreach (var child in content.Children) child.IsVisible = ReferenceEquals(child, player);
            var rows = player.RowDefinitions;
            Assert.Equal(5, rows.Count);
            var window = new Window { Content = view, Width = width, Height = 800, WindowState = initialState };
            window.Show();
            try
            {
                window.UpdateLayout();
                for (var cycle = 0; cycle < 10; cycle++)
                {
                    view.ApplyPlayerFullscreenPresentation(true);
                    view.ApplyPlayerFullscreenPresentation(true); // Duplicate enter must not overwrite the saved window state.
                    window.UpdateLayout(); // Originally throws in Grid.MeasureCellsGroup.
                    Assert.Equal(WindowState.FullScreen, window.WindowState);
                    Assert.Same(rows, player.RowDefinitions);
                    Assert.All(player.Children, child => Assert.InRange(Grid.GetRow(child), 0, rows.Count - 1));
                    Assert.Equal(GridLength.Star, rows[1].Height);
                    Assert.Same(mediaSurface, video.Content);
                    Assert.True(video.Bounds.Height > 0);
                    Assert.Equal(0, video.Bounds.Y);
                    Assert.Equal(player.Bounds.Height, video.Bounds.Height);
                    for (var row = 0; row < rows.Count; row++)
                        if (row != 1) Assert.Equal(new GridLength(0), rows[row].Height);

                    view.ApplyPlayerFullscreenPresentation(false);
                    window.UpdateLayout();
                    Assert.Equal(initialState, window.WindowState);
                    Assert.Same(rows, player.RowDefinitions);
                    for (var row = 0; row < rows.Count; row++)
                        Assert.Equal(row == 1 ? GridLength.Star : GridLength.Auto, rows[row].Height);

                    // Also cross the responsive breakpoints between transitions.
                    window.Width = cycle % 2 == 0 ? 640 : 1280;
                    window.UpdateLayout();
                }
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    public sealed class TestApp : App
    {
        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<TestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());

        public override void OnFrameworkInitializationCompleted() { }
    }
}
