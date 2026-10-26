using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UniversalMediaDistribution.UI.Composition;
using UniversalMediaDistribution.UI.Remote;
using UniversalMediaDistribution.UI.Views;

namespace UniversalMediaDistribution.UI.Tests;

[Collection("Player surface transitions")]
public sealed class ReaderDockInteractionTests
{
    [Theory]
    [InlineData(1280, false, false)]
    [InlineData(320, true, false)]
    [InlineData(960, false, true)]
    public async Task DockFitsAndItsActionsWorkAcrossDesktopPhoneAndTelevision(int width, bool touch, bool tv)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(PlayerFullscreenLayoutTests.TestApp));
        await session.Dispatch(() =>
        {
            using var reader = ReaderAutoScrollTests.CreateReaderAsync().GetAwaiter().GetResult();
            reader.UseContinuousModeCommand.Execute(null);
            using var remote = new HttpRemoteMediaServerClient();
            var model = ReaderScrollInteractionTests.CreateModel(remote,
                tv ? RuntimeProfile.AndroidTv : touch ? RuntimeProfile.Android : RuntimeProfile.Desktop);
            model.Reader = reader;
            model.DismissDonationPromptCommand.Execute(null);
            var view = new MainView { DataContext = model };
            var window = new Window { Content = view, Width = width, Height = 720 };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                var dock = view.FindControl<Border>("ReaderChapterNavigationBanner")!;
                var play = view.FindControl<Button>("ReaderAutoScrollButton")!;
                var settings = view.FindControl<Button>("ReaderDockSettingsButton")!;
                var chapter = view.FindControl<Button>("ReaderChapterListButton")!;
                var fullscreen = view.FindControl<Button>("ReaderFullscreenButton")!;
                Assert.True(dock.IsEffectivelyVisible);
                Assert.Equal(!touch && !tv, fullscreen.IsEffectivelyVisible);
                foreach (var button in dock.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible))
                {
                    var point = button.TranslatePoint(default, window)!.Value;
                    Assert.InRange(point.X, 0, width);
                    Assert.InRange(point.X + button.Bounds.Width, 0, width);
                    Assert.InRange(button.Bounds.Height, tv ? 60 : 44, 64);
                }
                Assert.False(view.FindControl<Button>("ReaderPreviousChapterButton")!.IsEnabled);
                Assert.False(view.FindControl<Button>("ReaderNextChapterButton")!.IsEnabled);
                if (tv)
                {
                    var pages = view.FindControl<ListBox>("ReaderPages")!;
                    var scroll = pages.GetVisualDescendants().OfType<ScrollViewer>().First();
                    scroll.Offset = scroll.Offset.WithY(scroll.Extent.Height - scroll.Viewport.Height);
                    pages.Focus(NavigationMethod.Directional);
                    view.HandleTelevisionKeyDown(new KeyEventArgs { Key = Key.Down });
                    Assert.Same(play, window.FocusManager!.GetFocusedElement());
                    view.HandleTelevisionKeyDown(new KeyEventArgs { Key = Key.Enter });
                    view.HandleTelevisionKeyDown(new KeyEventArgs { Key = Key.Enter });
                    Assert.True(reader.IsAutoScrollEnabled);
                    view.HandleTelevisionKeyUp(new KeyEventArgs { Key = Key.Enter });
                    view.HandleTelevisionKeyDown(new KeyEventArgs { Key = Key.Right });
                    Assert.Same(chapter, window.FocusManager!.GetFocusedElement());
                    view.HandleTelevisionKeyDown(new KeyEventArgs { Key = Key.Right });
                    Assert.Same(settings, window.FocusManager.GetFocusedElement());
                    view.HandleTelevisionKeyDown(new KeyEventArgs { Key = Key.Enter });
                    view.HandleTelevisionKeyUp(new KeyEventArgs { Key = Key.Enter });
                }
                else
                {
                    play.Command!.Execute(null);
                    Assert.True(reader.IsAutoScrollEnabled);
                    settings.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                Assert.True(model.IsReaderSettingsOpen);
                view.HandleTelevisionKeyDown(new KeyEventArgs { Key = Key.Escape });
                Assert.False(model.IsReaderSettingsOpen);
                Assert.Same(settings, window.FocusManager!.GetFocusedElement());
                reader.UsePagedModeCommand.Execute(null);
                Assert.False(play.IsEnabled);
                Assert.True(dock.IsVisible);
                if (!touch && !tv)
                {
                    var previous = window.WindowState;
                    fullscreen.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Equal(WindowState.FullScreen, window.WindowState);
                    Assert.False(view.FindControl<Border>("Sidebar")!.IsVisible);
                    view.HandleTelevisionKeyDown(new KeyEventArgs { Key = Key.Escape });
                    Assert.Equal(previous, window.WindowState);
                    fullscreen.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    chapter.Command!.Execute(null);
                    Assert.Null(model.Reader);
                    Assert.Equal(previous, window.WindowState);
                }
            }
            finally { window.Close(); view.DataContext = null; }
        }, CancellationToken.None);
    }
}
