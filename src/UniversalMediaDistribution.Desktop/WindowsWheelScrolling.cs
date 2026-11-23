using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace UniversalMediaDistribution.Desktop;

internal static partial class WindowsWheelScrolling
{
    private const uint WheelPageScroll = uint.MaxValue;
    private const uint GetWheelScrollLines = 0x0068;
    private const uint GetWheelScrollCharacters = 0x006C;
    // Chromium converts the default three Windows lines into 100 logical pixels.
    private const double PixelsPerLine = 100.0 / 3;
    private static bool _initialized;

    public static void Initialize()
    {
        if (!OperatingSystem.IsWindows() || _initialized)
        {
            return;
        }

        _initialized = true;
        Register(ReadSettings);
    }

    internal static IDisposable Register(Func<(uint Lines, uint Characters)> readSettings) =>
        InputElement.PointerWheelChangedEvent.AddClassHandler<ScrollContentPresenter>(
            (presenter, args) => OnWheel(presenter, args, readSettings),
            RoutingStrategies.Tunnel);

    private static void OnWheel(
        ScrollContentPresenter presenter,
        PointerWheelEventArgs args,
        Func<(uint Lines, uint Characters)> readSettings)
    {
        if (args.Handled || args.KeyModifiers.HasFlag(KeyModifiers.Control) ||
            args.Source is not Visual source)
        {
            return;
        }

        // Only the innermost scroller processes the event. Let controls which
        // use the wheel to change a value keep their own input handling.
        var ancestors = source.GetSelfAndVisualAncestors().ToArray();
        var nearest = ancestors.OfType<ScrollContentPresenter>().FirstOrDefault();
        if (!ReferenceEquals(nearest, presenter) || ancestors.TakeWhile(v => v != presenter)
                .Any(v => v is RangeBase or Spinner or NumericUpDown or ComboBox))
        {
            return;
        }

        var settings = readSettings();
        foreach (var scroll in ancestors.OfType<ScrollContentPresenter>())
        {
            var delta = args.Delta;
            var shift = args.KeyModifiers == KeyModifiers.Shift && delta.X == 0;
            if (shift)
            {
                delta = new Vector(delta.Y, 0);
            }
            else if (scroll.FlowDirection == FlowDirection.RightToLeft)
            {
                delta = new Vector(-delta.X, delta.Y);
            }

            var logical = scroll.Child as ILogicalScrollable;
            var isLogical = logical?.IsLogicalScrollEnabled == true;
            var lineSize = isLogical ? logical!.ScrollSize : new Size(PixelsPerLine, PixelsPerLine);
            var offset = scroll.Offset;
            var next = new Vector(
                scroll.CanHorizontallyScroll
                    ? Move(offset.X, delta.X, shift ? settings.Lines : settings.Characters,
                        lineSize.Width, scroll.Viewport.Width, scroll.Extent.Width)
                    : offset.X,
                scroll.CanVerticallyScroll
                    ? Move(offset.Y, delta.Y, settings.Lines,
                        lineSize.Height, scroll.Viewport.Height, scroll.Extent.Height)
                    : offset.Y);

            // A zero system setting disables scrolling on that axis; allowing
            // Avalonia's default handler through would still move it by 50px.
            if (next != offset || !scroll.IsScrollChainingEnabled ||
                (delta.Y != 0 && settings.Lines == 0) ||
                (delta.X != 0 && (shift ? settings.Lines : settings.Characters) == 0))
            {
                scroll.SetCurrentValue(ScrollContentPresenter.OffsetProperty, next);
                args.Handled = true;
                return;
            }
        }
    }

    private static double Move(
        double offset, double ticks, uint lines, double lineSize, double viewport, double extent)
    {
        var distance = lines == WheelPageScroll ? viewport : lines * lineSize;
        return Math.Clamp(offset - ticks * distance, 0, Math.Max(0, extent - viewport));
    }

    private static (uint Lines, uint Characters) ReadSettings()
    {
        // Query on each wheel event so changes in Windows Settings take effect
        // immediately, including high-resolution wheels with fractional ticks.
        var lines = SystemParametersInfo(GetWheelScrollLines, 0, out var value, 0) != 0 ? value : 3u;
        var characters = SystemParametersInfo(GetWheelScrollCharacters, 0, out value, 0) != 0 ? value : 1u;
        return (lines, characters);
    }

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    private static partial int SystemParametersInfo(uint action, uint parameter, out uint value, uint flags);
}
