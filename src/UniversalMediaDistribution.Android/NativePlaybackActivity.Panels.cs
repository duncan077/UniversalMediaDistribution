using System.ComponentModel;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Text;
using Android.Views;
using Android.Views.InputMethods;
using Android.Widget;
using UniversalMediaDistribution.Mobile;
using UniversalMediaDistribution.UI.Localization;
using UniversalMediaDistribution.UI.ViewModels;
using UiDispatcher = Avalonia.Threading.Dispatcher;

namespace UniversalMediaDistribution.Android;

internal sealed partial class NativePlaybackActivity
{
    private Button? _partyButton;
    private Button? _optionsButton;
    private Button? _muteButton;
    private TextView? _partyStatusLabel;
    private MainViewModel? _partyModel;
    private Dialog? _playerDialog;
    private AlertDialog? _choiceDialog;
    private LinearLayout? _dialogBody;
    private readonly List<View> _dialogFocusTargets = [];
    private Button? _dialogCloseButton;
    private View? _dialogReturnFocus;
    private bool _partyPanelOpen;
    private bool _partyActionBusy;
    private bool _refreshingPartyPanel;
    private bool _playerPanelsDisposed;
    private int _partyRefreshPending;
    private string? _partyActionError;
    private TextView? _partyUnavailable;
    private TextView? _partyCode;
    private TextView? _partyDetails;
    private LinearLayout? _partyMembers;
    private readonly List<TextView> _partyMemberRows = [];
    private string[] _partyMemberLabels = [];
    private TextView? _partyError;
    private TextView? _partyActionMessage;
    private EditText? _partyJoinCode;
    private CheckBox? _partyHostOnly;
    private Button? _partyCreate;
    private Button? _partyJoin;
    private Button? _partyCopy;
    private Button? _partyResync;
    private Button? _partyOpenContent;
    private Button? _partyLeave;

    private LinearLayout CreatePlayerActions()
    {
        var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        _partyButton = CreatePlaybackControlButton(UiTextCatalog.Get("PlayerPartyAction"), (_, _) => ShowPartyPanel(), 80);
        _optionsButton = CreatePlaybackControlButton(UiTextCatalog.Get("PlayerSettingsAction"), (_, _) => ShowOptionsPanel(), 80);
        _muteButton = CreatePlaybackControlButton(UiTextCatalog.Get("PlayerMuteShort"), (_, _) =>
        {
            _videoView?.SetMuted(_videoView.IsMuted is not true);
            RefreshPlayerActions();
            ShowPlaybackControls();
        }, 72);
        var buttons = new[] { _partyButton, _optionsButton, _muteButton };
        for (var index = 0; index < buttons.Length; index++)
        {
            var button = buttons[index];
            button.NextFocusLeftId = buttons[Math.Max(0, index - 1)].Id;
            button.NextFocusRightId = buttons[Math.Min(buttons.Length - 1, index + 1)].Id;
            button.NextFocusUpId = _variantSpinner?.Id ?? _engineButton!.Id;
            button.NextFocusDownId = button.Id;
            var layout = new LinearLayout.LayoutParams(0, Dp(_isTelevision ? 56 : 48), 1);
            layout.SetMargins(Dp(3), Dp(6), Dp(3), 0);
            row.AddView(button, layout);
        }
        if (_engineButton is not null) _engineButton.NextFocusDownId = _optionsButton.Id;
        if (_variantSpinner is not null) _variantSpinner.NextFocusDownId = _partyButton.Id;
        _partyModel = (Avalonia.Application.Current as UI.App)?.PlaybackViewModel;
        if (_partyModel is not null)
        {
            _partyModel.PropertyChanged += PartyModel_OnChanged;
            _partyModel.PartyFeedback.PropertyChanged += PartyModel_OnChanged;
        }
        return row;
    }

    private void PartyModel_OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_playerPanelsDisposed) return;
        if (ReferenceEquals(sender, _partyModel?.PartyFeedback) ||
            e.PropertyName?.Contains("WatchParty", StringComparison.Ordinal) is true)
        {
            // One party snapshot raises several properties. Update native
            // widgets once per batch, particularly on lower-powered TVs.
            if (Interlocked.Exchange(ref _partyRefreshPending, 1) != 0) return;
            UiDispatcher.UIThread.Post(() =>
            {
                Interlocked.Exchange(ref _partyRefreshPending, 0);
                if (!_playerPanelsDisposed && !IsFinishing && !IsDestroyed)
                {
                    RefreshPlayerActions();
                    RefreshPartyPanel();
                }
            });
        }
    }

    private void RefreshPlayerActions()
    {
        if (_partyStatusLabel is not null)
        {
            _partyStatusLabel.Text = _partyModel?.WatchPartySyncStatus;
            _partyStatusLabel.Visibility = _partyModel?.HasWatchParty is true ? ViewStates.Visible : ViewStates.Gone;
        }
        if (_muteButton is not null)
        {
            _muteButton.Text = UiTextCatalog.Get(_videoView?.IsMuted is true ? "PlayerUnmuteShort" : "PlayerMuteShort");
            _muteButton.ContentDescription = _muteButton.Text;
        }
    }

    private LinearLayout OpenPlayerPanel(string title, View? returnFocus)
    {
        ClosePlayerPanel(restoreFocus: false);
        CancelPlaybackControlsHide();
        _dialogReturnFocus = returnFocus;
        _dialogFocusTargets.Clear();
        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetPadding(Dp(20), Dp(16), Dp(20), Dp(16));
        root.Background = new ColorDrawable(Color.Rgb(15, 21, 32));
        var header = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        var heading = PanelText(title, title: true);
        header.AddView(heading, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));
        _dialogCloseButton = CreatePlaybackControlButton(UiTextCatalog.Get("SettingsClose"),
            (_, _) => ClosePlayerPanel(), 80);
        header.AddView(_dialogCloseButton);
        root.AddView(header);
        _dialogFocusTargets.Add(_dialogCloseButton);
        var scroll = new ScrollView(this) { FillViewport = false };
        _dialogBody = new LinearLayout(this) { Orientation = Orientation.Vertical };
        _dialogBody.SetPadding(0, Dp(12), 0, Dp(12));
        scroll.AddView(_dialogBody);
        root.AddView(scroll, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1));
        var dialog = new Dialog(this);
        dialog.RequestWindowFeature((int)WindowFeatures.NoTitle);
        dialog.SetContentView(root);
        dialog.SetCanceledOnTouchOutside(true);
        dialog.DismissEvent += (_, _) =>
        {
            if (!ReferenceEquals(_playerDialog, dialog)) return;
            _playerDialog = null;
            ClearPlayerPanelViews();
            if (!_playerPanelsDisposed && _isActivityResumed && !IsFinishing)
                ShowPlaybackControls(_dialogReturnFocus);
            dialog.Dispose();
        };
        _playerDialog = dialog;
        dialog.Show();
        dialog.Window?.SetBackgroundDrawable(new ColorDrawable(Color.Transparent));
        dialog.Window?.SetGravity(_isTelevision ? GravityFlags.Right | GravityFlags.CenterVertical : GravityFlags.Center);
        dialog.Window?.SetSoftInputMode(SoftInput.AdjustResize | SoftInput.StateAlwaysHidden);
        SizePlayerPanel();
        return _dialogBody;
    }

    private void SizePlayerPanel()
    {
        var metrics = Resources!.DisplayMetrics!;
        _playerDialog?.Window?.SetLayout(
            Math.Min(Dp(_isTelevision ? 540 : 480), metrics.WidthPixels - Dp(24)),
            (int)(metrics.HeightPixels * 0.86));
    }

    public override void OnConfigurationChanged(global::Android.Content.Res.Configuration newConfig)
    {
        base.OnConfigurationChanged(newConfig);
        SizePlayerPanel();
    }

    private TextView PanelText(string text, bool title = false)
    {
        var label = new TextView(this) { Text = text, TextSize = title ? (_isTelevision ? 24 : 20) : (_isTelevision ? 18 : 16) };
        label.SetTextColor(Color.Rgb(227, 234, 248));
        label.SetPadding(0, Dp(6), 0, Dp(8));
        return label;
    }

    private Button PanelAction(LinearLayout body, string text, EventHandler action)
    {
        var button = CreatePlaybackControlButton(text, action);
        var layout = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        layout.SetMargins(0, Dp(5), 0, Dp(5));
        button.SetMinimumHeight(Dp(_isTelevision ? 56 : 48));
        body.AddView(button, layout);
        _dialogFocusTargets.Add(button);
        return button;
    }

    private void RefreshDialogFocus(View? preferred = null)
    {
        if (_playerDialog is null || _dialogBody is null) return;
        var links = NativePlayerFocusNavigation.Build(_dialogFocusTargets.Select(v =>
            new NativePlayerFocusTarget(v.Id, v.IsShown, v.Enabled)));
        foreach (var link in links)
        {
            var view = _dialogFocusTargets.First(v => v.Id == link.Id);
            view.NextFocusUpId = link.UpId;
            view.NextFocusDownId = link.DownId;
            if (view is Button)
                view.NextFocusLeftId = view.NextFocusRightId = view.Id;
        }
        var focused = _playerDialog.CurrentFocus;
        if (preferred is { Enabled: true, IsShown: true }) preferred.RequestFocus();
        else if (focused is null || !focused.Enabled || !focused.IsShown)
            _dialogCloseButton?.RequestFocus();
    }

    private void ClosePlayerPanel(bool restoreFocus = true)
    {
        _choiceDialog?.Dismiss();
        _choiceDialog = null;
        var dialog = _playerDialog;
        _playerDialog = null;
        dialog?.Dismiss();
        dialog?.Dispose();
        ClearPlayerPanelViews();
        if (restoreFocus && !_playerPanelsDisposed && _isActivityResumed && !IsFinishing)
            ShowPlaybackControls(_dialogReturnFocus);
    }

    private void ClearPlayerPanelViews()
    {
        _partyPanelOpen = false;
        _dialogFocusTargets.Clear();
        _partyMemberRows.Clear();
        _partyMemberLabels = [];
        _dialogBody = _partyMembers = null;
        _dialogCloseButton = _partyCreate = _partyJoin = _partyCopy = _partyResync = _partyOpenContent = _partyLeave = null;
        _partyUnavailable = _partyCode = _partyDetails = _partyError = _partyActionMessage = _optionsMessage = null;
        _partyJoinCode = null;
        _partyHostOnly = null;
    }

    private void ShowPartyPanel()
    {
        var body = OpenPlayerPanel(UiTextCatalog.Get("PlayerPartyAction"), _partyButton);
        _partyPanelOpen = true;
        _partyActionError = null;
        _partyUnavailable = PanelText(UiTextCatalog.Get("WatchPartyUnavailableHint"));
        body.AddView(_partyUnavailable);
        _partyError = PanelText(string.Empty);
        _partyError.SetTextColor(Color.Rgb(255, 156, 156));
        body.AddView(_partyError);
        _partyCreate = PanelAction(body, UiTextCatalog.Get("WatchPartyStartAction"),
            async (_, _) => await RunPartyActionAsync(m => m.StartWatchPartyCommand.ExecuteAsync(null)));
        _partyJoinCode = new EditText(this)
        {
            Id = View.GenerateViewId(),
            Hint = UiTextCatalog.Get("WatchPartyJoinCodeLabel"),
            ContentDescription = UiTextCatalog.Get("WatchPartyJoinCodeLabel"),
            TextSize = _isTelevision ? 22 : 18,
            InputType = InputTypes.ClassText | InputTypes.TextFlagCapCharacters | InputTypes.TextFlagNoSuggestions,
            ImeOptions = ImeAction.Done,
            Text = _partyModel?.WatchPartyJoinCode ?? string.Empty
        };
        _partyJoinCode.SetTextColor(Color.White);
        _partyJoinCode.SetHintTextColor(Color.Rgb(170, 183, 205));
        _partyJoinCode.SetSingleLine(true);
        _partyJoinCode.SetFilters([new InputFilterAllCaps(), new InputFilterLengthFilter(6)]);
        body.AddView(_partyJoinCode, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(56)));
        _dialogFocusTargets.Add(_partyJoinCode);
        _partyJoin = PanelAction(body, UiTextCatalog.Get("WatchPartyJoinAction"), async (_, _) => await JoinPartyFromPlayerAsync());
        _partyJoinCode.TextChanged += (_, _) => RefreshPartyPanel();
        _partyJoinCode.EditorAction += async (_, e) =>
        {
            if (e.ActionId != ImeAction.Done) return;
            e.Handled = true;
            await JoinPartyFromPlayerAsync();
        };
        _partyCode = PanelText(string.Empty, title: true);
        body.AddView(_partyCode);
        _partyCopy = PanelAction(body, UiTextCatalog.Get("WatchPartyCopyCode"), (_, _) => CopyPartyCode());
        _partyActionMessage = PanelText(string.Empty);
        body.AddView(_partyActionMessage);
        _partyDetails = PanelText(string.Empty);
        body.AddView(_partyDetails);
        _partyMembers = new LinearLayout(this) { Orientation = Orientation.Vertical };
        body.AddView(_partyMembers);
        _partyHostOnly = new CheckBox(this)
        {
            Id = View.GenerateViewId(),
            Text = UiTextCatalog.Get("WatchPartyHostControlsLabel"),
            TextSize = _isTelevision ? 18 : 16
        };
        _partyHostOnly.SetTextColor(Color.White);
        _partyHostOnly.SetMinimumHeight(Dp(56));
        _partyHostOnly.Background = CreatePlaybackButtonBackground();
        _partyHostOnly.CheckedChange += async (_, e) =>
        {
            if (!_refreshingPartyPanel && _partyModel is { } model)
            {
                var value = e.IsChecked;
                await UiDispatcher.UIThread.InvokeAsync(() => model.WatchPartyHostControlsOnly = value);
            }
        };
        body.AddView(_partyHostOnly);
        _dialogFocusTargets.Add(_partyHostOnly);
        _partyResync = PanelAction(body, UiTextCatalog.Get("WatchPartyResync"),
            async (_, _) => await RunPartyActionAsync(m => m.ResynchronizeWatchPartyCommand.ExecuteAsync(null)));
        _partyOpenContent = PanelAction(body, UiTextCatalog.Get("WatchPartyOpenContentAction"),
            async (_, _) =>
            {
                await RunPartyActionAsync(m => m.OpenWatchPartyContentCommand.ExecuteAsync(null));
                // The command opens details/search in Avalonia's activity.
                // Return there so the viewer can actually choose that content.
                if (!_playerPanelsDisposed && !IsFinishing) Finish();
            });
        _partyLeave = PanelAction(body, UiTextCatalog.Get("WatchPartyLeaveAction"),
            async (_, _) => await RunPartyActionAsync(m => m.LeaveWatchPartyCommand.ExecuteAsync(null)));
        RefreshPartyPanel();
        RefreshDialogFocus(_partyModel?.HasWatchParty is true ? _partyCopy : _partyCreate);
    }

    private async Task JoinPartyFromPlayerAsync()
    {
        var code = _partyJoinCode?.Text?.Trim() ?? string.Empty;
        if (code.Length != 6) return;
        (GetSystemService(InputMethodService) as InputMethodManager)?.HideSoftInputFromWindow(_partyJoinCode?.WindowToken, 0);
        await RunPartyActionAsync(m =>
        {
            m.WatchPartyJoinCode = code;
            return m.JoinWatchPartyCommand.ExecuteAsync(null);
        });
    }

    private async Task RunPartyActionAsync(Func<MainViewModel, Task> action)
    {
        if (_partyActionBusy || _partyModel is not { } model || model.IsWatchPartyBusy) return;
        _partyActionBusy = true;
        _partyActionError = null;
        RefreshPartyPanel();
        try
        {
            await UiDispatcher.UIThread.InvokeAsync(() => action(model));
        }
        catch (Exception exception)
        {
            _partyActionError = exception.Message;
        }
        finally
        {
            _partyActionBusy = false;
            if (!_playerPanelsDisposed && !IsFinishing && !IsDestroyed)
            {
                RefreshPartyPanel();
                RefreshPlayerActions();
            }
        }
    }

    private void RefreshPartyPanel()
    {
        if (!_partyPanelOpen || _partyHostOnly is null) return;
        _refreshingPartyPanel = true;
        try
        {
            var available = _partyModel?.SupportsWatchParties is true;
            var joined = _partyModel?.HasWatchParty is true;
            var busy = _partyActionBusy || _partyModel?.IsWatchPartyBusy is true;
            SetPanelVisibility(_partyUnavailable, !available);
            SetPanelVisibility(_partyCreate, available && !joined);
            SetPanelVisibility(_partyJoinCode, available && !joined);
            SetPanelVisibility(_partyJoin, available && !joined);
            _partyCreate!.Enabled = !busy;
            _partyJoin!.Enabled = !busy && _partyJoinCode?.Text?.Trim().Length == 6;
            _partyJoinCode!.Enabled = !busy;
            foreach (var view in new View?[] { _partyCode, _partyCopy, _partyDetails, _partyMembers, _partyResync, _partyLeave })
                SetPanelVisibility(view, joined);
            _partyCode!.Text = UiTextCatalog.Get("WatchPartyCodeShareLabel") + "\n" + _partyModel?.WatchPartyCode;
            _partyDetails!.Text = _partyModel?.WatchPartyContentLabel + "\n" + _partyModel?.WatchPartySyncStatus;
            RefreshPartyMembers();
            _partyResync!.Enabled = !busy && _partyModel?.CanResynchronizeWatchParty is true;
            _partyLeave!.Enabled = !busy;
            _partyCopy!.Enabled = !busy;
            SetPanelVisibility(_partyOpenContent, joined && _partyModel?.WatchPartyHasContent is true);
            _partyOpenContent!.Enabled = !busy;
            SetPanelVisibility(_partyHostOnly, _partyModel?.ShowWatchPartyHostControlsToggle is true);
            _partyHostOnly.Enabled = !busy;
            _partyHostOnly.Checked = _partyModel?.WatchPartyHostControlsOnly is true;
            _partyError!.Text = _partyActionError ?? _partyModel?.PartyFeedback.Message;
            RefreshDialogFocus();
        }
        finally { _refreshingPartyPanel = false; }
    }

    private void RefreshPartyMembers()
    {
        var labels = _partyModel?.WatchPartyMemberLabels ?? [];
        if (_partyMembers is null || _partyMemberLabels.SequenceEqual(labels)) return;
        var previousFocus = _partyMemberRows.FindIndex(row => row.HasFocus);
        foreach (var row in _partyMemberRows) _dialogFocusTargets.Remove(row);
        _partyMembers.RemoveAllViews();
        _partyMemberRows.Clear();
        _partyMemberLabels = labels.ToArray();
        _partyMembers.AddView(PanelText(UiTextCatalog.Get("WatchPartyMembersTitle")));
        var focusIndex = _dialogFocusTargets.IndexOf(_partyCopy!) + 1;
        foreach (var label in labels)
        {
            // Each member is a separate scroll target: a group of 16 must
            // remain readable with only Up/Down on a small television.
            var row = PanelText(label);
            row.Id = View.GenerateViewId();
            row.Focusable = true;
            row.SetMinimumHeight(Dp(48));
            row.Background = CreatePlaybackButtonBackground();
            row.NextFocusLeftId = row.NextFocusRightId = row.Id;
            _partyMembers.AddView(row);
            _partyMemberRows.Add(row);
            _dialogFocusTargets.Insert(focusIndex++, row);
        }
        if (previousFocus >= 0 && _partyMemberRows.Count > 0)
            _partyMemberRows[Math.Min(previousFocus, _partyMemberRows.Count - 1)].RequestFocus();
    }

    private static void SetPanelVisibility(View? view, bool visible)
    {
        if (view is not null) view.Visibility = visible ? ViewStates.Visible : ViewStates.Gone;
    }

    private void CopyPartyCode()
    {
        try
        {
            if (_partyModel is not { HasWatchParty: true } model) return;
            if (GetSystemService(ClipboardService) is not global::Android.Content.ClipboardManager clipboard)
                throw new InvalidOperationException();
            clipboard.PrimaryClip = ClipData.NewPlainText(UiTextCatalog.Get("WatchPartyCodeShareLabel"), model.WatchPartyCode);
            _partyActionMessage!.Text = UiTextCatalog.Get("WatchPartyCodeCopied");
        }
        catch (Exception) { _partyActionMessage!.Text = UiTextCatalog.Get("WatchPartyCopyFailed"); }
    }

    private void DisposePlayerPanels()
    {
        _playerPanelsDisposed = true;
        if (_partyModel is not null)
        {
            _partyModel.PropertyChanged -= PartyModel_OnChanged;
            _partyModel.PartyFeedback.PropertyChanged -= PartyModel_OnChanged;
        }
        ClosePlayerPanel(restoreFocus: false);
        _partyModel = null;
    }
}
