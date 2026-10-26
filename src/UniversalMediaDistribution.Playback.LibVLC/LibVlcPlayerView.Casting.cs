using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using UniversalMediaDistribution.UI.Localization;

namespace UniversalMediaDistribution.Playback.LibVLC;

internal sealed record CastDeviceOption(string? Id, string Name)
{
    public override string ToString() => Name;
}

internal sealed partial class LibVlcPlayerView
{
    private Button _castButton = null!;
    private TextBlock _castDestination = null!;
    private StackPanel _castingPanel = null!;
    private StackPanel _castDevices = null!;
    private TextBlock _castMessage = null!;
    private bool _castingBusy;

    public event EventHandler? CastDiscoveryRequested;
    public event EventHandler<string?>? CastSelectionRequested;

    private void CreateCastingControls()
    {
        _castButton = CreateButton(string.Empty, 44);
        _castButton.IsVisible = OperatingSystem.IsWindows();
        _castButton.Content = new PathIcon
        {
            Width = 20,
            Height = 20,
            Foreground = Brushes.White,
            // Screen outline with wireless waves originating at bottom left.
            Data = Geometry.Parse(
                "M3,3 H21 A2,2 0 0 1 23,5 V19 A2,2 0 0 1 21,21 H14 V19 H21 V5 H3 V10 H1 V5 A2,2 0 0 1 3,3 Z " +
                "M1,12 A9,9 0 0 1 10,21 H8 A7,7 0 0 0 1,14 Z " +
                "M1,16 A5,5 0 0 1 6,21 H4 A3,3 0 0 0 1,18 Z " +
                "M1,20 A1,1 0 0 1 2,21 H1 Z")
        };
        AutomationProperties.SetName(_castButton, UiTextCatalog.Get("CastAction"));
        ToolTip.SetTip(_castButton, UiTextCatalog.Get("CastAction"));
        _castDestination = new TextBlock
        {
            Text = UiTextCatalog.Get("CastLocal"),
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };
        _castDevices = new StackPanel { Spacing = 6 };
        _castMessage = new TextBlock
        {
            Text = UiTextCatalog.Get("CastSearchHint"),
            Foreground = Brush.Parse("#A1A9BC"),
            TextWrapping = TextWrapping.Wrap
        };
        var wireless = CreateButton(UiTextCatalog.Get("CastWirelessDisplay"), 140);
        wireless.Click += (_, _) =>
        {
            try { WindowsWirelessDisplay.Open(); }
            catch (Exception)
            {
                SetCastMessage(UiTextCatalog.Get("CastWirelessFailed"), error: true);
            }
        };
        _castingPanel = new StackPanel
        {
            IsVisible = false,
            Spacing = 8,
            Children =
            {
                _castDestination,
                _castMessage,
                new ScrollViewer { MaxHeight = 170, Content = _castDevices },
                wireless
            }
        };
        _castButton.Click += (_, _) =>
        {
            var open = !_castingPanel.IsVisible;
            ClosePlayerPanels();
            _castingPanel.IsVisible = open;
            if (_castingPanel.IsVisible) CastDiscoveryRequested?.Invoke(this, EventArgs.Empty);
            RevealControls();
        };
    }

    internal void SetCastDevices(IReadOnlyList<CastDeviceOption> devices, string? selectedId)
    {
        _castDevices.Children.Clear();
        foreach (var device in devices)
        {
            var button = CreateButton(
                device.Id == selectedId ? $"✓ {device.Name}" : device.Name, 120);
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.HorizontalContentAlignment = HorizontalAlignment.Left;
            button.IsEnabled = !_castingBusy;
            button.Click += (_, _) => CastSelectionRequested?.Invoke(this, device.Id);
            _castDevices.Children.Add(button);
        }
    }

    internal void SetCastBusy(bool busy)
    {
        _castingBusy = busy;
        _castDevices.IsEnabled = !busy;
        _variantPicker.IsEnabled = !busy;
        RevealControls();
    }

    internal void SetCastMessage(string message, bool error = false)
    {
        _castMessage.Text = message;
        _castMessage.Foreground = Brush.Parse(error ? "#FF9C9C" : "#A1A9BC");
        if (error) _castingPanel.IsVisible = true;
        RevealControls();
    }

    internal void SetCastDestination(string? name)
    {
        _castDestination.Text = name is null
            ? UiTextCatalog.Get("CastLocal")
            : UiTextCatalog.Format("CastConnected", name);
    }

    internal void ResetCasting()
    {
        _castingPanel.IsVisible = false;
        _castDevices.Children.Clear();
        SetCastBusy(false);
        SetCastDestination(null);
        SetCastMessage(UiTextCatalog.Get("CastSearchHint"));
    }
}
