using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
public sealed class ReaderSettingsInteractionTests
{
    [Theory]
    [InlineData(1280, 720, false)]
    [InlineData(960, 540, true)]
    [InlineData(1920, 1080, true)]
    public async Task OptionsStayAlignedAndOpenDuringDraggingAndRemoteNavigation(int width, int height, bool television)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(PlayerFullscreenLayoutTests.TestApp));
        await session.Dispatch(() =>
        {
            using var reader = ReaderAutoScrollTests.CreateReaderAsync().GetAwaiter().GetResult();
            reader.UseContinuousModeCommand.Execute(null);
            using var remote = new HttpRemoteMediaServerClient();
            var model = ReaderScrollInteractionTests.CreateModel(remote, television ? RuntimeProfile.AndroidTv : RuntimeProfile.Desktop);
            model.DismissDonationPromptCommand.Execute(null);
            model.Reader = reader;
            var view = new MainView { DataContext = model };
            var window = new Window { Content = view, Width = width, Height = height };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                var toolbar = view.FindControl<Border>("ReaderToolbar")!;
                toolbar.IsVisible = true;
                window.UpdateLayout();
                var back = view.FindControl<Button>("ReaderBackButton")!;
                var menu = view.FindControl<Button>("ReaderSettingsButton")!;
                var panel = view.FindControl<Border>("ReaderSettingsPanel")!;
                var menuCenter = menu.TranslatePoint(new Point(0, menu.Bounds.Height / 2), window)!.Value.Y;
                var backCenter = back.TranslatePoint(new Point(0, back.Bounds.Height / 2), window)!.Value.Y;
                Assert.InRange(Math.Abs(menuCenter - backCenter), 0, 1);
                menu.Focus();
                if (television)
                {
                    view.FindControl<ListBox>("ReaderPages")!.Focus();
                    view.HandleTelevisionKeyDown(new KeyEventArgs { Key = Key.Enter });
                    view.HandleTelevisionKeyDown(new KeyEventArgs { Key = Key.Enter });
                    Assert.True(reader.IsContinuous); // Holding OK must not activate the newly focused mode.
                    view.HandleTelevisionKeyUp(new KeyEventArgs { Key = Key.Enter });
                }
                else menu.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.UpdateLayout();
                Assert.True(panel.IsVisible);
                Dispatcher.UIThread.RunJobs();
                reader.UsePagedModeCommand.Execute(null);
                Assert.True(panel.IsVisible);
                reader.UseContinuousModeCommand.Execute(null);
                Assert.True(panel.IsVisible);
                var panelTop = panel.TranslatePoint(default, window)!.Value;
                Assert.InRange(panelTop.Y + panel.Bounds.Height, 0, height);
                Assert.InRange(panelTop.X + panel.Bounds.Width, 0, width);
                var controls = view.FindControl<ReaderAutoScrollControls>("ReaderAutoScrollOptions")!;
                var slider = controls.FindControl<Slider>("SpeedSlider")!;
                if (television)
                {
                    Assert.True(panel.IsVisualAncestorOf((Visual)window.FocusManager!.GetFocusedElement()!));
                    // Traverse with only the remote until reaching the speed control.
                    for (var i = 0; i < 12 && !ReferenceEquals(window.FocusManager.GetFocusedElement(), slider); i++)
                        view.HandleTelevisionKeyDown(new KeyEventArgs { Key = Key.Down });
                    Assert.Same(slider, window.FocusManager.GetFocusedElement());
                    window.UpdateLayout();
                    var settingsScroll = view.FindControl<ScrollViewer>("ReaderSettingsScroll")!;
                    var sliderPosition = slider.TranslatePoint(new Point(0, slider.Bounds.Height / 2), settingsScroll)!.Value;
                    Assert.InRange(sliderPosition.Y, 0, settingsScroll.Bounds.Height);
                    view.HandleTelevisionKeyDown(new KeyEventArgs { Key = Key.Right });
                    Assert.Equal(1.25, reader.AutoScrollSpeed);
                    Assert.True(panel.IsVisible);
                    Assert.True(controls.FindControl<TextBlock>("RemoteScrollHint")!.IsVisible);
                    Assert.False(controls.FindControl<TextBlock>("MiddleScrollHint")!.IsVisible);
                    view.HandleTelevisionKeyDown(new KeyEventArgs { Key = Key.Down });
                    Assert.IsType<Button>(window.FocusManager.GetFocusedElement());
                    view.HandleTelevisionKeyDown(new KeyEventArgs { Key = Key.Enter });
                    view.HandleTelevisionKeyUp(new KeyEventArgs { Key = Key.Enter });
                    Assert.Equal(1, reader.AutoScrollSpeed);
                    for (var i = 0; i < 20; i++)
                        view.HandleTelevisionKeyDown(new KeyEventArgs { Key = Key.Down });
                    Assert.True(panel.IsVisualAncestorOf((Visual)window.FocusManager.GetFocusedElement()!));
                }
                else
                {
                    slider.BringIntoView();
                    window.UpdateLayout();
                    var thumb = slider.GetVisualDescendants().OfType<Thumb>().First();
                    var start = thumb.TranslatePoint(new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2), window)!.Value;
                    window.MouseMove(start);
                    window.MouseDown(start, MouseButton.Left);
                    window.MouseMove(start.WithX(start.X + 100), RawInputModifiers.LeftMouseButton);
                    window.MouseUp(start.WithX(start.X + 100), MouseButton.Left);
                    Assert.True(reader.AutoScrollSpeed > 1);
                    Assert.True(panel.IsVisible);
                    Assert.True(toolbar.IsVisible);
                    var timer = (DispatcherTimer)typeof(MainView).GetField("_readerToolbarHideTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
                    timer.Interval = TimeSpan.FromMilliseconds(1);
                    timer.Start();
                    Thread.Sleep(20);
                    Dispatcher.UIThread.RunJobs();
                    Assert.True(toolbar.IsVisible);
                    Assert.True(panel.IsVisible);
                }
                var close = new KeyEventArgs { Key = Key.BrowserBack };
                view.HandleTelevisionKeyDown(close);
                Assert.True(close.Handled);
                Assert.False(panel.IsVisible);
                Assert.Same(menu, window.FocusManager!.GetFocusedElement());
                Assert.Same(reader, model.Reader);
                // Android activities send system Back directly to the model.
                menu.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(model.NavigateBackAsync().GetAwaiter().GetResult());
                Assert.False(panel.IsVisible);
                Assert.Same(reader, model.Reader);
            }
            finally { window.Close(); view.DataContext = null; }
        }, CancellationToken.None);
    }
}
