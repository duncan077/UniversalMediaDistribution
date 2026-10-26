using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
public sealed class ReaderScrollInteractionTests
{
    [Fact]
    public async Task MiddleClickAndAutomaticMovementUseTheRealViewportAndReleaseInput()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(PlayerFullscreenLayoutTests.TestApp));
        await session.Dispatch<bool>(async () =>
        {
            using var reader = ReaderAutoScrollTests.CreateReaderAsync().GetAwaiter().GetResult();
            reader.UseContinuousModeCommand.Execute(null);
            using var remote = new HttpRemoteMediaServerClient();
            var model = CreateModel(remote, RuntimeProfile.Desktop);
            model.Reader = reader;
            model.DismissDonationPromptCommand.Execute(null);
            var view = new MainView { DataContext = model };
            var window = new Window { Content = view, Width = 1280, Height = 720 };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                var pages = view.FindControl<ListBox>("ReaderPages")!;
                var scroll = pages.GetVisualDescendants().OfType<ScrollViewer>().First();
                var anchor = view.FindControl<Border>("ReaderMiddleScrollAnchor")!;
                var point = pages.TranslatePoint(new Point(15, 300), window)!.Value;
                async Task Frame()
                {
                    await Task.Delay(25);
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                }
                await Frame();
                await Frame();
                Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
                object? Field(string name) => typeof(MainView).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view);
                window.MouseMove(point);
                window.MouseDown(point, MouseButton.Middle);
                Assert.False(anchor.IsVisible); // Holding is not an active toggle.
                var pointer = Assert.IsAssignableFrom<IPointer>(Field("_readerMiddlePress"));
                Assert.Same(view, pointer.Captured);
                window.MouseUp(point, MouseButton.Middle);
                Assert.True(anchor.IsVisible);
                Assert.Null(pointer.Captured);
                pointer.Capture(pages);
                pointer.Capture(null);
                Assert.True(anchor.IsVisible); // Subsequent capture changes do not clear the latch.
                window.MouseMove(point.WithY(point.Y + 100));
                var start = scroll.Offset.Y;
                await Frame();
                await Frame();
                for (var i = 0; i < 10 && scroll.Offset.Y <= start; i++) await Frame();
                Assert.True(scroll.Offset.Y > start, $"offset={scroll.Offset.Y}, start={start}, time={Field("_readerFrameTime")}, pending={Field("_readerFramePending")}, velocity={Field("_readerMiddleVelocity")}, restoring={Field("_readerViewportRestoreScheduled")}, anchor={anchor.IsVisible}");
                start = scroll.Offset.Y;
                for (var i = 0; i < 4; i++) await Frame();
                Assert.True(scroll.Offset.Y > start); // No button or further mouse movement required.
                window.MouseMove(point);
                start = scroll.Offset.Y;
                await Frame();
                await Frame();
                Assert.Equal(start, scroll.Offset.Y); // The origin pauses without switching off.
                Assert.True(anchor.IsVisible);
                window.MouseMove(point.WithY(point.Y - 80));
                await Frame();
                await Frame();
                Assert.True(scroll.Offset.Y < start);
                window.MouseDown(point, MouseButton.Middle);
                window.MouseUp(point, MouseButton.Middle);
                Assert.False(anchor.IsVisible);

                window.MouseDown(point, MouseButton.Middle);
                window.MouseMove(point.WithY(point.Y + 60), RawInputModifiers.MiddleMouseButton);
                start = scroll.Offset.Y;
                await Frame();
                await Frame();
                Assert.Equal(start, scroll.Offset.Y);
                Assert.False(anchor.IsVisible);
                window.MouseUp(point.WithY(point.Y + 60), MouseButton.Middle);
                Assert.True(anchor.IsVisible); // Release never cancels the toggle, even after movement.
                start = scroll.Offset.Y;
                await Frame();
                await Frame();
                Assert.True(scroll.Offset.Y > start);
                window.MouseDown(point, MouseButton.Middle);
                window.MouseUp(point, MouseButton.Middle);
                Assert.False(anchor.IsVisible);

                window.MouseDown(point, MouseButton.Middle);
                view.HandleTelevisionKeyDown(new KeyEventArgs { Key = Key.Escape });
                window.MouseUp(point, MouseButton.Middle);
                Assert.False(anchor.IsVisible); // Cancelling a pending click must not latch on release.
                window.MouseDown(point, MouseButton.Middle);
                pointer.Capture(null);
                window.MouseUp(point, MouseButton.Middle);
                Assert.False(anchor.IsVisible); // An interrupted press is not a completed click.
                window.MouseDown(point, MouseButton.Middle);
                window.MouseUp(point, MouseButton.Middle);
                Assert.True(anchor.IsVisible);
                view.HandleTelevisionKeyDown(new KeyEventArgs { Key = Key.Escape });
                Assert.False(anchor.IsVisible);
                Assert.Same(reader, model.Reader);

                reader.IsAutoScrollEnabled = true;
                start = scroll.Offset.Y;
                await Frame();
                await Frame();
                Assert.True(scroll.Offset.Y > start);
                var escape = new KeyEventArgs { Key = Key.Escape };
                view.HandleTelevisionKeyDown(escape);
                Assert.True(escape.Handled);
                Assert.False(reader.IsAutoScrollEnabled);
                Assert.Same(reader, model.Reader);

                reader.IsAutoScrollEnabled = true;
                view.FindControl<Border>("ReaderToolbar")!.IsVisible = true;
                view.FindControl<Border>("ReaderSettingsPanel")!.IsVisible = true;
                var slider = view.FindControl<ReaderAutoScrollControls>("ReaderAutoScrollOptions")!
                    .FindControl<Slider>("SpeedSlider")!;
                window.UpdateLayout();
                slider.Focus();
                window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
                Assert.Equal(1.25, reader.AutoScrollSpeed);
                Assert.True(reader.IsAutoScrollEnabled);
                window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
                Assert.IsAssignableFrom<Button>(window.FocusManager!.GetFocusedElement());
                Assert.True(reader.IsAutoScrollEnabled);
                window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                Assert.True(reader.IsAutoScrollEnabled);
                Assert.False(view.FindControl<Border>("ReaderSettingsPanel")!.IsVisible);
                window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                Assert.False(reader.IsAutoScrollEnabled);
                Assert.Same(reader, model.Reader);

                reader.IsAutoScrollEnabled = true;
                window.MouseWheel(point, new Vector(0, -1));
                Assert.False(reader.IsAutoScrollEnabled);
                Assert.True(scroll.Offset.Y > start);
                reader.AutoScrollSpeed = 10;
                scroll.Offset = scroll.Offset.WithY(scroll.Extent.Height - scroll.Viewport.Height - 1);
                reader.IsAutoScrollEnabled = true;
                await Frame();
                await Frame();
                Assert.False(reader.IsAutoScrollEnabled);
                Assert.Same(reader, model.Reader);
                scroll.Offset = default;
                reader.IsAutoScrollEnabled = true;
                window.Content = null;
                Assert.False(reader.IsAutoScrollEnabled);
            }
            finally { window.Close(); view.DataContext = null; }
            return true;
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(1280, false)]
    [InlineData(320, true)]
    public async Task ContinuousControlsBindAndMoveWithTheReaderLayout(double width, bool touch)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(PlayerFullscreenLayoutTests.TestApp));
        await session.Dispatch(() =>
        {
            using var reader = ReaderAutoScrollTests.CreateReaderAsync().GetAwaiter().GetResult();
            using var remote = new HttpRemoteMediaServerClient();
            var model = CreateModel(remote, touch ? RuntimeProfile.Android : RuntimeProfile.Desktop);
            model.Reader = reader;
            model.DismissDonationPromptCommand.Execute(null);
            var view = new MainView { DataContext = model };
            var window = new Window { Width = width, Height = 720, Content = view };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                var controls = view.FindControl<ReaderAutoScrollControls>("ReaderAutoScrollOptions")!;
                var settings = view.FindControl<Border>("ReaderSettingsPanel")!;
                Assert.False(settings.IsVisible);
                Assert.Same(reader, controls.DataContext);
                Assert.False(controls.IsVisible);
                reader.UseContinuousModeCommand.Execute(null);
                settings.IsVisible = true;
                view.FindControl<Border>("ReaderToolbar")!.IsVisible = true;
                window.UpdateLayout();
                Assert.True(controls.IsVisible);
                Assert.Equal(touch ? "ReaderTouchOptionsHost" : "ReaderHeaderGrid",
                    ((Control)view.FindControl<Button>("ReaderSettingsButton")!.Parent!).Name);
                Assert.True(controls.Bounds.Width > 0);
                foreach (var button in controls.GetVisualDescendants().OfType<Button>())
                {
                    var origin = button.TranslatePoint(default, window)!.Value;
                    Assert.InRange(origin.X, 0, width);
                    Assert.InRange(origin.X + button.Bounds.Width, 0, width + 1);
                }
            }
            finally { window.Close(); view.DataContext = null; }
        }, CancellationToken.None);
    }

    internal static MainViewModel CreateModel(HttpRemoteMediaServerClient remote, RuntimeProfile profile)
    {
        var repository = new InMemoryCatalogRepository();
        var settings = new InMemoryMetadataProviderSettingsStore();
        var model = new MainViewModel(
            new CatalogBrowseService(repository), null!, null,
            new CatalogMetadataRefreshService(repository, settings, []), settings,
            null!, new InMemoryUserMediaStateStore(), null!, null!,
            new UnavailablePlatformMediaPlayer(), [], profile,
            null!, null!, null!, null!, null!, null!, null, remote);
        // The fixture exercises the reader without starting catalog/network services.
        typeof(MainViewModel).GetField("_initialized", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(model, true);
        return model;
    }

}
