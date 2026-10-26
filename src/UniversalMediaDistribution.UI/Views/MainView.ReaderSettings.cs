using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace UniversalMediaDistribution.UI.Views;

public partial class MainView
{
    private Key? _readerSettingsActivationKey;
    private Button? _readerSettingsReturnFocus;

    private void ReaderSettingsButton_OnClick(object? sender, RoutedEventArgs e)
    {
        _readerSettingsReturnFocus = sender as Button;
        SetReaderSettingsOpen(!ReaderSettingsPanel.IsVisible);
    }

    private void ReaderSettingsCloseButton_OnClick(object? sender, RoutedEventArgs e) =>
        SetReaderSettingsOpen(false);

    private void SetReaderSettingsOpen(bool open)
    {
        if (_navigationViewModel is { } model && model.IsReaderSettingsOpen != open)
        {
            model.IsReaderSettingsOpen = open;
            return;
        }
        ReaderSettingsPanel.IsVisible = open;
        _readerToolbarHideTimer.Stop();
        ReaderSettingsButton.Classes.Set("active", open);
        ReaderDockSettingsButton.Classes.Set("active", open);
        if (open)
        {
            ShowReaderToolbar(autoHide: false);
            UpdateReaderSettingsLayout();
            ReaderSettingsPanel.UpdateLayout();
            if (_isTelevision)
                ReaderModeControls.GetVisualDescendants().OfType<Button>().FirstOrDefault()?.Focus(NavigationMethod.Directional);
        }
        else if (_navigationViewModel?.IsReaderOpen is true)
        {
            (_readerSettingsReturnFocus is { IsEffectivelyVisible: true } target ? target : ReaderSettingsButton).Focus(NavigationMethod.Directional);
        }
    }

    private void UpdateReaderSettingsLayout()
    {
        var viewport = ImageReaderSurface.Bounds.Size;
        var top = Math.Max(ReaderToolbar.Bounds.Height + 8, _isTelevision ? 96 : 80);
        var bottom = UsesTouchLayout ? 90 : 16;
        ReaderSettingsPanel.Width = Math.Min(_isTelevision ? 620 : 520, Math.Max(0, viewport.Width - 32));
        ReaderSettingsPanel.VerticalAlignment = UsesTouchLayout ? VerticalAlignment.Bottom : VerticalAlignment.Top;
        ReaderSettingsPanel.Margin = new Thickness(16, top, 16, bottom);
        ReaderSettingsScroll.MaxHeight = Math.Max(40, viewport.Height - top - bottom - 104);
        ReaderAutoScrollOptions.FindControl<TextBlock>("MiddleScrollHint")!.IsVisible = !_isTelevision && !UsesTouchLayout;
        ReaderAutoScrollOptions.FindControl<TextBlock>("RemoteScrollHint")!.IsVisible = _isTelevision;
    }

    private bool HandleReaderSettingsKey(KeyEventArgs e)
    {
        if (_navigationViewModel is not
            {
                IsReaderOpen: true, IsSettingsOpen: false,
                IsDonationPromptVisible: false, IsUpdatePromptVisible: false
            }) return false;
        if (e.Key is not (Key.Enter or Key.Space)) _readerSettingsActivationKey = null;
        if (_readerSettingsActivationKey == e.Key)
        {
            e.Handled = true;
            return true;
        }
        var manager = TopLevel.GetTopLevel(this)?.FocusManager;
        var focused = manager?.GetFocusedElement();
        if (!ReaderSettingsPanel.IsVisible)
        {
            if (_isTelevision && e.Key is Key.Enter or Key.Space &&
                (ReferenceEquals(focused, ReaderSettingsButton) || WithinReaderControl(focused, ReaderPages) || WithinReaderControl(focused, ReaderPagedScroll)))
            {
                _readerSettingsActivationKey = e.Key;
                SetReaderSettingsOpen(true);
                e.Handled = true;
                return true;
            }
            return false;
        }
        if (e.Key is Key.Escape or Key.BrowserBack)
        {
            SetReaderSettingsOpen(false);
            e.Handled = true;
            return true;
        }
        if (!_isTelevision && !WithinReaderControl(focused, ReaderSettingsPanel)) return false;
        if (focused is Slider slider && e.Key is Key.Left or Key.Right)
        {
            slider.Value = Math.Clamp(slider.Value + (e.Key == Key.Right ? slider.SmallChange : -slider.SmallChange), slider.Minimum, slider.Maximum);
            e.Handled = true;
            return true;
        }
        if (_isTelevision && e.Key is Key.Enter or Key.Space && focused is Button button)
        {
            _readerSettingsActivationKey = e.Key;
            if (ReferenceEquals(button, ReaderSettingsCloseButton)) SetReaderSettingsOpen(false);
            else if (button.Command?.CanExecute(button.CommandParameter) is true) button.Command.Execute(button.CommandParameter);
            e.Handled = true;
            return true;
        }
        var controls = ReaderSettingsPanel.GetVisualDescendants().OfType<Control>()
            .Where(c => c is Button or Slider && c.IsEffectivelyVisible && c.IsEffectivelyEnabled && c.Focusable).ToArray();
        if (e.Key is Key.Up or Key.Down or Key.Left or Key.Right or Key.Tab)
        {
            var backwards = e.Key is Key.Up or Key.Left || (e.Key == Key.Tab && e.KeyModifiers.HasFlag(KeyModifiers.Shift));
            var index = Array.FindIndex(controls, c => ReferenceEquals(c, focused));
            var next = controls.Length == 0 ? null : controls[(index + (backwards ? -1 : 1) + controls.Length) % controls.Length];
            next?.Focus(NavigationMethod.Directional);
            next?.BringIntoView();
            e.Handled = true;
            return true;
        }
        // Keep reader shortcuts and shell navigation out of an open TV panel.
        return _isTelevision;
    }
}
