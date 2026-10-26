using CommunityToolkit.Mvvm.Input;
using UniversalMediaDistribution.UI.Localization;

namespace UniversalMediaDistribution.UI.ViewModels;

public partial class ImageReaderViewModel
{
    private bool _isAutoScrollEnabled;
    private double _autoScrollSpeed = 1;

    public bool IsAutoScrollEnabled
    {
        get => _isAutoScrollEnabled;
        set
        {
            if (SetProperty(ref _isAutoScrollEnabled, value && IsContinuous && !_disposed && Pages.Count > 0))
                OnPropertyChanged(nameof(AutoScrollActionLabel));
        }
    }

    public double AutoScrollSpeed
    {
        get => _autoScrollSpeed;
        set
        {
            if (SetProperty(ref _autoScrollSpeed, double.IsFinite(value) ? Math.Clamp(value, 0.25, 10) : 1))
                OnPropertyChanged(nameof(AutoScrollSpeedLabel));
        }
    }

    public string AutoScrollActionLabel => UiTextCatalog.Get(
        IsAutoScrollEnabled ? "ReaderAutoScrollPause" : "ReaderAutoScrollStart");

    public string AutoScrollSpeedLabel => $"{AutoScrollSpeed:0.##}×";

    [RelayCommand]
    private void ToggleAutoScroll() => IsAutoScrollEnabled = !IsAutoScrollEnabled;

    [RelayCommand]
    private void SetAutoScrollSpeed(string value)
    {
        if (double.TryParse(value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var speed))
            AutoScrollSpeed = speed;
    }
}
