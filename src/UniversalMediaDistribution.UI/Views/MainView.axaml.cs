using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Controls.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UniversalMediaDistribution.UI.Diagnostics;
using UniversalMediaDistribution.UI.Localization;
using UniversalMediaDistribution.UI.ViewModels;

namespace UniversalMediaDistribution.UI.Views;

public partial class MainView : UserControl
{
    // Longer than any real press-and-release, shorter than the pause before a
    // viewer presses Center again after returning from another screen.
    private const long TelevisionActivationHoldMilliseconds = 700;

    // Android repeats a held key every few tens of milliseconds. Anything that
    // close to the previous press is the same press, not a new one.
    private const long TelevisionAutoRepeatMilliseconds = 250;

    /// <summary>
    /// Slack kept between a revealed control and the edge of its viewport, so
    /// the poster that just grew on focus is not left flush against the side.
    /// </summary>
    private const double ScrollRevealMargin = 36;

    /// <summary>
    /// How much of a shelf one chevron press travels. Short of a full viewport
    /// so the card at the edge stays partly on screen as an anchor.
    /// </summary>
    private const double ShelfPageFraction = 0.85;

    /// <summary>
    /// Style class on the per-provider buttons of an entry's source picker.
    /// Focus handling keys off this to tell a picker answer apart from the
    /// entry's own actions, so it has to match MainView.axaml.
    /// </summary>
    internal const string ProviderChoiceClass = "provider-choice";

    private bool? _usesCompactLayout;
    private bool? _usesNarrowLayout;
    private bool _isTelevision;
    private bool _catalogToolsExpanded;
    private bool _liveTvToolsExpanded;
    private bool _isLiveTvRemoteListActive;
    private Control? _lastFocusedLiveTvFilter;
    private TextBox? _activeTelevisionTextBox;
    private readonly Dictionary<TextBox, bool>
        _televisionTextBoxReadOnlyStates = [];
    private bool _isActivatingTelevisionTextInput;
    private IInputPane? _televisionInputPane;
    private bool _televisionKeyboardConfirmed;
    private long _televisionTextInputStartedMilliseconds;
    private Key? _suppressedTelevisionActivationKey;
    private long _televisionActivationStartedMilliseconds;
    private long _lastTelevisionKeyDownMilliseconds;
    private int _televisionActivationGeneration;
    private bool _focusInitialTelevisionPagePending;
    private bool _televisionPageContentReady;
    private readonly bool _traceTelevisionInput =
        Environment.GetEnvironmentVariable("UMD_TV_INPUT_TRACE") == "1";
    private WeakReference<Control>? _lastFocusedTelevisionPageControl;
    private Guid? _lastFocusedTelevisionCatalogItemId;
    private bool _restoreTelevisionCatalogFocus;
    private Guid? _televisionDetailsItemId;
    private WeakReference<Control>? _lastFocusedTelevisionDetailsControl;
    private string? _lastFocusedTelevisionEntryKey;
    private string? _lastFocusedTelevisionEntryAction;
    // The menu overlays a fixed-width rail so expanding it never reflows the
    // content that owns the next D-pad target.
    private bool _isTelevisionMenuExpanded = true;
    private int _televisionContentFocusGeneration;
    private bool _isTouchPrimary;
    private MainViewModel? _navigationViewModel;
    private ImageReaderViewModel? _readerViewModel;
    private ScrollViewer? _readerScrollViewer;
    private double _lastReaderOffsetY;
    private bool _hasReaderOffset;
    private ReaderViewportAnchor? _readerViewportAnchor;
    private bool _readerViewportRestoreScheduled;
    private int _readerViewportAnchorGeneration;
    private bool _readerControlsRevealed;
    private bool _readerIsAtEnd;
    private double _readerZoomFactor = 1;
    private bool _playerFullscreenApplied;
    private bool _isMenuDrawerOpen;
    private int _providerChoiceRevealGeneration;
    private WindowState? _windowStateBeforePlayerFullscreen;
    private readonly DispatcherTimer _readerControlsHideTimer;
    private readonly DispatcherTimer _readerToolbarHideTimer;

    private static readonly FilePickerFileType CatalogJsonFileType = new(
        UiTextCatalog.Get("CatalogJsonFileType"))
    {
        Patterns = ["*.umd-catalog.json", "*.json"],
        MimeTypes = ["application/json"]
    };

    private static readonly FilePickerFileType FullBackupFileType = new(
        UiTextCatalog.Get("FullBackupFileType"))
    {
        Patterns = ["*.umdbackup"],
        MimeTypes = ["application/vnd.sqlite3", "application/octet-stream"]
    };

    private static readonly FilePickerFileType TorrentFileType = new(
        UiTextCatalog.Get("TorrentFileType"))
    {
        Patterns = ["*.torrent"],
        MimeTypes = ["application/x-bittorrent", "application/octet-stream"]
    };

    public MainView()
    {
        InitializeComponent();
        InitializeReaderMotion();

        // Tunnel: the reader's ScrollViewer handles the wheel on the way back
        // up, so a bubbling handler would never see a Control+wheel notch.
        AddHandler(
            PointerWheelChangedEvent,
            MainView_OnPointerWheelChanged,
            RoutingStrategies.Tunnel);
        _readerControlsHideTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2.4)
        };
        _readerControlsHideTimer.Tick += (_, _) =>
        {
            _readerControlsHideTimer.Stop();
            if (!_readerIsAtEnd)
            {
                _readerControlsRevealed = false;
                UpdateReaderControlVisibility();
            }
        };
        _readerToolbarHideTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(4)
        };
        _readerToolbarHideTimer.Tick += (_, _) =>
        {
            _readerToolbarHideTimer.Stop();
            if (!_isTelevision && !ReaderSettingsPanel.IsVisible)
            {
                ReaderToolbar.IsVisible = false;
            }
        };
        ReaderPages.AddHandler(
            ScrollViewer.ScrollChangedEvent,
            ReaderPages_OnScrollChanged);
        Sidebar.AddHandler(
            Button.ClickEvent,
            Sidebar_OnNavigationInvoked,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        LiveTvFiltersPanel.AddHandler(
            GotFocusEvent,
            LiveTvFiltersPanel_OnGotFocus,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        // Registered on the shell, not on MainContent: the settings layer is a
        // sibling of the page, and a field that never reaches this handler is
        // never disarmed on the way out — so re-focusing it summons the
        // on-screen keyboard again.
        AddHandler(
            LostFocusEvent,
            TelevisionTextBox_OnLostFocus,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        AddHandler(
            GotFocusEvent,
            Shell_OnGotFocus,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        AddHandler(
            KeyDownEvent,
            MainView_OnKeyDown,
            RoutingStrategies.Tunnel);
        AddHandler(
            KeyUpEvent,
            MainView_OnKeyUp,
            RoutingStrategies.Tunnel);
        SizeChanged += (_, eventArgs) =>
        {
            UpdateCompactLayout(eventArgs.NewSize.Width);
            UpdateDonationLayout(eventArgs.NewSize);
        };
        DetachedFromVisualTree += (_, _) => EndTelevisionTextInput();
        DataContextChanged += (_, _) => AttachNavigationViewModel();

        // Shelves scroll sideways and the page scrolls down, so whatever the
        // remote just landed on has to be dragged into view on both axes.
        // Directional focus alone does not scroll: without this, travelling
        // right along a row moves focus onto cards that stay off screen.
        AddHandler<FocusChangedEventArgs>(
            GotFocusEvent,
            OnControlGotFocus,
            RoutingStrategies.Bubble);

        // Shelf chevrons and their enabled state are wired here rather than per
        // shelf, because every shelf is created from one DataTemplate and there
        // is no code-behind inside a template to hang handlers on.
        AddHandler(
            Button.ClickEvent,
            OnShelfNavigationClick,
            RoutingStrategies.Bubble);
        AddHandler(
            ScrollViewer.ScrollChangedEvent,
            OnShelfScrollChanged,
            RoutingStrategies.Bubble);

        Loaded += async (_, _) =>
        {
            AttachSafeAreaInsets();
            if (DataContext is MainViewModel viewModel)
            {
                AttachNavigationViewModel();
                _isTouchPrimary = viewModel.IsTouchPrimary;
                ApplyTelevisionMode(viewModel.IsTelevision);
                UpdateCompactLayout(Bounds.Width);
                if (_isTelevision)
                {
                    Dispatcher.UIThread.Post(
                        () => HomeNavButton.Focus(NavigationMethod.Directional),
                        DispatcherPriority.Loaded);

                    // Android TV sends the activity's initial focus event as the
                    // first frame is presented. Let rendering and input finish
                    // before catalog initialization adds the home shelves.
                    await Dispatcher.UIThread.InvokeAsync(
                        static () => { },
                        DispatcherPriority.Background);
                }

                await viewModel.InitializeAsync();
                if (viewModel.IsDonationPromptVisible)
                {
                    Dispatcher.UIThread.Post(
                        () => TryFocusDonationPrompt(),
                        DispatcherPriority.Loaded);
                }
            }
        };
    }

    /// <summary>
    /// Reveals whatever the keyboard or the remote just moved onto.
    /// <para>
    /// Only those. Scrolling on every focus change meant the page moved for
    /// reasons nobody asked for: a control that disables itself while a request
    /// runs hands focus to its neighbour, and a list that repopulates as a page
    /// finishes loading hands it to whatever replaced the old element — each of
    /// which dragged the reader somewhere else mid-read. A pointer never needs
    /// it either, since what was clicked is already under the cursor.
    /// </para>
    /// </summary>
    private void OnControlGotFocus(
        object? sender,
        FocusChangedEventArgs eventArgs)
    {
        if (eventArgs.Source is Control focused &&
            eventArgs.NavigationMethod is
                NavigationMethod.Tab or NavigationMethod.Directional)
        {
            TraceTelevisionInput("focus");
            RememberTelevisionPageFocus(focused);
            ScrollControlIntoView(focused);
        }
    }

    private void TraceTelevisionInput(string action)
    {
        if (!_traceTelevisionInput || !_isTelevision)
        {
            return;
        }

        var focused = TopLevel.GetTopLevel(this)?.FocusManager?
            .GetFocusedElement() as Control;
        ApplicationDiagnosticLog.Information("tv.input",
            $"action={action}; focus={focused?.GetType().Name}/{focused?.Name}; " +
            $"details={_navigationViewModel?.IsDetailsOpen}; " +
            $"catalog={_navigationViewModel?.IsCatalogOpen}; " +
            $"busy={_navigationViewModel?.IsBusy}; menu={_isTelevisionMenuExpanded}; " +
            $"ready={_televisionPageContentReady}; pending={_focusInitialTelevisionPagePending}; " +
            $"suppressed={_suppressedTelevisionActivationKey}; generation={_televisionContentFocusGeneration}");
    }

    private void RememberTelevisionPageFocus(Control focused)
    {
        if (_isTelevision &&
            _navigationViewModel?.IsDetailsOpen is true &&
            DetailsScroll.IsVisualAncestorOf(focused))
        {
            _lastFocusedTelevisionDetailsControl = new WeakReference<Control>(focused);
            _lastFocusedTelevisionEntryKey = FindDetailsEntry(focused)?.Identity.StableKey;
            _lastFocusedTelevisionEntryAction = (focused as Button)?.Content as string;
            return;
        }

        if (!_isTelevision ||
            _navigationViewModel?.IsDetailsOpen is true ||
            !MainContent.IsVisualAncestorOf(focused))
        {
            return;
        }

        var target = FindFocusableControl(focused);
        if (target is null)
        {
            return;
        }

        _lastFocusedTelevisionPageControl = new WeakReference<Control>(target);
        if (target.Classes.Contains("catalog-card-button") &&
            target.DataContext is CatalogCardViewModel card)
        {
            _lastFocusedTelevisionCatalogItemId = card.Id;
            _restoreTelevisionCatalogFocus = false;
        }
    }

    private static void OnShelfNavigationClick(
        object? sender,
        RoutedEventArgs eventArgs)
    {
        if (eventArgs.Source is not Button button ||
            FindShelf(button) is not { } shelf)
        {
            return;
        }

        var forward = button.Classes.Contains("shelf-next");
        if (!forward && !button.Classes.Contains("shelf-previous"))
        {
            return;
        }

        // Just under a full viewport, so the poster at the edge stays partly
        // visible and the viewer keeps their place in the row.
        var step = Math.Max(1, shelf.Viewport.Width * ShelfPageFraction);
        var target = Math.Clamp(
            shelf.Offset.X + (forward ? step : -step),
            0,
            Math.Max(0, shelf.Extent.Width - shelf.Viewport.Width));
        shelf.Offset = shelf.Offset.WithX(target);
        eventArgs.Handled = true;
    }

    private static void OnShelfScrollChanged(
        object? sender,
        ScrollChangedEventArgs eventArgs)
    {
        if (eventArgs.Source is ScrollViewer shelf &&
            shelf.Classes.Contains("shelf"))
        {
            UpdateShelfNavigation(shelf);
        }
    }

    /// <summary>
    /// Greys out the chevron that has nowhere left to go, so a row that fits
    /// entirely on screen shows neither.
    /// </summary>
    private static void UpdateShelfNavigation(ScrollViewer shelf)
    {
        if (shelf.Parent is not Grid host)
        {
            return;
        }

        var maximum = Math.Max(0, shelf.Extent.Width - shelf.Viewport.Width);
        foreach (var button in host.Children.OfType<Button>())
        {
            if (button.Classes.Contains("shelf-previous"))
            {
                button.IsEnabled = shelf.Offset.X > 0.5;
            }
            else if (button.Classes.Contains("shelf-next"))
            {
                button.IsEnabled = shelf.Offset.X < maximum - 0.5;
            }
        }
    }

    /// <summary>
    /// Settles every chevron once a new set of rows has been laid out.
    /// <para>
    /// A chevron is only ever corrected by <see cref="ScrollViewer.ScrollChanged"/>,
    /// which a row that fits entirely on screen has no reason to raise — and a
    /// genre row often holds three cards. Without this, such a row would offer
    /// two live chevrons that scroll nowhere.
    /// </para>
    /// </summary>
    private void RefreshShelfNavigationAfterLayout() =>
        ForEachShelfAfterLayout(UpdateShelfNavigation);

    /// <summary>
    /// Sends every row back to its first card. A shelf container outlives the
    /// page when the same row exists on both, so without this a row can open
    /// already scrolled into titles the viewer has not seen — and on a remote,
    /// initial focus then lands on whichever card that offset happened to
    /// leave realized.
    /// </summary>
    private void RewindShelvesAfterLayout() =>
        ForEachShelfAfterLayout(static shelf =>
        {
            shelf.Offset = shelf.Offset.WithX(0);
            UpdateShelfNavigation(shelf);
        });

    private void ForEachShelfAfterLayout(Action<ScrollViewer> apply) =>
        Dispatcher.UIThread.Post(
            () =>
            {
                foreach (var shelf in MainContent
                             .GetVisualDescendants()
                             .OfType<ScrollViewer>()
                             .Where(static scroll =>
                                 scroll.Classes.Contains("shelf")))
                {
                    apply(shelf);
                }
            },
            DispatcherPriority.Background);

    private static ScrollViewer? FindShelf(Button button) =>
        button.Parent is Grid host
            ? host.Children
                .OfType<ScrollViewer>()
                .FirstOrDefault(static child => child.Classes.Contains("shelf"))
            : null;

    internal void UseParentTelevisionKeyRouting()
    {
        RemoveHandler(KeyDownEvent, MainView_OnKeyDown);
        RemoveHandler(KeyUpEvent, MainView_OnKeyUp);
    }

    internal void HandleTelevisionKeyDown(KeyEventArgs eventArgs) =>
        MainView_OnKeyDown(this, eventArgs);

    internal void HandleTelevisionKeyUp(KeyEventArgs eventArgs) =>
        MainView_OnKeyUp(this, eventArgs);

    /// <summary>
    /// Android draws behind the status and navigation bars, so the content has
    /// to be inset by the safe area or the bottom navigation would sit
    /// underneath the system gesture bar.
    /// </summary>
    /// <remarks>
    /// The inset is deliberately left to the framework. Setting it by hand from
    /// <c>IInsetsManager.SafeAreaPadding</c> does not work here because that
    /// property reports physical pixels while <c>RenderScaling</c> still reads
    /// 1: on a 420dpi phone the 136px status bar inset became a 136 unit margin
    /// inside a 914 unit tall layout and cost about a sixth of the screen at
    /// each end.
    /// </remarks>
    private void AttachSafeAreaInsets() =>
        TopLevel.SetAutoSafeAreaPadding(this, true);

    private void UpdateCompactLayout(double width)
    {
        if (_playerFullscreenApplied)
        {
            return;
        }

        const double compactBreakpoint = 1040;
        const double narrowBreakpoint = 720;
        var useCompactLayout =
            !_isTelevision && width > 0 && width < compactBreakpoint;
        var useNarrowLayout =
            !_isTelevision && width > 0 && width < narrowBreakpoint;
        if (_usesCompactLayout == useCompactLayout &&
            _usesNarrowLayout == useNarrowLayout)
        {
            return;
        }

        _usesCompactLayout = useCompactLayout;
        _usesNarrowLayout = useNarrowLayout;
        ApplyGridDefinitions(useCompactLayout, useNarrowLayout);
        SetShellClass("compact", useCompactLayout);
        SetShellClass("narrow", useNarrowLayout);
        // Fingers get larger targets on real touch devices and in phone-sized
        // windows (a browser tab on a handset, for example).
        SetShellClass("touch", UsesTouchLayout);
        SetShellClass("drawer-nav", UsesDrawerNavigation);
        if (!UsesDrawerNavigation)
        {
            _isMenuDrawerOpen = false;
        }

        ApplyMenuDrawerPresentation();
        ApplyPageOptionsPresentation();

        // Crossing the breakpoint changes whether the filters collapse at all.
        UpdateToolPanels();
    }

    private bool UsesTouchLayout =>
        !_isTelevision && (_isTouchPrimary || _usesNarrowLayout is true);

    /// <summary>
    /// On a handset the navigation collapses into a drawer that slides over the
    /// page, leaving the bottom edge free for the options of the open page.
    /// </summary>
    private bool UsesDrawerNavigation =>
        _usesCompactLayout is true && UsesTouchLayout;

    /// <summary>
    /// A small desktop window still keeps the icon bar across the top.
    /// </summary>
    private bool UsesTopNavigationBar =>
        _usesCompactLayout is true && !UsesTouchLayout;

    private void SetShellClass(string className, bool isPresent)
    {
        var hasClass = Shell.Classes.Contains(className);
        if (isPresent && !hasClass)
        {
            Shell.Classes.Add(className);
        }
        else if (!isPresent && hasClass)
        {
            Shell.Classes.Remove(className);
        }
    }

    /// <summary>
    /// True while a chapter, book, or player owns the screen. Those surfaces
    /// carry their own back button and options, so the menu stands down.
    /// </summary>
    private bool IsImmersiveSurfaceOpen =>
        _navigationViewModel is
    {
        IsReaderOpen: true
    } or
    {
        IsEpubReaderOpen: true
    } or
    {
        IsEmbeddedMediaOpen: true
    };

    /// <summary>
    /// Expands the television menu when it receives focus and collapses it
    /// when content takes focus. Its fixed rail keeps page geometry stable.
    /// </summary>
    private void Shell_OnGotFocus(object? sender, RoutedEventArgs eventArgs)
    {
        if (!_isTelevision)
        {
            return;
        }

        var focusedElement = eventArgs.Source as Visual;
        if (_navigationViewModel?.IsDonationPromptVisible is true &&
            (focusedElement is null ||
             !DonationPrompt.IsVisualAncestorOf(focusedElement)))
        {
            TryFocusDonationPrompt();
            return;
        }

        if (_navigationViewModel?.IsUpdatePromptVisible is true &&
            (focusedElement is null ||
             !UpdatePrompt.IsVisualAncestorOf(focusedElement)))
        {
            FocusUpdatePromptForTelevision();
            return;
        }

        var focusIsInMenu =
            focusedElement is not null &&
            Sidebar.IsVisualAncestorOf(focusedElement);
        if (focusIsInMenu)
        {
            SetTelevisionMenuExpanded(true);
            return;
        }

        // Leaving the menu closes it. Without this the drawer only ever
        // widened — the chevron was the single thing in the app that made it
        // narrow again — so it sat open over the page for the rest of the
        // session, which is a third of the screen on a television.
        if (focusedElement is not null &&
            (MainContent.IsVisualAncestorOf(focusedElement) ||
             SettingsOverlay.IsVisualAncestorOf(focusedElement)))
        {
            SetTelevisionMenuExpanded(false);
        }
    }

    private void SetTelevisionMenuExpanded(bool isExpanded)
    {
        if (_isTelevisionMenuExpanded == isExpanded)
        {
            return;
        }

        _isTelevisionMenuExpanded = isExpanded;
        ApplyTelevisionMenuPresentation();
    }

    private void NavigationToggleButton_OnClick(
        object? sender,
        RoutedEventArgs eventArgs)
    {
        if (_isTelevision)
        {
            SetTelevisionMenuExpanded(false);
        }
    }

    private void ApplyTelevisionMenuPresentation() =>
        SetShellClass(
            "tv-rail",
            _isTelevision && !_isTelevisionMenuExpanded);

    /// <summary>
    /// Moves focus into the menu when the reader presses left with nothing to
    /// the left of them, which is how a television menu is expected to open.
    /// </summary>
    private bool TryOpenTelevisionMenu()
    {
        if (!_isTelevision)
        {
            return false;
        }

        _lastFocusedLiveTvFilter = null;
        SetLiveTvRemoteListActive(false);
        SetTelevisionMenuExpanded(true);
        var active = Sidebar.GetVisualDescendants()
            .OfType<Button>()
            .FirstOrDefault(static button => button.Classes.Contains("active"));
        var target = active ?? HomeNavButton;
        return target.Focus(NavigationMethod.Directional);
    }

    private bool TryMoveTelevisionMenuFocus(
        Visual focusedElement,
        NavigationDirection direction)
    {
        var current = focusedElement as Button ??
            focusedElement.GetVisualAncestors()
                .OfType<Button>()
                .FirstOrDefault();
        Button[] menuButtons =
        [
            // The collapse arrow is part of the menu column, so Up from the
            // first entry has to reach it. Without it the rail could only ever
            // be expanded on a television, never hidden again.
            NavigationToggleButton,
            HomeNavButton,
            MoviesNavButton,
            AnimeNavButton,
            BooksNavButton,
            LiveTvNavButton,
            UserListsNavButton,
            // Watch together on a television, friends elsewhere; the loop
            // below already skips it while it is hidden.
            FriendsNavButton,
            DownloadsNavButton,
            SettingsNavButton
        ];
        var step = direction is NavigationDirection.Down ? 1 : -1;
        var currentIndex = Array.IndexOf(menuButtons, current);
        var candidateIndex = currentIndex < 0
            ? step > 0 ? 0 : menuButtons.Length - 1
            : currentIndex + step;
        while (candidateIndex >= 0 &&
               candidateIndex < menuButtons.Length)
        {
            var candidate = menuButtons[candidateIndex];
            if (candidate.IsVisible &&
                candidate.IsEnabled &&
                candidate.Focusable)
            {
                return candidate.Focus(NavigationMethod.Directional);
            }

            candidateIndex += step;
        }

        // Keep focus on the first or last visible entry at the menu edge.
        return true;
    }

    private bool IsFocusInsideEmbeddedPlayer() =>
        TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement()
            is Visual focused &&
        (ReferenceEquals(focused, EmbeddedPlayerContent) ||
         EmbeddedPlayerContent.IsVisualAncestorOf(focused));

    /// <summary>
    /// Hands the remote to the embedded player when it opens on a television,
    /// where there is no pointer to click into it.
    /// </summary>
    private void FocusEmbeddedPlayerForTelevision()
    {
        if (!_isTelevision ||
            _navigationViewModel?.IsEmbeddedMediaOpen is not true ||
            _navigationViewModel?.IsDonationPromptVisible is true ||
            _navigationViewModel?.IsUpdatePromptVisible is true)
        {
            return;
        }

        Dispatcher.UIThread.Post(
            () =>
            {
                if (_navigationViewModel?.IsEmbeddedMediaOpen is true)
                {
                    EmbeddedPlayerContent.Focus(NavigationMethod.Directional);
                }
            },
            DispatcherPriority.Loaded);
    }

    private void ApplyMenuDrawerPresentation()
    {
        var useDrawer = UsesDrawerNavigation && !_playerFullscreenApplied;
        var showMenuButton = useDrawer && !IsImmersiveSurfaceOpen;
        MenuButton.IsVisible = showMenuButton;
        MenuScrim.IsVisible = useDrawer && _isMenuDrawerOpen;
        Sidebar.IsVisible = !useDrawer || _isMenuDrawerOpen;
        if (!showMenuButton && _isMenuDrawerOpen)
        {
            _isMenuDrawerOpen = false;
            MenuScrim.IsVisible = false;
            Sidebar.IsVisible = !useDrawer;
        }

        // Leave room for the floating button instead of letting the page slide
        // underneath it.
        MainContent.Margin = showMenuButton
            ? new Thickness(0, 56, 0, 0)
            : new Thickness();
    }

    private void SetMenuDrawerOpen(bool isOpen)
    {
        if (_isMenuDrawerOpen == isOpen)
        {
            return;
        }

        _isMenuDrawerOpen = isOpen;
        ApplyMenuDrawerPresentation();
    }

    private void MenuButton_OnClick(object? sender, RoutedEventArgs eventArgs) =>
        SetMenuDrawerOpen(!_isMenuDrawerOpen);

    private void MenuScrim_OnPointerPressed(
        object? sender,
        PointerPressedEventArgs eventArgs)
    {
        eventArgs.Handled = true;
        SetMenuDrawerOpen(false);
    }

    /// <summary>
    /// Choosing a destination closes the drawer, the way a phone menu should.
    /// </summary>
    private void Sidebar_OnNavigationInvoked(
        object? sender,
        RoutedEventArgs eventArgs)
    {
        if (UsesDrawerNavigation && eventArgs.Source is Button)
        {
            SetMenuDrawerOpen(false);
        }
    }

    /// <summary>
    /// On a handset the page options of an open chapter move out of the top
    /// toolbar into a translucent bar at the bottom edge, within thumb reach.
    /// </summary>
    private void ApplyPageOptionsPresentation()
    {
        var useBottomOptions = UsesTouchLayout;
        MoveControl(ReaderSettingsButton, useBottomOptions ? ReaderTouchOptionsHost : ReaderHeaderGrid);
        UpdateReaderSettingsLayout();
        MoveControl(
            EpubFontControls,
            useBottomOptions ? EpubPageOptionsPanel : EpubHeaderGrid);
        ReaderPageOptionsBar.IsVisible = false; // Settings now live in the compact reader dock.
        EpubPageOptionsBar.IsVisible = useBottomOptions;
        if (useBottomOptions)
        {
            ReaderModeControls.Margin = new Thickness(0);
            ReaderZoomControls.Margin = new Thickness(0);
            EpubFontControls.Margin = new Thickness(0);
            UpdatePageOptionsOffset();
        }
    }

    /// <summary>
    /// Keeps the floating bars clear of the paged reader's own footer.
    /// <para>
    /// This positions the stack, not the bars inside it. Setting each bar's
    /// margin here is what used to drop the page options on top of the chapter
    /// navigation: both were placed against the same edge from two different
    /// places, and a margin set here also outranks the style that was meant to
    /// hold them apart.
    /// </para>
    /// </summary>
    private void UpdatePageOptionsOffset() =>
        ReaderBottomBars.Margin = _readerViewModel?.IsPaged is true
            ? new Thickness(14, 0, 14, 78)
            : new Thickness(14, 0, 14, 16);

    private static void MoveControl(Control control, Panel destination)
    {
        if (ReferenceEquals(control.Parent, destination))
        {
            return;
        }

        if (control.Parent is Panel currentPanel)
        {
            currentPanel.Children.Remove(control);
        }

        destination.Children.Add(control);
    }

    private void UpdatePlayerFullscreenPresentation()
    {
        var shouldBeFullscreen = (_readerFullscreenRequested && _navigationViewModel?.IsReaderOpen is true) ||
            _navigationViewModel is
            {
                IsEmbeddedMediaOpen: true,
                IsPlayerFullscreen: true
            };
        ApplyPlayerFullscreenPresentation(shouldBeFullscreen);
        UpdateReaderFullscreenLabel();
    }

    internal void ApplyPlayerFullscreenPresentation(bool shouldBeFullscreen)
    {
        if (shouldBeFullscreen == _playerFullscreenApplied)
        {
            return;
        }

        if (shouldBeFullscreen)
        {
            _playerFullscreenApplied = true;
            SetShellClass("player-fullscreen", true);
            Sidebar.IsVisible = false;
            // A fullscreen video fills the display, system bars included.
            TopLevel.SetAutoSafeAreaPadding(this, false);
            Shell.ColumnDefinitions = new ColumnDefinitions("*");
            Shell.RowDefinitions = new RowDefinitions("*");
            PlaceInGrid(MainContent, 0, 0);
            Grid.SetColumnSpan(MainContent, 1);
            UpdatePlayerRowHeights(fullscreen: true);
            EmbeddedPlayerToolbar.IsVisible = false;
            EmbeddedPlayerServers.IsVisible = false;

            if (TopLevel.GetTopLevel(this) is Window window)
            {
                _windowStateBeforePlayerFullscreen = window.WindowState;
                window.WindowState = WindowState.FullScreen;
            }

            return;
        }

        _playerFullscreenApplied = false;
        SetShellClass("player-fullscreen", false);
        UpdatePlayerRowHeights(fullscreen: false);
        EmbeddedPlayerToolbar.IsVisible = true;
        // The footer follows the view model: it is only there when the title
        // actually offers more than one server.
        EmbeddedPlayerServers.IsVisible =
            _navigationViewModel?.HasActiveVideoServers is true;
        Sidebar.IsVisible = true;
        TopLevel.SetAutoSafeAreaPadding(this, true);
        _usesCompactLayout = null;
        _usesNarrowLayout = null;
        UpdateCompactLayout(Bounds.Width);
        // Restore the shell before Win32 synchronously measures it on resize.
        if (TopLevel.GetTopLevel(this) is Window restoredWindow &&
            _windowStateBeforePlayerFullscreen is { } previousState)
        {
            restoredWindow.WindowState = previousState;
        }

        _windowStateBeforePlayerFullscreen = null;
        Dispatcher.UIThread.Post(
            () => (_navigationViewModel?.IsReaderOpen is true ? ReaderFullscreenButton : PlayerFullscreenButton).Focus(
                NavigationMethod.Tab),
            DispatcherPriority.Loaded);
    }

    private void UpdatePlayerRowHeights(bool fullscreen)
    {
        // Keep the XAML's five rows (toolbar, video, feedback, servers,
        // providers) and their identities. Replacing them with the old three
        // rows leaves Avalonia's measured cell indices pointing past the end
        // of the collection. Updating Height also invalidates the cell cache.
        var rows = EmbeddedPlayerSurface.RowDefinitions;
        var videoRow = Grid.GetRow(EmbeddedPlayerContent);
        for (var row = 0; row < rows.Count; row++)
        {
            rows[row].Height = row == videoRow
                ? GridLength.Star
                : fullscreen ? new GridLength(0) : GridLength.Auto;
        }
    }

    private void ApplyGridDefinitions(
        bool useCompactLayout,
        bool useNarrowLayout)
    {
        var useDrawer = UsesDrawerNavigation;
        var useTopBar = UsesTopNavigationBar;
        Shell.ColumnDefinitions = new ColumnDefinitions(
            useCompactLayout
                ? "*"
                : _isTelevision
                    // The wide TV menu overlays the page. Keeping the content
                    // column fixed means every focus transfer no longer
                    // reflows all shelves and causes a visible sideways jump.
                    ? "104,*"
                    : _navigationViewModel?.IsNavigationCollapsed is true
                        ? "76,*"
                        : "256,*");
        Shell.RowDefinitions = new RowDefinitions(
            useTopBar ? "Auto,*" : "*");
        // The drawer shares row and column zero with the page and paints over
        // it, so the page keeps the full window while the menu is closed.
        PlaceInGrid(Sidebar, column: 0, row: 0);
        PlaceInGrid(
            MainContent,
            column: useCompactLayout ? 0 : 1,
            row: useTopBar ? 1 : 0);
        Grid.SetColumnSpan(MainContent, useCompactLayout ? 2 : 1);
        Sidebar.BorderThickness = useTopBar
            ? new Thickness(0, 0, 0, 1)
            : new Thickness(0, 0, 1, 0);
        // A drawer holds the same vertical rail the desktop shows.
        var useHorizontalBar = useTopBar;
        SidebarLayout.ColumnDefinitions =
            new ColumnDefinitions(useHorizontalBar ? "*,Auto,Auto" : "*");
        SidebarLayout.RowDefinitions = new RowDefinitions(
            useHorizontalBar
                ? "Auto"
                : "Auto,Auto,*,Auto,Auto");
        PlaceInGrid(
            PrimaryNavigation,
            column: 0,
            row: useHorizontalBar ? 0 : 1);
        PlaceInGrid(
            SecondaryNavigation,
            column: useHorizontalBar ? 1 : 0,
            row: useHorizontalBar ? 0 : 3);
        PlaceInGrid(
            SidebarFooter,
            column: useHorizontalBar ? 2 : 0,
            row: useHorizontalBar ? 0 : 4);

        SearchGrid.ColumnDefinitions = new ColumnDefinitions(
            useNarrowLayout ? "*" : "*,Auto");
        SearchGrid.RowDefinitions = new RowDefinitions(
            useNarrowLayout ? "Auto,Auto" : "Auto");
        PlaceInGrid(
            SearchActions,
            column: useNarrowLayout ? 0 : 1,
            row: useNarrowLayout ? 1 : 0);
        SearchActions.Margin = useNarrowLayout
            ? new Thickness(0, 10, 0, 0)
            : new Thickness(10, 0, 0, 0);
        SearchActions.HorizontalAlignment =
            useNarrowLayout
                ? Avalonia.Layout.HorizontalAlignment.Left
                : Avalonia.Layout.HorizontalAlignment.Right;

        // Live TV. The title and its two buttons share a row until there is no room,
        // and then the buttons drop underneath.
        LiveTvHeaderGrid.ColumnDefinitions = new ColumnDefinitions(
            useNarrowLayout ? "*" : "*,Auto");
        LiveTvHeaderGrid.RowDefinitions = new RowDefinitions(
            useNarrowLayout ? "Auto,Auto" : "Auto");
        PlaceInGrid(
            LiveTvHeaderActions,
            column: useNarrowLayout ? 0 : 1,
            row: useNarrowLayout ? 1 : 0);
        LiveTvHeaderActions.Margin = useNarrowLayout
            ? new Thickness(0, 12, 0, 0)
            : new Thickness(16, 0, 0, 0);
        LiveTvHeaderActions.HorizontalAlignment =
            useNarrowLayout
                ? Avalonia.Layout.HorizontalAlignment.Left
                : Avalonia.Layout.HorizontalAlignment.Right;

        // The one that actually broke the page. The channel summary is Auto-sized and
        // wants about 300px; beside it in a star column, the notice beside it was left
        // with the remainder - on a handset a strip a few characters wide, which wrapped
        // "Loaded 38 live channels" into a column of single letters tall enough to eat
        // the whole viewport. The channel list is measured after it, so it got nothing.
        LiveTvStatusGrid.ColumnDefinitions = new ColumnDefinitions(
            useNarrowLayout ? "*" : "*,Auto");
        LiveTvStatusGrid.RowDefinitions = new RowDefinitions(
            useNarrowLayout ? "Auto,Auto" : "Auto");
        PlaceInGrid(
            LiveTvChannelSummaryText,
            column: useNarrowLayout ? 0 : 1,
            row: useNarrowLayout ? 1 : 0);
        LiveTvChannelSummaryText.HorizontalAlignment =
            useNarrowLayout
                ? Avalonia.Layout.HorizontalAlignment.Left
                : Avalonia.Layout.HorizontalAlignment.Right;
        LiveTvChannelSummaryText.Margin = useNarrowLayout
            ? new Thickness(0, 10, 0, 0)
            : new Thickness(0);
        LiveTvNoticeStack.Margin = useNarrowLayout
            ? new Thickness(0)
            : new Thickness(0, 0, 16, 0);

        // 30 a side is a sixth of a handset's width.
        LiveTvPage.Margin = useNarrowLayout
            ? new Thickness(16)
            : new Thickness(30);

        MetadataSettingsGrid.ColumnDefinitions =
            new ColumnDefinitions(useCompactLayout ? "*" : "*,*,150,Auto");
        MetadataSettingsGrid.RowDefinitions =
            new RowDefinitions(useCompactLayout ? "Auto,Auto,Auto,Auto" : "Auto");
        PlaceInGrid(TmdbSettings, 0, 0);
        PlaceInGrid(
            MalSettings,
            useCompactLayout ? 0 : 1,
            useCompactLayout ? 1 : 0);
        PlaceInGrid(
            MalUnavailable,
            useCompactLayout ? 0 : 1,
            useCompactLayout ? 1 : 0);
        PlaceInGrid(
            LanguageSettings,
            useCompactLayout ? 0 : 2,
            useCompactLayout ? 2 : 0);
        PlaceInGrid(
            SaveMetadataButton,
            useCompactLayout ? 0 : 3,
            useCompactLayout ? 3 : 0);

        CatalogDataGrid.ColumnDefinitions =
            new ColumnDefinitions(useCompactLayout ? "*" : "*,Auto,Auto");
        CatalogDataGrid.RowDefinitions =
            new RowDefinitions(useCompactLayout ? "Auto,Auto,Auto" : "Auto");
        PlaceInGrid(CatalogDataDescription, 0, 0);
        PlaceInGrid(
            ImportCatalogButton,
            useCompactLayout ? 0 : 1,
            useCompactLayout ? 1 : 0);
        PlaceInGrid(
            ExportCatalogButton,
            useCompactLayout ? 0 : 2,
            useCompactLayout ? 2 : 0);

        BackupGrid.ColumnDefinitions =
            new ColumnDefinitions(useCompactLayout ? "*" : "*,Auto,Auto");
        BackupGrid.RowDefinitions =
            new RowDefinitions(useCompactLayout ? "Auto,Auto,Auto" : "Auto");
        PlaceInGrid(BackupDescription, 0, 0);
        PlaceInGrid(
            CreateBackupButton,
            useCompactLayout ? 0 : 1,
            useCompactLayout ? 1 : 0);
        PlaceInGrid(
            RestoreBackupButton,
            useCompactLayout ? 0 : 2,
            useCompactLayout ? 2 : 0);

        DetailsHeroGrid.ColumnDefinitions = new ColumnDefinitions(
            useNarrowLayout ? "*" : "Auto,*");
        DetailsHeroGrid.RowDefinitions = new RowDefinitions(
            useNarrowLayout ? "Auto,Auto" : "Auto");
        PlaceInGrid(DetailsArtworkContainer, 0, 0);
        PlaceInGrid(
            DetailsHeroContent,
            useNarrowLayout ? 0 : 1,
            useNarrowLayout ? 1 : 0);
        DetailsArtworkContainer.Margin = useNarrowLayout
            ? new Thickness(0, 0, 0, 20)
            : new Thickness(0, 0, 24, 0);
        ApplyDetailsArtworkSize(useNarrowLayout, useCompactLayout);

        ApplyReaderHeaderLayout(useNarrowLayout);
        CatalogBody.Margin = _isTelevision
            ? new Thickness(48, 40)
            : useNarrowLayout
                ? new Thickness(14)
                : useCompactLayout
                    ? new Thickness(20)
                    : new Thickness(30);
        CatalogBody.Spacing = _isTelevision ? 30 : 24;
        EpubReadingSurface.Margin = useNarrowLayout
            ? new Thickness(12)
            : new Thickness(24);
        EpubReadingSurface.Padding = useNarrowLayout
            ? new Thickness(24, 22)
            : new Thickness(52, 40, 52, 28);
    }

    /// <summary>
    /// Sizes the details cover to the surface it is shown on.
    /// <para>
    /// A single 176px poster was the same postage stamp on a phone and on a
    /// television, wasting the room the wider layouts already reserve for it.
    /// The 2:3 ratio is kept so the artwork still fills the frame without
    /// cropping differently per platform.
    /// </para>
    /// </summary>
    private void ApplyDetailsArtworkSize(
        bool useNarrowLayout,
        bool useCompactLayout)
    {
        const double posterAspect = 248d / 176d;
        var width = _isTelevision
            ? 320d
            : useNarrowLayout
                ? 220d
                : useCompactLayout
                    ? 232d
                    : 272d;
        DetailsArtwork.Width = width;
        DetailsArtwork.Height = Math.Round(width * posterAspect);
    }

    private void ApplyReaderHeaderLayout(bool useNarrowLayout)
    {
        ReaderHeaderGrid.ColumnDefinitions = new ColumnDefinitions(UsesTouchLayout ? "Auto,*,Auto" : "Auto,*,Auto,Auto");
        ReaderHeaderGrid.RowDefinitions = new RowDefinitions("Auto");
        PlaceInGrid(ReaderSettingsButton, 2, 0);
        PlaceInGrid(ReaderBusyIndicator, UsesTouchLayout ? 2 : 3, 0);
        if (UsesTouchLayout)
        {
            EpubHeaderGrid.ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto");
            EpubHeaderGrid.RowDefinitions = new RowDefinitions("Auto");
            PlaceInGrid(EpubBusyIndicator, 2, 0);
            return;
        }
        EpubHeaderGrid.ColumnDefinitions = new ColumnDefinitions(
            useNarrowLayout ? "Auto,*,Auto" : "Auto,*,Auto,Auto");
        EpubHeaderGrid.RowDefinitions = new RowDefinitions(
            useNarrowLayout ? "Auto,Auto" : "Auto");
        PlaceInGrid(
            EpubFontControls,
            useNarrowLayout ? 0 : 2,
            useNarrowLayout ? 1 : 0,
            useNarrowLayout ? 3 : 1);
        PlaceInGrid(
            EpubBusyIndicator,
            useNarrowLayout ? 2 : 3,
            0);
        EpubFontControls.Margin = useNarrowLayout
            ? new Thickness(0, 8, 0, 0)
            : new Thickness(0);
        EpubFontControls.HorizontalAlignment =
            Avalonia.Layout.HorizontalAlignment.Right;
    }

    private static void PlaceInGrid(
        Control control,
        int column,
        int row,
        int columnSpan = 1)
    {
        Grid.SetColumn(control, column);
        Grid.SetRow(control, row);
        Grid.SetColumnSpan(control, columnSpan);
    }

    private void AttachNavigationViewModel()
    {
        if (_navigationViewModel is not null)
        {
            if (_navigationViewModel.IsPlayerFullscreen)
            {
                _navigationViewModel.IsPlayerFullscreen = false;
            }

            _navigationViewModel.PropertyChanged -=
                NavigationViewModel_OnPropertyChanged;
        }

        _navigationViewModel = DataContext as MainViewModel;
        if (_navigationViewModel is not null)
        {
            _navigationViewModel.PropertyChanged +=
                NavigationViewModel_OnPropertyChanged;
        }

        AttachReaderViewModel();
        UpdateNavigationPresentation();
        UpdatePlayerFullscreenPresentation();
        UpdateToolPanels();
        SetReaderSettingsOpen(_navigationViewModel?.IsReaderSettingsOpen is true);
    }

    private void NavigationViewModel_OnPropertyChanged(
        object? sender,
        PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(MainViewModel.IsReaderSettingsOpen))
        {
            SetReaderSettingsOpen(_navigationViewModel?.IsReaderSettingsOpen is true);
            return;
        }
        if (eventArgs.PropertyName == nameof(MainViewModel.LatestErrorTarget))
        {
            var target = _navigationViewModel?.LatestErrorTarget;
            Dispatcher.UIThread.Post(() =>
            {
                if (target?.HasMessage is not true) return;
                // Reveal the failing action without moving keyboard/remote focus.
                this.GetVisualDescendants().OfType<ContentControl>()
                    .FirstOrDefault(control =>
                        control.IsEffectivelyVisible && ReferenceEquals(control.Content, target))
                    ?.BringIntoView();
            }, DispatcherPriority.Loaded);
            return;
        }

        if (eventArgs.PropertyName == nameof(
                MainViewModel.IsNavigationCollapsed))
        {
            UpdateNavigationPresentation();
        }
        else if (eventArgs.PropertyName is
                 nameof(MainViewModel.IsPlayerFullscreen) or
                 nameof(MainViewModel.IsEmbeddedMediaOpen))
        {
            UpdatePlayerFullscreenPresentation();
            ApplyMenuDrawerPresentation();
            FocusEmbeddedPlayerForTelevision();
        }
        else if (eventArgs.PropertyName is
                 nameof(MainViewModel.IsReaderOpen) or
                 nameof(MainViewModel.IsEpubReaderOpen))
        {
            if (_navigationViewModel?.IsReaderOpen is not true) _readerFullscreenRequested = false;
            UpdatePlayerFullscreenPresentation();
            ApplyMenuDrawerPresentation();
        }
        else if (eventArgs.PropertyName == nameof(MainViewModel.SectionTitle))
        {
            // A new section replaces the list, so keeping the old offset would
            // drop the reader into the middle of content they have not seen.
            // The rows scroll on their own axis and need the same treatment.
            CatalogScroll.Offset = default;
            RewindShelvesAfterLayout();
            _catalogToolsExpanded = false;
            _liveTvToolsExpanded = false;
            _televisionPageContentReady = false;
            _lastFocusedTelevisionPageControl = null;
            _lastFocusedTelevisionCatalogItemId = null;
            _restoreTelevisionCatalogFocus = false;
            SetLiveTvRemoteListActive(false);
            UpdateToolPanels();
            FocusInitialTelevisionPageTarget();
        }
        else if (eventArgs.PropertyName ==
                 nameof(MainViewModel.ShowCatalogOverviewTools))
        {
            // This notification also fires when returning to the same section,
            // without SectionTitle or the window size changing. Refresh here:
            // a later else-if for this same property is never reached.
            UpdateToolPanels();
            // The search field is realized with its panel, after the pass that
            // disarms every field has already run, so it arrives still able to
            // summon the keyboard the moment focus lands on it. Disarmed here,
            // off the input path: doing it as the field takes focus puts an
            // input-method call inside key dispatch, which is what Android's
            // five-second deadline kills the application for.
            ConfigureTelevisionTextInputsAfterLayout();
        }
        else if (eventArgs.PropertyName ==
                 nameof(MainViewModel.IsSettingsOpen))
        {
            ConfigureTelevisionTextInputsAfterLayout();
            if (_isTelevision)
            {
                // Opening moves the remote into the layer; closing hands it
                // back to the page that was underneath all along.
                Dispatcher.UIThread.Post(
                    () =>
                    {
                        if (_navigationViewModel?.IsSettingsOpen is true)
                        {
                            // The layer's fields have only just been realized.
                            // Disarm their input methods before focus reaches
                            // one, or the first field opens the keyboard on
                            // arrival rather than on Select.
                            ConfigureTelevisionTextInputs();
                            TryFocusSettingsOverlay();
                        }
                        else
                        {
                            _ = TryRestoreTelevisionPageFocus() ||
                                TryFocusFirstTelevisionPageItem();
                        }
                    },
                    DispatcherPriority.Background);
            }
        }
        else if (eventArgs.PropertyName ==
                 nameof(MainViewModel.SelectedDetails))
        {
            TraceTelevisionInput("details-changed");
            // A title transition removes the control that owned focus. Hand
            // focus to useful detail actions or back to the remembered card.
            var detailsItemId = _navigationViewModel?.SelectedDetails?.Details.Item.Id;
            var isRefresh = detailsItemId.HasValue &&
                detailsItemId == _televisionDetailsItemId;
            _televisionDetailsItemId = detailsItemId;
            if (!isRefresh)
            {
                DetailsScroll.Offset = default;
                _lastFocusedTelevisionDetailsControl = null;
                _lastFocusedTelevisionEntryKey = null;
                _lastFocusedTelevisionEntryAction = null;
            }
            if (_isTelevision)
            {
                var detailsAreOpen =
                    _navigationViewModel?.SelectedDetails is not null;
                // Details has useful actions as soon as the model exists. A
                // provider refresh may keep IsBusy true much longer and must
                // not leave the remote without a target in the meantime.
                // The catalog remains mounted behind details, so closing the
                // detail page also has content ready immediately even if the
                // provider refresh is still completing.
                _televisionPageContentReady = true;
                _restoreTelevisionCatalogFocus =
                    !detailsAreOpen &&
                    _lastFocusedTelevisionCatalogItemId.HasValue;
            }

            if (!isRefresh)
            {
                FocusInitialTelevisionPageTarget();
            }
            _ = EnsureTelevisionPageFocusAfterTransitionAsync();
        }
        else if (eventArgs.PropertyName == nameof(
                     MainViewModel.ProviderChoiceTargetStableKey))
        {
            RevealProviderChoiceAfterLayout();
        }
        else if (eventArgs.PropertyName ==
                 nameof(MainViewModel.IsLiveTvPageVisible))
        {
            UpdateToolPanels();
            ConfigureLiveTvDirectionalTargets();
            if (_isTelevision &&
                eventArgs.PropertyName ==
                    nameof(MainViewModel.IsLiveTvPageVisible) &&
                _navigationViewModel?.IsLiveTvPageVisible is true)
            {
                _televisionPageContentReady = true;
                FocusInitialTelevisionPageTarget();
            }
        }
        else if (eventArgs.PropertyName ==
                 nameof(MainViewModel.HasLiveTvChannels))
        {
            ConfigureLiveTvDirectionalTargets();
        }
        else if (eventArgs.PropertyName is
                 nameof(MainViewModel.ShowHomeSections) or
                 nameof(MainViewModel.ShowBrowseShelves) or
                 nameof(MainViewModel.ShowFlatCatalogList))
        {
            _televisionPageContentReady = true;
            RefreshShelfNavigationAfterLayout();
            // The catalog can refresh behind a title, reader, or settings.
            // Only the visible catalog should receive this focus handoff.
            if (_navigationViewModel?.IsCatalogOpen is true)
            {
                FocusInitialTelevisionPageTarget();
                _ = EnsureTelevisionPageFocusAfterTransitionAsync();
            }
        }
        else if (eventArgs.PropertyName ==
                nameof(MainViewModel.CatalogPage))
        {
            CatalogScroll.Offset = default;
            _televisionPageContentReady = false;
            _lastFocusedTelevisionPageControl = null;
            _lastFocusedTelevisionCatalogItemId = null;
            _restoreTelevisionCatalogFocus = false;
            FocusInitialTelevisionPageTarget();
            _ = EnsureTelevisionPageFocusAfterTransitionAsync();
        }
        else if (eventArgs.PropertyName ==
                 nameof(MainViewModel.IsUpdatePromptVisible))
        {
            if (_navigationViewModel?.IsDonationPromptVisible is true)
            {
                TryFocusDonationPrompt();
            }
            else if (_navigationViewModel?.IsUpdatePromptVisible is true)
            {
                FocusUpdatePromptForTelevision();
            }
            else
            {
                FocusInitialTelevisionPageTarget();
            }
        }
        else if (eventArgs.PropertyName ==
                 nameof(MainViewModel.IsDonationPromptVisible))
        {
            Dispatcher.UIThread.Post(
                () =>
                {
                    if (_navigationViewModel?.IsDonationPromptVisible is true)
                    {
                        TryFocusDonationPrompt();
                    }
                    else if (_isTelevision &&
                             _navigationViewModel?.IsSettingsOpen is true)
                    {
                        TryFocusSettingsOverlay();
                    }
                    else if (_isTelevision)
                    {
                        _ = TryRestoreTelevisionPageFocus() ||
                            TryFocusFirstTelevisionPageItem();
                    }
                },
                DispatcherPriority.Loaded);
        }
        else if (eventArgs.PropertyName == nameof(MainViewModel.IsBusy) &&
                 _navigationViewModel?.IsBusy is false &&
                 _focusInitialTelevisionPagePending)
        {
            FocusInitialTelevisionPageTarget();
        }
        else if (eventArgs.PropertyName == nameof(MainViewModel.Reader))
        {
            AttachReaderViewModel();
            ResetReaderScrollState();
            if (_navigationViewModel?.Reader is null)
            {
                // Closing the reader removes the control that held focus.
                // Restore focus to the detail page instead of making the
                // next D-pad press fall back to the sidebar.
                FocusInitialTelevisionPageTarget();
            }
            else
            {
                FocusInitialTelevisionReaderTarget();
            }

            // A chapter reached by stepping opens at its own beginning. Both
            // the restored page and ScrollIntoView work against that: the
            // first lands on wherever this chapter was left last time, and the
            // second only brings a page far enough into view to be seen, so
            // even page one arrives slightly short of the top.
            if (_navigationViewModel is
                { IsSteppingReaderChapter: true, Reader: { } steppedReader })
            {
                steppedReader.GoToFirstPage();
                ScrollReaderToTop();
            }
            else if (_navigationViewModel?.Reader?.SelectedContinuousPage is
            { } selectedPage)
            {
                Dispatcher.UIThread.Post(
                    () => ReaderPages.ScrollIntoView(selectedPage),
                    DispatcherPriority.Loaded);
            }
        }
        else if (eventArgs.PropertyName is
                 nameof(MainViewModel.HasReaderChapterNavigation) or
                 nameof(MainViewModel.CanOpenPreviousReaderChapter) or
                 nameof(MainViewModel.CanOpenNextReaderChapter))
        {
            UpdateReaderControlVisibility();
        }
    }

    private void FocusInitialTelevisionPageTarget()
    {
        if (_navigationViewModel?.IsDonationPromptVisible is true)
        {
            TryFocusDonationPrompt();
            return;
        }

        if (_navigationViewModel?.IsUpdatePromptVisible is true)
        {
            FocusUpdatePromptForTelevision();
            return;
        }

        if (!_isTelevision ||
            IsImmersiveSurfaceOpen)
        {
            return;
        }

        // Section commands publish their title before the catalog query has
        // replaced the previous cards. They can also do so while DPAD_CENTER
        // is still held. Waiting for both conditions prevents an old card from
        // receiving the navigation button's release event.
        if (_suppressedTelevisionActivationKey is not null)
        {
            _focusInitialTelevisionPagePending = true;
            // A synthetic/firmware KeyUp can arrive before the queued command
            // publishes its new page. The short retry closes that race instead
            // of requiring the next D-pad press to recover focus.
            _ = RetryInitialTelevisionPageFocusAsync();
            return;
        }

        if (!_televisionPageContentReady &&
            _navigationViewModel?.IsBusy is true)
        {
            _focusInitialTelevisionPagePending = true;
            return;
        }

        _focusInitialTelevisionPagePending = false;
        var generation = _televisionContentFocusGeneration;
        Dispatcher.UIThread.Post(
            () =>
            {
                if (generation != _televisionContentFocusGeneration ||
                    !_isTelevision ||
                    IsImmersiveSurfaceOpen ||
                    _navigationViewModel?.IsDonationPromptVisible is true ||
                    _navigationViewModel?.IsUpdatePromptVisible is true)
                {
                    return;
                }

                if (_suppressedTelevisionActivationKey is not null)
                {
                    _focusInitialTelevisionPagePending = true;
                    _ = RetryInitialTelevisionPageFocusAsync();
                    return;
                }

                if (!_televisionPageContentReady &&
                    _navigationViewModel?.IsBusy is true)
                {
                    _focusInitialTelevisionPagePending = true;
                    return;
                }

                if (_navigationViewModel?.IsSettingsOpen is true)
                {
                    TryFocusSettingsOverlay();
                }
                else if (_navigationViewModel?.IsLiveTvPageVisible is true)
                {
                    ActivateLiveTvRemoteList();
                }
                else if (!TryRestoreTelevisionPageFocus() &&
                         !TryFocusFirstTelevisionPageItem())
                {
                    _focusInitialTelevisionPagePending = true;
                    _ = RetryInitialTelevisionPageFocusAsync();
                }
            },
            // Data binding/render run ahead of Input priority, while lengthy
            // background provider work cannot starve the focus handoff.
            DispatcherPriority.Input);
    }

    private void FocusInitialTelevisionReaderTarget()
    {
        if (_navigationViewModel?.IsDonationPromptVisible is true)
        {
            TryFocusDonationPrompt();
            return;
        }

        if (_navigationViewModel?.IsUpdatePromptVisible is true)
        {
            FocusUpdatePromptForTelevision();
            return;
        }

        if (!_isTelevision)
        {
            return;
        }

        Dispatcher.UIThread.Post(
            () => TryFocusTelevisionReader(),
            DispatcherPriority.Loaded);
    }

    private void FocusUpdatePromptForTelevision()
    {
        if (!_isTelevision ||
            _navigationViewModel?.IsDonationPromptVisible is true ||
            _navigationViewModel?.IsUpdatePromptVisible is not true)
        {
            return;
        }

        // The prompt is a root overlay rather than a child of MainContent.
        // Cancel delayed page-focus work and release Live TV's virtual list
        // selection before moving the remote into the prompt.
        _televisionContentFocusGeneration++;
        _focusInitialTelevisionPagePending = false;
        SetLiveTvRemoteListActive(false);
        Dispatcher.UIThread.Post(
            () =>
            {
                if (!_isTelevision ||
                    _navigationViewModel?.IsDonationPromptVisible is true ||
                    _navigationViewModel?.IsUpdatePromptVisible is not true ||
                    !UpdatePrompt.IsVisible)
                {
                    return;
                }

                if (InstallAvailableUpdateButton is
                    { IsVisible: true, IsEnabled: true } &&
                    InstallAvailableUpdateButton.Focus(
                        NavigationMethod.Directional))
                {
                    return;
                }

                if (DismissUpdatePromptButton is
                    { IsVisible: true, IsEnabled: true } &&
                    DismissUpdatePromptButton.Focus(
                        NavigationMethod.Directional))
                {
                    return;
                }

                _ = DismissUpdatePromptTopButton.Focus(
                    NavigationMethod.Directional);
            },
            DispatcherPriority.Loaded);
    }

    private bool TryFocusTelevisionReader()
    {
        if (!_isTelevision ||
            _navigationViewModel?.IsReaderOpen is not true ||
            !IsVisibleWithin(ReaderBackButton, MainContent))
        {
            return false;
        }

        return ReaderBackButton.Focus(NavigationMethod.Directional);
    }

    /// <summary>
    /// Reports focus resting on the chapter/episode list itself rather than on
    /// one of the action buttons an entry contains.
    /// </summary>
    private bool IsTelevisionEntryListFocused(Visual? focusedElement) =>
        focusedElement is not null &&
        (ReferenceEquals(focusedElement, DetailsEntriesList) ||
         (DetailsEntriesList.IsVisualAncestorOf(focusedElement) &&
          focusedElement is not Button &&
          focusedElement.GetVisualAncestors().OfType<Button>().FirstOrDefault()
              is null));

    /// <summary>
    /// Steps focus from the entry list onto the first chapter or episode action
    /// currently on screen. The list is a single focus stop whose items are
    /// deliberately not focusable, so without this the per-entry actions —
    /// including the source buttons that start playback or reading — could not
    /// be reached by remote at all and Center appeared to do nothing.
    /// Only realized, on-screen entries are considered: the list virtualizes,
    /// so an index-based lookup returns nothing once it has been scrolled.
    /// </summary>
    private bool TryFocusFirstTelevisionEntryAction()
    {
        if (_navigationViewModel?.IsDetailsOpen is not true ||
            DetailsEntriesList.ItemCount == 0)
        {
            return false;
        }

        var target = EnumerateEntryActions(DetailsEntriesList)
            .OrderBy(candidate => candidate.Bounds.Top)
            .ThenBy(candidate => candidate.Bounds.Left)
            .Select(candidate => candidate.Button)
            .FirstOrDefault();
        return target?.Focus(NavigationMethod.Directional) is true;
    }

    /// <summary>
    /// Reveals the provider button that was added below an episode's existing
    /// actions. Expanding a row does not make either the virtualized list or
    /// the outer details scroller follow the new content automatically, which
    /// previously left the picker below the Android viewport and unreachable
    /// with a television remote.
    /// </summary>
    private void RevealProviderChoiceAfterLayout()
    {
        var generation = ++_providerChoiceRevealGeneration;
        var stableKey = _navigationViewModel?
            .ProviderChoiceTargetStableKey;
        if (stableKey is null)
        {
            return;
        }

        Dispatcher.UIThread.Post(
            () =>
            {
                if (generation != _providerChoiceRevealGeneration ||
                    _navigationViewModel?.SelectedDetails is not
                    { } details ||
                    !string.Equals(
                        _navigationViewModel.ProviderChoiceTargetStableKey,
                        stableKey,
                        StringComparison.Ordinal))
                {
                    return;
                }

                var entry = details.Entries.FirstOrDefault(candidate =>
                    string.Equals(
                        candidate.Identity.StableKey,
                        stableKey,
                        StringComparison.Ordinal));
                if (entry is null)
                {
                    return;
                }

                // Provider results change the row height. Finish measuring
                // before the virtualizing panel calculates a scroll offset.
                DetailsEntriesList.UpdateLayout();
                DetailsEntriesList.ScrollIntoView(entry);
                Dispatcher.UIThread.Post(
                    () => RevealRealizedProviderChoice(
                        stableKey,
                        generation),
                    DispatcherPriority.Background);
            },
            DispatcherPriority.Loaded);
    }

    private void RevealRealizedProviderChoice(
        string stableKey,
        int generation)
    {
        if (generation != _providerChoiceRevealGeneration ||
            !string.Equals(
                _navigationViewModel?.ProviderChoiceTargetStableKey,
                stableKey,
                StringComparison.Ordinal))
        {
            return;
        }

        DetailsEntriesList.UpdateLayout();
        var entryContainer = DetailsEntriesList
            .GetRealizedContainers()
            .OfType<Control>()
            .FirstOrDefault(container =>
                container.DataContext is CatalogContentEntryViewModel entry &&
                string.Equals(
                    entry.Identity.StableKey,
                    stableKey,
                    StringComparison.Ordinal));
        var providerButton = entryContainer?
            .GetVisualDescendants()
            .OfType<Button>()
            .FirstOrDefault(static button =>
                button.Classes.Contains(ProviderChoiceClass) &&
                button.IsVisible &&
                button.IsEnabled);
        if (providerButton is null)
        {
            return;
        }

        ScrollControlIntoView(providerButton);
        if (_isTelevision)
        {
            providerButton.Focus(NavigationMethod.Directional);
        }
    }

    /// <summary>
    /// Moves focus one entry along when the neighbouring entry has not been
    /// realized yet.
    /// Uses the item index rather than a scroll distance, which can leave
    /// focus on the same episode when rows have different heights or the
    /// scroll offset has reached an edge.
    /// </summary>
    private bool TryStepTelevisionEntryList(
        Visual focusedElement,
        NavigationDirection direction)
    {
        if (focusedElement.GetVisualAncestors().OfType<ListBoxItem>()
                .FirstOrDefault() is not { } entry)
        {
            return false;
        }

        var index = DetailsEntriesList.IndexFromContainer(entry);
        if (index < 0)
        {
            return false;
        }

        var nextIndex = index + (direction is NavigationDirection.Down ? 1 : -1);
        if (nextIndex < 0)
        {
            return TryMoveTelevisionPageFocus(DetailsScroll, focusedElement, direction);
        }

        if (nextIndex >= DetailsEntriesList.ItemCount)
        {
            return true;
        }

        var generation = _televisionContentFocusGeneration;
        var targetItem = DetailsEntriesList.Items[nextIndex];
        bool IsCurrentRequest() => generation == _televisionContentFocusGeneration &&
            DetailsEntriesList.IsVisible && nextIndex < DetailsEntriesList.ItemCount &&
            Equals(DetailsEntriesList.Items[nextIndex], targetItem);

        // Realize the next row after acknowledging input, then focus after
        // layout. A newer remote gesture invalidates both queued operations.
        Dispatcher.UIThread.Post(
            () =>
            {
                if (!IsCurrentRequest())
                {
                    return;
                }

                DetailsEntriesList.ScrollIntoView(nextIndex);
                Dispatcher.UIThread.Post(() =>
                {
                    if (!IsCurrentRequest() ||
                        DetailsEntriesList.ContainerFromIndex(nextIndex) is not { } nextEntry)
                    {
                        return;
                    }

                    var target = SelectLeadingEntryAction(nextEntry.GetVisualDescendants()
                        .OfType<Button>().Where(button => button.Focusable && button.IsEnabled &&
                            IsVisibleWithin(button, DetailsEntriesList)));
                    if (target?.Focus(NavigationMethod.Directional) is true)
                    {
                        ScrollControlIntoView(target);
                    }
                }, DispatcherPriority.Background);
            },
            DispatcherPriority.Background);
        return true;
    }

    /// <summary>
    /// Snaps focus to the first action of whichever entry now holds it, so a
    /// chapter is always entered on the action that opens it — unless that
    /// entry has an open source picker, which is entered on the picker.
    /// </summary>
    private void FocusLeadingEntryActionAfterLayout(ListBoxItem? previousEntry)
    {
        var generation = _televisionContentFocusGeneration;
        Dispatcher.UIThread.Post(
            () =>
            {
                if (generation != _televisionContentFocusGeneration ||
                    TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement()
                        is not Visual focused ||
                    focused.GetVisualAncestors()
                        .OfType<ListBoxItem>()
                        .FirstOrDefault() is not { } entry ||
                    ReferenceEquals(entry, previousEntry))
                {
                    return;
                }

                var leading = SelectLeadingEntryAction(
                    EnumerateEntryActions(entry)
                        .OrderBy(candidate => candidate.Bounds.Top)
                        .ThenBy(candidate => candidate.Bounds.Left)
                        .Select(candidate => candidate.Button));
                if (leading is not null &&
                    !ReferenceEquals(leading, focused))
                {
                    leading.Focus(NavigationMethod.Directional);
                }
            },
            DispatcherPriority.Background);
    }

    /// <summary>
    /// Picks the action a chapter is entered on, given that chapter's actions
    /// in visual order.
    /// <para>
    /// An entry whose source picker is open is asking which provider to use,
    /// so arriving at it has to land on the answer. The picker renders below
    /// the entry's own actions, so taking the first action outright snapped
    /// the pad back onto "search the enabled APIs" on every arrival, which
    /// left the provider buttons reachable only by the one-shot focus fired
    /// when the picker first appeared and unreachable by the remote ever
    /// after. Callers pass viewport-filtered actions, so a provider button
    /// reaching this point means that picker is open and on screen.
    /// </para>
    /// </summary>
    internal static Button? SelectLeadingEntryAction(
        IEnumerable<Button> actionsInVisualOrder)
    {
        Button? leading = null;
        foreach (var button in actionsInVisualOrder)
        {
            if (button.Classes.Contains(ProviderChoiceClass))
            {
                return button;
            }

            leading ??= button;
        }

        return leading;
    }

    /// <summary>
    /// Puts the remote on the first control inside the settings layer, so
    /// opening it never leaves focus stranded on the page underneath.
    /// </summary>
    /// <summary>
    /// The focusable cards and shelf headings of the rows that are actually on
    /// screen, skipping the visual subtree of any row that is not.
    /// </summary>
    private IEnumerable<Control> EnumerateShelfFocusCandidates(
        ItemsControl shelvesControl)
    {
        var viewportHeight = MainContent.Bounds.Height;
        foreach (var shelf in shelvesControl.GetRealizedContainers())
        {
            if (shelf is not Control container ||
                TryGetBoundsWithin(container, MainContent) is not { } bounds ||
                bounds.Bottom <= 0 ||
                bounds.Top >= viewportHeight)
            {
                continue;
            }

            foreach (var button in container
                         .GetVisualDescendants()
                         .OfType<Button>())
            {
                if (button.Classes.Contains("catalog-card-button") ||
                    button.Classes.Contains("shelf-see-all"))
                {
                    yield return button;
                }
            }
        }
    }

    private bool TryFocusSettingsOverlay()
    {
        if (!SettingsOverlay.IsVisible)
        {
            return false;
        }

        var target = SettingsOverlay
            .GetVisualDescendants()
            .OfType<Control>()
            .FirstOrDefault(control =>
                control.Focusable &&
                control.IsEnabled &&
                IsVisibleWithin(control, SettingsOverlay) &&
                control.Bounds.Width > 0 &&
                control.Bounds.Height > 0);
        return target?.Focus(NavigationMethod.Directional) is true;
    }

    private bool TryFocusDonationPrompt()
    {
        if (!DonationPrompt.IsVisible)
        {
            return false;
        }

        return DonationDismissButton.Focus(NavigationMethod.Directional) ||
            DonationCopyButton.Focus(NavigationMethod.Directional) ||
            DonationCloseButton.Focus(NavigationMethod.Directional);
    }

    private void UpdateDonationLayout(Size viewport)
    {
        var compact = viewport.Width < 520 || viewport.Height < 520;
        var margin = compact ? 12 : 24;
        DonationCard.Margin = new Thickness(margin);
        DonationCard.Padding = new Thickness(compact ? 16 : 30);
        DonationCard.MaxHeight = Math.Max(0, viewport.Height - 2 * margin);
        var qrSize = Math.Clamp(Math.Min(viewport.Width - 80, viewport.Height * .32), 80, 260);
        DonationQrImage.Width = qrSize;
        DonationQrImage.Height = qrSize;
        var stackActions = viewport.Width < 520;
        DonationActions.ColumnDefinitions = new ColumnDefinitions(stackActions ? "*" : "*,*");
        DonationActions.RowDefinitions = new RowDefinitions(stackActions ? "Auto,Auto" : "Auto");
        PlaceInGrid(DonationDismissButton, 0, 0);
        PlaceInGrid(DonationCopyButton, stackActions ? 0 : 1, stackActions ? 1 : 0);
    }

    private async void CopyDonationAddress_OnClick(
        object? sender,
        RoutedEventArgs eventArgs)
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null)
            {
                return;
            }

            await clipboard.SetTextAsync(MainViewModel.DonationWalletAddress);
            if (DataContext is MainViewModel viewModel)
            {
                viewModel.MarkDonationAddressCopied();
            }
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError(
                $"Donation address could not be copied: {exception.Message}");
        }
    }

    /// <summary>
    /// The focusable actions inside the entry list that are actually on
    /// screen, with their bounds measured against the list.
    /// <para>
    /// One definition for all three callers. Written out separately they
    /// drifted: the copy behind <see cref="FocusLeadingEntryActionAfterLayout"/>
    /// measured against the entry rather than the list and applied no viewport
    /// test at all, so it could focus an action scrolled out of view.
    /// </para>
    /// </summary>
    private IEnumerable<(Button Button, Rect Bounds)> EnumerateEntryActions(
        Visual root)
    {
        var viewportHeight = DetailsEntriesList.Bounds.Height;
        foreach (var button in root.GetVisualDescendants().OfType<Button>())
        {
            if (!button.Focusable ||
                !button.IsEnabled ||
                !IsVisibleWithin(button, DetailsEntriesList) ||
                TryGetBoundsWithin(button, DetailsEntriesList) is not
                { } bounds ||
                bounds.Bottom <= 0 ||
                bounds.Top >= viewportHeight)
            {
                continue;
            }

            yield return (button, bounds);
        }
    }

    private bool IsFocusInsideTelevisionEntryList(Visual? focusedElement) =>
        focusedElement is not null &&
        DetailsEntriesList.IsVisualAncestorOf(focusedElement);

    /// <summary>
    /// Treats focus that is not on the menu or the page as lost. Android TV
    /// hands focus back to the window root whenever the control holding it is
    /// removed — a page swap, a virtualized row recycling, a surface closing —
    /// and the root has no focus ring and answers no direction, so the remote
    /// simply appears dead until the app is restarted.
    /// </summary>
    /// <summary>
    /// Whether an open drop-down currently owns the remote.
    /// <para>
    /// A popup is hosted in the window's overlay layer, outside both the menu
    /// and the page, so every test that asks "is focus still on the page"
    /// answers no for it. Until this existed the navigation beam read an open
    /// drop-down as focus escaping and pulled the remote back to the control
    /// behind the list — which is what made every ComboBox unusable on a
    /// television. Focus on the box itself counts too: Avalonia leaves it there
    /// until an item is reached, and moving it away strands the open list.
    /// </para>
    /// </summary>
    private static bool IsDropDownActive(Visual? focusedElement) =>
        focusedElement is not null &&
        (focusedElement is ComboBox { IsDropDownOpen: true } ||
         focusedElement.GetVisualAncestors().Any(static ancestor =>
             ancestor is Avalonia.Controls.Primitives.PopupRoot or
                 Avalonia.Controls.Primitives.OverlayPopupHost or
                 ComboBox { IsDropDownOpen: true }));

    /// <summary>
    /// Shuts an open drop-down and puts the remote back on the box it belongs
    /// to, so Back dismisses the list rather than leaving the page.
    /// </summary>
    private bool TryCloseOpenDropDown()
    {
        foreach (var comboBox in this.GetVisualDescendants().OfType<ComboBox>())
        {
            if (!comboBox.IsDropDownOpen)
            {
                continue;
            }

            comboBox.IsDropDownOpen = false;
            comboBox.Focus(NavigationMethod.Directional);
            return true;
        }

        return false;
    }

    private bool IsTelevisionFocusLost(Visual? focusedElement) =>
        focusedElement is null ||
        focusedElement is Control
        {
            IsVisible: false
        } ||
        (!ReferenceEquals(focusedElement, Sidebar) &&
         !Sidebar.IsVisualAncestorOf(focusedElement) &&
         !ReferenceEquals(focusedElement, MainContent) &&
         !MainContent.IsVisualAncestorOf(focusedElement));

    private bool IsFocusInsideContinuousReader(Visual? focusedElement) =>
        _navigationViewModel?.Reader is { IsContinuous: true } &&
        focusedElement is not null &&
        (ReferenceEquals(focusedElement, ReaderPages) ||
         ReaderPages.IsVisualAncestorOf(focusedElement));

    /// <summary>
    /// Scrolls the continuous reader by part of a screen. Letting the list box
    /// handle the key instead moved its selection a whole page at a time, and
    /// realizing a full-size page inside key dispatch blew past Android's
    /// five-second input deadline and had the app killed for not responding.
    /// </summary>
    private bool TryScrollContinuousReader(double direction)
    {
        var scrollViewer = _readerScrollViewer ??= ReaderPages
            .GetVisualDescendants()
            .OfType<ScrollViewer>()
            .FirstOrDefault();
        if (scrollViewer is null)
        {
            return false;
        }

        // Complete key dispatch before the new pages are measured.
        Dispatcher.UIThread.Post(
            () =>
            {
                var step = Math.Max(160, scrollViewer.Viewport.Height * 0.6);
                var maximum = Math.Max(
                    0,
                    scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
                scrollViewer.Offset = new Vector(
                    scrollViewer.Offset.X,
                    Math.Clamp(
                        scrollViewer.Offset.Y + (step * direction),
                        0,
                        maximum));
            },
            DispatcherPriority.Background);
        return true;
    }

    private bool IsContinuousReaderAtTop() =>
        _readerScrollViewer is not { } scrollViewer ||
        scrollViewer.Offset.Y <= 1;

    private bool IsFocusInsideReaderToolbar(Visual? focusedElement) =>
        focusedElement is not null &&
        (ReferenceEquals(focusedElement, ReaderToolbar) ||
         ReaderToolbar.IsVisualAncestorOf(focusedElement));

    /// <summary>
    /// Turns the page for a horizontal remote press. The key is consumed even
    /// at the first and last page so the press cannot fall through and move
    /// focus out of the reader.
    /// </summary>
    private bool TryTurnTelevisionReaderPage(NavigationDirection direction)
    {
        if (_navigationViewModel?.Reader is not { IsPaged: true } reader)
        {
            return false;
        }

        var command = direction is NavigationDirection.Left
            ? reader.PreviousPageCommand
            : reader.NextPageCommand;
        if (command.CanExecute(null))
        {
            command.Execute(null);
        }

        return true;
    }

    private async Task RetryInitialTelevisionPageFocusAsync()
    {
        var generation = _televisionContentFocusGeneration;
        await Task.Delay(TimeSpan.FromMilliseconds(150));
        await Dispatcher.UIThread.InvokeAsync(
            () =>
            {
                if (generation == _televisionContentFocusGeneration &&
                    _focusInitialTelevisionPagePending &&
                    _isTelevision &&
                    !IsImmersiveSurfaceOpen &&
                    _navigationViewModel?.IsDonationPromptVisible is not true &&
                    _navigationViewModel?.IsUpdatePromptVisible is not true &&
                    _navigationViewModel?.IsLiveTvPageVisible is not true &&
                    _suppressedTelevisionActivationKey is null &&
                    (_televisionPageContentReady ||
                     _navigationViewModel?.IsBusy is not true))
                {
                    FocusInitialTelevisionPageTarget();
                }
            },
            DispatcherPriority.Background);
    }

    private void AttachReaderViewModel()
    {
        StopReaderMotion();
        if (_readerViewModel is not null)
        {
            _readerZoomFactor = _readerViewModel.ZoomFactor;
            _readerViewModel.PropertyChanged -=
                ReaderViewModel_OnPropertyChanged;
            foreach (var page in _readerViewModel.Pages)
            {
                page.LayoutMetricsChanging -=
                    ReaderPage_OnLayoutMetricsChanging;
            }
        }

        _readerViewModel = _navigationViewModel?.Reader;
        if (_readerViewModel is not null)
        {
            // The reader has already restored the stored zoom by the time it
            // gets here. Pushing this view's cached value back into it undid
            // that — on a fresh start the cache is still the default, so every
            // saved zoom was overwritten with 100% the moment a chapter
            // opened. The reader owns the value; the cache only mirrors it so
            // a mid-session chapter change keeps the current zoom.
            _readerZoomFactor = _readerViewModel.ZoomFactor;
            ApplyReaderPagePanel();
            UpdateReaderViewportWidth(ImageReaderSurface.Bounds.Width);
            _readerViewModel.PropertyChanged +=
                ReaderViewModel_OnPropertyChanged;
            foreach (var page in _readerViewModel.Pages)
            {
                page.LayoutMetricsChanging +=
                    ReaderPage_OnLayoutMetricsChanging;
            }
        }
    }

    private void ReaderViewModel_OnPropertyChanged(
        object? sender,
        PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(ImageReaderViewModel.IsAutoScrollEnabled))
        {
            if (_readerViewModel?.IsAutoScrollEnabled is true) ScheduleReaderMotion();
            else StopReaderMotion();
        }
        if (eventArgs.PropertyName is
            nameof(ImageReaderViewModel.CurrentPageIndex) or
            nameof(ImageReaderViewModel.DisplayMode))
        {
            if (eventArgs.PropertyName ==
                nameof(ImageReaderViewModel.DisplayMode))
            {
                ResetReaderScrollState();
            }
            else if (_readerViewModel?.IsPaged == true)
            {
                // One page at a time means each page is read from its top.
                // Keeping the previous offset landed the reader part-way down
                // the next page whenever the one before it was taller than the
                // viewport. Continuous mode is excluded on purpose: there the
                // offset is the reading position.
                ScrollReaderToTop();
                _lastReaderOffsetY = 0;
                _hasReaderOffset = false;
                _readerIsAtEnd = false;
            }

            UpdateReaderControlVisibility();
        }
        else if (eventArgs.PropertyName ==
                 nameof(ImageReaderViewModel.ZoomFactor) &&
                 sender is ImageReaderViewModel reader)
        {
            _readerZoomFactor = reader.ZoomFactor;
        }
    }

    private void ReaderViewport_OnSizeChanged(
        object? sender,
        SizeChangedEventArgs eventArgs)
    {
        UpdateReaderSettingsLayout();
        UpdateReaderViewportWidth(eventArgs.NewSize.Width);
    }

    private void UpdateReaderViewportWidth(double width)
    {
        const double scrollbarGutter = 18;
        if (width > scrollbarGutter)
        {
            _readerViewModel?.SetViewportWidth(width - scrollbarGutter);
        }
    }

    private void ReaderPage_OnLayoutMetricsChanging(
        object? sender,
        EventArgs eventArgs)
    {
        if (!Dispatcher.UIThread.CheckAccess() ||
            _readerViewportRestoreScheduled ||
            sender is not ReaderPageViewModel ||
            _readerViewModel is not { IsContinuous: true })
        {
            return;
        }

        var scrollViewer = _readerScrollViewer ??= ReaderPages
            .GetVisualDescendants()
            .OfType<ScrollViewer>()
            .FirstOrDefault();
        if (scrollViewer is null || scrollViewer.Offset.Y <= 0.5)
        {
            return;
        }

        var changingPage = (ReaderPageViewModel)sender;
        var anchorView = FindVisibleReaderAnchor(
            scrollViewer,
            changingPage);
        if (anchorView?.DataContext is not ReaderPageViewModel anchorPage ||
            anchorView.TranslatePoint(default, scrollViewer) is not
            { } anchorTopLeft)
        {
            return;
        }

        _readerViewportAnchor = new ReaderViewportAnchor(
            anchorPage,
            anchorTopLeft.Y,
            scrollViewer.Offset.Y);
        _readerViewportRestoreScheduled = true;
        var generation = _readerViewportAnchorGeneration;
        Dispatcher.UIThread.Post(
            () => RestoreReaderViewportAnchor(generation),
            // Loaded runs after the new measure/arrange pass but before the
            // next input event, so compensation cannot swallow part of an
            // in-progress wheel or touch gesture.
            DispatcherPriority.Loaded);
    }

    private ReaderPageView? FindVisibleReaderAnchor(
        ScrollViewer scrollViewer,
        ReaderPageViewModel changingPage)
    {
        ReaderPageView? pageCoveringViewportTop = null;
        var coveringPageTop = double.NegativeInfinity;
        ReaderPageView? closestVisiblePage = null;
        var closestDistance = double.PositiveInfinity;
        var viewportHeight = Math.Max(0, scrollViewer.Viewport.Height);
        foreach (var pageView in ReaderPages
                     .GetVisualDescendants()
                     .OfType<ReaderPageView>())
        {
            // Anchoring to the page whose bitmap is appearing is unstable:
            // its own height is the value being replaced. Prefer the next
            // already-measured page, particularly when reversing upward.
            if (ReferenceEquals(pageView.DataContext, changingPage))
            {
                continue;
            }

            if (pageView.TranslatePoint(default, scrollViewer) is not
                { } topLeft)
            {
                continue;
            }

            var top = topLeft.Y;
            var bottom = top + pageView.Bounds.Height;
            if (bottom <= 0 || top >= viewportHeight)
            {
                continue;
            }

            if (top <= 0 && bottom > 0 && top > coveringPageTop)
            {
                pageCoveringViewportTop = pageView;
                coveringPageTop = top;
            }

            var distance = Math.Abs(top);
            if (distance < closestDistance)
            {
                closestVisiblePage = pageView;
                closestDistance = distance;
            }
        }

        return pageCoveringViewportTop ?? closestVisiblePage;
    }

    private void RestoreReaderViewportAnchor(int generation)
    {
        if (generation != _readerViewportAnchorGeneration)
        {
            return;
        }

        _readerViewportRestoreScheduled = false;
        var anchor = _readerViewportAnchor;
        _readerViewportAnchor = null;
        var scrollViewer = _readerScrollViewer;
        if (anchor is null ||
            scrollViewer is null ||
            _readerViewModel is not { IsContinuous: true })
        {
            return;
        }

        var anchorView = ReaderPages
            .GetVisualDescendants()
            .OfType<ReaderPageView>()
            .FirstOrDefault(view =>
                ReferenceEquals(view.DataContext, anchor.Value.Page));
        if (anchorView?.TranslatePoint(default, scrollViewer) is not
            { } anchorTopLeft)
        {
            return;
        }

        var currentOffset = scrollViewer.Offset;
        var retainedOffset =
            ReaderViewportRetention.CalculateCompensatedOffset(
                anchor.Value.ScrollOffset,
                currentOffset.Y,
                anchor.Value.ViewportTop,
                anchorTopLeft.Y,
                scrollViewer.Extent.Height,
                scrollViewer.Viewport.Height);
        if (Math.Abs(retainedOffset - currentOffset.Y) <= 0.5)
        {
            return;
        }

        // Treat this as layout compensation, not an upward/downward reader
        // gesture when ScrollChanged updates the auto-hiding controls.
        _lastReaderOffsetY = retainedOffset;
        _hasReaderOffset = true;
        scrollViewer.Offset = currentOffset.WithY(retainedOffset);
    }

    private void ReaderPages_OnScrollChanged(
        object? sender,
        ScrollChangedEventArgs eventArgs)
    {
        if (eventArgs.Source is not ScrollViewer scrollViewer)
        {
            return;
        }

        _readerScrollViewer = scrollViewer;
        var offset = Math.Max(0, scrollViewer.Offset.Y);
        var viewportHeight = Math.Max(0, scrollViewer.Viewport.Height);
        var extentHeight = Math.Max(0, scrollViewer.Extent.Height);
        var endThreshold = Math.Max(36, viewportHeight * 0.06);
        var isAtEnd =
            extentHeight > 0 &&
            offset + viewportHeight >= extentHeight - endThreshold;
        var delta = _hasReaderOffset
            ? offset - _lastReaderOffsetY
            : 0;

        var wasAtEnd = _readerIsAtEnd;
        _readerIsAtEnd = isAtEnd;
        if (isAtEnd)
        {
            _readerControlsHideTimer.Stop();
            _readerControlsRevealed = true;
        }
        else if (offset <= 64)
        {
            _readerControlsHideTimer.Stop();
            _readerControlsRevealed = false;
        }
        else if (_hasReaderOffset && delta < -6)
        {
            _readerControlsRevealed = true;
            _readerControlsHideTimer.Stop();
            _readerControlsHideTimer.Start();
        }
        else if (_hasReaderOffset && delta > 3)
        {
            _readerControlsHideTimer.Stop();
            _readerControlsRevealed = false;
        }
        else if (wasAtEnd)
        {
            _readerControlsRevealed = false;
        }

        _lastReaderOffsetY = offset;
        _hasReaderOffset = true;
        // Animation changes offset every frame; preload/progress needs only
        // a few updates a second, avoiding repeated scans of a long chapter.
        if (ShouldReportReaderMotionPage()) ReportVisibleReaderPage(scrollViewer);
        UpdateReaderControlVisibility();
    }

    /// <summary>
    /// Tells the reader which page the viewport is on, so it can decode around
    /// it and record where reading got to.
    /// <para>
    /// The page covering the top of the viewport, rather than the nearest one:
    /// that is the page being read, and it is stable while a tall page fills
    /// the screen. Falls back to whatever is closest when a gap is in view.
    /// </para>
    /// </summary>
    private void ReportVisibleReaderPage(ScrollViewer scrollViewer)
    {
        if (_readerViewModel is not { IsContinuous: true } reader)
        {
            return;
        }

        ReaderPageViewModel? visiblePage = null;
        var closestDistance = double.PositiveInfinity;
        foreach (var pageView in ReaderPages
                     .GetVisualDescendants()
                     .OfType<ReaderPageView>())
        {
            if (pageView.DataContext is not ReaderPageViewModel page ||
                pageView.TranslatePoint(default, scrollViewer) is not
                { } topLeft)
            {
                continue;
            }

            var top = topLeft.Y;
            var bottom = top + pageView.Bounds.Height;
            if (top <= 0 && bottom > 0)
            {
                visiblePage = page;
                break;
            }

            var distance = Math.Abs(top);
            if (top >= 0 && distance < closestDistance)
            {
                visiblePage = page;
                closestDistance = distance;
            }
        }

        if (visiblePage is not null)
        {
            reader.ReportVisiblePage(visiblePage.Position);
        }
    }

    /// <summary>
    /// A chapter short enough to give every page a container gets the exact,
    /// unmoving extent that comes with it. Longer ones go back to virtualizing,
    /// where the containers stay affordable and the drift comes back with them.
    /// </summary>
    private void ApplyReaderPagePanel()
    {
        const int MaximumUnvirtualizedPages = 400;
        var pageCount = _readerViewModel?.Pages.Count ?? 0;
        var virtualize = pageCount > MaximumUnvirtualizedPages;
        if (ReaderPages.Classes.Contains("virtualized-pages") == virtualize)
        {
            return;
        }

        if (virtualize)
        {
            ReaderPages.Classes.Add("virtualized-pages");
        }
        else
        {
            ReaderPages.Classes.Remove("virtualized-pages");
        }
    }

    private void ReaderTopHoverZone_OnPointerEntered(
        object? sender,
        PointerEventArgs eventArgs)
    {
        // Touch contacts also raise pointer-entered; they are handled on press
        // so a tap can toggle the toolbar instead of pinning it open.
        if (eventArgs.Pointer.Type is PointerType.Touch or PointerType.Pen)
        {
            return;
        }

        ShowReaderToolbar(autoHide: false);
    }

    private void ReaderTopHoverZone_OnPointerPressed(
        object? sender,
        PointerPressedEventArgs eventArgs)
    {
        var isTouch = eventArgs.Pointer.Type is PointerType.Touch or PointerType.Pen;
        if (isTouch && ReaderToolbar.IsVisible && !_isTelevision)
        {
            HideReaderToolbar();
            return;
        }

        ShowReaderToolbar(autoHide: isTouch);
    }

    private void ReaderToolbar_OnPointerExited(
        object? sender,
        PointerEventArgs eventArgs)
    {
        // Touch has no hover, so tapping outside or the auto-hide timer closes it.
        if (_isTelevision ||
            eventArgs.Pointer.Type is PointerType.Touch or PointerType.Pen)
        {
            return;
        }

        HideReaderToolbar();
    }

    private void ShowReaderToolbar(bool autoHide)
    {
        ReaderToolbar.IsVisible = true;
        _readerToolbarHideTimer.Stop();
        if (autoHide && !_isTelevision)
        {
            _readerToolbarHideTimer.Start();
        }
    }

    private void HideReaderToolbar()
    {
        _readerToolbarHideTimer.Stop();
        if (ReaderSettingsPanel.IsVisible) return;
        ReaderToolbar.IsVisible = false;
    }

    private void ScrollToTop_OnClick(
        object? sender,
        RoutedEventArgs eventArgs)
    {
        if (_readerScrollViewer is not null)
        {
            _readerScrollViewer.ScrollToHome();
        }
        else
        {
            ReaderPages.ScrollIntoView(0);
        }

        ResetReaderScrollState();
    }

    private void ResetReaderScrollState()
    {
        StopReaderMotion();
        ResetReaderViewportRetention();
        _lastReaderOffsetY = 0;
        _hasReaderOffset = false;
        _readerControlsRevealed = false;
        _readerIsAtEnd = false;
        _readerControlsHideTimer.Stop();
        _readerToolbarHideTimer.Stop();
        ReaderToolbar.IsVisible = _isTelevision || ReaderSettingsPanel.IsVisible;
        UpdateReaderControlVisibility();
    }

    private void ResetReaderViewportRetention()
    {
        _readerViewportAnchor = null;
        _readerViewportRestoreScheduled = false;
        _readerViewportAnchorGeneration++;
    }

    private void UpdateReaderControlVisibility()
    {
        if (UsesTouchLayout)
        {
            UpdatePageOptionsOffset();
        }

        var reader = _readerViewModel;
        var viewModel = _navigationViewModel;
        if (reader is null || viewModel is null)
        {
            ScrollToTopButton.IsVisible = false;
            ReaderChapterNavigationBanner.IsVisible = false;
            return;
        }

        // The dock remains available for settings and chapter navigation.
        ReaderChapterNavigationBanner.IsVisible = true;
        UpdatePageOptionsOffset();
        if (reader.IsPaged)
        {
            ScrollToTopButton.IsVisible = false;
            return;
        }

        var offset = _readerScrollViewer?.Offset.Y ?? 0;
        ScrollToTopButton.IsVisible =
            _readerControlsRevealed && offset > 64;
    }

    private void UpdateNavigationPresentation()
    {
        if (_playerFullscreenApplied)
        {
            return;
        }

        var isCollapsed =
            _navigationViewModel?.IsNavigationCollapsed is true;
        var hasCollapsedClass =
            Shell.Classes.Contains("nav-collapsed");
        if (isCollapsed && !hasCollapsedClass)
        {
            Shell.Classes.Add("nav-collapsed");
        }
        else if (!isCollapsed && hasCollapsedClass)
        {
            Shell.Classes.Remove("nav-collapsed");
        }

        ApplyGridDefinitions(
            _usesCompactLayout is true,
            _usesNarrowLayout is true);
    }

    private void ApplyTelevisionMode(bool isTelevision)
    {
        _isTelevision = isTelevision;
        // A fresh television session starts on the menu, so it starts wide.
        _isTelevisionMenuExpanded = true;
        ReaderToolbar.IsVisible = isTelevision;
        ReaderTopHoverZone.IsVisible = !isTelevision;
        var hasTvClass = Shell.Classes.Contains("tv");
        if (isTelevision && !hasTvClass)
        {
            Shell.Classes.Add("tv");
        }
        else if (!isTelevision && hasTvClass)
        {
            Shell.Classes.Remove("tv");
        }

        ConfigureTelevisionTextInputs();
        // Shell mode can arrive after the first size notification. Invalidate
        // the breakpoint cache or the TV rail retains the desktop's 256px
        // content column until a later resize.
        _usesCompactLayout = null;
        _usesNarrowLayout = null;
        UpdateCompactLayout(Bounds.Width);
        ApplyTelevisionMenuPresentation();
        UpdateToolPanels();
    }

    /// <summary>
    /// Whether the search and filter panels hide behind their toggle.
    ///
    /// Television needs it because a remote has to step past every field to reach the
    /// content. A handset needs it for room: the Live TV filters are three stacked
    /// fields and the page lays the channel list out beneath them, so left open on a
    /// phone they take the whole viewport and the list is measured into nothing - a page
    /// that looks empty and will not scroll, because there is nothing below the fold to
    /// scroll to.
    /// </summary>
    private bool UsesCollapsibleTools =>
        _isTelevision || _usesNarrowLayout is true;

    private void UpdateToolPanels()
    {
        // A window can be dragged back out of the narrow range, so every branch below
        // assigns rather than leaving whatever the last pass set.
        var collapsible = UsesCollapsibleTools;

        var showCatalog =
            _navigationViewModel?.ShowCatalogOverviewTools is true;
        CatalogToolsToggleButton.IsVisible = showCatalog && collapsible;
        CatalogSearchPanel.IsVisible =
            showCatalog && (!collapsible || _catalogToolsExpanded);
        CatalogFilterPanel.IsVisible =
            showCatalog && (!collapsible || _catalogToolsExpanded);

        var showLiveTv =
            _navigationViewModel?.IsLiveTvPageVisible is true;
        LiveTvToolsToggleButton.IsVisible = showLiveTv && collapsible;
        LiveTvFiltersPanel.IsVisible =
            !collapsible || (showLiveTv && _liveTvToolsExpanded);

        // The description wraps to five lines on a handset, above the fold, saying what
        // the page title already says.
        LiveTvDescriptionText.IsVisible = !collapsible;

        // Short labels where the header is tight; the full ones everywhere else.
        LiveTvToolsToggleButton.Content = UiTextCatalog.Get(
            collapsible ? "SearchAndFiltersShort" : "SearchAndFilters");
        LiveTvRefreshButton.Content = UiTextCatalog.Get(
            collapsible ? "LiveTvRefreshShort" : "LiveTvRefresh");
    }

    private void CatalogToolsToggleButton_OnClick(
        object? sender,
        RoutedEventArgs eventArgs)
    {
        _catalogToolsExpanded = !_catalogToolsExpanded;
        UpdateToolPanels();
    }

    private void LiveTvToolsToggleButton_OnClick(
        object? sender,
        RoutedEventArgs eventArgs)
    {
        _liveTvToolsExpanded = !_liveTvToolsExpanded;
        UpdateToolPanels();
        if (!_isTelevision)
        {
            return;
        }

        Dispatcher.UIThread.Post(
            () =>
            {
                if (_liveTvToolsExpanded)
                {
                    TryFocusFirstLiveTvFilter();
                }
                else
                {
                    _lastFocusedLiveTvFilter = null;
                    LiveTvToolsToggleButton.Focus(
                        NavigationMethod.Directional);
                }
            },
            DispatcherPriority.Loaded);
    }

    private void MainView_OnKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (HandleReaderSettingsKey(eventArgs)) return;
        if (HandleReaderDockKey(eventArgs)) return;
        if (HandleReaderMotionKey(eventArgs)) return;
        TraceTelevisionInput($"down:{eventArgs.Key}");
        if (DataContext is MainViewModel playerViewModel)
        {
            if (eventArgs.Key is Key.F11 &&
                playerViewModel.IsEmbeddedMediaOpen)
            {
                eventArgs.Handled = true;
                playerViewModel.TogglePlayerFullscreenCommand.Execute(null);
                return;
            }

            if (eventArgs.Key is Key.Escape or Key.BrowserBack &&
                playerViewModel.IsPlayerFullscreen)
            {
                eventArgs.Handled = true;
                playerViewModel.TogglePlayerFullscreenCommand.Execute(null);
                return;
            }
        }

        if (eventArgs.Key is Key.Escape or Key.BrowserBack &&
            DataContext is MainViewModel
            {
                IsDonationPromptVisible: true
            } donationViewModel)
        {
            eventArgs.Handled = true;
            donationViewModel.DismissDonationPromptCommand.Execute(null);
            return;
        }

        if (!_isTelevision)
        {
            if (DataContext is MainViewModel desktopViewModel &&
                TryHandleReaderNavigationKey(desktopViewModel, eventArgs.Key))
            {
                eventArgs.Handled = true;
            }

            return;
        }

        var nowMilliseconds = Environment.TickCount64;
        var continuesHeldKey =
            nowMilliseconds - _lastTelevisionKeyDownMilliseconds <
            TelevisionAutoRepeatMilliseconds;
        _lastTelevisionKeyDownMilliseconds = nowMilliseconds;
        var staleActivation = _suppressedTelevisionActivationKey is not null &&
            !continuesHeldKey &&
            nowMilliseconds - _televisionActivationStartedMilliseconds >
                TelevisionActivationHoldMilliseconds;

        if (!IsTelevisionActivationKey(eventArgs.Key) ||
            _suppressedTelevisionActivationKey is null || staleActivation)
        {
            // A fresh gesture owns focus. Clearing stale suppression below
            // must not queue another handoff behind this gesture. Auto-repeat
            // from a held Select still belongs to its original transition.
            _televisionContentFocusGeneration++;
            _focusInitialTelevisionPagePending = false;
        }

        // Some Android TV firmwares omit a release event. A later directional
        // press proves the previous activation is over and safely releases the
        // fallback suppression without waiting for its timer.
        if (!IsTelevisionActivationKey(eventArgs.Key) &&
            _suppressedTelevisionActivationKey is not null)
        {
            ClearTelevisionActivationSuppression();
        }

        // An activation that opens the native player hands the matching release
        // to that activity, so this view never sees it. Without an age check the
        // stale suppression swallowed every Center press until its five-second
        // timer expired, which is exactly the window a returning viewer uses.
        // A held key keeps its suppression so one long press stays one press.
        if (staleActivation)
        {
            ClearTelevisionActivationSuppression();
        }

        // While the on-screen keyboard is up the D-pad belongs to it. Some
        // Android TV firmwares hand directional keys to the app instead of
        // consuming them in the input-method window, and moving focus in
        // response takes focus off the field being typed into — which is what
        // tears the keyboard down. Down is where viewers meet it, being the
        // first press after the keyboard opens.
        //
        // A text-editing session alone does not prove a keyboard opened. PCs,
        // hardware keyboards, and TVs without an IME must retain a way out.
        if (IsTelevisionTextInputActive())
        {
            if (IsDirectionalKey(eventArgs.Key))
            {
                if (ShouldReserveTelevisionTextDirections(
                        _televisionKeyboardConfirmed,
                        _televisionInputPane?.State == InputPaneState.Open,
                        OperatingSystem.IsAndroid(),
                        Environment.TickCount64 - _televisionTextInputStartedMilliseconds))
                {
                    eventArgs.Handled = true;
                    return;
                }

                // With a physical keyboard, horizontal arrows still edit the
                // caret. Up/Down end editing and take the normal focus route.
                if (eventArgs.Key is Key.Left or Key.Right)
                {
                    return;
                }
                EndTelevisionTextInput();
            }

            if (eventArgs.Key is Key.Escape or Key.BrowserBack)
            {
                eventArgs.Handled = true;
                EndTelevisionTextInput();
                return;
            }
        }

        // An open drop-down owns the remote, the same way the embedded player
        // does. Avalonia walks its own items and commits on Select; everything
        // below would only drag focus onto the page behind the open list.
        if (IsDropDownActive(
                TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement()
                    as Visual))
        {
            // Back has no meaning inside a list, and left unhandled it would
            // leave the page with the list still open.
            if (eventArgs.Key is Key.Escape or Key.BrowserBack)
            {
                eventArgs.Handled = TryCloseOpenDropDown();
                return;
            }

            // The list consumes the press itself, but some firmwares deliver
            // Center as a key a Button acts on when it is released. Claiming
            // that release stops the one press from carrying on into whatever
            // the closing list leaves focused — which opened a title behind
            // the filter panel.
            if (IsTelevisionActivationKey(eventArgs.Key))
            {
                BeginTelevisionActivationSuppression(eventArgs.Key);
            }

            return;
        }

        if (IsTelevisionActivationKey(eventArgs.Key) &&
            TryHandleTelevisionUpdatePromptActivation(eventArgs.Key))
        {
            eventArgs.Handled = true;
            return;
        }

        if (IsTelevisionActivationKey(eventArgs.Key))
        {
            var activationFocus = TopLevel.GetTopLevel(this)?
                .FocusManager?
                .GetFocusedElement() as Visual;
            if (IsTelevisionFocusLost(activationFocus) &&
                _navigationViewModel?.IsEmbeddedMediaOpen is not true &&
                _navigationViewModel?.IsSettingsOpen is not true &&
                _navigationViewModel?.IsDonationPromptVisible is not true &&
                !IsImmersiveSurfaceOpen)
            {
                // Select should never act on whichever stale control Avalonia
                // last retained. Recover focus and require the next Select to
                // activate the newly highlighted action.
                eventArgs.Handled =
                    _navigationViewModel?.IsLiveTvPageVisible is true
                        ? QueueTelevisionContentFocus(
                            ActivateLiveTvRemoteList)
                        : QueueTelevisionContentFocus(
                            () => TryRestoreTelevisionPageFocus() ||
                                TryFocusFirstTelevisionPageItem());
                return;
            }
        }

        if (IsTelevisionActivationKey(eventArgs.Key) &&
            eventArgs.KeyModifiers == KeyModifiers.None &&
            IsFocusInsideEmbeddedPlayer() &&
            _navigationViewModel?.CanActivateWebPlayerCenter is true)
        {
            eventArgs.Handled = true;
            if (_suppressedTelevisionActivationKey is null)
            {
                BeginTelevisionActivationSuppression(eventArgs.Key);
                _navigationViewModel.TryActivateWebPlayerCenter();
            }
            return;
        }

        if (IsTelevisionActivationKey(eventArgs.Key) &&
            TryBeginTelevisionTextInput())
        {
            eventArgs.Handled = true;
            return;
        }

        if (IsTelevisionActivationKey(eventArgs.Key) &&
            TryQueueFocusedTelevisionCheckBoxToggle(eventArgs.Key))
        {
            eventArgs.Handled = true;
            return;
        }

        if (IsTelevisionActivationKey(eventArgs.Key) &&
            _navigationViewModel?.IsLiveTvPageVisible is true &&
            TryActivateFocusedLiveTvListItem())
        {
            eventArgs.Handled = true;
            return;
        }

        if (IsTelevisionActivationKey(eventArgs.Key) &&
            TryQueueFocusedTelevisionButtonActivation(eventArgs.Key))
        {
            eventArgs.Handled = true;
            return;
        }

        if (IsTelevisionActivationKey(eventArgs.Key) &&
            IsTelevisionEntryListFocused(
                TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement()
                    as Visual) &&
            TryFocusFirstTelevisionEntryAction())
        {
            eventArgs.Handled = true;
            return;
        }

        if (_isLiveTvRemoteListActive &&
            _navigationViewModel?.IsLiveTvPageVisible is true)
        {
            switch (eventArgs.Key)
            {
                case Key.Up:
                    if (LiveTvChannelList.SelectedIndex <= 0)
                    {
                        SetLiveTvRemoteListActive(false);
                        eventArgs.Handled =
                            TryFocusLiveTvToolsOrFilters();
                        return;
                    }

                    MoveLiveTvSelection(-1);
                    eventArgs.Handled = true;
                    return;
                case Key.Down:
                    MoveLiveTvSelection(1);
                    eventArgs.Handled = true;
                    return;
                case Key.Left:
                    SetLiveTvRemoteListActive(false);
                    eventArgs.Handled = TryOpenTelevisionMenu();
                    return;
                case Key.Right:
                    eventArgs.Handled = true;
                    return;
            }
        }

        if (eventArgs.Key is Key.Escape or Key.BrowserBack &&
            DataContext is MainViewModel { CanNavigateBack: true } viewModel)
        {
            eventArgs.Handled = true;
            _ = viewModel.NavigateBackAsync();
            return;
        }

        if (eventArgs.Key is Key.Escape or Key.BrowserBack &&
            _navigationViewModel?.IsCatalogOpen is true &&
            !_isTelevisionMenuExpanded)
        {
            // Claim Back before the directional-only early return below.
            eventArgs.Handled = TryOpenTelevisionMenu();
            return;
        }

        // While the embedded player holds focus the directional pad belongs to
        // the page inside it, not to the shell; Back still leaves.
        if (_navigationViewModel?.IsEmbeddedMediaOpen is true &&
            IsFocusInsideEmbeddedPlayer())
        {
            return;
        }

        var direction = eventArgs.Key switch
        {
            Key.Left => NavigationDirection.Left,
            Key.Right => NavigationDirection.Right,
            Key.Up => NavigationDirection.Up,
            Key.Down => NavigationDirection.Down,
            _ => (NavigationDirection?)null
        };
        if (direction is null)
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        var focusedElement =
            topLevel?.FocusManager?.GetFocusedElement() as Visual;
        if (TryHandleTelevisionUpdatePromptNavigation(
                focusedElement,
                direction.Value))
        {
            eventArgs.Handled = true;
            return;
        }

        if (_navigationViewModel?.IsDonationPromptVisible is true &&
            DonationPrompt.IsVisible)
        {
            if (ReferenceEquals(focusedElement, DonationBodyScroll))
            {
                var maximum = Math.Max(0, DonationBodyScroll.Extent.Height -
                    DonationBodyScroll.Viewport.Height);
                var delta = direction.Value == NavigationDirection.Down ? 1 : -1;
                if (direction.Value is NavigationDirection.Up or NavigationDirection.Down)
                {
                    var next = Math.Clamp(DonationBodyScroll.Offset.Y +
                        delta * DonationBodyScroll.Viewport.Height * .7, 0, maximum);
                    if (Math.Abs(next - DonationBodyScroll.Offset.Y) > 1)
                    {
                        DonationBodyScroll.Offset = new Vector(0, next);
                    }
                    else if (direction.Value == NavigationDirection.Down)
                    {
                        DonationDismissButton.Focus(NavigationMethod.Directional);
                    }
                    else
                    {
                        DonationCloseButton.Focus(NavigationMethod.Directional);
                    }
                }
                eventArgs.Handled = true;
                return;
            }

            if (direction.Value == NavigationDirection.Up &&
                (ReferenceEquals(focusedElement, DonationDismissButton) ||
                 ReferenceEquals(focusedElement, DonationCopyButton)))
            {
                DonationBodyScroll.Focus(NavigationMethod.Directional);
                eventArgs.Handled = true;
                return;
            }

            eventArgs.Handled =
                focusedElement is not null &&
                DonationPrompt.IsVisualAncestorOf(focusedElement)
                    ? TryMoveTelevisionPageFocus(
                        DonationPrompt,
                        focusedElement,
                        direction.Value) || true
                    : TryFocusDonationPrompt() || true;
            return;
        }

        // Settings is modal, and the page it covers is still mounted behind the
        // scrim. Without confining the beam, a press walked straight through
        // into shelves the viewer cannot see and left focus somewhere the
        // scrim was hiding.
        if (_navigationViewModel?.IsSettingsOpen is true &&
            SettingsOverlay.IsVisible)
        {
            eventArgs.Handled =
                focusedElement is not null &&
                SettingsOverlay.IsVisualAncestorOf(focusedElement)
                    ? TryMoveTelevisionPageFocus(
                        SettingsOverlay,
                        focusedElement,
                        direction.Value) || true
                    // Consumed either way: if the layer has nothing focusable
                    // realized yet, leaving the key unhandled let Avalonia's
                    // default navigation move focus behind the scrim.
                    : TryFocusSettingsOverlay() || true;
            return;
        }

        // The search field is the top of the catalog tools, but it is not a
        // catalog card, so it never reached the edge guard that keeps a remote
        // inside the result set. Up escaped to the tools toggle in the header —
        // a collapse button, from which Up does nothing and the field is only
        // reachable again by going down and then left twice. To a viewer that
        // reads as the search bar throwing focus away the moment they try to
        // write, so the panel holds on to it instead.
        if (direction.Value is NavigationDirection.Up &&
            focusedElement is not null &&
            CatalogSearchPanel.IsVisible &&
            CatalogSearchPanel.IsVisualAncestorOf(focusedElement))
        {
            eventArgs.Handled = true;
            return;
        }

        if (IsTelevisionFocusLost(focusedElement))
        {
            // A web stream replaces the page with a browser surface, and the
            // control that had focus goes with it. Hand the remote to the
            // player rather than to a detail page hidden behind it.
            if (_navigationViewModel?.IsEmbeddedMediaOpen is true &&
                EmbeddedPlayerContent.Focus(NavigationMethod.Directional))
            {
                eventArgs.Handled = true;
            }
            else if (_navigationViewModel?.IsReaderOpen is true &&
                TryFocusTelevisionReader())
            {
                eventArgs.Handled = true;
            }
            else if (direction.Value is not NavigationDirection.Left &&
                     _navigationViewModel?.IsLiveTvPageVisible is true)
            {
                QueueTelevisionContentFocus(
                    ActivateLiveTvRemoteList);
                eventArgs.Handled = true;
            }
            else if (direction.Value is not NavigationDirection.Left &&
                     QueueTelevisionContentFocus(
                         () => TryRestoreTelevisionPageFocus() ||
                             TryFocusFirstTelevisionPageItem()))
            {
                eventArgs.Handled = true;
            }
            else
            {
                eventArgs.Handled = TryOpenTelevisionMenu();
            }

            return;
        }

        var focusIsInMenu =
            focusedElement is not null &&
            Sidebar.IsVisualAncestorOf(focusedElement);
        var focusIsInPage =
            focusedElement is not null &&
            MainContent.IsVisualAncestorOf(focusedElement);
        var focusIsOnLiveTvList =
            focusedElement is not null &&
            (ReferenceEquals(focusedElement, LiveTvChannelList) ||
             LiveTvChannelList.IsVisualAncestorOf(focusedElement));
        if (focusIsInMenu &&
            direction.Value is
                NavigationDirection.Up or NavigationDirection.Down)
        {
            _ = TryMoveTelevisionMenuFocus(
                focusedElement!,
                direction.Value);
            eventArgs.Handled = true;
            return;
        }

        if (focusIsInMenu &&
            direction.Value is NavigationDirection.Right)
        {
            if (_navigationViewModel?.IsLiveTvPageVisible is true)
            {
                QueueTelevisionContentFocus(
                    ActivateLiveTvRemoteList);
                eventArgs.Handled = true;
                return;
            }

            QueueTelevisionContentFocus(
                () => TryRestoreTelevisionPageFocus() ||
                    TryFocusFirstTelevisionPageItem());
            eventArgs.Handled = true;
            return;
        }
        else if (!focusIsInPage &&
                 direction.Value is NavigationDirection.Right &&
                 _navigationViewModel?.IsLiveTvPageVisible is true)
        {
            QueueTelevisionContentFocus(
                ActivateLiveTvRemoteList);
            eventArgs.Handled = true;
            return;
        }

        // Up/Down inside the sidebar belong to the sidebar's normal linear
        // navigation. Feeding them into page geometry made Down from Home jump
        // straight into a poster instead of selecting Movies.
        if (focusIsInMenu)
        {
            // Left and any unavailable vertical move stay in the menu. This
            // prevents global focus search from leaking into the page behind
            // the expanded overlay.
            eventArgs.Handled = true;
            return;
        }

        // Right steps from the entry list into the selected entry's actions.
        if (direction.Value is NavigationDirection.Right &&
            IsTelevisionEntryListFocused(focusedElement) &&
            TryFocusFirstTelevisionEntryAction())
        {
            eventArgs.Handled = true;
            return;
        }

        // Once focus is on an action inside an entry it stays within the list.
        // The entries are virtualized, so letting a press fall through to the
        // ListBox scrolls the focused button out of existence and dumps focus
        // on the window root, where the pad appears to stop working entirely.
        if (IsFocusInsideTelevisionEntryList(focusedElement) &&
            !IsTelevisionEntryListFocused(focusedElement))
        {
            var previousEntry = focusedElement!.GetVisualAncestors()
                .OfType<ListBoxItem>().FirstOrDefault();
            if (TryMoveTelevisionPageFocus(
                    DetailsEntriesList,
                    focusedElement!,
                    direction.Value))
            {
                // Changing chapter re-enters at that chapter's leading action.
                // Without this the pad kept whichever column it was in, so a
                // viewer who once touched "mark as seen" was handed it again
                // on every chapter after it.
                if (direction.Value is
                    NavigationDirection.Up or NavigationDirection.Down)
                {
                    FocusLeadingEntryActionAfterLayout(previousEntry);
                }

                eventArgs.Handled = true;
                return;
            }

            // Nothing realized that way. Falling back to focusing the list
            // itself put the ring around the whole chapter list and took the
            // per-entry actions out of reach, which is what a long list looked
            // like from about the fifth chapter down.
            if (direction.Value is
                NavigationDirection.Up or NavigationDirection.Down)
            {
                eventArgs.Handled =
                    TryStepTelevisionEntryList(
                        focusedElement!,
                        direction.Value);
                return;
            }

            // Left is the way out of the list; right has nowhere to go.
            eventArgs.Handled =
                direction.Value is not NavigationDirection.Left ||
                TryOpenTelevisionMenu();
            return;
        }

        // The continuous page list is drawn behind the reader's floating
        // toolbar, so it never measures as being "below" it and a geometric
        // search skipped straight past the reader into the shell menu. Step
        // into it explicitly instead.
        if (_navigationViewModel?.IsReaderOpen is true &&
            _navigationViewModel.Reader is { IsContinuous: true } &&
            direction.Value is NavigationDirection.Down &&
            IsFocusInsideReaderToolbar(focusedElement))
        {
            eventArgs.Handled =
                ReaderPages.Focus(NavigationMethod.Directional);
            return;
        }

        // Continuous reading is one long scrolling surface, so the pad scrolls
        // it instead of searching for controls. Letting the generic spatial
        // search run here sent Up straight out of the page and left focus
        // stranded once the list recycled what it had moved to.
        if (_navigationViewModel?.IsReaderOpen is true &&
            IsFocusInsideContinuousReader(focusedElement))
        {
            switch (direction.Value)
            {
                case NavigationDirection.Up
                    when IsContinuousReaderAtTop():
                    eventArgs.Handled =
                        ReaderBackButton.Focus(NavigationMethod.Directional);
                    return;
                case NavigationDirection.Up:
                    eventArgs.Handled = TryScrollContinuousReader(-1);
                    return;
                case NavigationDirection.Down:
                    eventArgs.Handled = TryScrollContinuousReader(1);
                    return;
                case NavigationDirection.Left:
                    eventArgs.Handled = TryOpenTelevisionMenu();
                    return;
                default:
                    eventArgs.Handled = true;
                    return;
            }
        }

        // Reading by remote is a left/right activity. Outside the reader's own
        // toolbar the horizontal pad turns pages directly instead of hunting
        // for the footer buttons and pressing Center once per page.
        if (_navigationViewModel?.IsReaderOpen is true &&
            direction.Value is
                NavigationDirection.Left or NavigationDirection.Right &&
            !IsFocusInsideReaderToolbar(focusedElement) &&
            TryTurnTelevisionReaderPage(direction.Value))
        {
            eventArgs.Handled = true;
            return;
        }

        if (_navigationViewModel?.IsLiveTvPageVisible is true &&
            TryHandleLiveTvToolNavigation(
                focusedElement,
                direction.Value))
        {
            eventArgs.Handled = true;
            return;
        }

        if (focusIsOnLiveTvList &&
            focusedElement is not Button)
        {
            if (direction.Value is NavigationDirection.Left)
            {
                eventArgs.Handled = TryOpenTelevisionMenu();
            }
            else if (direction.Value is NavigationDirection.Right)
            {
                eventArgs.Handled = true;
            }

            // Up and Down remain unhandled so ListBox changes its selection
            // and scrolls the chosen channel into view.
            return;
        }

        var focusedButton = focusedElement as Button ??
            focusedElement?.GetVisualAncestors()
                .OfType<Button>()
                .FirstOrDefault();
        var focusedLiveTvItem = focusedElement as Control;
        if (focusedLiveTvItem?.DataContext is not LiveTvChannelViewModel)
        {
            focusedLiveTvItem = focusedElement?.GetVisualAncestors()
                .OfType<Control>()
                .FirstOrDefault(static control =>
                    control.DataContext is LiveTvChannelViewModel);
        }

        // "See all" sits in a shelf's heading, not in its row, so the item
        // collection branch below never claims it and the generic spatial
        // search would answer Left by dropping into the row underneath — the
        // one place a card is guaranteed not to be. It gets a card's edge
        // semantics instead: Left opens the menu, Right stays put.
        if (focusedButton?.Classes.Contains("shelf-see-all") is true &&
            direction.Value is
                NavigationDirection.Left or NavigationDirection.Right)
        {
            eventArgs.Handled =
                direction.Value is not NavigationDirection.Left ||
                TryOpenTelevisionMenu();
            return;
        }

        var focusIsInItemCollection =
            focusedButton?.Classes.Contains("catalog-card-button") is true ||
            focusedButton?.Classes.Contains("live-tv-channel") is true ||
            focusedLiveTvItem is not null;
        var itemCollection = focusIsInItemCollection
            ? (focusedButton as Visual ?? focusedLiveTvItem)?.GetVisualAncestors()
                .OfType<ItemsControl>()
                .FirstOrDefault()
            : null;
        if (itemCollection is not null)
        {
            if (TryMoveTelevisionPageFocus(
                    itemCollection,
                    focusedElement!,
                    direction.Value))
            {
                eventArgs.Handled = true;
                return;
            }

            if (direction.Value is
                    NavigationDirection.Up or NavigationDirection.Down &&
                TryMoveBetweenShelves(
                    itemCollection,
                    focusedElement!,
                    direction.Value))
            {
                eventArgs.Handled = true;
                return;
            }

            if (direction.Value is NavigationDirection.Left)
            {
                eventArgs.Handled = TryOpenTelevisionMenu();
                return;
            }

            if (direction.Value is NavigationDirection.Up &&
                focusedButton?.Classes.Contains(
                    "catalog-card-button") is true &&
                TryFocusCatalogTools(focusedElement!))
            {
                eventArgs.Handled = true;
                return;
            }

            if (direction.Value is NavigationDirection.Down &&
                focusedButton?.Classes.Contains(
                    "catalog-card-button") is true &&
                TryFocusCatalogPagination())
            {
                eventArgs.Handled = true;
                return;
            }

            // Stay in the result set at its top, bottom, and right edges. This
            // prevents a remote press from jumping back into the search fields.
            eventArgs.Handled = true;
            return;
        }

        // Avalonia's unrestricted spatial search walks every element in every
        // long detail/episode page. On Android TV that made a single key spend
        // several seconds in dispatch and eventually trigger an input ANR.
        // Limit the search to enabled controls on the currently visible page
        // and use a small deterministic distance calculation.
        var moved = TryMoveTelevisionPageFocus(
            MainContent,
            focusedElement!,
            direction.Value);
        if (!moved &&
            direction.Value is NavigationDirection.Down &&
            _navigationViewModel?.IsDetailsOpen is true &&
            !DetailsEntriesList.IsVisualAncestorOf(focusedElement!) &&
            TryFocusDetailsEntries())
        {
            moved = true;
        }

        if (moved)
        {
            eventArgs.Handled = true;
            return;
        }

        // Nothing to the left inside the page opens the menu. Every other edge
        // holds position, avoiding an unpredictable default focus jump into a
        // hidden or unrelated control.
        eventArgs.Handled = direction.Value is not NavigationDirection.Left ||
            TryOpenTelevisionMenu();
    }

    private bool TryHandleTelevisionUpdatePromptNavigation(
        Visual? focusedElement,
        NavigationDirection direction)
    {
        if (_navigationViewModel?.IsDonationPromptVisible is true ||
            _navigationViewModel?.IsUpdatePromptVisible is not true)
        {
            return false;
        }

        _navigationViewModel.KeepUpdatePromptOpenForInteraction();
        SetLiveTvRemoteListActive(false);
        var focusIsInPrompt =
            focusedElement is not null &&
            (ReferenceEquals(focusedElement, UpdatePrompt) ||
             UpdatePrompt.IsVisualAncestorOf(focusedElement));
        if (!focusIsInPrompt)
        {
            FocusUpdatePromptForTelevision();
            return true;
        }

        // This modal has a fixed three-control topology. Direct focus moves
        // avoid a visual-tree geometry scan inside Android's key dispatch,
        // which can exceed the five-second ANR deadline on low-power TV boxes.
        var focusedButton = focusedElement as Button ??
            focusedElement?.GetVisualAncestors()
                .OfType<Button>()
                .FirstOrDefault();
        if (direction is NavigationDirection.Up &&
            !ReferenceEquals(focusedButton, DismissUpdatePromptTopButton))
        {
            DismissUpdatePromptTopButton.Focus(
                NavigationMethod.Directional);
        }
        else if (direction is NavigationDirection.Down &&
                 ReferenceEquals(
                     focusedButton,
                     DismissUpdatePromptTopButton))
        {
            InstallAvailableUpdateButton.Focus(
                NavigationMethod.Directional);
        }
        else if (direction is NavigationDirection.Left &&
                 ReferenceEquals(
                     focusedButton,
                     InstallAvailableUpdateButton))
        {
            DismissUpdatePromptButton.Focus(
                NavigationMethod.Directional);
        }
        else if (direction is NavigationDirection.Right &&
                 !ReferenceEquals(
                     focusedButton,
                     InstallAvailableUpdateButton))
        {
            InstallAvailableUpdateButton.Focus(
                NavigationMethod.Directional);
        }

        // Keep navigation inside the visible decision card. At an edge, consume
        // the key instead of falling through to the obscured catalog page.
        return true;
    }

    private bool TryHandleTelevisionUpdatePromptActivation(Key activationKey)
    {
        if (_navigationViewModel?.IsDonationPromptVisible is true ||
            _navigationViewModel?.IsUpdatePromptVisible is not true)
        {
            return false;
        }

        _navigationViewModel.KeepUpdatePromptOpenForInteraction();
        SetLiveTvRemoteListActive(false);
        var focusedElement =
            TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement()
                as Visual;
        var focusIsInPrompt =
            focusedElement is not null &&
            (ReferenceEquals(focusedElement, UpdatePrompt) ||
             UpdatePrompt.IsVisualAncestorOf(focusedElement));
        if (!focusIsInPrompt)
        {
            FocusUpdatePromptForTelevision();
            return true;
        }

        // The prompt is a modal decision on television. Consume Center/Enter
        // even if Android temporarily reports focus on a non-button descendant
        // so the same physical press cannot activate the obscured page.
        _ = TryQueueFocusedTelevisionButtonActivation(activationKey);
        return true;
    }

    private void MainView_OnKeyUp(object? sender, KeyEventArgs eventArgs)
    {
        if (_readerSettingsActivationKey == eventArgs.Key)
        {
            _readerSettingsActivationKey = null;
            eventArgs.Handled = true;
            return;
        }
        TraceTelevisionInput($"up:{eventArgs.Key}");
        if (!_isTelevision ||
            _suppressedTelevisionActivationKey is null)
        {
            return;
        }

        // The command was deliberately queued from KeyDown. Swallow the
        // next KeyUp so Button does not activate a second time—possibly
        // after navigation has already moved focus to a control on a new page.
        // Android TV firmwares do not all map DPAD_CENTER to the same Avalonia
        // Key value on down and up, so matching the enum here is unsafe.
        ClearTelevisionActivationSuppression();
        eventArgs.Handled = true;
    }

    private bool TryQueueFocusedTelevisionButtonActivation(Key activationKey)
    {
        var focused =
            TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement()
                as Visual;
        var button = focused as Button ??
            focused?.GetVisualAncestors().OfType<Button>().FirstOrDefault();
        if (button is not { IsVisible: true, IsEnabled: true })
        {
            return false;
        }

        return QueueTelevisionButtonActivation(button, activationKey);
    }

    private bool QueueTelevisionButtonActivation(
        Button button,
        Key activationKey)
    {
        if (_suppressedTelevisionActivationKey is not null)
        {
            return true;
        }

        // Complete key dispatch first. Provider-backed commands can perform
        // some synchronous preparation before their first await, which must
        // not hold Android's input dispatcher.
        Dispatcher.UIThread.Post(
            () =>
            {
                if (!button.IsVisible || !button.IsEnabled)
                {
                    return;
                }

                var currentFocus =
                    TopLevel.GetTopLevel(this)?.FocusManager
                        ?.GetFocusedElement() as Visual;
                if (!ReferenceEquals(currentFocus, button) &&
                    (currentFocus is null ||
                     !button.IsVisualAncestorOf(currentFocus)))
                {
                    return;
                }

                // Mirror Button.OnClick: the click event first, then the
                // command. Running only the command skipped code-behind Click
                // handlers, which is why the television menu's own collapse
                // arrow never hid the menu — its handler holds that state.
                var click = new RoutedEventArgs(Button.ClickEvent);
                button.RaiseEvent(click);
                if (!click.Handled &&
                    button.Command is { } command &&
                    command.CanExecute(button.CommandParameter))
                {
                    command.Execute(button.CommandParameter);
                }

                // Commands commonly replace the focused menu/card before the
                // new controls have completed layout. Follow the transition
                // until a usable target exists instead of waiting for another
                // remote press to repair focus.
                _ = EnsureTelevisionPageFocusAfterTransitionAsync();
            },
            DispatcherPriority.Input);
        BeginTelevisionActivationSuppression(activationKey);
        return true;
    }

    private bool TryQueueFocusedTelevisionCheckBoxToggle(Key activationKey)
    {
        var focused =
            TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement()
                as Visual;
        var checkBox = focused as CheckBox ??
            focused?.GetVisualAncestors().OfType<CheckBox>().FirstOrDefault();
        if (checkBox is not { IsVisible: true, IsEnabled: true })
        {
            return false;
        }

        if (_suppressedTelevisionActivationKey is not null)
        {
            return true;
        }

        // Raising Button.ClickEvent directly does not run ToggleButton's
        // input gesture, so Android TV's OK button could focus a checkbox
        // without ever changing it. Toggle the bound value after key dispatch
        // and keep the existing binding attached.
        Dispatcher.UIThread.Post(
            () =>
            {
                if (!checkBox.IsVisible || !checkBox.IsEnabled)
                {
                    return;
                }

                var currentFocus =
                    TopLevel.GetTopLevel(this)?.FocusManager
                        ?.GetFocusedElement() as Visual;
                if (!ReferenceEquals(currentFocus, checkBox) &&
                    (currentFocus is null ||
                     !checkBox.IsVisualAncestorOf(currentFocus)))
                {
                    return;
                }

                checkBox.SetCurrentValue(
                    CheckBox.IsCheckedProperty,
                    !(checkBox.IsChecked ?? false));
            },
            DispatcherPriority.Input);
        BeginTelevisionActivationSuppression(activationKey);
        return true;
    }

    private async Task ClearTelevisionActivationSuppressionAsync(
        Key activationKey,
        int activationGeneration)
    {
        await Task.Delay(TimeSpan.FromSeconds(5));
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_televisionActivationGeneration == activationGeneration &&
                _suppressedTelevisionActivationKey == activationKey)
            {
                ClearTelevisionActivationSuppression();
            }
        });
    }

    /// <summary>
    /// Claims the release that follows an activation this view has already
    /// dealt with, so one press cannot act twice.
    /// </summary>
    private void BeginTelevisionActivationSuppression(Key activationKey)
    {
        _suppressedTelevisionActivationKey = activationKey;
        _televisionActivationStartedMilliseconds = Environment.TickCount64;
        var activationGeneration = ++_televisionActivationGeneration;
        _ = ClearTelevisionActivationSuppressionAsync(
            activationKey,
            activationGeneration);
    }

    private void ClearTelevisionActivationSuppression()
    {
        _suppressedTelevisionActivationKey = null;
        _televisionActivationGeneration++;
        if (_focusInitialTelevisionPagePending)
        {
            FocusInitialTelevisionPageTarget();
        }
    }

    /// <summary>
    /// The shelf list currently on screen. Home and the media-type pages use
    /// the same template, so the beam that walks between rows has to be told
    /// which of the two lists it is walking.
    /// </summary>
    private ItemsControl? ActiveShelvesControl =>
        _navigationViewModel?.ShowHomeSections is true
            ? HomeSectionsControl
            : _navigationViewModel?.ShowBrowseShelves is true
                ? BrowseShelvesControl
                : null;

    private bool TryMoveBetweenShelves(
        ItemsControl currentShelf,
        Visual focusedElement,
        NavigationDirection direction)
    {
        if (ActiveShelvesControl is not { } shelvesControl ||
            direction is not
                (NavigationDirection.Up or NavigationDirection.Down) ||
            FindFocusableControl(focusedElement) is not { } current ||
            current.TranslatePoint(
                new Point(
                    current.Bounds.Width / 2,
                    current.Bounds.Height / 2),
                MainContent) is not { } currentCenter)
        {
            return false;
        }

        // "See all" sits above its row rather than in it, so a remote can only
        // reach it if the vertical beam counts it as a stop of its own.
        // Pruned per shelf rather than walking the whole list. The shelf list
        // does not virtualize, so all fourteen rows are realized at once, and
        // descending into every card of every row inside key dispatch is real
        // exposure to Android TV's input deadline. A row wholly off screen can
        // hold no candidate that survives the IsVisibleWithin test below, so
        // its card trees are never entered.
        var candidates = EnumerateShelfFocusCandidates(shelvesControl);
        if (ReferenceEquals(shelvesControl, HomeSectionsControl))
        {
            candidates = candidates.Append(HomeLiveTvButton);
        }
        Control? best = null;
        var bestScore = double.MaxValue;
        foreach (var candidate in candidates)
        {
            if (currentShelf.IsVisualAncestorOf(candidate) ||
                !candidate.Focusable ||
                !candidate.IsEnabled ||
                !IsVisibleWithin(candidate, MainContent) ||
                candidate.Bounds.Width <= 0 ||
                candidate.Bounds.Height <= 0 ||
                candidate.TranslatePoint(
                    new Point(
                        candidate.Bounds.Width / 2,
                        candidate.Bounds.Height / 2),
                    MainContent) is not { } candidateCenter)
            {
                continue;
            }

            var vertical = candidateCenter.Y - currentCenter.Y;
            var primary = direction is NavigationDirection.Down
                ? vertical
                : -vertical;
            if (primary <= 2)
            {
                continue;
            }

            var secondary = Math.Abs(candidateCenter.X - currentCenter.X);
            var score = primary + (secondary * 2.5);
            if (score >= bestScore)
            {
                continue;
            }

            best = candidate;
            bestScore = score;
        }

        if (best?.Focus(NavigationMethod.Directional) is not true)
        {
            return false;
        }

        Dispatcher.UIThread.Post(
            () => ScrollControlIntoView(best),
            DispatcherPriority.Background);
        return true;
    }

    private static bool TryMoveTelevisionPageFocus(
        Visual searchRoot,
        Visual focusedElement,
        NavigationDirection direction)
    {
        var current = FindFocusableControl(focusedElement);
        if (current is null ||
            TryGetBoundsWithin(current, searchRoot) is not { } source)
        {
            return false;
        }

        Control? best = null;
        Rect? bestBounds = null;
        foreach (var candidate in searchRoot
                     .GetVisualDescendants()
                     .OfType<Control>())
        {
            if (ReferenceEquals(candidate, current) ||
                !candidate.Focusable ||
                !candidate.IsEnabled ||
                !IsVisibleWithin(candidate, searchRoot) ||
                candidate.Bounds.Width <= 0 ||
                candidate.Bounds.Height <= 0 ||
                TryGetBoundsWithin(candidate, searchRoot) is not
                { } candidateBounds)
            {
                continue;
            }

            if (!IsBetterTelevisionCandidate(
                    direction,
                    source,
                    candidateBounds,
                    bestBounds))
            {
                continue;
            }

            best = candidate;
            bestBounds = candidateBounds;
        }

        if (best?.Focus(NavigationMethod.Directional) is not true)
        {
            return false;
        }

        // Scrolling synchronously from KeyDown can make Android wait for a
        // complete layout before it acknowledges the remote press. Queue it
        // after input instead, and discard stale requests when focus has
        // already moved again.
        Dispatcher.UIThread.Post(
            () =>
            {
                var focused = TopLevel.GetTopLevel(best)?
                    .FocusManager?
                    .GetFocusedElement();
                var focusIsWithinTarget =
                    ReferenceEquals(focused, best) ||
                    focused is Visual focusedVisual &&
                    best.IsVisualAncestorOf(focusedVisual);
                if (focusIsWithinTarget && best.IsVisible)
                {
                    ScrollControlIntoView(best);
                }
            },
            DispatcherPriority.Background);
        return true;
    }

    private static Rect? TryGetBoundsWithin(Control control, Visual searchRoot) =>
        control.TranslatePoint(default, searchRoot) is { } origin
            ? new Rect(origin, control.Bounds.Size)
            : null;

    /// <summary>
    /// Applies Android's directional focus rules instead of a plain
    /// center-to-center score. Scoring by center distance let a press escape
    /// the row it started in: with nothing to the right of the reader's page
    /// bar, "next page" handed focus to the zoom button in the top toolbar
    /// because that was the only control whose center sat further right. A
    /// candidate now has to actually begin beyond the focused control's edge,
    /// and a control sharing the current row or column always wins over one
    /// that merely lies in the general direction.
    /// </summary>
    internal static bool IsBetterTelevisionCandidate(
        NavigationDirection direction,
        Rect source,
        Rect candidate,
        Rect? best)
    {
        if (!IsTelevisionCandidate(source, candidate, direction))
        {
            return false;
        }

        if (best is not { } bestBounds ||
            !IsTelevisionCandidate(source, bestBounds, direction))
        {
            return true;
        }

        if (TelevisionBeamBeats(direction, source, candidate, bestBounds))
        {
            return true;
        }

        if (TelevisionBeamBeats(direction, source, bestBounds, candidate))
        {
            return false;
        }

        return TelevisionWeightedDistance(source, candidate, direction) <
            TelevisionWeightedDistance(source, bestBounds, direction);
    }

    private static bool IsTelevisionCandidate(
        Rect source,
        Rect candidate,
        NavigationDirection direction) =>
        direction switch
        {
            NavigationDirection.Left =>
                (source.Right > candidate.Right ||
                 source.Left >= candidate.Right) &&
                source.Left > candidate.Left,
            NavigationDirection.Right =>
                (source.Left < candidate.Left ||
                 source.Right <= candidate.Left) &&
                source.Right < candidate.Right,
            NavigationDirection.Up =>
                (source.Bottom > candidate.Bottom ||
                 source.Top >= candidate.Bottom) &&
                source.Top > candidate.Top,
            NavigationDirection.Down =>
                (source.Top < candidate.Top ||
                 source.Bottom <= candidate.Top) &&
                source.Bottom < candidate.Bottom,
            _ => false
        };

    /// <summary>
    /// Reports whether <paramref name="first"/> wins purely because it shares
    /// the focused control's row or column while <paramref name="second"/>
    /// does not.
    /// </summary>
    private static bool TelevisionBeamBeats(
        NavigationDirection direction,
        Rect source,
        Rect first,
        Rect second)
    {
        var firstInBeam = TelevisionBeamsOverlap(source, first, direction);
        var secondInBeam = TelevisionBeamsOverlap(source, second, direction);
        if (secondInBeam || !firstInBeam)
        {
            return false;
        }

        if (!IsStrictlyInDirection(source, second, direction))
        {
            return true;
        }

        if (direction is NavigationDirection.Left or NavigationDirection.Right)
        {
            return true;
        }

        // A vertical move keeps the off-axis candidate when it starts before
        // the aligned one ends, so a tall neighbouring column is not skipped.
        return TelevisionMajorAxisDistance(source, first, direction) <
            TelevisionMajorAxisDistanceToFarEdge(source, second, direction);
    }

    private static bool TelevisionBeamsOverlap(
        Rect source,
        Rect candidate,
        NavigationDirection direction) =>
        direction is NavigationDirection.Left or NavigationDirection.Right
            ? candidate.Bottom >= source.Top && candidate.Top <= source.Bottom
            : candidate.Right >= source.Left && candidate.Left <= source.Right;

    private static bool IsStrictlyInDirection(
        Rect source,
        Rect candidate,
        NavigationDirection direction) =>
        direction switch
        {
            NavigationDirection.Left => source.Left >= candidate.Right,
            NavigationDirection.Right => source.Right <= candidate.Left,
            NavigationDirection.Up => source.Top >= candidate.Bottom,
            NavigationDirection.Down => source.Bottom <= candidate.Top,
            _ => false
        };

    private static double TelevisionMajorAxisDistance(
        Rect source,
        Rect candidate,
        NavigationDirection direction) =>
        Math.Max(
            0,
            direction switch
            {
                NavigationDirection.Left => source.Left - candidate.Right,
                NavigationDirection.Right => candidate.Left - source.Right,
                NavigationDirection.Up => source.Top - candidate.Bottom,
                NavigationDirection.Down => candidate.Top - source.Bottom,
                _ => 0d
            });

    private static double TelevisionMajorAxisDistanceToFarEdge(
        Rect source,
        Rect candidate,
        NavigationDirection direction) =>
        Math.Max(
            1,
            direction switch
            {
                NavigationDirection.Left => source.Left - candidate.Left,
                NavigationDirection.Right => candidate.Right - source.Right,
                NavigationDirection.Up => source.Top - candidate.Top,
                NavigationDirection.Down => candidate.Bottom - source.Bottom,
                _ => 0d
            });

    private static double TelevisionMinorAxisDistance(
        Rect source,
        Rect candidate,
        NavigationDirection direction) =>
        direction is NavigationDirection.Left or NavigationDirection.Right
            ? Math.Abs(
                (source.Y + (source.Height / 2)) -
                (candidate.Y + (candidate.Height / 2)))
            : Math.Abs(
                (source.X + (source.Width / 2)) -
                (candidate.X + (candidate.Width / 2)));

    /// <summary>
    /// Weights travel along the pressed direction far more cheaply than drift
    /// across it, so focus prefers a distant control straight ahead over a
    /// close one well off to the side.
    /// </summary>
    private static double TelevisionWeightedDistance(
        Rect source,
        Rect candidate,
        NavigationDirection direction)
    {
        var major = TelevisionMajorAxisDistance(source, candidate, direction);
        var minor = TelevisionMinorAxisDistance(source, candidate, direction);
        return (13 * major * major) + (minor * minor);
    }

    /// <summary>
    /// Reveals a control in every scroll viewer between it and the root, on
    /// both axes.
    ///
    /// A catalog poster now sits inside a horizontal shelf nested in the
    /// vertical page scroll. Taking only the nearest ancestor moved neither of
    /// them: the shelf cannot scroll vertically, so the vertical maximum came
    /// out as zero and the page it was nested in was never asked at all. Each
    /// viewer is asked about each axis, and an axis with nothing to scroll is
    /// skipped rather than clamped to zero.
    /// </summary>
    private static void ScrollControlIntoView(Control target)
    {
        foreach (var scrollViewer in target
                     .GetVisualAncestors()
                     .OfType<ScrollViewer>())
        {
            if (target.TranslatePoint(default, scrollViewer) is not { } topLeft)
            {
                continue;
            }

            var offset = scrollViewer.Offset;
            var horizontal = ResolveScrollOffset(
                topLeft.X,
                target.Bounds.Width,
                scrollViewer.Viewport.Width,
                scrollViewer.Extent.Width,
                offset.X);
            var vertical = ResolveScrollOffset(
                topLeft.Y,
                target.Bounds.Height,
                scrollViewer.Viewport.Height,
                scrollViewer.Extent.Height,
                offset.Y);
            if (horizontal is null && vertical is null)
            {
                continue;
            }

            scrollViewer.Offset = new Vector(
                horizontal ?? offset.X,
                vertical ?? offset.Y);
        }
    }

    /// <summary>
    /// Where one axis must move to reveal the target, or null when it is
    /// already visible or the axis cannot scroll.
    /// </summary>
    private static double? ResolveScrollOffset(
        double start,
        double length,
        double viewport,
        double extent,
        double offset)
    {
        var maximum = Math.Max(0, extent - viewport);
        if (maximum < 1)
        {
            return null;
        }

        // A poster can be nearly as wide as its shelf's viewport. Asking for a
        // full margin on both sides would then be unsatisfiable and the offset
        // would oscillate on every focus move.
        var margin = Math.Min(
            ScrollRevealMargin,
            Math.Max(0, (viewport - length) / 2));
        var end = start + length;
        var delta = start < margin
            ? start - margin
            : end > viewport - margin
                ? end - (viewport - margin)
                : 0;
        return Math.Abs(delta) < 1
            ? null
            : Math.Clamp(offset + delta, 0, maximum);
    }

    private static Control? FindFocusableControl(Visual focusedElement)
    {
        if (focusedElement is Control { Focusable: true } focusedControl)
        {
            return focusedControl;
        }

        return focusedElement.GetVisualAncestors()
            .OfType<Control>()
            .FirstOrDefault(static control => control.Focusable);
    }

    private static bool IsVisibleWithin(
        Control candidate,
        Visual searchRoot)
    {
        if (!candidate.IsVisible)
        {
            return false;
        }

        foreach (var ancestor in candidate.GetVisualAncestors())
        {
            if (ancestor is Control { IsVisible: false })
            {
                return false;
            }

            if (ReferenceEquals(ancestor, searchRoot))
            {
                return true;
            }
        }

        return false;
    }

    private bool QueueTelevisionContentFocus(Func<bool> focusAction) =>
        QueueTelevisionContentFocusCore(
            () => _ = focusAction());

    private bool QueueTelevisionContentFocus(Action focusAction) =>
        QueueTelevisionContentFocusCore(focusAction);

    private bool QueueTelevisionContentFocusCore(Action focusAction)
    {
        var generation = ++_televisionContentFocusGeneration;
        Dispatcher.UIThread.Post(
            () =>
            {
                if (generation != _televisionContentFocusGeneration ||
                    !_isTelevision ||
                    IsImmersiveSurfaceOpen)
                {
                    return;
                }

                focusAction();
                _ = ReinforceTelevisionContentFocusAsync(
                    generation,
                    focusAction);
            },
            DispatcherPriority.Input);
        return true;
    }

    private async Task ReinforceTelevisionContentFocusAsync(
        int generation,
        Action focusAction)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(120));
        await Dispatcher.UIThread.InvokeAsync(
            () =>
            {
                if (generation != _televisionContentFocusGeneration ||
                    !_isTelevision ||
                    IsImmersiveSurfaceOpen)
                {
                    return;
                }

                var focused = TopLevel.GetTopLevel(this)?
                    .FocusManager?
                    .GetFocusedElement() as Visual;
                var focusIsInContent =
                    focused is not null &&
                    (ReferenceEquals(focused, MainContent) ||
                     MainContent.IsVisualAncestorOf(focused));
                if (!focusIsInContent)
                {
                    focusAction();
                }
            },
            DispatcherPriority.Background);
    }

    private bool TryFocusFirstTelevisionPageItem()
    {
        if (_navigationViewModel?.IsDetailsOpen is true)
        {
            // Opening a title should offer to play it. Landing on "back to
            // catalog" made the remote's own Back button redundant and put the
            // one action the viewer came for two presses away.
            if (DetailsContinueButton.IsEnabled &&
                IsVisibleWithin(DetailsContinueButton, MainContent) &&
                DetailsContinueButton.Focus(NavigationMethod.Directional))
            {
                return true;
            }

            // Nothing to resume — the entry list is the next most useful place
            // to be, and back remains one Back press away either way.
            if (TryFocusDetailsEntries())
            {
                return true;
            }

            if (IsVisibleWithin(DetailsBackButton, MainContent) &&
                DetailsBackButton.Focus(NavigationMethod.Directional))
            {
                return true;
            }

            return false;
        }

        var buttons = MainContent
            .GetVisualDescendants()
            .OfType<Button>()
            .Where(button =>
                button.IsEnabled &&
                IsVisibleWithin(button, MainContent))
            .ToArray();
        var target = SelectCatalogFocusTarget(
                buttons,
                _restoreTelevisionCatalogFocus
                    ? _lastFocusedTelevisionCatalogItemId
                    : null) ??
            buttons.FirstOrDefault(static button =>
                button.Focusable);
        var result = target?.Focus(NavigationMethod.Directional) is true;
        if (result &&
            target?.Classes.Contains("catalog-card-button") is true)
        {
            _restoreTelevisionCatalogFocus = false;
        }

        return result;
    }

    private async Task EnsureTelevisionPageFocusAfterTransitionAsync()
    {
        var generation = _televisionContentFocusGeneration;
        for (var attempt = 0; attempt < 16; attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(150));
            var finished = await Dispatcher.UIThread.InvokeAsync(
                () =>
                {
                    if (generation != _televisionContentFocusGeneration ||
                        !_isTelevision ||
                        IsImmersiveSurfaceOpen ||
                        _navigationViewModel?.IsDonationPromptVisible is true ||
                        _navigationViewModel?.IsUpdatePromptVisible is true)
                    {
                        return true;
                    }

                    var focused = TopLevel.GetTopLevel(this)?
                        .FocusManager?
                        .GetFocusedElement() as Visual;
                    if (_navigationViewModel?.IsSettingsOpen is true)
                    {
                        var inSettings = focused is Control settingsControl &&
                            settingsControl.IsEnabled &&
                            IsVisibleWithin(settingsControl, SettingsOverlay);
                        var ready = inSettings || TryFocusSettingsOverlay();
                        if (ready)
                        {
                            _focusInitialTelevisionPagePending = false;
                        }
                        return ready;
                    }

                    var focusMatchesCurrentPage = focusedControlIsCurrentPage(
                        focused);
                    if (focused is Control focusedControl &&
                        focusedControl.IsEnabled &&
                        IsVisibleWithin(focusedControl, MainContent) &&
                        focusMatchesCurrentPage)
                    {
                        _focusInitialTelevisionPagePending = false;
                        return true;
                    }

                    if (_suppressedTelevisionActivationKey is not null &&
                        Environment.TickCount64 -
                            _televisionActivationStartedMilliseconds <
                            TelevisionActivationHoldMilliseconds)
                    {
                        return false;
                    }

                    if (_navigationViewModel?.IsLiveTvPageVisible is true)
                    {
                        ActivateLiveTvRemoteList();
                        return true;
                    }

                    var restored = TryRestoreTelevisionPageFocus() ||
                        TryFocusFirstTelevisionPageItem();
                    if (restored)
                    {
                        _focusInitialTelevisionPagePending = false;
                    }
                    TraceTelevisionInput($"transition:{attempt}:{restored}");
                    return restored;
                },
                DispatcherPriority.Input);
            if (finished)
            {
                return;
            }
        }

        bool focusedControlIsCurrentPage(Visual? focused) =>
            focused is not null &&
            (_navigationViewModel?.IsDetailsOpen is true
                ? DetailsScroll.IsVisualAncestorOf(focused)
                : _navigationViewModel?.IsCatalogOpen is true
                    ? CatalogScroll.IsVisualAncestorOf(focused)
                    : MainContent.IsVisualAncestorOf(focused));
    }

    private bool TryRestoreTelevisionPageFocus()
    {
        if (_navigationViewModel?.IsDetailsOpen is true)
        {
            return TryRestoreTelevisionDetailsFocus();
        }

        // Prefer the actual surviving control: a title can appear in several
        // shelves, and matching only its ID would jump to its first occurrence.
        if (_lastFocusedTelevisionPageControl is not null &&
            _lastFocusedTelevisionPageControl.TryGetTarget(out var target) &&
            target.Focusable &&
            target.IsEnabled &&
            IsVisibleWithin(target, MainContent) &&
            (target.DataContext is not CatalogCardViewModel rememberedCard ||
             rememberedCard.Id == _lastFocusedTelevisionCatalogItemId) &&
            target.Focus(NavigationMethod.Directional))
        {
            _restoreTelevisionCatalogFocus = false;
            return true;
        }

        if (_lastFocusedTelevisionCatalogItemId is { } catalogItemId)
        {
            var restoredCard = MainContent
                .GetVisualDescendants()
                .OfType<Button>()
                .FirstOrDefault(button =>
                    button.Focusable &&
                    button.IsEnabled &&
                    IsVisibleWithin(button, MainContent) &&
                    button.Classes.Contains("catalog-card-button") &&
                    button.DataContext is CatalogCardViewModel card &&
                    card.Id == catalogItemId);
            if (restoredCard?.Focus(NavigationMethod.Directional) is true)
            {
                _restoreTelevisionCatalogFocus = false;
                return true;
            }
        }

        return false;
    }

    private static CatalogContentEntryViewModel? FindDetailsEntry(Control control) =>
        control.DataContext as CatalogContentEntryViewModel ??
        control.GetVisualAncestors().OfType<Control>()
            .Select(static ancestor => ancestor.DataContext)
            .OfType<CatalogContentEntryViewModel>().FirstOrDefault();

    private bool TryRestoreTelevisionDetailsFocus()
    {
        if (_lastFocusedTelevisionDetailsControl is not null &&
            _lastFocusedTelevisionDetailsControl.TryGetTarget(out var target) &&
            target.Focusable && target.IsEnabled &&
            IsVisibleWithin(target, DetailsScroll) &&
            FindDetailsEntry(target)?.Identity.StableKey == _lastFocusedTelevisionEntryKey &&
            target.Focus(NavigationMethod.Directional))
        {
            return true;
        }

        if (_lastFocusedTelevisionEntryKey is null)
        {
            return false;
        }

        // Provider progress replaces entry view models. Match the chapter's
        // stable identity, not its old object or its position in the list.
        var actions = DetailsEntriesList.GetVisualDescendants().OfType<Button>()
            .Where(button => button.Focusable && button.IsEnabled &&
                IsVisibleWithin(button, DetailsEntriesList) &&
                FindDetailsEntry(button)?.Identity.StableKey == _lastFocusedTelevisionEntryKey)
            .ToArray();
        var restored = actions.FirstOrDefault(button =>
            button.Content is string text && text == _lastFocusedTelevisionEntryAction) ??
            SelectLeadingEntryAction(actions);
        return restored?.Focus(NavigationMethod.Directional) is true;
    }

    internal static Button? SelectCatalogFocusTarget(
        IEnumerable<Button> buttons,
        Guid? preferredItemId)
    {
        var catalogButtons = buttons
            .Where(static button =>
                button.Focusable &&
                button.IsEnabled &&
                button.IsVisible &&
                button.Classes.Contains("catalog-card-button"))
            .ToArray();
        return preferredItemId is { } itemId
            ? catalogButtons.FirstOrDefault(button =>
                  button.DataContext is CatalogCardViewModel card &&
                  card.Id == itemId) ?? catalogButtons.FirstOrDefault()
            : catalogButtons.FirstOrDefault();
    }

    private bool TryFocusCatalogTools(Visual focusedElement)
    {
        if (_navigationViewModel?.ShowCatalogOverviewTools is not true ||
            !IsVisibleWithin(CatalogToolsToggleButton, MainContent))
        {
            return false;
        }

        _catalogToolsExpanded = true;
        UpdateToolPanels();

        // Enter the nearest filter row first. Search is above it and remains
        // reachable with another Up press; jumping straight to Search made
        // every provider/category/year filter impossible to revisit by remote.
        if (TryMoveTelevisionPageFocus(
                CatalogFilterPanel,
                focusedElement,
                NavigationDirection.Up))
        {
            return true;
        }

        var filter = CatalogFilterPanel
            .GetVisualDescendants()
            .OfType<Control>()
            .Where(static control =>
                control.Focusable &&
                control.IsVisible &&
                control.IsEnabled)
            .OrderByDescending(static control => control.Bounds.Bottom)
            .ThenBy(static control => control.Bounds.Left)
            .FirstOrDefault();
        if (filter?.Focus(NavigationMethod.Directional) is true)
        {
            Dispatcher.UIThread.Post(
                () => ScrollControlIntoView(filter),
                DispatcherPriority.Background);
            return true;
        }

        return CatalogToolsToggleButton.Focus(NavigationMethod.Directional);
    }

    private static bool IsTelevisionActivationKey(Key key) =>
        key is Key.Enter or Key.Return or Key.Space;

    private static bool IsDirectionalKey(Key key) =>
        key is Key.Left or Key.Right or Key.Up or Key.Down;

    /// <summary>
    /// How close to an edge the scroll counts as being against it. A viewport
    /// rarely lands on the extent exactly, so an exact test would leave a
    /// fractional pixel between the viewer and the next chapter.
    /// </summary>
    private const double ReaderScrollEdgeTolerance = 1;

    /// <summary>
    /// How far one arrow press moves the reader. Roughly a few lines of a
    /// scan — enough to feel like progress, short enough to place a panel.
    /// </summary>
    private const double ReaderLineScrollDelta = 140;

    /// <summary>
    /// How much of the viewport a Page press covers. The remainder is overlap,
    /// so the panel that was at the bottom is still on screen afterwards.
    /// </summary>
    private const double ReaderPageScrollFraction = 0.9;

    /// <summary>
    /// Arrow keys drive the reader on desktop.
    /// <para>
    /// Paged mode steps a page with left and right and falls through to the
    /// chapter on that side once no page is left to reach. Up and down scroll,
    /// so a page zoomed past the window can still be read.
    /// </para>
    /// <para>
    /// Continuous mode scrolls with up and down and only steps a chapter when
    /// the scroll is already against that end — a press with somewhere left to
    /// scroll must never jump the chapter out from under the viewer.
    /// </para>
    /// <para>
    /// Every one of those keys is consumed rather than left to travel on, even
    /// where there is nothing to scroll. Handling only the interesting cases
    /// and letting the rest fall through is what tangled this with focus:
    /// continuous mode's list treats an arrow as a request to move its
    /// selection, which jumps a page and takes focus with it.
    /// </para>
    /// </summary>
    private bool TryHandleReaderNavigationKey(MainViewModel viewModel, Key key)
    {
        if (!viewModel.IsReaderOpen ||
            viewModel.IsSettingsOpen ||
            viewModel.Reader is not { } reader ||
            TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement()
                is TextBox)
        {
            return false;
        }

        if (reader.IsPaged)
        {
            return key switch
            {
                Key.Right or Key.PageDown =>
                    StepReaderPage(viewModel, reader, forward: true),
                Key.Left or Key.PageUp =>
                    StepReaderPage(viewModel, reader, forward: false),
                Key.Up => ScrollReaderBy(
                    ReaderPagedScroll,
                    -ReaderLineScrollDelta),
                Key.Down => ScrollReaderBy(
                    ReaderPagedScroll,
                    ReaderLineScrollDelta),
                _ => false
            };
        }

        if (_readerScrollViewer is not { } scrollViewer)
        {
            return false;
        }

        return key switch
        {
            // Left and right have nothing to scroll in a vertical strip, so
            // they are the chapter step here — the same thing they mean in
            // paged mode once its pages run out.
            Key.Right => StepReaderChapter(viewModel, next: true),
            Key.Left => StepReaderChapter(viewModel, next: false),
            Key.Down => ScrollReaderOrStepChapter(
                viewModel,
                scrollViewer,
                ReaderLineScrollDelta),
            Key.Up => ScrollReaderOrStepChapter(
                viewModel,
                scrollViewer,
                -ReaderLineScrollDelta),
            Key.PageDown => ScrollReaderOrStepChapter(
                viewModel,
                scrollViewer,
                ReaderPageScrollDelta(scrollViewer)),
            Key.PageUp => ScrollReaderOrStepChapter(
                viewModel,
                scrollViewer,
                -ReaderPageScrollDelta(scrollViewer)),
            _ => false
        };
    }

    /// <summary>
    /// Moves the reader's scroll and reports the key as consumed.
    /// <para>
    /// Consumed even when the scroll is already against the end it was asked to
    /// move towards. Letting the key travel on is what put focus into this: in
    /// continuous mode it reaches <c>ReaderPages</c>, whose answer to an arrow
    /// is to move its selection — a whole page at a time, dragging focus along
    /// — and in paged mode it lands on the toolbar behind the page.
    /// </para>
    /// </summary>
    private static bool ScrollReaderBy(ScrollViewer? scrollViewer, double delta)
    {
        if (scrollViewer is null)
        {
            return false;
        }

        var furthest = Math.Max(
            0,
            scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        var target = Math.Clamp(scrollViewer.Offset.Y + delta, 0, furthest);
        if (Math.Abs(target - scrollViewer.Offset.Y) > 0.5)
        {
            scrollViewer.Offset = scrollViewer.Offset.WithY(target);
        }

        return true;
    }

    /// <summary>
    /// Scrolls, unless the scroll is already against that end and there is a
    /// chapter on the other side of it.
    /// </summary>
    private static bool ScrollReaderOrStepChapter(
        MainViewModel viewModel,
        ScrollViewer scrollViewer,
        double delta)
    {
        var forward = delta > 0;
        var againstEdge = forward
            ? IsReaderScrolledToEnd(scrollViewer)
            : scrollViewer.Offset.Y <= ReaderScrollEdgeTolerance;
        return againstEdge
            ? StepReaderChapter(viewModel, forward)
            : ScrollReaderBy(scrollViewer, delta);
    }

    private static double ReaderPageScrollDelta(ScrollViewer scrollViewer) =>
        Math.Max(
            ReaderLineScrollDelta,
            scrollViewer.Viewport.Height * ReaderPageScrollFraction);

    private static bool StepReaderPage(
        MainViewModel viewModel,
        ImageReaderViewModel reader,
        bool forward)
    {
        if (forward && reader.CanGoToNextPage)
        {
            reader.NextPageCommand.Execute(null);
            return true;
        }

        if (!forward && reader.CanGoToPreviousPage)
        {
            reader.PreviousPageCommand.Execute(null);
            return true;
        }

        return StepReaderChapter(viewModel, forward);
    }

    /// <summary>
    /// Opens the chapter either side and reports the key as consumed.
    /// <para>
    /// Consumed even at the ends of a book, where there is no chapter to reach.
    /// Left and right inside a chapter belong to the chapter; letting them
    /// travel on at the ends would hand the last page's press to the focus
    /// system, which is the one place it would visibly move focus.
    /// </para>
    /// <para>
    /// The command decides whether the step is possible. Testing
    /// <c>CanOpenNextReaderChapter</c> here first only added a second opinion
    /// that could disagree with the one that matters —
    /// <c>OpenAdjacentReaderChapterAsync</c> already returns when the target
    /// index falls outside the chapter list.
    /// </para>
    /// </summary>
    private static bool StepReaderChapter(MainViewModel viewModel, bool next)
    {
        var command = next
            ? viewModel.OpenNextReaderChapterCommand
            : viewModel.OpenPreviousReaderChapterCommand;
        command.Execute(null);
        return true;
    }

    /// <summary>
    /// Returns the reader to the very top, both modes.
    /// </summary>
    private void ScrollReaderToTop()
    {
        ResetReaderViewportRetention();
        Dispatcher.UIThread.Post(
            () =>
            {
                _readerScrollViewer?.ScrollToHome();
                ReaderPagedScroll?.ScrollToHome();
            },
            DispatcherPriority.Loaded);
    }

    private static bool IsReaderScrolledToEnd(ScrollViewer scrollViewer)
    {
        var extentHeight = scrollViewer.Extent.Height;
        return extentHeight > 0 &&
            scrollViewer.Offset.Y + scrollViewer.Viewport.Height >=
                extentHeight - ReaderScrollEdgeTolerance;
    }

    /// <summary>
    /// Control and the wheel zoom the reader.
    /// <para>
    /// Taken on the tunnel so it arrives before the ScrollViewer, which
    /// otherwise consumes the notch and scrolls the chapter instead. Marking it
    /// handled is what stops the page moving under the zoom.
    /// </para>
    /// </summary>
    private void MainView_OnPointerWheelChanged(
        object? sender,
        PointerWheelEventArgs eventArgs)
    {
        if (WithinReaderControl(eventArgs.Source, ReaderSettingsPanel)) return;
        if (WithinReaderControl(eventArgs.Source, ImageReaderSurface) &&
            !WithinReaderControl(eventArgs.Source, ReaderAutoScrollOptions)) StopReaderMotion();
        if (!eventArgs.KeyModifiers.HasFlag(KeyModifiers.Control) ||
            eventArgs.Delta.Y == 0 ||
            DataContext is not MainViewModel
            {
                IsReaderOpen: true,
                IsSettingsOpen: false
            } viewModel ||
            viewModel.Reader is not { } reader)
        {
            return;
        }

        eventArgs.Handled = true;
        if (eventArgs.Delta.Y > 0)
        {
            if (reader.CanZoomIn)
            {
                reader.ZoomInCommand.Execute(null);
            }

            return;
        }

        if (reader.CanZoomOut)
        {
            reader.ZoomOutCommand.Execute(null);
        }
    }

    /// <summary>
    /// Whether a field is being typed into with the on-screen keyboard.
    /// <para>
    /// Focus is checked alongside the session so the session cannot outlive its
    /// field. Anything that moves focus — a page change, the reader opening —
    /// takes the keyboard with it, and this has to report false again
    /// afterwards or the remote is left with nowhere to go.
    /// </para>
    /// </summary>
    private bool IsTelevisionTextInputActive() =>
        _activeTelevisionTextBox is not null &&
        ReferenceEquals(
            TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement(),
            _activeTelevisionTextBox);

    /// <summary>
    /// Dismisses the on-screen keyboard and puts the field back into
    /// remote-navigation mode, leaving focus where the viewer left it.
    /// </summary>
    private void EndTelevisionTextInput()
    {
        if (_televisionInputPane is not null)
        {
            _televisionInputPane.StateChanged -= TelevisionInputPane_OnStateChanged;
            _televisionInputPane = null;
        }
        _televisionKeyboardConfirmed = false;
        if (_activeTelevisionTextBox is not { } textBox)
        {
            return;
        }

        _activeTelevisionTextBox = null;
        SetTelevisionTextInputEnabled(textBox, enabled: false);
    }

    // Ignore a stale Open value inherited from a previous session. Only a
    // fresh open notification reserves D-pad input indefinitely; Android gets
    // a short grace period while its IME starts, never an unlimited focus trap.
    internal static bool ShouldReserveTelevisionTextDirections(
        bool confirmedThisSession, bool currentlyOpen, bool isAndroid, long elapsedMs) =>
        (confirmedThisSession && currentlyOpen) ||
        (isAndroid && !confirmedThisSession && elapsedMs < 500);

    private void TelevisionInputPane_OnStateChanged(object? sender, InputPaneStateEventArgs args)
    {
        if (args.NewState == InputPaneState.Open)
        {
            _televisionKeyboardConfirmed = true;
        }
    }

    private bool TryBeginTelevisionTextInput()
    {
        var focused =
            TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement()
                as Visual;
        var textBox = focused as TextBox ??
            focused?.GetVisualAncestors().OfType<TextBox>().FirstOrDefault();
        if (textBox is null ||
            !IsInsideTelevisionTextInputScope(textBox))
        {
            return false;
        }

        RememberTelevisionTextBoxState(textBox);
        if (_televisionTextBoxReadOnlyStates[textBox])
        {
            return false;
        }

        if (ReferenceEquals(_activeTelevisionTextBox, textBox))
        {
            return true;
        }
        EndTelevisionTextInput();
        _activeTelevisionTextBox = textBox;
        _televisionTextInputStartedMilliseconds = Environment.TickCount64;
        _televisionInputPane = TopLevel.GetTopLevel(this)?.InputPane;
        if (_televisionInputPane is not null)
        {
            _televisionInputPane.StateChanged += TelevisionInputPane_OnStateChanged;
        }
        _isActivatingTelevisionTextInput = true;
        SetTelevisionTextInputEnabled(textBox, enabled: true);

        // Enabling the input method on the already-focused editable TextBox
        // makes Avalonia attach its text client and ask Android for the
        // software keyboard. Keep the post only for caret placement.
        Dispatcher.UIThread.Post(
            () =>
            {
                textBox.CaretIndex = textBox.Text?.Length ?? 0;
                _isActivatingTelevisionTextInput = false;
            },
            DispatcherPriority.Input);
        return true;
    }

    private void TelevisionTextBox_OnLostFocus(
        object? sender,
        RoutedEventArgs eventArgs)
    {
        if (!_isTelevision ||
            _isActivatingTelevisionTextInput ||
            eventArgs.Source is not TextBox textBox)
        {
            return;
        }

        SetTelevisionTextInputEnabled(textBox, enabled: false);
        if (ReferenceEquals(_activeTelevisionTextBox, textBox))
        {
            EndTelevisionTextInput();
        }
    }

    private void ConfigureTelevisionTextInputsAfterLayout()
    {
        if (!_isTelevision)
        {
            return;
        }

        Dispatcher.UIThread.Post(
            ConfigureTelevisionTextInputs,
            DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Every text field a remote can reach. Settings lives in its own layer
    /// beside the page rather than inside it, and a field left out of this
    /// walk keeps its input method attached — so merely focusing it summons
    /// the on-screen keyboard instead of waiting to be opened.
    /// </summary>
    private IEnumerable<TextBox> TelevisionTextInputs() =>
        MainContent.GetVisualDescendants().OfType<TextBox>()
            .Concat(SettingsOverlay.GetVisualDescendants().OfType<TextBox>());

    private bool IsInsideTelevisionTextInputScope(Visual textBox) =>
        MainContent.IsVisualAncestorOf(textBox) ||
        SettingsOverlay.IsVisualAncestorOf(textBox);

    private void ConfigureTelevisionTextInputs()
    {
        foreach (var textBox in TelevisionTextInputs())
        {
            RememberTelevisionTextBoxState(textBox);
            SetTelevisionTextInputEnabled(
                textBox,
                enabled: !_isTelevision);
        }
    }

    private void RememberTelevisionTextBoxState(TextBox textBox)
    {
        _televisionTextBoxReadOnlyStates.TryAdd(
            textBox,
            textBox.IsReadOnly);
    }

    private void SetTelevisionTextInputEnabled(
        TextBox textBox,
        bool enabled)
    {
        RememberTelevisionTextBoxState(textBox);
        var wasReadOnly =
            _televisionTextBoxReadOnlyStates[textBox];
        if (enabled)
        {
            if (wasReadOnly)
            {
                return;
            }

            // TextBox supplies an input-method client only while it is
            // editable. Make it editable before enabling the input method so
            // Avalonia's focused-element requery can attach that client.
            textBox.IsReadOnly = false;
            InputMethod.SetIsInputMethodEnabled(textBox, true);
            return;
        }

        // Detach the client first so the platform keyboard is dismissed
        // before the control returns to remote-navigation-only mode.
        InputMethod.SetIsInputMethodEnabled(textBox, false);
        textBox.IsReadOnly = true;
    }

    private bool TryFocusDetailsEntries()
    {
        if (_navigationViewModel?.IsDetailsOpen is not true ||
            !IsVisibleWithin(DetailsEntriesList, MainContent) ||
            DetailsEntriesList.ItemCount == 0)
        {
            return false;
        }

        // The list itself is a poor place to leave a remote: its ring wraps
        // every chapter at once, so there is nothing to say which one is
        // selected, and the chapter's own actions are still a press away.
        // Enter on the first chapter's leading action instead, and keep the
        // list as the fallback only for the moment before it has laid out.
        if (!TryFocusFirstTelevisionEntryAction() &&
            !DetailsEntriesList.Focus(NavigationMethod.Directional))
        {
            return false;
        }

        Dispatcher.UIThread.Post(
            () => ScrollControlIntoView(DetailsEntriesList),
            DispatcherPriority.Background);
        return true;
    }

    private bool TryFocusCatalogPagination()
    {
        if (!IsVisibleWithin(CatalogNextPageButton, MainContent))
        {
            return false;
        }

        var target = CatalogNextPageButton.IsEnabled
            ? CatalogNextPageButton
            : CatalogPreviousPageButton.IsEnabled
                ? CatalogPreviousPageButton
                : null;
        return target?.Focus(NavigationMethod.Directional) is true;
    }

    private bool TryActivateFocusedLiveTvListItem()
    {
        var focused =
            TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement()
                as Visual;
        if (focused is null)
        {
            return false;
        }

        var channel =
            (ReferenceEquals(focused, LiveTvChannelList) ||
             LiveTvChannelList.IsVisualAncestorOf(focused)
                ? LiveTvChannelList.SelectedItem as LiveTvChannelViewModel
                : null) ??
            (focused as Control)?.DataContext
            as LiveTvChannelViewModel ??
            focused.GetVisualAncestors()
                .OfType<Control>()
                .Select(static control => control.DataContext)
                .OfType<LiveTvChannelViewModel>()
                .FirstOrDefault();
        if (channel?.PlayCommand.CanExecute(null) is not true)
        {
            return false;
        }

        channel.PlayCommand.Execute(null);
        return true;
    }

    private void ActivateLiveTvRemoteList()
    {
        if (_navigationViewModel?.LiveTvChannels.Count is not > 0)
        {
            SetLiveTvRemoteListActive(false);
            TryFocusLiveTvToolsOrFilters();
            return;
        }

        _lastFocusedLiveTvFilter = null;
        SetLiveTvRemoteListActive(true);
        if (LiveTvChannelList.SelectedIndex < 0)
        {
            LiveTvChannelList.SelectedIndex = 0;
        }

        LiveTvChannelList.ScrollIntoView(
            LiveTvChannelList.SelectedIndex);
        if (!FocusSelectedLiveTvChannel())
        {
            LiveTvChannelList.Focus(NavigationMethod.Directional);
        }

        QueueSelectedLiveTvChannelFocus();
    }

    private void MoveLiveTvSelection(int offset)
    {
        _lastFocusedLiveTvFilter = null;
        var count = _navigationViewModel?.LiveTvChannels.Count ?? 0;
        if (count == 0)
        {
            return;
        }

        LiveTvChannelList.SelectedIndex = Math.Clamp(
            Math.Max(0, LiveTvChannelList.SelectedIndex) + offset,
            0,
            count - 1);
        LiveTvChannelList.ScrollIntoView(
            LiveTvChannelList.SelectedIndex);
        if (!FocusSelectedLiveTvChannel())
        {
            LiveTvChannelList.Focus(NavigationMethod.Directional);
        }

        QueueSelectedLiveTvChannelFocus();
    }

    private void SetLiveTvRemoteListActive(bool isActive)
    {
        _isLiveTvRemoteListActive = isActive;
        if (_navigationViewModel is not null)
        {
            _navigationViewModel.IsLiveTvRemoteSelectionActive = isActive;
        }
    }

    private void ConfigureLiveTvDirectionalTargets()
    {
        if (!_isTelevision ||
            _navigationViewModel?.IsLiveTvPageVisible is not true)
        {
            return;
        }

        Dispatcher.UIThread.Post(
            () =>
            {
                var channelButtons = LiveTvChannelList
                    .GetVisualDescendants()
                    .OfType<Button>()
                    .Where(static button =>
                        button.Classes.Contains("live-tv-channel"))
                    .ToArray();
                var firstChannel = channelButtons.FirstOrDefault();
                if (TryRestoreLiveTvFilterFocus())
                {
                    return;
                }

                if (firstChannel is null)
                {
                    XYFocus.SetRight(
                        LiveTvNavButton,
                        LiveTvToolsToggleButton);
                    var emptyStateFocus = TopLevel.GetTopLevel(this)?
                        .FocusManager?
                        .GetFocusedElement() as Visual;
                    if (ShouldRecoverLiveTvPageFocus(emptyStateFocus))
                    {
                        SetLiveTvRemoteListActive(false);
                        TryFocusLiveTvToolsOrFilters();
                    }

                    return;
                }

                XYFocus.SetRight(LiveTvNavButton, firstChannel);
                XYFocus.SetUp(firstChannel, firstChannel);
                XYFocus.SetRight(firstChannel, firstChannel);
                foreach (var channelButton in channelButtons)
                {
                    XYFocus.SetLeft(channelButton, LiveTvNavButton);
                }

                LiveTvChannelList.SelectedIndex = Math.Max(
                    0,
                    LiveTvChannelList.SelectedIndex);
                var focused = TopLevel.GetTopLevel(this)?
                    .FocusManager?
                    .GetFocusedElement() as Visual;
                if (ShouldRecoverLiveTvPageFocus(focused) ||
                    ReferenceEquals(focused, LiveTvChannelList) ||
                    LiveTvChannelList.IsVisualAncestorOf(focused))
                {
                    SetLiveTvRemoteListActive(true);
                    if (!FocusSelectedLiveTvChannel())
                    {
                        firstChannel.Focus(
                            NavigationMethod.Directional);
                    }
                }
            },
            DispatcherPriority.Loaded);
    }

    private bool TryHandleLiveTvToolNavigation(
        Visual? focusedElement,
        NavigationDirection direction)
    {
        if (focusedElement is null)
        {
            ActivateLiveTvRemoteList();
            return true;
        }

        var focusedControl = FindFocusableControl(focusedElement);
        var isInFilters =
            LiveTvFiltersPanel.IsVisible &&
            (ReferenceEquals(focusedElement, LiveTvFiltersPanel) ||
             LiveTvFiltersPanel.IsVisualAncestorOf(focusedElement));
        var isToolsButton =
            ReferenceEquals(focusedControl, LiveTvToolsToggleButton);
        var isRefreshButton =
            ReferenceEquals(focusedControl, LiveTvRefreshButton);
        if (!isInFilters && !isToolsButton && !isRefreshButton)
        {
            // Android can return focus to Avalonia's root panel while the page
            // is loading or after a virtualized item is recycled. Recover to a
            // real control on any directional press instead of allowing focus
            // to escape to the native root view.
            if (_navigationViewModel?.LiveTvChannels.Count is > 0)
            {
                ActivateLiveTvRemoteList();
            }
            else
            {
                SetLiveTvRemoteListActive(false);
                TryFocusLiveTvToolsOrFilters();
            }

            return true;
        }

        if (direction is NavigationDirection.Left)
        {
            if (ReferenceEquals(
                    focusedControl,
                    LiveTvGroupComboBox))
            {
                return LiveTvProviderComboBox.Focus(
                    NavigationMethod.Directional);
            }

            if (ReferenceEquals(
                    focusedControl,
                    LiveTvProviderComboBox))
            {
                return LiveTvSearchTextBox.Focus(
                    NavigationMethod.Directional);
            }

            return TryOpenTelevisionMenu();
        }

        if (direction is NavigationDirection.Right)
        {
            if (ReferenceEquals(
                    focusedControl,
                    LiveTvSearchTextBox))
            {
                return LiveTvProviderComboBox.Focus(
                    NavigationMethod.Directional);
            }

            if (ReferenceEquals(
                    focusedControl,
                    LiveTvProviderComboBox))
            {
                return LiveTvGroupComboBox.Focus(
                    NavigationMethod.Directional);
            }

            if (isToolsButton)
            {
                return LiveTvRefreshButton.Focus(
                    NavigationMethod.Directional);
            }

            // Keep focus on the right-most control instead of letting Android
            // hand it back to the native root view.
            return true;
        }

        if (direction is NavigationDirection.Up)
        {
            if (isInFilters)
            {
                _lastFocusedLiveTvFilter = null;
                return LiveTvToolsToggleButton.Focus(
                    NavigationMethod.Directional);
            }

            return true;
        }

        if (direction is not NavigationDirection.Down)
        {
            return false;
        }

        if (isToolsButton && !_liveTvToolsExpanded &&
            _navigationViewModel?.LiveTvChannels.Count is not > 0)
        {
            _liveTvToolsExpanded = true;
            UpdateToolPanels();
            Dispatcher.UIThread.Post(
                () => TryFocusFirstLiveTvFilter(),
                DispatcherPriority.Loaded);
            return true;
        }

        if (isToolsButton && _liveTvToolsExpanded)
        {
            return TryFocusFirstLiveTvFilter();
        }

        if (_navigationViewModel?.LiveTvChannels.Count is > 0)
        {
            ActivateLiveTvRemoteList();
            return true;
        }

        // The empty state is not focusable. Consume Down at the final control
        // so focus stays visible and recoverable.
        return true;
    }

    private bool TryFocusLiveTvToolsOrFilters()
    {
        if (_liveTvToolsExpanded && TryFocusFirstLiveTvFilter())
        {
            return true;
        }

        if (IsVisibleWithin(LiveTvToolsToggleButton, MainContent) &&
            LiveTvToolsToggleButton.Focus(
                NavigationMethod.Directional))
        {
            return true;
        }

        return IsVisibleWithin(LiveTvRefreshButton, MainContent) &&
               LiveTvRefreshButton.Focus(
                   NavigationMethod.Directional);
    }

    private bool ShouldRecoverLiveTvPageFocus(Visual? focusedElement)
    {
        if (focusedElement is null)
        {
            return true;
        }

        if (Sidebar.IsVisualAncestorOf(focusedElement))
        {
            return false;
        }

        var focusedControl = FindFocusableControl(focusedElement);
        return !ReferenceEquals(focusedControl, LiveTvToolsToggleButton) &&
               !ReferenceEquals(focusedControl, LiveTvRefreshButton) &&
               !LiveTvFiltersPanel.IsVisualAncestorOf(focusedElement) &&
               !LiveTvChannelList.IsVisualAncestorOf(focusedElement);
    }

    private void LiveTvFiltersPanel_OnGotFocus(
        object? sender,
        RoutedEventArgs eventArgs)
    {
        if (eventArgs.Source is not Visual focusedElement)
        {
            return;
        }

        var focusedControl = FindFocusableControl(focusedElement);
        if (ReferenceEquals(focusedControl, LiveTvSearchTextBox) ||
            ReferenceEquals(focusedControl, LiveTvProviderComboBox) ||
            ReferenceEquals(focusedControl, LiveTvGroupComboBox))
        {
            _lastFocusedLiveTvFilter = focusedControl;
        }
    }

    private bool TryRestoreLiveTvFilterFocus()
    {
        if (_lastFocusedLiveTvFilter is not { } filter ||
            !IsVisibleWithin(LiveTvFiltersPanel, MainContent) ||
            !filter.IsVisible ||
            !filter.IsEnabled)
        {
            return false;
        }

        if (!filter.Focus(NavigationMethod.Directional))
        {
            return false;
        }

        SetLiveTvRemoteListActive(false);
        return true;
    }

    private bool TryFocusFirstLiveTvFilter()
    {
        if (!IsVisibleWithin(LiveTvFiltersPanel, MainContent))
        {
            return false;
        }

        var target = new Control[]
        {
            LiveTvSearchTextBox,
            LiveTvProviderComboBox,
            LiveTvGroupComboBox
        }.FirstOrDefault(static control =>
            control.IsVisible &&
            control.IsEnabled &&
            control.Focusable);
        if (target?.Focus(NavigationMethod.Directional) is not true)
        {
            return false;
        }

        _lastFocusedLiveTvFilter = target;
        SetLiveTvRemoteListActive(false);
        return true;
    }

    private bool FocusSelectedLiveTvChannel()
    {
        var selected = LiveTvChannelList.SelectedItem;
        if (selected is null)
        {
            return false;
        }

        var target = LiveTvChannelList
            .GetVisualDescendants()
            .OfType<Button>()
            .FirstOrDefault(button =>
                button.Classes.Contains("live-tv-channel") &&
                ReferenceEquals(button.DataContext, selected));
        return target?.Focus(NavigationMethod.Directional) is true;
    }

    private void QueueSelectedLiveTvChannelFocus() =>
        Dispatcher.UIThread.Post(
            () =>
            {
                if (_isLiveTvRemoteListActive &&
                    _navigationViewModel?.IsLiveTvPageVisible is true)
                {
                    FocusSelectedLiveTvChannel();
                }
            },
            DispatcherPriority.Loaded);

    private async void ImportCatalogJson_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        var storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storageProvider is null || !storageProvider.CanOpen || DataContext is not MainViewModel viewModel)
        {
            return;
        }

        // Nothing observes an event handler's task, so an escaping exception
        // would reach the dispatcher and end the process. The picker and the
        // stream sit outside the view model's own error handling: a file the
        // user picked can be deleted, or its permission revoked, before it is
        // opened.
        try
        {
            var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = UiTextCatalog.Get("ImportCatalogDialog"),
                AllowMultiple = false,
                FileTypeFilter = [CatalogJsonFileType]
            });
            var file = files.FirstOrDefault();
            if (file is null)
            {
                return;
            }

            using (file)
            await using (var stream = await file.OpenReadAsync())
            {
                await viewModel.ImportCatalogJsonAsync(stream, file.Name);
            }
        }
        catch (Exception exception)
        {
            viewModel.SetError(viewModel.FilesFeedback, UiTextCatalog.Format(
                "CatalogImportFailed",
                exception.Message));
        }
    }

    private async void OpenTorrentFile_OnClick(
        object? sender,
        RoutedEventArgs eventArgs)
    {
        var storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storageProvider is null ||
            !storageProvider.CanOpen ||
            DataContext is not MainViewModel viewModel)
        {
            return;
        }

        try
        {
            var files = await storageProvider.OpenFilePickerAsync(
                new FilePickerOpenOptions
                {
                    Title = UiTextCatalog.Get("OpenTorrentFileDialog"),
                    AllowMultiple = false,
                    FileTypeFilter = [TorrentFileType]
                });
            var file = files.FirstOrDefault();
            if (file is null)
            {
                return;
            }

            using (file)
            await using (var stream = await file.OpenReadAsync())
            {
                await viewModel.OpenTorrentFileAsync(stream);
            }
        }
        catch (Exception exception)
        {
            viewModel.SetError(viewModel.MagnetFeedback, UiTextCatalog.Format(
                "TorrentFileOpenFailed",
                exception.Message));
        }
    }

    private async void ExportCatalogJson_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        var storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storageProvider is null || !storageProvider.CanSave || DataContext is not MainViewModel viewModel)
        {
            return;
        }

        try
        {
            var file = await storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = UiTextCatalog.Get("ExportCatalogDialog"),
                SuggestedFileName = $"catalog-{DateTimeOffset.UtcNow:yyyyMMdd}.umd-catalog.json",
                DefaultExtension = "json",
                FileTypeChoices = [CatalogJsonFileType]
            });
            if (file is null)
            {
                return;
            }

            using (file)
            await using (var stream = await file.OpenWriteAsync())
            {
                await ClearSaveTargetAsync(file, stream);
                await viewModel.ExportCatalogJsonAsync(stream);
            }
        }
        catch (Exception exception)
        {
            viewModel.SetError(viewModel.FilesFeedback, UiTextCatalog.Format(
                "CatalogExportFailed",
                exception.Message));
        }
    }

    private async void ExportFullDatabase_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        var storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storageProvider is null || !storageProvider.CanSave || DataContext is not MainViewModel viewModel)
        {
            return;
        }

        try
        {
            var file = await storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = UiTextCatalog.Get("CreateBackupDialog"),
                SuggestedFileName = $"umd-full-{DateTimeOffset.UtcNow:yyyyMMdd}.umdbackup",
                DefaultExtension = "umdbackup",
                FileTypeChoices = [FullBackupFileType]
            });
            if (file is null)
            {
                return;
            }

            using (file)
            await using (var stream = await file.OpenWriteAsync())
            {
                await ClearSaveTargetAsync(file, stream);
                await viewModel.ExportFullDatabaseAsync(stream);
            }
        }
        catch (Exception exception)
        {
            viewModel.SetError(viewModel.BackupFeedback, UiTextCatalog.Format(
                "BackupExportFailed",
                exception.Message));
        }
    }

    private async void RestoreFullDatabase_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        var storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storageProvider is null || !storageProvider.CanOpen || DataContext is not MainViewModel viewModel)
        {
            return;
        }

        try
        {
            var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = UiTextCatalog.Get("RestoreBackupDialog"),
                AllowMultiple = false,
                FileTypeFilter = [FullBackupFileType]
            });
            var file = files.FirstOrDefault();
            if (file is null)
            {
                return;
            }

            using (file)
            await using (var stream = await file.OpenReadAsync())
            {
                await viewModel.RestoreFullDatabaseAsync(stream);
            }
        }
        catch (Exception exception)
        {
            viewModel.SetError(viewModel.BackupFeedback, UiTextCatalog.Format(
                "BackupRestoreFailed",
                exception.Message));
        }
    }

    /// <summary>
    /// Empties a picked save target before it is written over.
    ///
    /// A seekable stream is truncated directly. Android's storage provider
    /// hands back a stream that can do neither, and its write mode does not
    /// reliably truncate, so a destination that still holds bytes is refused
    /// rather than half-overwritten: the tail of the previous, longer file
    /// would otherwise survive and leave an export that only fails later, when
    /// someone tries to restore from it.
    /// </summary>
    private static async Task ClearSaveTargetAsync(IStorageFile file, Stream stream)
    {
        if (stream.CanSeek)
        {
            stream.SetLength(0);
            return;
        }

        var properties = await file.GetBasicPropertiesAsync();
        if (properties.Size is > 0)
        {
            throw new IOException(
                UiTextCatalog.Format("SaveTargetNotEmpty", file.Name));
        }
    }

    private readonly record struct ReaderViewportAnchor(
        ReaderPageViewModel Page,
        double ViewportTop,
        double ScrollOffset);
}
