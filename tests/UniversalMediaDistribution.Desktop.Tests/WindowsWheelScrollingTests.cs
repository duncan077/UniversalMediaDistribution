using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Themes.Fluent;

namespace UniversalMediaDistribution.Desktop.Tests;

public sealed class WindowsWheelScrollingTests
{
    [Theory]
    [InlineData(1u, -1, 100.0 / 3)]
    [InlineData(3u, -1, 100)]
    [InlineData(6u, -1, 200)]
    [InlineData(3u, -0.25, 25)]
    [InlineData(0u, -1, 0)]
    public async Task UsesWindowsLinesAndPreservesFractionalTicks(uint lines, double ticks, double distance)
    {
        await Run((window, scroll) =>
        {
            using var registration = WindowsWheelScrolling.Register(() => (lines, 1u));
            window.MouseWheel(new Point(50, 50), new Vector(0, ticks));
            Assert.Equal(distance, scroll.Offset.Y, 5);
        });
    }

    [Fact]
    public async Task ReadsChangedSettingsAndSupportsPageModeWithoutRestarting()
    {
        await Run((window, scroll) =>
        {
            uint lines = 3;
            using var registration = WindowsWheelScrolling.Register(() => (lines, 1u));
            window.MouseWheel(new Point(50, 50), new Vector(0, -1));
            Assert.Equal(100, scroll.Offset.Y, 5);
            lines = 6;
            window.MouseWheel(new Point(50, 50), new Vector(0, -1));
            Assert.Equal(300, scroll.Offset.Y, 5);
            lines = uint.MaxValue;
            var before = scroll.Offset.Y;
            window.MouseWheel(new Point(50, 50), new Vector(0, -1));
            Assert.Equal(before + scroll.Viewport.Height, scroll.Offset.Y, 5);
        });
    }

    [Fact]
    public async Task HorizontalWheelUsesCharactersAndShiftWheelUsesLines()
    {
        await Run((window, scroll) =>
        {
            using var registration = WindowsWheelScrolling.Register(() => (3u, 6u));
            window.MouseWheel(new Point(50, 50), new Vector(-1, 0));
            Assert.Equal(200, scroll.Offset.X, 5);
            window.MouseWheel(new Point(50, 50), new Vector(0, -1), RawInputModifiers.Shift);
            Assert.Equal(300, scroll.Offset.X, 5);
            Assert.Equal(0, scroll.Offset.Y);
        }, horizontal: true);
    }

    [Fact]
    public async Task NestedScrollerConsumesOnceAndChainsAtTheBoundary()
    {
        await Run((window, outer) =>
        {
            var inner = new ScrollViewer
            {
                Height = 200,
                Content = new Border { Height = 1000 }
            };
            outer.Content = new StackPanel
            {
                Children = { inner, new Border { Height = 2000 } }
            };
            window.UpdateLayout();
            using var registration = WindowsWheelScrolling.Register(() => (3u, 1u));
            window.MouseWheel(new Point(50, 50), new Vector(0, -1));
            Assert.Equal(100, inner.Offset.Y, 5);
            Assert.Equal(0, outer.Offset.Y);
            inner.Offset = new Vector(0, inner.Extent.Height - inner.Viewport.Height);
            window.MouseWheel(new Point(50, 50), new Vector(0, -1));
            Assert.Equal(100, outer.Offset.Y, 5);
            outer.Offset = default;
            inner.IsScrollChainingEnabled = false;
            window.UpdateLayout();
            window.MouseWheel(new Point(50, 50), new Vector(0, -1));
            Assert.Equal(0, outer.Offset.Y);
        });
    }

    [Fact]
    public async Task ControlWheelRemainsAvailableForReaderZoom()
    {
        await Run((window, scroll) =>
        {
            var queried = false;
            using var registration = WindowsWheelScrolling.Register(() =>
            {
                queried = true;
                return (3u, 1u);
            });
            var zoomed = false;
            scroll.AddHandler(InputElement.PointerWheelChangedEvent, (_, args) =>
            {
                zoomed = true;
                args.Handled = true;
            }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            window.MouseWheel(new Point(50, 50), new Vector(0, -1), RawInputModifiers.Control);
            Assert.True(zoomed);
            Assert.False(queried);
            Assert.Equal(0, scroll.Offset.Y);
        });
    }

    private static async Task Run(Action<Window, ScrollViewer> check, bool horizontal = false)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(TestApp));
        await session.Dispatch(() =>
        {
            var scroll = new ScrollViewer
            {
                HorizontalScrollBarVisibility = horizontal
                    ? Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
                    : Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                Content = new Border { Width = horizontal ? 2000 : 200, Height = 3000 }
            };
            var window = new Window { Width = 400, Height = 400, Content = scroll };
            window.Show();
            try
            {
                window.UpdateLayout();
                check(window, scroll);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    public sealed class TestApp : Avalonia.Application
    {
        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<TestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());

        public override void Initialize() => Styles.Add(new FluentTheme());
    }
}
