using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UniversalMediaDistribution.UI.Views;

namespace UniversalMediaDistribution.UI.Tests;

[Collection("Player surface transitions")]
public sealed class TelevisionEpisodeNavigationTests
{
    [Fact]
    public async Task WrappedActionsRemainReachableAndNavigationEscapesTheEpisodeList()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(PlayerFullscreenLayoutTests.TestApp));
        await session.Dispatch(() =>
        {
            var view = new MainView();
            var scroll = view.FindControl<ScrollViewer>("DetailsScroll")!;
            foreach (var child in view.FindControl<Grid>("MainContent")!.Children)
                child.IsVisible = ReferenceEquals(child, scroll);
            var list = view.FindControl<ListBox>("DetailsEntriesList")!;
            ((Panel)list.Parent!).Children.Remove(list);
            var header = new Button { Content = "Back to catalog", Height = 48 };
            scroll.Content = new StackPanel { Children = { header, list } };
            list.ItemTemplate = new FuncDataTemplate<int>((value, _) => new StackPanel
            {
                Height = 180,
                Children =
                {
                    new TextBlock { Text = $"Episode {value}", Height = 40 },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        Children =
                        {
                            new Button { Content = "Search", Height = 48 },
                            new Button { Content = "Seen", Height = 48 }
                        }
                    },
                    new Button { Content = "Delete", Height = 48 }
                }
            });
            list.ItemsSource = Enumerable.Range(0, 1000).ToArray();
            typeof(MainView).GetField("_isTelevision", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(view, true);
            var window = new Window { Content = view, Width = 1280, Height = 720 };
            window.Show();
            try
            {
                window.UpdateLayout();
                Button Action(int index, string label) => list.ContainerFromIndex(index)!
                    .GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, label));
                void Press(Key key)
                {
                    view.HandleTelevisionKeyDown(new KeyEventArgs { Key = key });
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                    Dispatcher.UIThread.RunJobs();
                }

                Assert.True(Action(0, "Search").Focus());
                Press(Key.Down);
                Assert.Same(Action(0, "Delete"), window.FocusManager!.GetFocusedElement());
                Press(Key.Down);
                Assert.Same(Action(1, "Search"), window.FocusManager.GetFocusedElement());
                // Cross the initially realized window repeatedly without losing focus.
                for (var index = 1; index < 20; index++)
                {
                    Press(Key.Down);
                    Assert.Same(Action(index, "Delete"), window.FocusManager.GetFocusedElement());
                    Press(Key.Down);
                    Assert.Same(Action(index + 1, "Search"), window.FocusManager.GetFocusedElement());
                }
                Assert.InRange(list.GetRealizedContainers().Count(), 1, 40);
                list.ScrollIntoView(0);
                window.UpdateLayout();
                Action(0, "Search").Focus();
                Press(Key.Up);
                Assert.Same(header, window.FocusManager.GetFocusedElement());

                list.ScrollIntoView(999);
                window.UpdateLayout();
                Action(999, "Delete").Focus();
                Press(Key.Down);
                Assert.Same(Action(999, "Delete"), window.FocusManager.GetFocusedElement());
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }
}
