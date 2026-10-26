using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace UniversalMediaDistribution.UI.Views;

public partial class MainView
{
    private int _readerMotionGeneration;
    private bool _readerFramePending;
    private TimeSpan? _readerFrameTime;
    private IPointer? _readerMiddlePress;
    private bool _readerMiddleScrollActive;
    private Point _readerMiddleOrigin;
    private double _readerMiddleVelocity;
    private Cursor? _readerPreviousCursor;
    private Window? _readerMotionWindow;
    private long _readerMotionPageReportAt;
    private readonly Cursor _readerScrollCursor = new(StandardCursorType.SizeNorthSouth);

    private bool ReaderMotionActive => _readerMiddleScrollActive || _readerViewModel?.IsAutoScrollEnabled is true;

    private void InitializeReaderMotion()
    {
        AddHandler(PointerPressedEvent, ReaderMotion_OnPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerMovedEvent, ReaderMotion_OnPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, ReaderMotion_OnPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        PointerCaptureLost += (_, e) =>
        {
            if (_readerMiddlePress == e.Pointer && e.Pointer.Captured != this) StopReaderMotion();
        };
        AttachedToVisualTree += (_, _) =>
        {
            _readerMotionWindow = TopLevel.GetTopLevel(this) as Window;
            if (_readerMotionWindow is not null) _readerMotionWindow.Deactivated += ReaderMotion_OnDeactivated;
        };
        DetachedFromVisualTree += (_, _) =>
        {
            StopReaderMotion();
            if (_readerMotionWindow is not null) _readerMotionWindow.Deactivated -= ReaderMotion_OnDeactivated;
            _readerMotionWindow = null;
        };
    }

    private void ReaderMotion_OnDeactivated(object? sender, EventArgs e) => StopReaderMotion();

    private static bool WithinReaderControl(object? source, Control control) =>
        source is Visual visual && (ReferenceEquals(visual, control) || visual.GetVisualAncestors().Contains(control));

    private bool CanAnimateReader => _navigationViewModel is
    { IsReaderOpen: true, IsSettingsOpen: false, IsDonationPromptVisible: false, IsUpdatePromptVisible: false } &&
        _readerViewModel is { IsContinuous: true } && ImageReaderSurface.IsEffectivelyVisible;

    private void StopReaderMotion()
    {
        _readerMotionGeneration++;
        _readerFramePending = false;
        _readerFrameTime = null;
        var press = _readerMiddlePress;
        _readerMiddlePress = null;
        var wasMiddleScrolling = _readerMiddleScrollActive;
        _readerMiddleScrollActive = false;
        if (press?.Captured == this) press.Capture(null);
        ReaderMiddleScrollAnchor.IsVisible = false;
        if (wasMiddleScrolling)
        {
            ReaderPages.Cursor = _readerPreviousCursor;
        }
        if (_readerViewModel is { } reader) reader.IsAutoScrollEnabled = false;
    }

    private bool ShouldReportReaderMotionPage()
    {
        if (!ReaderMotionActive) return true;
        var now = Environment.TickCount64;
        if (now < _readerMotionPageReportAt) return false;
        _readerMotionPageReportAt = now + 100;
        return true;
    }

    private void ScheduleReaderMotion()
    {
        if (_readerFramePending || !ReaderMotionActive) return;
        if (!CanAnimateReader || TopLevel.GetTopLevel(this) is not { } topLevel)
        {
            StopReaderMotion();
            return;
        }
        _readerFramePending = true;
        var generation = _readerMotionGeneration;
        topLevel.RequestAnimationFrame(time =>
        {
            if (generation != _readerMotionGeneration) return;
            _readerFramePending = false;
            if (!CanAnimateReader || !ReaderMotionActive)
            {
                StopReaderMotion();
                return;
            }
            var elapsed = _readerFrameTime is { } previous ? (time - previous).TotalSeconds : 0;
            _readerFrameTime = time;
            var scroll = _readerScrollViewer ??= ReaderPages.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            if (scroll is not null && scroll.Viewport.Height > 0 && !_readerViewportRestoreScheduled)
            {
                var velocity = _readerMiddleScrollActive ? _readerMiddleVelocity : 60 * _readerViewModel!.AutoScrollSpeed;
                var maximum = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
                scroll.Offset = scroll.Offset.WithY(ReaderScrollMotion.Advance(scroll.Offset.Y, maximum, velocity, elapsed));
                if (!_readerMiddleScrollActive && scroll.Offset.Y >= maximum)
                {
                    StopReaderMotion(); // The next chapter always requires an explicit action.
                    return;
                }
            }
            ScheduleReaderMotion();
        });
    }

    private void ReaderMotion_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_readerMiddleScrollActive || _readerMiddlePress is not null)
        {
            StopReaderMotion();
            e.PreventGestureRecognition();
            e.Handled = true;
            return;
        }
        if (!CanAnimateReader || !WithinReaderControl(e.Source, ReaderPages)) return;
        StopReaderMotion(); // Touch, scrollbar dragging, or a page click takes over immediately.
        if (e.Pointer.Type != PointerType.Mouse || !e.GetCurrentPoint(this).Properties.IsMiddleButtonPressed) return;
        if (e.Source is Visual source && source.GetSelfAndVisualAncestors().Any(v => v is Button or ScrollBar)) return;
        // Capture only the unfinished click, so its release is received even
        // when the pointer leaves the page. Scrolling starts on button-up.
        _readerMiddlePress = e.Pointer;
        _readerMiddleOrigin = e.GetPosition(ImageReaderSurface);
        _readerMiddleVelocity = 0;
        e.Pointer.Capture(this);
        e.PreventGestureRecognition();
        e.Handled = true;
    }

    private void ReaderMotion_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_readerMiddleScrollActive || e.Pointer.Type != PointerType.Mouse) return;
        var distance = e.GetPosition(ImageReaderSurface).Y - _readerMiddleOrigin.Y;
        _readerMiddleVelocity = ReaderScrollMotion.PointerVelocity(distance);
        e.Handled = true;
    }

    private void ReaderMotion_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_readerMiddlePress != e.Pointer || e.InitialPressMouseButton != MouseButton.Middle) return;
        // A completed click latches a mode, independently of the pressed
        // pointer and its capture lifetime. Only a later click/Escape cancels it.
        _readerMiddlePress = null;
        if (e.Pointer.Captured == this) e.Pointer.Capture(null);
        e.PreventGestureRecognition();
        e.Handled = true;
        if (!CanAnimateReader) return;
        _readerMiddleScrollActive = true;
        _readerMiddleVelocity = ReaderScrollMotion.PointerVelocity(
            e.GetPosition(ImageReaderSurface).Y - _readerMiddleOrigin.Y);
        _readerPreviousCursor = ReaderPages.Cursor;
        ReaderPages.Cursor = _readerScrollCursor;
        Canvas.SetLeft(ReaderMiddleScrollAnchor, _readerMiddleOrigin.X - 16);
        Canvas.SetTop(ReaderMiddleScrollAnchor, _readerMiddleOrigin.Y - 16);
        ReaderMiddleScrollAnchor.IsVisible = true;
        ScheduleReaderMotion();
    }

    private bool HandleReaderMotionKey(KeyEventArgs e)
    {
        if (!ReaderMotionActive && _readerMiddlePress is null) return false;
        if (e.Key is Key.Escape or Key.BrowserBack)
        {
            StopReaderMotion();
            e.Handled = true;
            return true;
        }
        if (!WithinReaderControl(TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement(), ReaderSettingsPanel) &&
            e.Key is Key.Up or Key.Down or Key.Left or Key.Right or Key.PageUp or Key.PageDown or Key.Home or Key.End)
            StopReaderMotion();
        return false;
    }

}
