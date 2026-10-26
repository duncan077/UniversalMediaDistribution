using CommunityToolkit.Mvvm.ComponentModel;

namespace UniversalMediaDistribution.UI.ViewModels;

public partial class FeedbackViewModel : ViewModelBase
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private string? _message;

    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);
}
