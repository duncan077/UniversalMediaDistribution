using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UniversalMediaDistribution.Application.Catalog;
using UniversalMediaDistribution.Domain.Catalog;
using UniversalMediaDistribution.Torrenting.Ingestion;
using UniversalMediaDistribution.UI.ViewModels;
using UniversalMediaDistribution.UI.Views;

namespace UniversalMediaDistribution.UI.Tests;

[Collection("Player surface transitions")]
public sealed class ProviderPickerLayoutTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MouseClicksKeepChapterPositionAndOnlyTelevisionHighlightsTheWholeRow(bool television)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(PlayerFullscreenLayoutTests.TestApp));
        await session.Dispatch<bool>(async () =>
        {
            var view = new MainView();
            var shell = view.FindControl<Grid>("Shell")!;
            var main = view.FindControl<Grid>("MainContent")!;
            var details = view.FindControl<ScrollViewer>("DetailsScroll")!;
            var list = view.FindControl<ListBox>("DetailsEntriesList")!;
            foreach (var child in shell.Children) child.IsVisible = ReferenceEquals(child, main);
            foreach (var child in main.Children) child.IsVisible = ReferenceEquals(child, details);
            foreach (var child in ((Panel)details.Content!).Children) child.IsVisible = ReferenceEquals(child, list);
            if (television) shell.Classes.Add("tv");
            var entries = Enumerable.Range(1, 30).Select(CreateEntry).ToArray();
            entries[0].ShowProviderChoices("Choose a provider.",
                [new("Provider one", .95, static () => Task.CompletedTask)], static () => { });
            list.ItemsSource = entries;
            var window = new Window { Content = view, Width = 1280, Height = 720 };
            window.Show();
            try
            {
                async Task Frame()
                {
                    await Task.Delay(25);
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                }
                await Frame();
                await Frame();
                var row = (ListBoxItem)list.ContainerFromIndex(0)!;
                var card = row.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("entry-card"));
                var button = row.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && b.IsEnabled);
                var presenter = row.GetVisualDescendants().OfType<ContentPresenter>()
                    .First(p => ReferenceEquals(p.TemplatedParent, row));
                var scroll = list.GetVisualDescendants().OfType<ScrollViewer>().First();
                Assert.Equal(television, list.Focusable);
                var normalBorder = button.BorderThickness;
                var buttonPoint = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
                window.MouseMove(buttonPoint);
                window.MouseDown(buttonPoint, MouseButton.Left);
                window.MouseUp(buttonPoint, MouseButton.Left);
                await Frame();
                Assert.True(button.IsFocused);
                if (!television) Assert.Equal(normalBorder, button.BorderThickness);

                var innerOffset = scroll.Offset;
                var outerOffset = details.Offset;
                var blankPoint = card.TranslatePoint(new Point(card.Bounds.Width - 30, 30), window)!.Value;
                window.MouseMove(blankPoint);
                window.MouseDown(blankPoint, MouseButton.Left);
                window.MouseUp(blankPoint, MouseButton.Left);
                await Frame();
                Assert.True(row.IsSelected); // Exercise the framework's actual selection path.
                var background = Assert.IsAssignableFrom<ISolidColorBrush>(presenter.Background);
                Assert.Equal(!television, background.Color.A == 0);
                if (!television)
                {
                    Assert.True(button.IsFocused);
                    Assert.Equal(innerOffset, scroll.Offset);
                    Assert.Equal(outerOffset, details.Offset);
                }
                // Keyboard/remote users still get a visible focus target.
                if (television) Assert.True(button.Focus(NavigationMethod.Directional));
                else window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
                await Frame();
                var focusedButton = Assert.IsAssignableFrom<Button>(window.FocusManager!.GetFocusedElement());
                Assert.True(focusedButton.BorderThickness == new Thickness(television ? 3 : 2),
                    $"Focused {focusedButton.Content}: {string.Join(",", focusedButton.Classes)}, border={focusedButton.BorderThickness}");
            }
            finally { window.Close(); }
            return true;
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(1280, false)]
    [InlineData(380, false)]
    [InlineData(1280, true)]
    public async Task ExpandedProviderPickerStaysInsideItsChapterAndPushesFollowingRowsDown(double width, bool television)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(PlayerFullscreenLayoutTests.TestApp));
        await session.Dispatch(() =>
        {
            var view = new MainView();
            foreach (var child in view.FindControl<Grid>("Shell")!.Children)
                child.IsVisible = ReferenceEquals(child, view.FindControl<Grid>("MainContent"));
            var details = view.FindControl<ScrollViewer>("DetailsScroll")!;
            var list = view.FindControl<ListBox>("DetailsEntriesList")!;
            foreach (var child in view.FindControl<Grid>("MainContent")!.Children)
                child.IsVisible = ReferenceEquals(child, details);
            foreach (var child in ((Panel)details.Content!).Children)
                child.IsVisible = ReferenceEquals(child, list);
            var entries = Enumerable.Range(1, 30).Select(CreateEntry).ToArray();
            list.ItemsSource = entries;
            var window = new Window { Content = view, Width = width, Height = 720 };
            window.Show();
            if (television) view.FindControl<Grid>("Shell")!.Classes.Add("tv");
            try
            {
                var entry = entries[10];
                void RevealEntry()
                {
                    window.UpdateLayout();
                    list.ScrollIntoView(entry);
                    window.UpdateLayout();
                }
                RevealEntry();
                entry.BeginProviderSearch("Searching enabled providers…", static () => { });
                RevealEntry();
                AssertPickerFits(list, entry);
                entry.ShowProviderChoices("2 enabled providers found this entry. Choose the one to use.",
                    [new("Provider one", .95, static () => Task.CompletedTask),
                     new("Provider two", .9, static () => Task.CompletedTask)], static () => { });
                entry.EndProviderSearch();
                Dispatcher.UIThread.RunJobs();
                RevealEntry();
                AssertPickerFits(list, entry);
                list.ScrollIntoView(entries[^1]);
                window.UpdateLayout();
                RevealEntry();
                AssertPickerFits(list, entry);
                entry.ClearProviderChoices();
                window.UpdateLayout();
                entry.ShowProviderChoices("Choose a provider.",
                    [new("Provider three", .8, static () => Task.CompletedTask)], static () => { });
                RevealEntry();
                AssertPickerFits(list, entry);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    private static void AssertPickerFits(ListBox list, CatalogContentEntryViewModel entry)
    {
        var row = (Control)list.ContainerFromItem(entry)!;
        Assert.NotNull(row);
        var borders = row.GetVisualDescendants().OfType<Border>().ToArray();
        var card = borders.Single(b => b.Classes.Contains("entry-card"));
        var picker = borders.Single(b => b.Classes.Contains("provider-choice-picker"));
        Assert.True(picker.IsEffectivelyVisible);
        var top = picker.TranslatePoint(default, card)!.Value.Y;
        Assert.True(top >= 0);
        Assert.True(top + picker.Bounds.Height <= card.Bounds.Height,
            $"Picker bottom {top + picker.Bounds.Height} exceeds chapter card height {card.Bounds.Height}.");
        foreach (var button in picker.GetVisualDescendants().OfType<Button>())
        {
            var position = button.TranslatePoint(default, card)!.Value;
            Assert.InRange(position.X, 0, card.Bounds.Width);
            Assert.InRange(position.X + button.Bounds.Width, 0, card.Bounds.Width);
        }
        foreach (var other in list.GetRealizedContainers().OfType<Control>())
        {
            if (list.IndexFromContainer(other) <= list.IndexFromContainer(row)) continue;
            Assert.True(other.Bounds.Top >= row.Bounds.Bottom,
                $"Next chapter begins at {other.Bounds.Top} before expanded chapter ends at {row.Bounds.Bottom}.");
        }
    }

    private static CatalogContentEntryViewModel CreateEntry(int number)
    {
        var id = Guid.NewGuid();
        return new CatalogContentEntryViewModel($"Chapter {number}", null, null,
            new CatalogContentIdentity(id, $"chapter:{number}", ContentUnitKind.Chapter, number.ToString()),
            new CatalogContentIdentity(id, "item"), MediaType.Book, [],
            new TorrentMediaSessionService(engine: null), static _ => Task.CompletedTask,
            findApiImagesAsync: static () => Task.CompletedTask);
    }
}
