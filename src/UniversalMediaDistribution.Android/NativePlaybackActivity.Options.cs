using Android.App;
using Android.Views;
using Android.Widget;
using UniversalMediaDistribution.Mobile;
using UniversalMediaDistribution.UI.Localization;

namespace UniversalMediaDistribution.Android;

internal sealed partial class NativePlaybackActivity
{
    private TextView? _optionsMessage;

    private void ShowOptionsPanel()
    {
        var body = OpenPlayerPanel(UiTextCatalog.Get("PlayerSettingsAction"), _optionsButton);
        _optionsMessage = PanelText(string.Empty);
        body.AddView(_optionsMessage);
        var audio = PanelAction(body, UiTextCatalog.Get("PlayerAudioTrack"), (_, _) => ShowTrackPicker(subtitles: false));
        PanelAction(body, UiTextCatalog.Get("PlayerSubtitleTrack"), (_, _) => ShowTrackPicker(subtitles: true));
        if (_launch is { Variants.Count: > 1 } launch)
        {
            PanelAction(body, UiTextCatalog.Get("PlayerServerQuality"), (_, _) => ShowPlayerChoices(
                UiTextCatalog.Get("PlayerServerQuality"), launch.Variants.Select(v => v.Label).ToArray(),
                _selectedVariantIndex, index =>
                {
                    ClosePlayerPanel();
                    _attemptedEngines.Clear();
                    _attemptedVariantIndexes.Clear();
                    ApplyVariant(index);
                }));
        }
        if (_launch is { } currentLaunch &&
            AndroidPlaybackEnginePolicy.CanUseLibVlc(currentLaunch.Variants[_selectedVariantIndex].MediaSource))
            PanelAction(body, UiTextCatalog.Get("PlayerEngineLabel"), (_, _) => ShowPlayerChoices(
                UiTextCatalog.Get("PlayerEngineLabel"), ["Android", "VLC"],
                _videoView?.Engine == AndroidPlaybackEngine.LibVlc ? 1 : 0, index =>
            {
                var engine = index == 0 ? AndroidPlaybackEngine.System : AndroidPlaybackEngine.LibVlc;
                if (_videoView?.Engine == engine) return;
                if (!AndroidPlaybackEnginePolicy.CanUseLibVlc(currentLaunch.Variants[_selectedVariantIndex].MediaSource)) return;
                ClosePlayerPanel();
                _attemptedEngines.Clear();
                _attemptedVariantIndexes.Clear();
                ApplyVariant(_selectedVariantIndex, engine);
            }));
        RefreshDialogFocus(audio);
    }

    private void ShowTrackPicker(bool subtitles)
    {
        if (!_isPrepared || _videoView is null)
        {
            _optionsMessage!.Text = UiTextCatalog.Get("PlayerTracksUnavailable");
            return;
        }
        try
        {
            var tracks = _videoView.GetTracks(subtitles);
            if (tracks.Count == 0)
            {
                _optionsMessage!.Text = UiTextCatalog.Get("PlayerTracksUnavailable");
                return;
            }
            var generation = _playbackAttemptGeneration;
            ShowPlayerChoices(UiTextCatalog.Get(subtitles ? "PlayerSubtitleTrack" : "PlayerAudioTrack"),
                tracks.Select(t => t.Label).ToArray(), tracks.ToList().FindIndex(t => t.IsSelected), index =>
                {
                    try
                    {
                        var success = generation == _playbackAttemptGeneration && _isPrepared &&
                            _videoView?.SelectTrack(tracks[index].Id, subtitles) is true;
                        _optionsMessage!.Text = success ? string.Empty : UiTextCatalog.Get("PlayerTrackFailed");
                    }
                    catch (Exception) { _optionsMessage!.Text = UiTextCatalog.Get("PlayerTrackFailed"); }
                });
        }
        catch (Exception) { _optionsMessage!.Text = UiTextCatalog.Get("PlayerTracksUnavailable"); }
    }

    private void ShowPlayerChoices(string title, string[] options, int selected, Action<int> choose)
    {
        _choiceDialog?.Dismiss();
        var dialog = new AlertDialog.Builder(this)!
            .SetTitle(title)!
            .SetSingleChoiceItems(options, selected, (_, args) =>
            {
                _choiceDialog?.Dismiss();
                _choiceDialog = null;
                if (!_playerPanelsDisposed && args.Which >= 0 && args.Which < options.Length) choose(args.Which);
            })!
            .SetNegativeButton(UiTextCatalog.Get("SettingsClose"), (_, _) => { })!
            .Create()!;
        _choiceDialog = dialog;
        dialog.Show();
        dialog.ListView?.SetSelection(Math.Max(0, selected));
        dialog.ListView?.RequestFocus();
    }
}
