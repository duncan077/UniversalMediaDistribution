using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using UniversalMediaDistribution.UI.Localization;
using UniversalMediaDistribution.UI.ViewModels;
using UniversalMediaDistribution.UI.Views;

namespace UniversalMediaDistribution.Playback.LibVLC;

internal sealed record PlayerTrackOption(int Id, string Label)
{
    public override string ToString() => Label;
}

internal sealed partial class LibVlcPlayerView
{
    private Button _partyButton = null!;
    private Button _settingsButton = null!;
    private Button _muteButton = null!;
    private TextBlock _partyStatus = null!;
    private WatchPartyPanel _partyView = null!;
    private ScrollViewer _partyPanel = null!;
    private StackPanel _settingsPanel = null!;
    private ComboBox _audioTracks = null!;
    private ComboBox _subtitleTracks = null!;
    private TextBlock _trackMessage = null!;
    private MainViewModel? _playerViewModel;
    private bool _updatingTracks;
    private bool _isSeeking;
    private readonly PathIcon _playIcon = new()
    {
        Width = 18,
        Height = 18,
        Data = Geometry.Parse("M6,3 L21,12 L6,21 Z"),
        Foreground = Brush.Parse("#081715")
    };
    private readonly PathIcon _pauseIcon = new()
    {
        Width = 18,
        Height = 18,
        Data = Geometry.Parse("M5,3 H10 V21 H5 Z M14,3 H19 V21 H14 Z"),
        Foreground = Brush.Parse("#081715")
    };

    private void CreateAdditionalControls()
    {
        _partyButton = CreateButton(UiTextCatalog.Get("PlayerPartyAction"), 100);
        _partyButton.Name = "PlayerPartyButton";
        _partyButton.BorderBrush = Brush.Parse("#65D6AD");
        _partyButton.BorderThickness = new Thickness(1);
        _settingsButton = CreateButton(UiTextCatalog.Get("PlayerSettingsAction"), 100);
        _muteButton = CreateButton(string.Empty, 44);
        _muteButton.Content = new PathIcon
        {
            Width = 20,
            Height = 20,
            Foreground = Brushes.White,
            Data = Geometry.Parse("M3,9 H7 L12,4 V20 L7,15 H3 Z M15,7 L17,5 A10,10 0 0 1 17,19 L15,17 A7,7 0 0 0 15,7 Z")
        };
        _playPause.MinWidth = 48;
        _playPause.Width = 48;
        _playPause.Background = Brush.Parse("#65D6AD");
        _playPause.CornerRadius = new CornerRadius(24);
        _partyStatus = new TextBlock
        {
            Foreground = Brush.Parse("#65D6AD"),
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsVisible = false
        };
        _partyView = new WatchPartyPanel();
        _partyPanel = new ScrollViewer
        {
            Name = "PlayerPartyPanel",
            Content = _partyView,
            MaxHeight = 300,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            IsVisible = false
        };
        _audioTracks = CreateTrackPicker("PlayerAudioTrack");
        _subtitleTracks = CreateTrackPicker("PlayerSubtitleTrack");
        _trackMessage = new TextBlock { TextWrapping = TextWrapping.Wrap };
        _settingsPanel = new StackPanel
        {
            IsVisible = false,
            Spacing = 8,
            Children =
            {
                _variantPanel,
                new WrapPanel
                {
                    Children =
                    {
                        TrackField("PlayerAudioTrack", _audioTracks),
                        TrackField("PlayerSubtitleTrack", _subtitleTracks)
                    }
                },
                _trackMessage,
                new TextBlock
                {
                    Text = UiTextCatalog.Get("PlayerShortcutsHint"),
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                    Opacity = 0.75
                }
            }
        };
        _partyButton.Click += (_, _) =>
        {
            var open = !_partyPanel.IsVisible;
            ClosePlayerPanels();
            _partyPanel.IsVisible = open;
            RevealControls();
            if (open) _partyView.FindControl<Button>(
                _playerViewModel?.HasWatchParty is true ? "CopyCodeButton" : "CreatePartyButton")?.Focus();
        };
        _settingsButton.Click += (_, _) =>
        {
            var open = !_settingsPanel.IsVisible;
            ClosePlayerPanels();
            _settingsPanel.IsVisible = open;
            if (open) RefreshTrackControls();
            RevealControls();
        };
        _muteButton.Click += (_, _) => ToggleMute();
        _audioTracks.SelectionChanged += (_, _) => SelectTrack(_audioTracks, subtitles: false);
        _subtitleTracks.SelectionChanged += (_, _) => SelectTrack(_subtitleTracks, subtitles: true);
        DataContextChanged += OnPlayerDataContextChanged;
        AutomationProperties.SetName(_seek, UiTextCatalog.Get("PlayerSeekLabel"));
        AutomationProperties.SetName(_volume, UiTextCatalog.Get("PlayerVolume"));
        AutomationProperties.SetName(_partyButton, UiTextCatalog.Get("PlayerPartyAction"));
    }

    private static ComboBox CreateTrackPicker(string label)
    {
        var picker = new ComboBox { Width = 230, MinHeight = 36 };
        AutomationProperties.SetName(picker, UiTextCatalog.Get(label));
        return picker;
    }

    private static StackPanel TrackField(string label, ComboBox picker) => new()
    {
        Spacing = 5,
        Margin = new Thickness(0, 0, 12, 8),
        Children = { new TextBlock { Text = UiTextCatalog.Get(label), FontSize = 12 }, picker }
    };

    private void OnPlayerDataContextChanged(object? sender, EventArgs e)
    {
        if (_playerViewModel is not null) _playerViewModel.PropertyChanged -= OnPlayerModelChanged;
        _playerViewModel = _disposed ? null : DataContext as MainViewModel;
        if (_playerViewModel is not null) _playerViewModel.PropertyChanged += OnPlayerModelChanged;
        _partyView.DataContext = _playerViewModel;
        RefreshPartyControls();
    }

    private void OnPlayerModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.HasWatchParty) or
            nameof(MainViewModel.WatchPartyPlayerStatus) or nameof(MainViewModel.WatchPartySyncStatus) or
            nameof(MainViewModel.WatchPartyMemberLabels))
        {
            RefreshPartyControls();
        }
    }

    private void RefreshPartyControls()
    {
        var inParty = _playerViewModel?.HasWatchParty is true;
        _partyButton.Content = inParty
            ? $"{UiTextCatalog.Get("PlayerPartyAction")} · {_playerViewModel!.WatchPartyMemberLabels.Count}"
            : UiTextCatalog.Get("PlayerPartyAction");
        _partyStatus.IsVisible = inParty;
        _partyStatus.Text = _playerViewModel?.WatchPartySyncStatus;
    }

    private bool ClosePlayerPanels()
    {
        var hadPanel = _partyPanel.IsVisible || _settingsPanel.IsVisible || _castingPanel.IsVisible;
        _partyPanel.IsVisible = false;
        _settingsPanel.IsVisible = false;
        _castingPanel.IsVisible = false;
        return hadPanel;
    }

    private void RefreshTrackControls()
    {
        if (_playerAccessSuspended || _disposed) return;
        _updatingTracks = true;
        try
        {
            SetTrackOptions(_audioTracks,
                _mediaPlayer.AudioTrackDescription.Select(t => new PlayerTrackOption(t.Id, TrackLabel(t.Id, t.Name))),
                _mediaPlayer.AudioTrack);
            SetTrackOptions(_subtitleTracks,
                _mediaPlayer.SpuDescription.Select(t => new PlayerTrackOption(t.Id, TrackLabel(t.Id, t.Name))),
                _mediaPlayer.Spu);
            if (_audioTracks.ItemCount == 0 && _subtitleTracks.ItemCount == 0)
                _trackMessage.Text = UiTextCatalog.Get("PlayerTracksUnavailable");
            else if (_trackMessage.Text == UiTextCatalog.Get("PlayerTracksUnavailable"))
                _trackMessage.Text = string.Empty;
        }
        finally { _updatingTracks = false; }
    }

    private static string TrackLabel(int id, string name) =>
        id == -1 ? UiTextCatalog.Get("PlayerTrackNone") : name;

    private static void SetTrackOptions(ComboBox picker, IEnumerable<PlayerTrackOption> tracks, int selected)
    {
        var options = tracks.ToArray();
        if (picker.ItemsSource is not PlayerTrackOption[] previous || !previous.SequenceEqual(options))
            picker.ItemsSource = options;
        picker.SelectedItem = options.FirstOrDefault(t => t.Id == selected);
        picker.IsEnabled = options.Length > 0;
    }

    private void SelectTrack(ComboBox picker, bool subtitles)
    {
        if (_updatingTracks || _playerAccessSuspended || _disposed || picker.SelectedItem is not PlayerTrackOption track)
            return;
        var selected = subtitles ? _mediaPlayer.SetSpu(track.Id) : _mediaPlayer.SetAudioTrack(track.Id);
        _trackMessage.Text = selected ? string.Empty : UiTextCatalog.Get("PlayerTrackFailed");
        RefreshTrackControls();
    }

    private void ToggleMute()
    {
        if (_playerAccessSuspended || _disposed) return;
        _mediaPlayer.Mute = !_mediaPlayer.Mute;
        RefreshControls();
    }

    private static bool IsInteractiveSource(object? source) => source is Control control &&
        control.GetVisualAncestors().Prepend(control).Any(c => c is TextBox or Button or Slider or ComboBox);

    private void CompleteSeek()
    {
        if (!_isSeeking) return;
        _isSeeking = false;
        ApplySeek();
        RevealControls();
    }
}
