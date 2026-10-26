using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace UniversalMediaDistribution.UI.ViewModels;

/// <summary>
/// One choice in the season selector.
/// <para>
/// Drawn as a button rather than an entry in a drop-down: on a television the
/// drop-down's popup opened without taking focus, so the remote went on driving
/// the page behind it. A row of buttons has no popup to hand focus to — every
/// season is an ordinary focus target the D-pad already reaches.
/// </para>
/// </summary>
public sealed partial class SeasonOptionViewModel : ViewModelBase
{
    [ObservableProperty]
    private bool _isSelected;

    public SeasonOptionViewModel(
        string label,
        string? seasonLabel,
        Action<SeasonOptionViewModel>? select = null)
    {
        Label = label;
        SeasonLabel = seasonLabel;
        SelectCommand = new RelayCommand(() => select?.Invoke(this));
    }

    public string Label { get; }

    /// <summary>
    /// The season to narrow to, or <see langword="null"/> for the button that
    /// leaves every season on screen.
    /// </summary>
    public string? SeasonLabel { get; }

    public IRelayCommand SelectCommand { get; }

    public bool IsAllSeasons => SeasonLabel is null;

    public override string ToString() => Label;
}
