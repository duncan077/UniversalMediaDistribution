using System.Globalization;
using Avalonia.Controls;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.UI.Localization;

/// <summary>
/// Provides the app-owned user-interface text for native and browser hosts.
/// English is the fallback language; Spanish is selected for every Spanish locale.
/// Set UMD_UI_LANGUAGE to a BCP-47 language tag to override the operating-system locale.
/// </summary>
public static class UiTextCatalog
{
    private static readonly IReadOnlyDictionary<string, string> English =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AppName"] = "Universal Media Distribution",
            ["ReaderAutoScrollStart"] = "Start auto-scroll",
            ["ReaderChapterShort"] = "Ch. {0}",
            ["ReaderAutoScrollPause"] = "Pause auto-scroll",
            ["ReaderAutoScrollSpeed"] = "Speed",
            ["ReaderSettingsTitle"] = "Reading options",
            ["ReaderModeTitle"] = "Reading mode",
            ["ReaderPageSizeTitle"] = "Page size",
            ["ReaderAutoScrollTitle"] = "Automatic scrolling",
            ["ReaderAutoScrollContinuousHint"] = "Choose Continuous to enable automatic scrolling.",
            ["ReaderMiddleScrollHint"] = "Middle-click once and release. Move the mouse to scroll; click again or press Escape to stop.",
            ["ReaderRemoteScrollHint"] = "Use Up/Down to move through options, Left/Right to adjust speed, and Back to close.",
            ["ReaderSettingsClose"] = "Close reading options",
            ["ReaderAutoScrollHint"] = "Stops at the end of the chapter. Escape pauses. Middle-click the page to scroll by moving the mouse; click again or press Escape to stop.",
            ["ShellSubtitle"] = "Universal media",
            ["RuntimeLabel"] = "RUNTIME",
            ["NavHome"] = "Home",
            ["NavMovies"] = "Movies & series",
            ["NavAnime"] = "Anime",
            ["NavBooks"] = "Books & manga",
            ["NavLiveTv"] = "Live TV",
            ["NavLists"] = "Lists",
            ["NavFriends"] = "Friends",
            ["NavDownloads"] = "Downloads",
            ["NavSettings"] = "Settings",
            ["CollapseMenu"] = "Collapse menu",
            ["ExpandMenu"] = "Expand menu",
            ["SectionAll"] = "All content",
            ["HomeLastSeen"] = "Last seen",
            ["HomeSectionCount"] = "{0} titles",
            ["HomeSeeAll"] = "See all",
            ["ShelfScrollLeft"] = "Scroll left",
            ["ShelfScrollRight"] = "Scroll right",
            ["SettingsClose"] = "Close",
            ["FavoriteTitles"] = "Favourite titles",
            ["ListFilter"] = "List",
            ["AllLists"] = "All lists",
            ["ListFavoritesOnly"] = "Only favourite titles",
            ["ContentSourceDuplicate"] =
                "A provider with that address is already configured.",
            ["ShelfRecentlyAdded"] = "Recently added",
            ["ShelfRecentlyUpdated"] = "Recently updated",
            ["CatalogViewGrid"] = "Show all",
            ["CatalogViewRows"] = "Show rows",
            ["HomeLiveTvPrompt"] = "Channels load when you open Live TV.",
            ["CatalogSearchLabel"] = "Search the catalog and enabled online providers",
            ["SearchAndFilters"] = "Search & filters",
            ["SearchAndFiltersShort"] = "Filters",
            ["CatalogSearchPlaceholder"] = "Search titles, alternative names, genres, or distributors",
            ["Search"] = "Search",
            ["ClearSearch"] = "Clear",
            ["LocalResult"] = "LOCAL",
            ["OnlineResult"] = "ONLINE · {0}",
            ["ContentSourcesTitle"] = "Content sources",
            ["ContentSourcesDescription"] =
                "Choose which online catalogs and Live TV providers the app may contact. Manual magnets stay local.",
            ["ContentSourceManual"] = "MANUAL",
            ["ContentSourceTorrentCatalog"] = "TORRENT CATALOG",
            ["ContentSourceApi"] = "ONLINE SOURCE",
            ["ContentSourceLiveTv"] = "LIVE TV",
            ["ContentSourceGroupManual"] = "Manual",
            ["ContentSourceGroupTorrentCatalog"] = "Torrent catalogs",
            ["ContentSourceGroupApi"] = "Online sources",
            ["ContentSourceGroupLiveTv"] = "Live TV",
            ["ApiProviderCategoryMoviesSeries"] = "Movies & series",
            ["ApiProviderCategoryAnime"] = "Anime",
            ["ApiProviderCategoryBooksManga"] = "Books & manga",
            ["ApiProviderCategoryAllMedia"] = "All media",
            ["ContentSourceGroupSummary"] = "{0} of {1} on",
            ["ContentSourceGroupEnableAll"] = "Enable all",
            ["ContentSourceGroupDisableAll"] = "Disable all",
            ["ManualProviderName"] = "Manual magnet links",
            ["Enabled"] = "Enabled",
            ["ContentSourceNameLabel"] = "Content source name",
            ["ContentSourceBaseUrlLabel"] = "Content source base URL",
            ["Remove"] = "Remove",
            ["TorrentProviderNamePlaceholder"] = "Torrent catalog name",
            ["TorrentProviderUrlPlaceholder"] = "Catalog JSON endpoint",
            ["AddTorrentProvider"] = "Add torrent provider",
            ["ApiProviderCodeOnlyHint"] =
                "API providers are installed by application code. You can enable them and change their base URL, but cannot create or remove them here.",
            ["SaveContentSources"] = "Save content sources",
            ["DonationTitle"] = "Support the project",
            ["DonationStartupMessage"] =
                "Universal Media Distribution is built and maintained independently. If it is useful to you, you can help support its continued development with USDT or USDC.",
            ["DonationSettingsDescription"] =
                "Contribute with USDT or USDC to help fund continued development and maintenance.",
            ["DonationStablecoins"] = "USDT / USDC",
            ["DonationAddressLabel"] = "Wallet address",
            ["DonationQrCode"] = "Donation wallet QR code",
            ["DonationNetworkWarning"] =
                "Confirm the intended network with the project owner and make sure your wallet supports it before sending. Transfers cannot be reversed.",
            ["DonationCopyAddress"] = "Copy address",
            ["DonationAddressCopied"] = "Address copied",
            ["DonationOpen"] = "Donate",
            ["DonationDismiss"] = "Maybe later",
            ["ProviderFilter"] = "Content provider",
            ["AllProviders"] = "All content providers",
            ["LocalProvider"] = "Local",
            ["OnlineProvider"] = "Online",
            ["CategoryFilter"] = "Category",
            ["AllCategories"] = "All categories",
            ["StatusFilter"] = "Status",
            ["AllStatuses"] = "All statuses",
            ["PublicationStatusOngoing"] = "Ongoing",
            ["PublicationStatusFinished"] = "Finished",
            ["PublicationStatusPaused"] = "On pause",
            ["PublicationStatusCancelled"] = "Cancelled",
            ["PublicationStatusAnnounced"] = "Announced",
            ["PublicationStatusUnknown"] = "Unknown",
            ["YearFilter"] = "Year",
            ["AllYears"] = "All years",
            ["GenreFilter"] = "Genre",
            ["AllGenres"] = "All genres",
            ["Genre_action"] = "Action",
            ["Genre_adventure"] = "Adventure",
            ["Genre_comedy"] = "Comedy",
            ["Genre_crime"] = "Crime",
            ["Genre_documentary"] = "Documentary",
            ["Genre_drama"] = "Drama",
            ["Genre_ecchi"] = "Ecchi",
            ["Genre_family"] = "Family",
            ["Genre_fantasy"] = "Fantasy",
            ["Genre_harem"] = "Harem",
            ["Genre_historical"] = "Historical",
            ["Genre_horror"] = "Horror",
            ["Genre_isekai"] = "Isekai",
            ["Genre_josei"] = "Josei",
            ["Genre_magic"] = "Magic",
            ["Genre_martial-arts"] = "Martial arts",
            ["Genre_mecha"] = "Mecha",
            ["Genre_military"] = "Military",
            ["Genre_music"] = "Music",
            ["Genre_mystery"] = "Mystery",
            ["Genre_psychological"] = "Psychological",
            ["Genre_reality"] = "Reality",
            ["Genre_romance"] = "Romance",
            ["Genre_school"] = "School",
            ["Genre_science-fiction"] = "Science fiction",
            ["Genre_seinen"] = "Seinen",
            ["Genre_shoujo"] = "Shoujo",
            ["Genre_shounen"] = "Shounen",
            ["Genre_slice-of-life"] = "Slice of life",
            ["Genre_sports"] = "Sports",
            ["Genre_supernatural"] = "Supernatural",
            ["Genre_suspense"] = "Suspense",
            ["Genre_thriller"] = "Thriller",
            ["Genre_tragedy"] = "Tragedy",
            ["Genre_war"] = "War",
            ["Genre_western"] = "Western",
            ["Favorites"] = "Favorites",
            ["CatalogItem"] = "Title",
            ["AddFavoriteItem"] = "Add to favorites",
            ["RemoveFavoriteItem"] = "Remove from favorites",
            ["FavoriteItemAdded"] = "\"{0}\" was added to favorites.",
            ["FavoriteItemRemoved"] = "\"{0}\" was removed from favorites.",
            ["FavoriteItemSaveFailed"] =
                "The favorite could not be saved: {0}",
            ["CatalogUserStateLoadFailed"] =
                "Favorites and progress could not be loaded: {0}",
            ["UserListsTitle"] = "Your lists",
            ["UserListsDescription"] =
                "Create lists that can mix manga, anime, movies, books, and audio.",
            ["FriendsTitle"] = "Friends",
            ["FriendsDescription"] =
                "Add someone by their account name. Once you are both friends, you can choose to let them see how far you have got in a title.",
            ["WatchPartyTitle"] = "Watch together",
            ["WatchPartyDescription"] =
                "Watch in sync: one party, one timeline. Anyone can pause or jump, and it happens for everyone.",
            ["WatchPartyStartAction"] = "Start a party",
            ["WatchPartyJoinCodeLabel"] = "Party code",
            ["WatchPartyJoinAction"] = "Join party",
            ["WatchPartyLeaveAction"] = "Leave party",
            ["WatchPartyCodeShareLabel"] = "Invite code",
            ["WatchPartyMembersTitle"] = "In the party",
            ["WatchPartyHostSuffix"] = "host",
            ["WatchPartyAwaySuffix"] = "away",
            ["WatchPartyBufferingSuffix"] = "buffering",
            ["WatchPartyHostControlsLabel"] =
                "Only I can pause and jump for everyone",
            ["WatchPartyNoTransportNotice"] =
                "This source plays in its own web player, so the party cannot pause or move it. Keep up by hand, or pick a direct source.",
            ["WatchPartyOptionsFailed"] =
                "The watch party setting could not be changed: {0}",
            ["WatchPartyWatchingLabel"] = "Watching: {0}",
            ["WatchPartyNothingPlaying"] =
                "Nothing is playing yet. What the host plays becomes the party's title.",
            ["WatchPartySyncedLabel"] = "Synced with the party.",
            ["WatchPartyReconnectingLabel"] = "Reconnecting to the party…",
            ["WatchPartyNotSyncedLabel"] =
                "Play {0} to sync with the party.",
            ["WatchPartyOpenContentAction"] = "Open the party's title",
            ["WatchPartyContentNotInCatalog"] =
                "“{0}” is not in your catalog yet, so its name was placed in search.",
            ["WatchPartyNotFound"] = "No watch party uses that code.",
            ["WatchPartyFollowPrompt"] =
                "The party is watching {0}.",
            ["WatchPartyFollowDismiss"] = "Not now",
            ["WatchPartyPlayerStatus"] =
                "Watch together · {0} in the party",
            ["WatchPartyCreateFailed"] =
                "The watch party could not be started: {0}",
            ["WatchPartyJoinFailed"] =
                "The watch party could not be joined: {0}",
            ["WatchPartyLeaveFailed"] =
                "Leaving the watch party failed: {0}",
            ["FriendsSignedOutNotice"] =
                "Sign in to a media server that supports friends to use this page.",
            ["FriendsRequestsTitle"] = "Pending requests",
            ["FriendsListTitle"] = "Your friends",
            ["FriendsNone"] =
                "No friends yet. Add someone by their account name above.",
            ["FriendUsernameLabel"] = "Account name",
            ["FriendUsernamePlaceholder"] = "Their account name",
            ["FriendAddAction"] = "Send request",
            ["FriendRefreshAction"] = "Refresh",
            ["FriendRemoveAction"] = "Remove",
            ["FriendRequestAcceptAction"] = "Accept",
            ["FriendRequestIncoming"] = "Wants to be friends",
            ["FriendRequestOutgoing"] = "Waiting for a reply",
            ["FriendRequestDecline"] = "Decline",
            ["FriendRequestWithdraw"] = "Withdraw",
            ["FriendSharesProgressLabel"] = "Share my progress",
            ["FriendsShareProgressLabel"] =
                "Let friends see how far I have got",
            ["FriendsDiscoverableLabel"] =
                "Let people find me by my account name",
            ["FriendsSaveVisibility"] = "Save sharing settings",
            ["FriendsLoadFailed"] = "Friends could not be loaded: {0}",
            ["FriendRequestSent"] = "A friend request was sent to {0}.",
            ["FriendRequestAccepted"] = "You and {0} are now friends.",
            ["FriendRequestNoSuchAccount"] =
                "No account named {0} accepts friend requests.",
            ["FriendRequestFailed"] =
                "The friend request could not be completed: {0}",
            ["FriendRemoved"] = "{0} was removed from your friends.",
            ["FriendRemoveFailed"] = "The friend could not be removed: {0}",
            ["FriendSharingSaveFailed"] =
                "The sharing setting could not be saved: {0}",
            ["FriendVisibilitySharingOn"] =
                "Friends will see your progress after the next sync.",
            ["FriendVisibilitySharingOff"] =
                "Sharing is off. Your progress is removed from friends on the next sync.",
            ["FriendVisibilitySaveFailed"] =
                "The sharing settings could not be saved: {0}",
            ["FriendProgressTitle"] = "Friends",
            ["FriendProgressChapter"] = "{0} read up to chapter {1}",
            ["FriendProgressEpisode"] = "{0} watched up to episode {1}",
            ["FriendProgressEpisodeInSeason"] =
                "{0} watched up to season {1}, episode {2}",
            ["FriendProgressFinished"] = "{0} finished this",
            ["FriendProgressStarted"] = "{0} has started this",
            ["FriendSharedTitlesCount"] = "{0} in common",
            ["FriendCommonWithFavorites"] =
                "{0} in common · {1} you both love",
            ["FriendTitlesOnlyTheirs"] = "{0} they have that you do not",
            ["FriendTitlesOnlyMine"] = "{0} you have that they do not",
            ["FriendTitleFavoriteOnly"] = "Marked as a favourite",
            ["FriendTitleSearchHint"] = "Search for this title",
            ["FriendTitleStarted"] = "Started",
            ["FriendTitleFinished"] = "Finished",
            ["FriendTitleChapter"] = "Up to chapter {0}",
            ["FriendTitleEpisode"] = "Up to episode {0}",
            ["FriendTitleEpisodeInSeason"] =
                "Up to season {0}, episode {1}",
            ["FriendProgressNeverShared"] = "Has not shared progress yet",
            ["FriendProgressJustNow"] = "Progress shared just now",
            ["FriendProgressMinutesAgo"] = "Progress shared {0} min ago",
            ["FriendProgressHoursAgo"] = "Progress shared {0} h ago",
            ["FriendProgressDaysAgo"] = "Progress shared {0} d ago",
            ["NewUserListName"] = "New list name",
            ["NewUserListPlaceholder"] = "For example: Read later",
            ["CreateUserList"] = "Create list",
            ["SelectUserList"] = "Choose a list",
            ["RenameUserList"] = "Rename",
            ["DeleteUserList"] = "Delete list",
            ["ConfirmDeleteUserList"] = "Delete this list?",
            ["ConfirmDelete"] = "Yes, delete",
            ["Cancel"] = "Cancel",
            ["UserListDisplayLabel"] = "{0} ({1})",
            ["EmptyUserList"] =
                "This list is empty. Add a title from its details page.",
            ["NoUserLists"] = "You have no lists yet.",
            ["AddToList"] = "Add to list",
            ["RemoveFromList"] = "Remove from list",
            ["ListMembershipTitle"] = "Lists",
            ["NoListsForItem"] =
                "Create a list first, then add this title.",
            ["OpenUserLists"] = "Manage lists",
            ["ProviderList"] = "Distributors: {0}",
            ["ContributorsTitle"] = "Providers & contributors",
            ["ContributorOrganization"] = "Organization",
            ["ContributorPerson"] = "Person",
            ["CatalogTitle"] = "Local catalog",
            ["CatalogSubtitle"] =
                "Your library, reading progress, and online sources in one place",
            ["SettingsTitle"] = "Settings",
            ["SettingsSubtitle"] =
                "Choose sources, manage your data, and personalize playback",
            ["DownloadsTitle"] = "Downloads",
            ["DownloadsSubtitle"] =
                "Active transfers and files saved on this device",
            ["DownloadsDescription"] =
                "Follow torrent progress, pause active transfers, and remove one or many downloaded chapters or episodes.",
            ["RefreshDownloads"] = "Refresh",
            ["DownloadsTotalLabel"] = "SAVED ITEMS",
            ["DownloadsActiveLabel"] = "ACTIVE",
            ["DownloadsStorageLabel"] = "TORRENT STORAGE",
            ["DownloadsStorageSummary"] = "{0} used",
            ["DownloadsSelectionSummary"] = "{0} of {1} selected",
            ["DownloadsDeleteHint"] =
                "Deleting an item permanently removes its managed torrent files from this device.",
            ["SelectAllDownloads"] = "Select all",
            ["DeleteSelectedDownloads"] = "Delete selected",
            ["DeleteAllDownloads"] = "Delete all",
            ["DownloadsDeleteConfirmation"] =
                "Permanently delete {0} selected download(s)?",
            ["DownloadsDeletePermanent"] =
                "This cannot be undone. Active transfers will be stopped first.",
            ["DeleteDownloadsNow"] = "Delete files",
            ["DeleteDownload"] = "Delete",
            ["DownloadsEmptyTitle"] = "No downloads yet",
            ["DownloadsEmptyDescription"] =
                "Downloaded chapters, episodes, archives, and manually opened torrents will appear here.",
            ["DownloadStateAdding"] = "ADDING",
            ["DownloadStateMetadata"] = "METADATA",
            ["DownloadStateChecking"] = "CHECKING",
            ["DownloadStatePeers"] = "WAITING FOR PEERS",
            ["DownloadStateDownloading"] = "DOWNLOADING",
            ["DownloadStateComplete"] = "COMPLETE",
            ["DownloadStateSeeding"] = "SEEDING",
            ["DownloadStatePaused"] = "PAUSED",
            ["DownloadStateFailed"] = "FAILED",
            ["DownloadStateStopped"] = "STOPPED",
            ["DownloadStateRemoving"] = "REMOVING",
            ["DownloadProgressWithSize"] = "{0:0}% · {1} / {2}",
            ["DownloadProgressOnly"] = "{0:0}%",
            ["DownloadStoredSize"] = "{0} downloaded",
            ["DownloadUpdated"] = "Updated {0}",
            ["DownloadsLoadFailed"] =
                "The download history could not be loaded: {0}",
            ["DownloadsSaveFailed"] =
                "The download history could not be saved: {0}",
            ["DownloadsDeleteFailed"] =
                "The selected downloads could not all be deleted: {0}",
            ["DownloadsDeleted"] = "{0} download(s) deleted.",
            ["DownloadCleanupTitle"] = "Torrent cleanup",
            ["DownloadCleanupDescription"] =
                "Choose whether torrent files from the current app session remain available after the app closes.",
            ["DeleteDownloadsAtSessionEnd"] =
                "Delete torrent data when this app session ends",
            ["DownloadCleanupWarning"] =
                "Downloaded files from active torrents will be permanently removed when the app closes.",
            ["SaveDownloadOptions"] = "Save cleanup option",
            ["DownloadOptionsSaved"] = "Torrent cleanup preference saved.",
            ["DownloadOptionsSaveFailed"] =
                "The torrent cleanup preference could not be saved: {0}",
            ["ContentFilterTitle"] = "Advertisement and tracker filtering",
            ["ContentFilterDescription"] =
                "Filters advertisements and trackers in embedded players. The selected player page and video resources remain protected; ad requests can be blocked even when served by the player's own site.",
            ["HideAdultContent"] = "Hide +18 content",
            ["HideAdultContentDescription"] =
                "Hides titles from the catalog and API providers when their category is adult, porn, +18, or 18+.",
            ["ContentFilterModeLabel"] = "Filtering level",
            ["ContentFilterModeOff"] = "Off",
            ["ContentFilterModeOffHint"] =
                "No filtering. Embedded players will show their advertisements and may open popups.",
            ["ContentFilterModeBasic"] = "Built-in rules only",
            ["ContentFilterModeBasicHint"] =
                "Uses the rules included with the app and never contacts a filter list server. Works fully offline.",
            ["ContentFilterModeStandard"] = "Standard",
            ["ContentFilterModeStandardHint"] =
                "Adds the uBlock Origin, EasyList, EasyPrivacy, and Peter Lowe lists, downloaded and refreshed in the background.",
            ["ContentFilterModeComplete"] = "Complete",
            ["ContentFilterModeCompleteHint"] =
                "Everything in Standard plus annoyance lists. Uses noticeably more memory.",
            ["ContentFilterModeCapped"] =
                "This device does not have the memory for the selected level, so a reduced one is in use. Request blocking is unaffected; some advertisement boxes may remain visible but empty.",
            ["ContentFilterNeverRefreshed"] = "never",
            ["ContentFilterStatusReady"] =
                "{0} rules loaded · last checked: {1}",
            ["ContentFilterStatusUpdated"] =
                "Updated {0} list(s); {1} rules loaded.",
            ["ContentFilterStatusSeedOnly"] =
                "{0} built-in rules loaded. No lists are downloaded at this level.",
            ["ContentFilterStatusDeferred"] =
                "Postponed: a video is playing, or the connection is metered or in power-saving mode. It will retry later.",
            ["ContentFilterStatusNoStorage"] =
                "This platform cannot store downloaded lists, so only the built-in rules are used.",
            ["ContentFilterStatusFailed"] =
                "The lists could not be updated: {0}. The previous rules are still in use.",
            ["ContentFilterStatusSomeListsFailed"] =
                " {0} list(s) could not be refreshed and are being used as last downloaded.",
            ["ContentFilterListSources"] =
                "Lists are downloaded from ublockorigin.github.io, easylist.to, and pgl.yoyo.org. Only a conditional request is sent; no browsing activity leaves this device.",
            ["ContentFilterPlaybackNetwork"] =
                "Last embedded player: {0} of {1} requests refused before leaving this device.",
            ["ContentFilterPlaybackInPage"] =
                "Last embedded player: limited filtering ({2}). Request counts do not include blocks made inside the page.",
            ["ContentFilterPlaybackAllowances"] =
                " Allowed: {0} to the page's own site, {1} as video. Requests to the site's own resources are also checked against blocking rules.",
            ["ContentFilterPlaybackUnmatched"] =
                " Loaded because no rule mentions them: {0}.",
            ["UpdateFilterLists"] = "Update lists now",
            ["SaveContentFilterOptions"] = "Save content filter settings",
            ["ContentFilterSaved"] = "Content filter settings saved.",
            ["ContentFilterSaveFailed"] =
                "The filtering level could not be saved: {0}",
            ["ApplicationUpdatesTitle"] = "Application updates",
            ["ApplicationUpdatesDescription"] =
                "The app checks the configured release server and only downloads or opens an update package after you approve it.",
            ["CheckForUpdates"] = "Check for updates",
            ["UpdateAvailableTitle"] = "Version {0} is available",
            ["UpdateAvailableDefaultNotes"] =
                "A verified application update is ready to download.",
            ["UpdateDownloadSize"] = "Download size: {0}",
            ["UpdateAskFirstNotice"] =
                "Nothing is downloaded or installed until you select Download and install.",
            ["UpdateLater"] = "Later",
            ["DownloadAndInstallUpdate"] = "Download and install",
            ["UpdateInstallerDidNotStart"] =
                "The verified installer was downloaded but could not be opened.",
            ["UpdateAndRestart"] = "Update and restart",
            ["UpdateRestarting"] =
                "Installing the update and restarting the app...",
            ["UpdateInstallPermissionRequired"] =
                "Allow this app to install apps, then tap update again.",
            ["UpdateCancelled"] = "The update was not applied: {0}",
            ["UpdateInstallerStarted"] =
                "The verified installer was opened. Follow its prompts to finish updating.",
            ["UpdateInstallFailed"] =
                "The update could not be installed: {0}",
            ["ApplicationUpToDate"] =
                "This desktop app is up to date.",
            ["UpdateCheckFailed"] =
                "The update server could not be checked: {0}",
            ["RemoteServerTitle"] = "Online account & sync",
            ["RemoteServerDescription"] =
                "Connect to your own UMD server for an optional online catalog, private device sync, and shareable lists.",
            ["RemoteServerUrl"] = "Server address",
            ["RemoteServerUrlPlaceholder"] = "https://umd.duncan.com.ar/",
            ["RemoteUsername"] = "Username",
            ["RemotePassword"] = "Password",
            ["RemoteDisplayName"] = "Display name",
            ["RemoteDeviceName"] = "This device",
            ["RemoteOnlineCatalog"] = "Use the server's online catalog",
            ["RemoteOnlineCatalogDescription"] =
                "When enabled, server catalog results appear alongside local and provider results. Your local catalog remains available offline.",
            ["RemoteSaveSettings"] = "Save server settings",
            ["RemoteCreateAccount"] = "Create account",
            ["RemoteSignIn"] = "Sign in",
            ["RemoteSignOut"] = "Sign out",
            ["RemoteAccountSignedOut"] = "Not signed in",
            ["RemoteAccountSignedIn"] = "Signed in as {0} (@{1})",
            ["RemoteSnapshotTitle"] = "Device sync",
            ["RemoteSnapshotEmpty"] = "No online snapshot is stored yet.",
            ["RemoteSnapshotAvailable"] =
                "Online snapshot revision {0}, updated {1}.",
            ["RemoteRefresh"] = "Check online copy",
            ["RemoteUpload"] = "Sync now",
            ["RemoteRestore"] = "Merge online copy",
            ["RemoteRestoreWarning"] =
                "Sync combines changes from this device and the online copy. Newer record changes win, while removals are retained so an offline device cannot restore deleted favorites or list entries.",
            ["RemoteRestoreNow"] = "Merge and sync",
            ["RemoteSyncPrivacy"] =
                "Passwords and session tokens are never saved. Sync excludes provider credentials, app settings, download records, and downloaded files.",
            ["RemoteShareList"] = "Share selected list",
            ["RemoteShareListDescription"] =
                "Publish a read-only catalog link containing only the titles in this list.",
            ["RemotePublishList"] = "Publish list",
            ["RemoteRevokeShare"] = "Stop sharing",
            ["RemoteSharedCatalogUrl"] = "Shared catalog address",
            ["RemoteOnlineCatalogProvider"] = "Your UMD server",
            ["RemoteServerSettingsSaved"] = "Online server settings saved.",
            ["RemoteServerSettingsSaveFailed"] =
                "The online server settings could not be saved: {0}",
            ["RemoteAccountSignedOutStatus"] = "Signed out of the online server.",
            ["RemoteSignOutFailed"] =
                "The server session could not be closed: {0}",
            ["RemoteSnapshotRefreshed"] = "The online copy was checked.",
            ["RemoteSnapshotNone"] = "No online snapshot exists yet.",
            ["RemoteSnapshotCheckFailed"] =
                "The online snapshot could not be checked: {0}",
            ["RemoteSnapshotUploaded"] =
                "Device sync completed at revision {0}.",
            ["RemoteSnapshotConflict"] =
                "Other devices kept updating the account through revision {0}; automatic reconciliation could not finish. Try Sync now again.",
            ["RemoteSnapshotUploadFailed"] =
                "This device could not be uploaded: {0}",
            ["RemoteSnapshotRestored"] =
                "Changes reconciled: {0} catalog title(s) added, {1} progress record(s), and {2} list(s).",
            ["RemoteSnapshotRestoreFailed"] =
                "The online copy could not be restored: {0}",
            ["RemoteSharedListPublished"] =
                "The list “{0}” is now available through its shared catalog link.",
            ["RemoteSharedListPublishFailed"] =
                "The selected list could not be shared: {0}",
            ["RemoteSharedListRevoked"] = "The shared list link was revoked.",
            ["RemoteSharedListRevokeFailed"] =
                "The shared list link could not be revoked: {0}",
            ["RemoteServerSettingsLoadFailed"] =
                "The online server settings could not be loaded: {0}",
            ["RemoteServerUrlRequired"] =
                "Enter the address of your UMD server first.",
            ["RemoteRegistrationDisabled"] =
                "This server does not allow public account creation. Ask its administrator to create your account.",
            ["RemoteSignInSucceeded"] = "Signed in as {0}.",
            ["RemoteRegistrationFailed"] =
                "The account could not be created: {0}",
            ["RemoteSignInFailed"] = "Sign-in failed: {0}",
            ["RemoteInitialSyncFailed"] =
                "You are signed in, but device sync could not finish: {0}",
            ["RemoteSessionExpired"] =
                "The server ended this session. Sign in again: {0}",
            ["RemoteSessionRestoreFailed"] =
                "The saved session could not be restored: {0}",
            ["RemoteSessionSaveFailed"] =
                "The session could not be saved for the next start: {0}",
            ["RemoteServerIncompatible"] =
                "This server does not support the account and sync API required by this app.",
            ["RemoteSyncUnavailable"] =
                "Device sync is unavailable in this version of the app.",
            ["CatalogData"] = "Catalog data",
            ["CatalogDataDescription"] =
                "Import or export portable UMD catalog files.",
            ["ImportCatalog"] = "Import catalog",
            ["ExportCatalog"] = "Export catalog",
            ["LocalFirst"] = "SAVED ON THIS DEVICE",
            ["TotalLabel"] = "TOTAL",
            ["MoviesLabel"] = "MOVIES",
            ["BooksLabel"] = "BOOKS",
            ["LiveTvLabel"] = "LIVE TV",
            ["LiveTvTitle"] = "Live TV",
            ["LiveTvSubtitle"] = "Live channels from Argentina",
            ["LiveTvDescription"] =
                "Browse and watch Argentine live channels from the configured public playlists.",
            ["LiveTvRefresh"] = "Refresh channels",
            ["LiveTvRefreshShort"] = "Refresh",
            ["LiveTvSearchLabel"] = "Find a channel",
            ["LiveTvSearchPlaceholder"] =
                "Search by channel, category, or provider",
            ["LiveTvProviderLabel"] = "Provider",
            ["LiveTvGroupLabel"] = "Category",
            ["LiveTvAllProviders"] = "All providers",
            ["LiveTvAllGroups"] = "All categories",
            ["LiveTvOtherGroup"] = "Other",
            ["LiveTvChannelSummary"] =
                "{0} of {1} channels · {2} providers loaded",
            ["LiveTvProviderFailures"] =
                "Some channel providers could not be loaded: {0}",
            ["LiveTvLoaded"] =
                "Loaded {0} live channels from {1} providers.",
            ["LiveTvLoadedPartial"] =
                "Loaded {0} live channels from {1} providers; {2} providers failed.",
            ["LiveTvLoadFailed"] =
                "Live channels could not be loaded: {0}",
            ["LiveTvPlaybackStarted"] =
                "Playing {0} from {1}.",
            ["LiveTvPlaybackFailed"] =
                "{0} could not be played: {1}",
            ["LiveTvWatch"] = "Watch live",
            ["LiveTvNoChannels"] = "No matching live channels",
            ["LiveTvNoChannelsHint"] =
                "Clear the filters or refresh the channel providers.",
            ["MetadataProviders"] = "Metadata providers & credits",
            ["MetadataAvailabilityAll"] =
                "Refresh movies and live-action series from TMDB, or anime, manga and web novels from MyAnimeList.",
            ["MetadataAvailabilityNative"] =
                "Refresh movies and live-action series from TMDB. MyAnimeList blocks direct browser API access, so anime, manga and web-novel refresh is available in native builds.",
            ["TmdbApiKey"] = "TMDB v3 API key",
            ["TmdbApiKeyPlaceholder"] = "TMDB API key",
            ["MalClientId"] = "MyAnimeList client ID",
            ["MalClientIdPlaceholder"] = "MAL client ID",
            ["PreferredLanguage"] = "Preferred language",
            ["PreferredLanguagePlaceholder"] = "en-US",
            ["NativeOnly"] = "Native app only",
            ["SaveProviderSettings"] = "Save provider settings",
            ["CredentialDisclosure"] =
                "Credentials are stored only in this device's local app data. They are masked here but are not encrypted by the app. Matching uses an exact current or alternative title, with the release year used to resolve duplicates.",
            ["MetadataAttribution"] =
                "This product uses the TMDB API but is not endorsed or certified by TMDB. Anime, manga and web-novel metadata is provided by MyAnimeList.",
            ["OpenMagnetTitle"] = "Open a magnet link or torrent file",
            ["PublicByDefault"] = "PUBLIC BY DEFAULT",
            ["OpenMagnet"] = "Open magnet",
            ["OpenTorrentFile"] = "Open .torrent file",
            ["OpenMagnetsAutomatically"] = "Open incoming magnets automatically",
            ["ImportInfoJson"] = "Import torrent info.json",
            ["ForcePrivate"] = "Force unmarked torrent sources into private mode",
            ["SaveOptions"] = "Save options",
            ["PendingMagnet"] = "Received magnet waiting for confirmation",
            ["OpenReceivedMagnet"] = "Open received magnet",
            ["FullBackup"] = "Full local-data backup",
            ["FullBackupDescription"] =
                "Preserves catalog IDs, library, progress, preferences, and download state.",
            ["CreateBackup"] = "Create backup",
            ["RestoreBackup"] = "Restore backup",
            ["ViewDetails"] = "View details",
            ["BackToCatalog"] = "Back to catalog",
            ["EntriesTitle"] = "Episodes, chapters, tracks & releases",
            ["ContinueCatalogEntry"] = "Continue · {0}",
            ["SortNewestFirst"] = "Newest first",
            ["SortOldestFirst"] = "Oldest first",
            ["MarkUnitSeen"] = "Mark as seen",
            ["MarkUnitUnseen"] = "Mark as unseen",
            ["MarkAllUnitsSeen"] = "Mark all as seen",
            ["MarkAllUnitsUnseen"] = "Mark all as unseen",
            ["PlaybackAvailable"] = "Playback available",
            ["NoMappedSource"] = "No torrent media source is mapped.",
            ["BackToDetails"] = "Back to details",
            ["StreamingSource"] = "Streaming from the selected torrent",
            ["OnePage"] = "One page",
            ["Continuous"] = "Continuous",
            ["Previous"] = "Previous",
            ["Next"] = "Next",
            ["CatalogPageSummary"] = "Page {0} of {1} · {2} items",
            ["DecreaseFont"] = "Decrease font size",
            ["IncreaseFont"] = "Increase font size",
            ["ZoomOut"] = "Zoom out",
            ["ResetZoom"] = "Reset zoom",
            ["ZoomIn"] = "Zoom in",
            ["PreviousChapter"] = "Previous chapter",
            ["NextChapter"] = "Next chapter",
            ["PreviousEpisode"] = "Previous episode",
            ["NextEpisode"] = "Next episode",
            ["BackToManga"] = "Manga",
            ["GoToTop"] = "Go to top",
            ["BusyLabel"] = "Loading",
            ["ErrorMessageLabel"] = "Error message",
            ["StatusMessageLabel"] = "Status message",
            ["TmdbApiKeyLabel"] = "TMDB API key",
            ["MalClientIdLabel"] = "MyAnimeList client ID",
            ["MetadataLanguageLabel"] = "Metadata language",
            ["MagnetInputLabel"] = "Magnet link",
            ["ReaderPagesLabel"] = "Continuous reader pages",
            ["EpubContentLabel"] = "EPUB chapter content",
            ["EpubIllustrationLabel"] = "EPUB illustration",
            ["ArtworkLabel"] = "Cover artwork",
            ["CatalogJsonFileType"] = "UMD catalog JSON",
            ["TorrentFileType"] = "BitTorrent metadata",
            ["FullBackupFileType"] = "UMD full backup",
            ["ImportCatalogDialog"] = "Import catalog JSON",
            ["ExportCatalogDialog"] = "Export catalog JSON",
            ["OpenTorrentFileDialog"] = "Open a .torrent file",
            ["CreateBackupDialog"] = "Create full local-data backup",
            ["RestoreBackupDialog"] = "Restore full local-data backup",

            ["PlatformNativeDesktop"] = "Native desktop",
            ["PlatformBrowser"] = "Browser / WebAssembly",
            ["PlatformAndroid"] = "Android",
            ["PlatformAndroidTv"] = "Android TV",
            ["PlatformIos"] = "iOS / iPadOS",
            ["Unknown"] = "Unknown",
            ["PrivacyMse"] = "Public + optional private torrents · MSE",
            ["PrivacyDtls"] = "Public + optional private torrents · DTLS",
            ["TrackerDetailsHidden"] = "tracker details hidden",
            ["MagnetDetailsHidden"] = "Magnet details hidden",
            ["TorrentAdapterConnected"] = "{0} adapter connected",
            ["TorrentAdapterDisconnected"] = "{0} adapter is not connected yet",
            ["CatalogRecovered"] =
                "A damaged local catalog was replaced with a clean database. The original files were preserved at {0}",
            ["MagnetOptionsSaved"] = "Magnet opening options saved.",
            ["MagnetOptionsSaveFailed"] = "Magnet options could not be saved: {0}",
            ["MetadataOptionsSaved"] = "Metadata provider settings saved on this device.",
            ["MetadataOptionsSaveFailed"] = "Metadata provider settings could not be saved: {0}",
            ["MetadataUpdated"] = "Metadata updated from {0}.",
            ["MetadataUpdateFailed"] = "Metadata could not be updated: {0}",
            ["CatalogItemDeleted"] = "Deleted {0} from the catalog.",
            ["CatalogItemDeleteFailed"] =
                "The catalog item could not be deleted: {0}",
            ["CatalogImported"] = "Catalog imported: {0} created, {1} updated.",
            ["CatalogInvalid"] = "The selected catalog is invalid: {0}",
            ["CatalogImportFailed"] = "The catalog could not be imported: {0}",
            ["CatalogExported"] = "Catalog JSON exported without database IDs or user data.",
            ["CatalogExportFailed"] = "The catalog could not be exported: {0}",
            ["BackupExported"] = "Full backup exported with catalog IDs and local user data.",
            ["BackupExportFailed"] = "The full backup could not be exported: {0}",
            ["BackupRestored"] = "Full backup restored. The previous database was preserved at {0}",
            ["BackupRestoreFailed"] = "The full backup could not be restored: {0}",
            ["SaveTargetNotEmpty"] =
                "'{0}' already contains data and this platform cannot clear it. " +
                "Choose a new file name, or delete that file first.",
            ["CatalogLoadFailed"] = "The local catalog could not be loaded: {0}",
            ["SearchResultSources"] = "{0} · {1} sources",
            ["SearchSourcesLoading"] = "Adding sources from {0}…",
            ["SearchSourcesLoaded"] = "{0} more sources added.",
            ["SearchResultsTitle"] = "Search: {0} ({1})",
            ["SearchCompleted"] = "{0} local and online results found.",
            ["SearchCompletedWithFailures"] =
                "{0} results found; {1} online providers were unavailable.",
            ["SearchFailed"] = "The catalog search failed: {0}",
            ["ContentSourceAdded"] = "Torrent catalog provider added.",
            ["ContentSourcesSaved"] = "Content-source settings saved on this device.",
            ["ContentSourceRemoved"] = "Torrent catalog provider removed.",
            ["ContentSourceSaveFailed"] = "Content-source settings could not be saved: {0}",
            ["ContentSourceLoadFailed"] = "Content-source settings could not be loaded: {0}",
            ["DailyUpdatesChecking"] =
                "Checking daily sources...",
            ["DailyUpdatesComplete"] =
                "Daily sources synced: {0} new title(s), {1} refreshed.",
            ["DailyUpdatesPartial"] =
                "Daily sources synced: {0} new title(s), {1} refreshed. {2} source(s) will retry next time Home opens.",
            ["DailyUpdatesFailed"] =
                "Daily sources will retry next time Home opens: {0}",
            ["OnlineItemLoaded"] =
                "Loaded from {0} and merged into the local catalog: {1} created, {2} updated.",
            ["OnlineItemLoadFailed"] = "The online title could not be loaded: {0}",
            ["CatalogItemMissing"] = "The selected catalog item no longer exists.",
            ["DetailsLoadFailed"] = "The title details could not be loaded: {0}",
            ["TorrentEngineMissing"] = "This host has no torrent engine configured.",
            ["OrderedPagesOpened"] = "Opened {0} ordered pages.",
            ["SingleImageOpened"] = "Opened a single image page.",
            ["ReadOnlinePages"] = "Read {0} online pages",
            ["FindChapterOnline"] = "Find chapter on enabled APIs",
            ["WatchOnlineServers"] = "Watch online ({0} servers)",
            ["FindEpisodeOnline"] = "Find episode on enabled APIs",
            ["ChooseContentProvider"] = "Choose a source for {0}",
            ["MultipleContentProvidersFound"] =
                "{0} enabled providers found this entry. Choose the one to use.",
            ["ContentProvidersFoundWhileSearching"] =
                "Sources found so far: {0}. You can choose now while the others continue.",
            ["SearchingContentProviders"] =
                "Searching the enabled providers for this entry…",
            ["ProviderTitleMatch"] = "{0:P0} title match",
            ["VideoServers"] = "Video servers",
            ["EmbeddedVideoServerShort"] = "Web",
            ["EmbeddedWebPlayerNotConfigured"] =
                "This platform cannot show embedded video servers.",
            ["EmbeddedWebPlayerUiThreadRequired"] =
                "The embedded video player must be created on the app interface thread.",
            ["EmbeddedWebAddressInvalid"] =
                "The embedded video server returned an invalid web address.",
            ["OnlinePagesOpened"] = "Opened {0} ordered pages from the API provider.",
            ["OnlinePagesOpenFailed"] = "The provider pages could not be opened: {0}",
            ["OnlineChapterResolved"] =
                "Matched on {0}, synchronized the local manga and chapters, and opened {1} pages (title similarity {2:P0}).",
            ["OnlineChapterResolveFailed"] =
                "The selected chapter could not be found on enabled APIs: {0}",
            ["SavedMangaRefreshed"] =
                "Chapter list refreshed from {0}.",
            ["SavedAnimeRefreshed"] =
                "Episode list refreshed from {0}.",
            ["OnlineVideoOpened"] =
                "Playing from {0}. {1} servers are available below the player.",
            ["NoPlayableOnlineVideoServer"] =
                "None of the available online video servers could be opened.",
            ["OnlineVideoOpenFailed"] =
                "The online video server could not be opened: {0}",
            ["OnlineEpisodeResolved"] =
                "Matched on {0}, synchronized the local episode list, and found {1} video servers (title similarity {2:P0}).",
            ["OnlineEpisodeResolveFailed"] =
                "The selected episode could not be found on enabled APIs: {0}",
            ["AdjacentEpisodeNeedsProvider"] =
                "{0} could not be opened automatically. Choose a source for it below.",
            ["AdjacentEpisodeNotFound"] =
                "No enabled API provider offers {0}.",
            ["VideoServerChanged"] =
                "Switched playback to {0}.",
            ["RemoteImageTypeUnsupported"] =
                "The provider returned an unsupported image type.",
            ["CbzPagesOpened"] = "Opened {0} CBZ pages.",
            ["EpubChaptersOpened"] = "Opened {0} EPUB chapters.",
            ["PdfRendererUnavailable"] = "This host could not initialize its PDF renderer.",
            ["PdfPagesOpened"] = "Opened {0} PDF pages.",
            ["SelectPrimarySource"] =
                "Select a primary video source; mapped subtitles are attached automatically.",
            ["ReaderNotImplemented"] =
                "This packaged reading format is cataloged but its reader is not implemented yet.",
            ["MediaPresenterUnavailable"] =
                "This host could not initialize its in-app video/audio presenter.",
            ["PlaybackResumed"] = "Resumed {0} from {1}.",
            ["PlaybackStarted"] = "Playing {0}.",
            ["MediaOpenFailed"] = "The media source could not be opened: {0}",
            ["MagnetOptionsLoadFailed"] = "Incoming magnet options could not be loaded: {0}",
            ["MetadataOptionsLoadFailed"] = "Metadata provider settings could not be loaded: {0}",
            ["UnitsMarkedSeenThrough"] =
                "Marked {0} entries as seen through {1}.",
            ["UnitMarkedSeen"] = "{0} was marked as seen.",
            ["UnitMarkedUnseen"] = "{0} was marked as unseen.",
            ["AllUnitsMarkedSeen"] = "Marked {0} entries as seen.",
            ["AllUnitsMarkedUnseen"] = "Marked {0} entries as unseen.",
            ["UnitSeenSaveFailed"] =
                "The seen state could not be saved: {0}",
            ["UserListsLoadFailed"] = "Your lists could not be loaded: {0}",
            ["UserListNameRequired"] = "Enter a name for the list.",
            ["UserListNameExists"] = "A list with this name already exists.",
            ["UserListCreated"] = "Created the list {0}.",
            ["UserListRenamed"] = "Renamed the list to {0}.",
            ["UserListDeleted"] = "Deleted the list {0}.",
            ["UserListSaveFailed"] = "The list could not be saved: {0}",
            ["ItemAddedToUserList"] = "Added {0} to {1}.",
            ["ItemRemovedFromUserList"] = "Removed {0} from {1}.",
            ["UserListMembershipSaveFailed"] =
                "The list membership could not be changed: {0}",
            ["PasteMagnetFirst"] = "Paste a magnet link first.",
            ["MagnetConfirmationRequired"] =
                "A magnet link was received. Select Open received magnet to continue.",
            ["MagnetEngineUnavailable"] =
                "The magnet link is valid, but the {0} adapter is not connected yet.",
            ["MagnetAlreadyOpen"] = "This magnet is already open.",
            ["MagnetInvalid"] = "The magnet link is invalid: {0}",
            ["MagnetIncompatible"] =
                "The magnet link is valid, but this build cannot open it: {0}",
            ["MagnetUnsafe"] = "The magnet could not be opened safely: {0}",
            ["MagnetOpenFailed"] = "The magnet could not be opened: {0}",
            ["TorrentFileEngineUnavailable"] =
                "The torrent file is valid, but the {0} adapter is not connected yet.",
            ["TorrentFileAlreadyOpen"] = "This torrent is already open.",
            ["TorrentFileInvalid"] = "The torrent file is invalid: {0}",
            ["TorrentFileIncompatible"] =
                "The torrent file is valid, but this build cannot open it: {0}",
            ["TorrentFileUnsafe"] =
                "The torrent file could not be opened safely: {0}",
            ["TorrentFileOpenFailed"] =
                "The torrent file could not be opened: {0}",
            ["TorrentManifestImported"] =
                "Torrent opened and info.json imported: {0} created, {1} updated.",
            ["TorrentManifestNotFound"] =
                "Torrent opened; no info.json catalog manifest was found.",
            ["TorrentManifestPending"] =
                "Torrent opened. info.json will be checked in the background.",
            ["TorrentOpened"] = "Torrent opened.",
            ["TorrentActivityAdding"] = "Adding torrent...",
            ["TorrentActivityMetadata"] = "Finding torrent metadata...",
            ["TorrentActivityChecking"] = "Checking downloaded files...",
            ["TorrentActivityFindingPeers"] = "Waiting for peers...",
            ["TorrentActivityDownloading"] = "Downloading...",
            ["TorrentActivityReady"] = "Ready to open",
            ["TorrentActivitySeeding"] = "Download complete - seeding",
            ["TorrentActivityPaused"] = "Download paused",
            ["TorrentActivityFailed"] = "Torrent failed",
            ["TorrentActivityRemoving"] = "Stopping torrent...",
            ["TorrentActivityPreparingContent"] =
                "Preparing the selected chapter or episode",
            ["TorrentActivityTorrentFile"] = "Torrent file",
            ["TorrentActivityReadingFile"] = "Reading torrent metadata",
            ["TorrentActivityManualMagnet"] = "Manual magnet",
            ["TorrentActivityOpeningMagnet"] = "Opening magnet link",
            ["TorrentActivityProgressWithSize"] = "{0:0}% - {1} / {2}",
            ["TorrentActivityProgress"] = "{0:0}%",
            ["TorrentActivitySpeed"] = "{0}/s",
            ["TorrentActivityPeers"] = "{0} peers",
            ["TorrentActivityEta"] = "{0} left",
            ["TorrentActivityActionFailed"] =
                "The torrent action failed: {0}",
            ["PauseTorrent"] = "Pause",
            ["ResumeTorrent"] = "Resume",
            ["StopTorrent"] = "Stop",
            ["DismissTorrentActivity"] = "Dismiss",
            ["ManifestWarning"] = "{0} Manifest warning: {1}",
            ["Subtitles"] = "Subtitles",
            ["OriginalUnspecified"] = "Original / unspecified",
            ["PlaybackFailed"] = "Playback failed: {0}",
            ["PlaybackProgressSaveFailed"] = "Playback progress could not be saved: {0}",

            ["Uncategorized"] = "Uncategorized",
            ["MediaTypeMovie"] = "Movie",
            ["MediaTypeBook"] = "Book",
            ["MediaTypeAudio"] = "Audio",
            ["SubtypeMovie"] = "Movie",
            ["SubtypeSeries"] = "Series",
            ["SubtypeAnimeMovie"] = "Anime movie",
            ["SubtypeAnimeSeries"] = "Anime series",
            ["SubtypeDocumentary"] = "Documentary",
            ["SubtypeShort"] = "Short",
            ["SubtypeBook"] = "Book",
            ["SubtypeNovel"] = "Novel",
            ["SubtypeManga"] = "Manga",
            ["SubtypeComic"] = "Comic",
            ["SubtypeLightNovel"] = "Light novel",
            ["SubtypeWebNovel"] = "Web novel",
            ["SubtypeAlbum"] = "Music album",
            ["SubtypeSingle"] = "Single",
            ["SubtypeAudiobook"] = "Audiobook",
            ["SubtypePodcast"] = "Podcast",
            ["SubtypeRadioDrama"] = "Radio drama",
            ["LanguageUnknown"] = "Language unknown",
            ["NameKindOriginal"] = "original",
            ["NameKindTranslation"] = "translation",
            ["NameKindAlternative"] = "alternative",
            ["NameKindCanonical"] = "canonical",
            ["NameKindSynonym"] = "synonym",
            ["NameKindPrevious"] = "previous",
            ["NameKindOther"] = "other",
            ["Read"] = "Read",
            ["Play"] = "Play",
            ["PlayLanguage"] = "Play {0}",
            ["Subtitle"] = "Subtitle",
            ["SourceImageSequence"] = "Numbered image sequence",
            ["SourceSingleImage"] = "Single JPG, PNG, or WebP image",
            ["SourceCbz"] = "Packaged CBZ images",
            ["SourceEpub"] = "Packaged EPUB book",
            ["SourcePdf"] = "Packaged PDF document",
            ["SourceTorrentFile"] = "Mapped torrent file",
            ["AssetRoleMedia"] = "Media",
            ["AssetRolePrimary"] = "Primary",
            ["AssetRoleSubtitle"] = "Subtitle",
            ["AssetRoleCaption"] = "Caption",
            ["AssetRoleCover"] = "Cover",
            ["AssetRoleThumbnail"] = "Thumbnail",
            ["FullRecording"] = "Full recording",
            ["MainRelease"] = "Main release",
            ["SeasonNumber"] = "Season {0}",
            ["AllSeasons"] = "All seasons",
            ["SeasonSelectorLabel"] = "Season",
            ["UpdateFromProvider"] = "Update from {0}",
            ["DeleteChapter"] = "Delete chapter",
            ["DeleteEpisode"] = "Delete episode",
            ["ConfirmDeleteEntry"] = "Delete {0} from this local catalog? Downloaded files are kept. Provider refresh or sync may restore this entry.",
            ["CatalogEntryMissing"] = "This chapter or episode is no longer in the catalog.",
            ["CatalogEntryDeleted"] = "The entry was deleted from the local catalog.",
            ["CatalogEntryDeleteFailed"] = "Could not delete this entry: {0}",
            ["DeleteCatalogItem"] = "Delete from catalog",
            ["ConfirmDeleteCatalogItem"] =
                "Delete this title? Its saved progress, favorites, and list memberships will also be removed.",
            ["NoReadableSource"] =
                "No playable or readable source has been mapped for this title.",
            ["UnitEpisode"] = "Episode",
            ["UnitChapter"] = "Chapter",
            ["UnitTrack"] = "Track",
            ["UnitAudioChapter"] = "Audio chapter",
            ["UnitPart"] = "Part",
            ["ProgressPage"] = "page {0}",
            ["ProgressTime"] = "{0}",
            ["ContinueProgress"] = "Continue at {0}",
            ["NewContentBadge"] = "New",
            ["SeasonUnitProgress"] = "Season {0} · {1} {2}",
            ["ProgressTargetMissing"] =
                "That saved chapter or episode is no longer available.",
            ["ProgressSourceMissing"] =
                "No playable or readable source is available for this progress.",
            ["ProgressOpenFailed"] =
                "The saved progress could not be opened: {0}",
            ["PageCount"] = "{0} pages",
            ["DurationHoursMinutes"] = "{0}h {1}m",
            ["DurationMinutes"] = "{0}m",
            ["NoPages"] = "No pages",
            ["PageOf"] = "Page {0} of {1}",
            ["PageNumber"] = "Page {0}",
            ["ImageStreamMissing"] = "The torrent engine did not provide an image stream.",
            ["ImageTooLarge"] = "The image exceeds the 96 MiB reader limit.",
            ["PageLoadFailed"] = "This page could not be loaded: {0}",
            ["RetryPageLoad"] = "Try this page again",
            ["ProfilesTitle"] = "Who is watching?",
            ["ProfilesDescription"] =
                "Everyone keeps their own favourites, progress and lists. The library is shared.",
            ["ProfileAdded"] = "Profile added.",
            ["ProfileRemoved"] = "Profile removed.",
            ["AddProfile"] = "Add profile",
            ["RemoveProfile"] = "Remove",
            ["NewProfileName"] = "Profile name",
            ["SwitchProfile"] = "Switch profile",
            ["CurrentProfile"] = "Using {0}",
            ["InUseLabel"] = "In use",
            ["NoChapters"] = "No chapters",
            ["ChapterOf"] = "Chapter {0} of {1}",
            ["EpubImageStreamMissing"] = "The EPUB reader did not provide an image stream.",
            ["EpubImageLoadFailed"] = "This EPUB image could not be loaded: {0}",

            ["PlayerAudio"] = "AUDIO",
            ["PlayerPartyAction"] = "Party View",
            ["PlayerMuteShort"] = "Mute",
            ["PlayerUnmuteShort"] = "Unmute",
            ["PlayerTrackNumber"] = "Track {0} · {1}",
            ["PlayerEngineLabel"] = "Playback engine",
            ["PlayerSettingsAction"] = "Options",
            ["PlayerAudioTrack"] = "Audio track",
            ["PlayerSubtitleTrack"] = "Subtitles",
            ["PlayerTrackNone"] = "Off",
            ["PlayerTracksUnavailable"] = "No tracks available yet.",
            ["PlayerTrackFailed"] = "This track could not be selected.",
            ["PlayerMuteAction"] = "Mute (M)",
            ["PlayerUnmuteAction"] = "Unmute (M)",
            ["PlayerSeekLabel"] = "Playback position",
            ["PlayerShortcutsHint"] = "Space: play / pause · ← / →: seek · M: mute · F: fullscreen · Esc: close panel / exit fullscreen",
            ["WatchPartyUnavailableHint"] = "Sign in to a server with watch parties enabled to watch with friends.",
            ["WatchPartyCopyCode"] = "Copy code",
            ["WatchPartyCodeCopied"] = "Code copied. Share it with your friends.",
            ["WatchPartyCopyFailed"] = "Could not copy the code. Select it above to copy manually.",
            ["WatchPartyResync"] = "Sync me now",
            ["StreamingMonoTorrent"] = "Streaming from MonoTorrent",
            ["PlayerPlay"] = "Play",
            ["CastAction"] = "Cast",
            ["CastLocal"] = "This PC",
            ["CastSearchHint"] = "Looking for Chromecast devices on your local network…",
            ["CastWirelessDisplay"] = "Windows wireless display (Win + K)",
            ["CastWirelessFailed"] = "Could not open wireless display. Press Win + K to open it in Windows.",
            ["CastChooseDevice"] = "Choose where to play.",
            ["CastConnected"] = "Playing on {0}",
            ["CastDiscoveryFailed"] = "Could not search for Chromecast devices. Close and reopen this menu to retry.",
            ["CastNoDevices"] = "No Chromecast found yet. Check that your PC and device are on the same network.",
            ["CastDeviceLost"] = "The device is no longer available. You can continue on this PC.",
            ["CastConnecting"] = "Changing playback destination…",
            ["CastConnectFailed"] = "Could not connect to the selected device.",
            ["CastFailedResumedLocal"] = "Could not cast. Playback has returned to this PC.",
            ["CastRestoreFailed"] = "Could not resume playback. Choose This PC to retry, or select another source.",
            ["PlayerReplay"] = "Replay",
            ["PlayerPause"] = "Pause",
            ["PlayerRewind10"] = "Rewind 10 s",
            ["PlayerForward30"] = "Forward 30 s",
            ["PlayerTvRemoteHint"] = "OK: select  •  ↑: timeline  •  ↓: server / player",
            ["PlayerTvLiveRemoteHint"] = "OK: select  •  ↓: server / player",
            ["PlayerSeekPosition"] = "Playback position",
            ["PlayerLive"] = "LIVE",
            ["PlayerVolume"] = "Volume",
            ["PlayerEnterFullscreen"] = "Enter fullscreen",
            ["PlayerExitFullscreen"] = "Exit fullscreen",
            ["PlayerAudioRelease"] = "Audio / release",
            ["PlayerServerQuality"] = "Server / quality",
            ["PlayerAudioOrRelease"] = "Audio or release",
            ["PlayerClose"] = "Close player",
            ["PlayingTitle"] = "Playing {0}",
            ["BrowserPlaybackFailed"] = "The browser could not play this torrent file.",
            ["BrowserStateInvalid"] = "The browser media player returned an invalid state.",
            ["BrowserServiceWorkerUrlRequired"] =
                "Browser playback requires a service-worker URL for every media rendition.",
            ["LibVlcInitializeFailed"] = "LibVLC could not be initialized on this device.",
            ["NativeRenditionListInvalid"] =
                "The media request contains an invalid audio/release rendition list.",
            ["NativeMediaStreamMissing"] =
                "The native torrent engine did not provide a readable media stream.",
            ["NativeMediaStreamUnreadable"] = "The torrent media stream is not readable.",
            ["NativePlaybackSourceMissing"] =
                "The media request has no native playback source.",
            ["LibVlcStreamRejected"] = "LibVLC rejected the selected media stream.",
            ["LibVlcVideoSurfaceUnavailable"] =
                "The in-app video surface was not ready, so playback was not started outside the application.",
            ["RenditionOpenFailed"] =
                "The selected audio/release rendition could not be opened: {0}",
            ["RenditionRestoreFailed"] =
                "The rendition switch failed and the previous source could not be restored.",
            ["LibVlcDecodeFailed"] =
                "LibVLC could not decode or read the selected video stream.",
            ["LibVlcPlaybackStartTimedOut"] =
                "The selected video server did not start playback in time.",
            ["MediaRenditionListInvalid"] = "The media rendition list is invalid.",
            ["MediaRenditionInvalid"] = "A media rendition is invalid.",
            ["PlaybackUrlInvalid"] = "The torrent playback URL is invalid.",
            ["PlaybackOriginInvalid"] =
                "The torrent playback URL must use the application origin.",
            ["SubtitleListInvalid"] = "The subtitle track list is invalid.",
            ["SubtitleTrackInvalid"] = "A subtitle track is invalid.",
            ["PdfEmptyOrTooLarge"] =
                "The PDF is empty or exceeds the 256 MiB reader limit.",
            ["BrowserPdfUrlRequired"] =
                "Browser PDF reading requires a service-worker torrent URL.",
            ["BrowserPdfMetadataInvalid"] =
                "The browser PDF renderer returned invalid metadata.",
            ["BrowserPdfDimensionsInvalid"] =
                "The browser PDF renderer returned invalid page dimensions.",
            ["PdfPageNotOwned"] = "The page does not belong to this PDF document.",
            ["RenderedPdfPageTooLarge"] =
                "The rendered PDF page exceeds the 64 MiB image limit.",
            ["RenderedPdfPageInvalid"] =
                "The browser PDF renderer returned an invalid or oversized PNG page.",
            ["PlayerBack"] = "Back",
            ["PlayerBackDescription"] = "Close player and return to title details",
            ["AudioPlayerFor"] = "Audio player for {0}",
            ["VideoPlayerFor"] = "Video player for {0}",
            ["AndroidPlaybackLoading"] = "Loading {0}…",
            ["AndroidPlaybackEngine"] = "Player: {0}",
            ["AndroidPlaybackEncryptionUnsupported"] = "This encrypted stream requires a DRM-capable player; neither bundled Android engine can open it.",
            ["AndroidPlaybackFailed"] =
                "Android could not decode or read the media stream ({0}).",
            ["IosPlaybackAddressRejected"] =
                "iOS rejected the local torrent playback address.",
            ["IosPlayerCreateFailed"] =
                "iOS could not create a player for the torrent stream.",
            ["IosWindowUnavailable"] =
                "iOS did not provide an active window for playback.",
            ["InAppPlayerNotConfigured"] =
                "This host has not configured an in-app video/audio presenter.",
            ["TvNavigationHint"] =
                "Use the directional pad to move, Select to open, and Back to return."
        };

    private static readonly IReadOnlyDictionary<string, string> Spanish =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AppName"] = "Distribución Multimedia Universal",
            ["ReaderAutoScrollStart"] = "Iniciar desplazamiento",
            ["ReaderChapterShort"] = "Cap. {0}",
            ["ReaderAutoScrollPause"] = "Pausar desplazamiento",
            ["ReaderAutoScrollSpeed"] = "Velocidad",
            ["ReaderSettingsTitle"] = "Opciones de lectura",
            ["ReaderModeTitle"] = "Modo de lectura",
            ["ReaderPageSizeTitle"] = "Tamaño de página",
            ["ReaderAutoScrollTitle"] = "Desplazamiento automático",
            ["ReaderAutoScrollContinuousHint"] = "Selecciona Continuo para activar el desplazamiento automático.",
            ["ReaderMiddleScrollHint"] = "Haz un clic central y suelta. Mueve el ratón para desplazar; otro clic o Escape lo detiene.",
            ["ReaderRemoteScrollHint"] = "Usa arriba/abajo para recorrer las opciones, izquierda/derecha para ajustar la velocidad y Atrás para cerrar.",
            ["ReaderSettingsClose"] = "Cerrar opciones de lectura",
            ["ReaderAutoScrollHint"] = "Se detiene al final del capítulo. Escape pausa. Haz clic central en la página para desplazar moviendo el ratón; otro clic o Escape lo detiene.",
            ["PlayerMuteShort"] = "Silenciar",
            ["PlayerUnmuteShort"] = "Activar sonido",
            ["PlayerTrackNumber"] = "Pista {0} · {1}",
            ["PlayerEngineLabel"] = "Motor de reproducción",
            ["PlayerPartyAction"] = "Party View",
            ["PlayerSettingsAction"] = "Opciones",
            ["PlayerAudioTrack"] = "Pista de audio",
            ["PlayerSubtitleTrack"] = "Subtítulos",
            ["PlayerTrackNone"] = "Desactivados",
            ["PlayerTracksUnavailable"] = "Todavía no hay pistas disponibles.",
            ["PlayerTrackFailed"] = "No se pudo seleccionar esta pista.",
            ["PlayerMuteAction"] = "Silenciar (M)",
            ["PlayerUnmuteAction"] = "Activar sonido (M)",
            ["PlayerSeekLabel"] = "Posición de reproducción",
            ["PlayerShortcutsHint"] = "Espacio: reproducir / pausar · ← / →: saltar · M: silenciar · F: pantalla completa · Esc: cerrar panel / salir de pantalla completa",
            ["WatchPartyUnavailableHint"] = "Inicia sesión en un servidor con salas habilitadas para ver contenido con amigos.",
            ["WatchPartyCopyCode"] = "Copiar código",
            ["WatchPartyCodeCopied"] = "Código copiado. Compártelo con tus amigos.",
            ["WatchPartyCopyFailed"] = "No se pudo copiar el código. Selecciónalo arriba para copiarlo manualmente.",
            ["WatchPartyResync"] = "Sincronizarme ahora",
            ["ShellSubtitle"] = "Contenido universal",
            ["RuntimeLabel"] = "ENTORNO",
            ["NavHome"] = "Inicio",
            ["NavMovies"] = "Películas y series",
            ["NavAnime"] = "Anime",
            ["NavBooks"] = "Libros y manga",
            ["NavLiveTv"] = "TV en vivo",
            ["NavLists"] = "Listas",
            ["NavFriends"] = "Amigos",
            ["NavDownloads"] = "Descargas",
            ["NavSettings"] = "Configuración",
            ["CollapseMenu"] = "Contraer menú",
            ["ExpandMenu"] = "Expandir menú",
            ["SectionAll"] = "Todo el contenido",
            ["HomeLastSeen"] = "Visto recientemente",
            ["HomeSectionCount"] = "{0} títulos",
            ["HomeSeeAll"] = "Ver todo",
            ["ShelfScrollLeft"] = "Desplazar a la izquierda",
            ["ShelfScrollRight"] = "Desplazar a la derecha",
            ["SettingsClose"] = "Cerrar",
            ["FavoriteTitles"] = "Títulos favoritos",
            ["ListFilter"] = "Lista",
            ["AllLists"] = "Todas las listas",
            ["ListFavoritesOnly"] = "Solo títulos favoritos",
            ["ContentSourceDuplicate"] =
                "Ya hay un proveedor configurado con esa dirección.",
            ["ShelfRecentlyAdded"] = "Añadido recientemente",
            ["ShelfRecentlyUpdated"] = "Actualizado recientemente",
            ["CatalogViewGrid"] = "Ver todo",
            ["CatalogViewRows"] = "Ver por filas",
            ["HomeLiveTvPrompt"] = "Los canales se cargan al abrir TV en vivo.",
            ["CatalogSearchLabel"] = "Buscar en el catálogo y proveedores en línea habilitados",
            ["SearchAndFilters"] = "Buscar y filtrar",
            ["SearchAndFiltersShort"] = "Filtros",
            ["CatalogSearchPlaceholder"] =
                "Buscar títulos, nombres alternativos, géneros o distribuidores",
            ["Search"] = "Buscar",
            ["ClearSearch"] = "Limpiar",
            ["LocalResult"] = "LOCAL",
            ["OnlineResult"] = "EN LÍNEA · {0}",
            ["ContentSourcesTitle"] = "Fuentes de contenido",
            ["ContentSourcesDescription"] =
                "Elige qué catálogos en línea y proveedores de TV en vivo puede contactar la aplicación. Los magnets manuales permanecen locales.",
            ["ContentSourceManual"] = "MANUAL",
            ["ContentSourceTorrentCatalog"] = "CATÁLOGO TORRENT",
            ["ContentSourceApi"] = "FUENTE EN LÍNEA",
            ["ContentSourceLiveTv"] = "TV EN VIVO",
            ["ContentSourceGroupManual"] = "Manual",
            ["ContentSourceGroupTorrentCatalog"] = "Catálogos torrent",
            ["ContentSourceGroupApi"] = "Fuentes en línea",
            ["ContentSourceGroupLiveTv"] = "TV en vivo",
            ["ApiProviderCategoryMoviesSeries"] = "Películas y series",
            ["ApiProviderCategoryAnime"] = "Anime",
            ["ApiProviderCategoryBooksManga"] = "Libros y manga",
            ["ApiProviderCategoryAllMedia"] = "Todo el contenido",
            ["ContentSourceGroupSummary"] = "{0} de {1} activas",
            ["ContentSourceGroupEnableAll"] = "Activar todas",
            ["ContentSourceGroupDisableAll"] = "Desactivar todas",
            ["ManualProviderName"] = "Enlaces magnet manuales",
            ["Enabled"] = "Habilitado",
            ["ContentSourceNameLabel"] = "Nombre de la fuente de contenido",
            ["ContentSourceBaseUrlLabel"] = "URL base de la fuente de contenido",
            ["Remove"] = "Quitar",
            ["TorrentProviderNamePlaceholder"] = "Nombre del catálogo torrent",
            ["TorrentProviderUrlPlaceholder"] = "Endpoint JSON del catálogo",
            ["AddTorrentProvider"] = "Agregar proveedor torrent",
            ["ApiProviderCodeOnlyHint"] =
                "Los proveedores API se instalan mediante el código de la aplicación. Aquí puedes habilitarlos y cambiar su URL base, pero no crearlos ni eliminarlos.",
            ["SaveContentSources"] = "Guardar fuentes de contenido",
            ["DonationTitle"] = "Apoya el proyecto",
            ["DonationStartupMessage"] =
                "Universal Media Distribution se desarrolla y mantiene de forma independiente. Si te resulta útil, puedes apoyar su desarrollo continuo con USDT o USDC.",
            ["DonationSettingsDescription"] =
                "Contribuye con USDT o USDC para ayudar a financiar el desarrollo y mantenimiento continuos.",
            ["DonationStablecoins"] = "USDT / USDC",
            ["DonationAddressLabel"] = "Dirección de la billetera",
            ["DonationQrCode"] = "Código QR de la billetera de donaciones",
            ["DonationNetworkWarning"] =
                "Confirma la red prevista con el responsable del proyecto y asegúrate de que tu billetera sea compatible antes de enviar. Las transferencias no se pueden revertir.",
            ["DonationCopyAddress"] = "Copiar dirección",
            ["DonationAddressCopied"] = "Dirección copiada",
            ["DonationOpen"] = "Donar",
            ["DonationDismiss"] = "Quizás más tarde",
            ["ProviderFilter"] = "Proveedor de contenido",
            ["AllProviders"] = "Todos los proveedores de contenido",
            ["LocalProvider"] = "Local",
            ["OnlineProvider"] = "En línea",
            ["CategoryFilter"] = "Categoría",
            ["AllCategories"] = "Todas las categorías",
            ["StatusFilter"] = "Estado",
            ["AllStatuses"] = "Todos los estados",
            ["PublicationStatusOngoing"] = "En curso",
            ["PublicationStatusFinished"] = "Finalizado",
            ["PublicationStatusPaused"] = "En pausa",
            ["PublicationStatusCancelled"] = "Cancelado",
            ["PublicationStatusAnnounced"] = "Anunciado",
            ["PublicationStatusUnknown"] = "Desconocido",
            ["YearFilter"] = "Año",
            ["AllYears"] = "Todos los años",
            ["GenreFilter"] = "Género",
            ["AllGenres"] = "Todos los géneros",
            ["Genre_action"] = "Acción",
            ["Genre_adventure"] = "Aventura",
            ["Genre_comedy"] = "Comedia",
            ["Genre_crime"] = "Crimen",
            ["Genre_documentary"] = "Documental",
            ["Genre_drama"] = "Drama",
            ["Genre_ecchi"] = "Ecchi",
            ["Genre_family"] = "Familiar",
            ["Genre_fantasy"] = "Fantasía",
            ["Genre_harem"] = "Harem",
            ["Genre_historical"] = "Histórico",
            ["Genre_horror"] = "Terror",
            ["Genre_isekai"] = "Isekai",
            ["Genre_josei"] = "Josei",
            ["Genre_magic"] = "Magia",
            ["Genre_martial-arts"] = "Artes marciales",
            ["Genre_mecha"] = "Mecha",
            ["Genre_military"] = "Militar",
            ["Genre_music"] = "Música",
            ["Genre_mystery"] = "Misterio",
            ["Genre_psychological"] = "Psicológico",
            ["Genre_reality"] = "Telerrealidad",
            ["Genre_romance"] = "Romance",
            ["Genre_school"] = "Escolar",
            ["Genre_science-fiction"] = "Ciencia ficción",
            ["Genre_seinen"] = "Seinen",
            ["Genre_shoujo"] = "Shoujo",
            ["Genre_shounen"] = "Shounen",
            ["Genre_slice-of-life"] = "Cosas de la vida",
            ["Genre_sports"] = "Deportes",
            ["Genre_supernatural"] = "Sobrenatural",
            ["Genre_suspense"] = "Suspenso",
            ["Genre_thriller"] = "Thriller",
            ["Genre_tragedy"] = "Tragedia",
            ["Genre_war"] = "Guerra",
            ["Genre_western"] = "Oeste",
            ["Favorites"] = "Favoritos",
            ["CatalogItem"] = "Título",
            ["AddFavoriteItem"] = "Agregar a favoritos",
            ["RemoveFavoriteItem"] = "Quitar de favoritos",
            ["FavoriteItemAdded"] = "\"{0}\" se agregó a favoritos.",
            ["FavoriteItemRemoved"] = "\"{0}\" se quitó de favoritos.",
            ["FavoriteItemSaveFailed"] =
                "No se pudo guardar el favorito: {0}",
            ["CatalogUserStateLoadFailed"] =
                "No se pudieron cargar los favoritos y el progreso: {0}",
            ["UserListsTitle"] = "Tus listas",
            ["UserListsDescription"] =
                "Crea listas que pueden mezclar manga, anime, películas, libros y audio.",
            ["FriendsTitle"] = "Amigos",
            ["FriendsDescription"] =
                "Agrega a alguien por su nombre de cuenta. Cuando ambos sean amigos, puedes decidir si ve hasta dónde has llegado en un título.",
            ["WatchPartyTitle"] = "Ver juntos",
            ["WatchPartyDescription"] =
                "Mira en sincronía: una sala, una sola línea de tiempo. Cualquiera puede pausar o saltar, y ocurre para todos.",
            ["WatchPartyStartAction"] = "Crear una sala",
            ["WatchPartyJoinCodeLabel"] = "Código de la sala",
            ["WatchPartyJoinAction"] = "Unirse a la sala",
            ["WatchPartyLeaveAction"] = "Salir de la sala",
            ["WatchPartyCodeShareLabel"] = "Código de invitación",
            ["WatchPartyMembersTitle"] = "En la sala",
            ["WatchPartyHostSuffix"] = "anfitrión",
            ["WatchPartyAwaySuffix"] = "ausente",
            ["WatchPartyBufferingSuffix"] = "cargando",
            ["WatchPartyHostControlsLabel"] =
                "Solo yo puedo pausar y saltar para todos",
            ["WatchPartyNoTransportNotice"] =
                "Esta fuente se reproduce en su propio reproductor web, así que la sala no puede pausarla ni moverla. Sigue el ritmo a mano o elige una fuente directa.",
            ["WatchPartyOptionsFailed"] =
                "No se pudo cambiar la opción de la sala: {0}",
            ["WatchPartyWatchingLabel"] = "Viendo: {0}",
            ["WatchPartyNothingPlaying"] =
                "Todavía no se reproduce nada. Lo que reproduzca el anfitrión se convierte en el título de la sala.",
            ["WatchPartySyncedLabel"] = "Sincronizado con la sala.",
            ["WatchPartyReconnectingLabel"] = "Reconectando con la sala…",
            ["WatchPartyNotSyncedLabel"] =
                "Reproduce {0} para sincronizarte con la sala.",
            ["WatchPartyOpenContentAction"] = "Abrir el título de la sala",
            ["WatchPartyContentNotInCatalog"] =
                "«{0}» todavía no está en tu catálogo, así que su nombre quedó en la búsqueda.",
            ["WatchPartyNotFound"] = "Ninguna sala usa ese código.",
            ["WatchPartyFollowPrompt"] =
                "La sala está viendo {0}.",
            ["WatchPartyFollowDismiss"] = "Ahora no",
            ["WatchPartyPlayerStatus"] =
                "Ver juntos · {0} en la sala",
            ["WatchPartyCreateFailed"] =
                "No se pudo crear la sala: {0}",
            ["WatchPartyJoinFailed"] =
                "No se pudo entrar a la sala: {0}",
            ["WatchPartyLeaveFailed"] =
                "No se pudo salir de la sala: {0}",
            ["FriendsSignedOutNotice"] =
                "Inicia sesión en un servidor multimedia compatible con amigos para usar esta página.",
            ["FriendsRequestsTitle"] = "Solicitudes pendientes",
            ["FriendsListTitle"] = "Tus amigos",
            ["FriendsNone"] =
                "Todavía no tienes amigos. Agrega a alguien por su nombre de cuenta arriba.",
            ["FriendUsernameLabel"] = "Nombre de cuenta",
            ["FriendUsernamePlaceholder"] = "Su nombre de cuenta",
            ["FriendAddAction"] = "Enviar solicitud",
            ["FriendRefreshAction"] = "Actualizar",
            ["FriendRemoveAction"] = "Quitar",
            ["FriendRequestAcceptAction"] = "Aceptar",
            ["FriendRequestIncoming"] = "Quiere ser tu amigo",
            ["FriendRequestOutgoing"] = "Esperando respuesta",
            ["FriendRequestDecline"] = "Rechazar",
            ["FriendRequestWithdraw"] = "Retirar",
            ["FriendSharesProgressLabel"] = "Compartir mi progreso",
            ["FriendsShareProgressLabel"] =
                "Permitir que mis amigos vean hasta dónde he llegado",
            ["FriendsDiscoverableLabel"] =
                "Permitir que me encuentren por mi nombre de cuenta",
            ["FriendsSaveVisibility"] = "Guardar ajustes para compartir",
            ["FriendsLoadFailed"] =
                "No se pudieron cargar los amigos: {0}",
            ["FriendRequestSent"] = "Se envió una solicitud de amistad a {0}.",
            ["FriendRequestAccepted"] = "Ahora {0} y tú son amigos.",
            ["FriendRequestNoSuchAccount"] =
                "Ninguna cuenta llamada {0} acepta solicitudes de amistad.",
            ["FriendRequestFailed"] =
                "No se pudo completar la solicitud de amistad: {0}",
            ["FriendRemoved"] = "Se quitó a {0} de tus amigos.",
            ["FriendRemoveFailed"] = "No se pudo quitar al amigo: {0}",
            ["FriendSharingSaveFailed"] =
                "No se pudo guardar el ajuste para compartir: {0}",
            ["FriendVisibilitySharingOn"] =
                "Tus amigos verán tu progreso después de la próxima sincronización.",
            ["FriendVisibilitySharingOff"] =
                "Compartir está desactivado. Tu progreso se quitará de tus amigos en la próxima sincronización.",
            ["FriendVisibilitySaveFailed"] =
                "No se pudieron guardar los ajustes para compartir: {0}",
            ["FriendProgressTitle"] = "Amigos",
            ["FriendProgressChapter"] = "{0} leyó hasta el capítulo {1}",
            ["FriendProgressEpisode"] = "{0} vio hasta el episodio {1}",
            ["FriendProgressEpisodeInSeason"] =
                "{0} vio hasta la temporada {1}, episodio {2}",
            ["FriendProgressFinished"] = "{0} terminó esto",
            ["FriendProgressStarted"] = "{0} ya empezó esto",
            ["FriendSharedTitlesCount"] = "{0} en común",
            ["FriendCommonWithFavorites"] =
                "{0} en común · {1} que les gustan a los dos",
            ["FriendTitlesOnlyTheirs"] = "{0} que tiene y vos no",
            ["FriendTitlesOnlyMine"] = "{0} que tenés y él no",
            ["FriendTitleFavoriteOnly"] = "Marcado como favorito",
            ["FriendTitleSearchHint"] = "Buscar este título",
            ["FriendTitleStarted"] = "Empezado",
            ["FriendTitleFinished"] = "Terminado",
            ["FriendTitleChapter"] = "Hasta el capítulo {0}",
            ["FriendTitleEpisode"] = "Hasta el episodio {0}",
            ["FriendTitleEpisodeInSeason"] =
                "Hasta la temporada {0}, episodio {1}",
            ["FriendProgressNeverShared"] = "Todavía no compartió su progreso",
            ["FriendProgressJustNow"] = "Progreso compartido recién",
            ["FriendProgressMinutesAgo"] = "Progreso compartido hace {0} min",
            ["FriendProgressHoursAgo"] = "Progreso compartido hace {0} h",
            ["FriendProgressDaysAgo"] = "Progreso compartido hace {0} d",
            ["NewUserListName"] = "Nombre de la nueva lista",
            ["NewUserListPlaceholder"] = "Por ejemplo: Leer después",
            ["CreateUserList"] = "Crear lista",
            ["SelectUserList"] = "Elegir una lista",
            ["RenameUserList"] = "Renombrar",
            ["DeleteUserList"] = "Eliminar lista",
            ["ConfirmDeleteUserList"] = "¿Eliminar esta lista?",
            ["ConfirmDelete"] = "Sí, eliminar",
            ["Cancel"] = "Cancelar",
            ["UserListDisplayLabel"] = "{0} ({1})",
            ["EmptyUserList"] =
                "Esta lista está vacía. Agrega un título desde su página de detalles.",
            ["NoUserLists"] = "Todavía no tienes listas.",
            ["AddToList"] = "Agregar a la lista",
            ["RemoveFromList"] = "Quitar de la lista",
            ["ListMembershipTitle"] = "Listas",
            ["NoListsForItem"] =
                "Primero crea una lista y luego agrega este título.",
            ["OpenUserLists"] = "Administrar listas",
            ["ProviderList"] = "Distribuidores: {0}",
            ["ContributorsTitle"] = "Proveedores y colaboradores",
            ["ContributorOrganization"] = "Organización",
            ["ContributorPerson"] = "Persona",
            ["CatalogTitle"] = "Catálogo local",
            ["CatalogSubtitle"] =
                "Tu biblioteca, progreso y fuentes en línea en un solo lugar",
            ["SettingsTitle"] = "Configuración",
            ["SettingsSubtitle"] =
                "Elige fuentes, administra tus datos y personaliza la reproducción",
            ["DownloadsTitle"] = "Descargas",
            ["DownloadsSubtitle"] =
                "Transferencias activas y archivos guardados en este dispositivo",
            ["DownloadsDescription"] =
                "Sigue el progreso, pausa transferencias activas y elimina uno o varios capítulos o episodios descargados.",
            ["RefreshDownloads"] = "Actualizar",
            ["DownloadsTotalLabel"] = "ELEMENTOS GUARDADOS",
            ["DownloadsActiveLabel"] = "ACTIVAS",
            ["DownloadsStorageLabel"] = "ALMACENAMIENTO TORRENT",
            ["DownloadsStorageSummary"] = "{0} usados",
            ["DownloadsSelectionSummary"] = "{0} de {1} seleccionados",
            ["DownloadsDeleteHint"] =
                "Eliminar un elemento borra permanentemente sus archivos torrent administrados de este dispositivo.",
            ["SelectAllDownloads"] = "Seleccionar todo",
            ["DeleteSelectedDownloads"] = "Eliminar seleccionados",
            ["DeleteAllDownloads"] = "Eliminar todo",
            ["DownloadsDeleteConfirmation"] =
                "¿Eliminar permanentemente {0} descarga(s) seleccionada(s)?",
            ["DownloadsDeletePermanent"] =
                "No se puede deshacer. Primero se detendrán las transferencias activas.",
            ["DeleteDownloadsNow"] = "Eliminar archivos",
            ["DeleteDownload"] = "Eliminar",
            ["DownloadsEmptyTitle"] = "Todavía no hay descargas",
            ["DownloadsEmptyDescription"] =
                "Aquí aparecerán los capítulos, episodios, archivos y torrents manuales descargados.",
            ["DownloadStateAdding"] = "AGREGANDO",
            ["DownloadStateMetadata"] = "METADATOS",
            ["DownloadStateChecking"] = "COMPROBANDO",
            ["DownloadStatePeers"] = "ESPERANDO PARES",
            ["DownloadStateDownloading"] = "DESCARGANDO",
            ["DownloadStateComplete"] = "COMPLETA",
            ["DownloadStateSeeding"] = "COMPARTIENDO",
            ["DownloadStatePaused"] = "PAUSADA",
            ["DownloadStateFailed"] = "FALLÓ",
            ["DownloadStateStopped"] = "DETENIDA",
            ["DownloadStateRemoving"] = "ELIMINANDO",
            ["DownloadProgressWithSize"] = "{0:0}% · {1} / {2}",
            ["DownloadProgressOnly"] = "{0:0}%",
            ["DownloadStoredSize"] = "{0} descargados",
            ["DownloadUpdated"] = "Actualizada {0}",
            ["DownloadsLoadFailed"] =
                "No se pudo cargar el historial de descargas: {0}",
            ["DownloadsSaveFailed"] =
                "No se pudo guardar el historial de descargas: {0}",
            ["DownloadsDeleteFailed"] =
                "No se pudieron eliminar todas las descargas seleccionadas: {0}",
            ["DownloadsDeleted"] = "{0} descarga(s) eliminada(s).",
            ["DownloadCleanupTitle"] = "Limpieza de torrents",
            ["DownloadCleanupDescription"] =
                "Elige si los archivos torrent de la sesión actual permanecen disponibles después de cerrar la aplicación.",
            ["DeleteDownloadsAtSessionEnd"] =
                "Eliminar los datos torrent al terminar esta sesión",
            ["DownloadCleanupWarning"] =
                "Los archivos descargados de torrents activos se eliminarán permanentemente al cerrar la aplicación.",
            ["SaveDownloadOptions"] = "Guardar opción de limpieza",
            ["DownloadOptionsSaved"] =
                "Se guardó la preferencia de limpieza de torrents.",
            ["DownloadOptionsSaveFailed"] =
                "No se pudo guardar la preferencia de limpieza: {0}",
            ["ContentFilterTitle"] = "Filtrado de publicidad y rastreadores",
            ["ContentFilterDescription"] =
                "Filtra publicidad y rastreadores en los reproductores incrustados. La página seleccionada y los recursos de video siguen protegidos; las solicitudes publicitarias pueden bloquearse incluso si las sirve el propio sitio del reproductor.",
            ["HideAdultContent"] = "Ocultar contenido +18",
            ["HideAdultContentDescription"] =
                "Oculta títulos del catálogo y de los proveedores API cuando su categoría es adulto, porno, +18 o 18+.",
            ["ContentFilterModeLabel"] = "Nivel de filtrado",
            ["ContentFilterModeOff"] = "Desactivado",
            ["ContentFilterModeOffHint"] =
                "Sin filtrado. Los reproductores incrustados mostrarán su publicidad y podrán abrir ventanas emergentes.",
            ["ContentFilterModeBasic"] = "Solo reglas incluidas",
            ["ContentFilterModeBasicHint"] =
                "Usa las reglas incluidas en la aplicación y nunca contacta a un servidor de listas. Funciona sin conexión.",
            ["ContentFilterModeStandard"] = "Estándar",
            ["ContentFilterModeStandardHint"] =
                "Añade las listas de uBlock Origin, EasyList, EasyPrivacy y Peter Lowe, descargadas y actualizadas en segundo plano.",
            ["ContentFilterModeComplete"] = "Completo",
            ["ContentFilterModeCompleteHint"] =
                "Todo lo de Estándar más listas de molestias. Usa notablemente más memoria.",
            ["ContentFilterModeCapped"] =
                "Este dispositivo no tiene memoria para el nivel elegido, así que se usa uno reducido. El bloqueo de solicitudes no cambia; algunos espacios publicitarios pueden quedar visibles pero vacíos.",
            ["ContentFilterNeverRefreshed"] = "nunca",
            ["ContentFilterStatusReady"] =
                "{0} reglas cargadas · última consulta: {1}",
            ["ContentFilterStatusUpdated"] =
                "Se actualizaron {0} lista(s); {1} reglas cargadas.",
            ["ContentFilterStatusSeedOnly"] =
                "{0} reglas incluidas cargadas. En este nivel no se descarga ninguna lista.",
            ["ContentFilterStatusDeferred"] =
                "Pospuesto: hay un video en reproducción, o la conexión es medida o está en ahorro de energía. Se reintentará más tarde.",
            ["ContentFilterStatusNoStorage"] =
                "Esta plataforma no puede almacenar listas descargadas, así que solo se usan las reglas incluidas.",
            ["ContentFilterStatusFailed"] =
                "No se pudieron actualizar las listas: {0}. Se siguen usando las reglas anteriores.",
            ["ContentFilterStatusSomeListsFailed"] =
                " {0} lista(s) no se pudieron actualizar y se usan tal como se descargaron por última vez.",
            ["ContentFilterListSources"] =
                "Las listas se descargan de ublockorigin.github.io, easylist.to y pgl.yoyo.org. Solo se envía una solicitud condicional; ninguna actividad de navegación sale de este dispositivo.",
            ["ContentFilterPlaybackNetwork"] =
                "Último reproductor incrustado: {0} de {1} solicitudes rechazadas antes de salir de este dispositivo.",
            ["ContentFilterPlaybackInPage"] =
                "Último reproductor incrustado: filtrado limitado ({2}). Los contadores no incluyen los bloqueos realizados dentro de la página.",
            ["ContentFilterPlaybackAllowances"] =
                " Permitidas: {0} al propio sitio de la página, {1} como video. Los recursos del propio sitio también se comprueban con las reglas de bloqueo.",
            ["ContentFilterPlaybackUnmatched"] =
                " Se cargaron porque ninguna regla las menciona: {0}.",
            ["UpdateFilterLists"] = "Actualizar listas ahora",
            ["SaveContentFilterOptions"] = "Guardar configuración de filtrado",
            ["ContentFilterSaved"] = "Se guardó la configuración de filtrado.",
            ["ContentFilterSaveFailed"] =
                "No se pudo guardar el nivel de filtrado: {0}",
            ["ApplicationUpdatesTitle"] = "Actualizaciones de la aplicación",
            ["ApplicationUpdatesDescription"] =
                "La aplicación consulta el servidor configurado y solo descarga o abre un paquete de actualización después de tu aprobación.",
            ["CheckForUpdates"] = "Buscar actualizaciones",
            ["UpdateAvailableTitle"] = "La versión {0} está disponible",
            ["UpdateAvailableDefaultNotes"] =
                "Hay una actualización verificada de la aplicación lista para descargar.",
            ["UpdateDownloadSize"] = "Tamaño de la descarga: {0}",
            ["UpdateAskFirstNotice"] =
                "No se descarga ni instala nada hasta que selecciones Descargar e instalar.",
            ["UpdateLater"] = "Más tarde",
            ["DownloadAndInstallUpdate"] = "Descargar e instalar",
            ["UpdateInstallerDidNotStart"] =
                "El instalador verificado se descargó, pero no se pudo abrir.",
            ["UpdateAndRestart"] = "Actualizar y reiniciar",
            ["UpdateRestarting"] =
                "Instalando la actualización y reiniciando la aplicación...",
            ["UpdateInstallPermissionRequired"] =
                "Permite que esta aplicación instale aplicaciones y vuelve a tocar actualizar.",
            ["UpdateCancelled"] = "La actualización no se aplicó: {0}",
            ["UpdateInstallerStarted"] =
                "Se abrió el instalador verificado. Sigue sus indicaciones para terminar la actualización.",
            ["UpdateInstallFailed"] =
                "No se pudo instalar la actualización: {0}",
            ["ApplicationUpToDate"] =
                "La aplicación de escritorio está actualizada.",
            ["UpdateCheckFailed"] =
                "No se pudo consultar el servidor de actualizaciones: {0}",
            ["RemoteServerTitle"] = "Cuenta y sincronización en línea",
            ["RemoteServerDescription"] =
                "Conecta tu propio servidor UMD para usar un catálogo en línea opcional, sincronización privada entre dispositivos y listas compartibles.",
            ["RemoteServerUrl"] = "Dirección del servidor",
            ["RemoteServerUrlPlaceholder"] = "https://umd.duncan.com.ar/",
            ["RemoteUsername"] = "Usuario",
            ["RemotePassword"] = "Contraseña",
            ["RemoteDisplayName"] = "Nombre visible",
            ["RemoteDeviceName"] = "Este dispositivo",
            ["RemoteOnlineCatalog"] = "Usar el catálogo en línea del servidor",
            ["RemoteOnlineCatalogDescription"] =
                "Al habilitarlo, los resultados del servidor aparecen junto con los locales y los de proveedores. Tu catálogo local sigue disponible sin conexión.",
            ["RemoteSaveSettings"] = "Guardar servidor",
            ["RemoteCreateAccount"] = "Crear cuenta",
            ["RemoteSignIn"] = "Iniciar sesión",
            ["RemoteSignOut"] = "Cerrar sesión",
            ["RemoteAccountSignedOut"] = "Sesión no iniciada",
            ["RemoteAccountSignedIn"] = "Sesión iniciada como {0} (@{1})",
            ["RemoteSnapshotTitle"] = "Sincronización de dispositivos",
            ["RemoteSnapshotEmpty"] =
                "Todavía no hay una copia guardada en línea.",
            ["RemoteSnapshotAvailable"] =
                "Copia en línea revisión {0}, actualizada {1}.",
            ["RemoteRefresh"] = "Comprobar copia",
            ["RemoteUpload"] = "Sincronizar ahora",
            ["RemoteRestore"] = "Combinar copia en línea",
            ["RemoteRestoreWarning"] =
                "La sincronización combina los cambios de este dispositivo y de la copia en línea. Gana el cambio de registro más reciente y las eliminaciones se conservan para que un dispositivo sin conexión no restaure favoritos o elementos borrados.",
            ["RemoteRestoreNow"] = "Combinar y sincronizar",
            ["RemoteSyncPrivacy"] =
                "Las contraseñas y sesiones nunca se guardan. La sincronización excluye credenciales de proveedores, ajustes de la aplicación, registros de descargas y archivos descargados.",
            ["RemoteShareList"] = "Compartir lista seleccionada",
            ["RemoteShareListDescription"] =
                "Publica un enlace de catálogo de solo lectura que contiene únicamente los títulos de esta lista.",
            ["RemotePublishList"] = "Publicar lista",
            ["RemoteRevokeShare"] = "Dejar de compartir",
            ["RemoteSharedCatalogUrl"] = "Dirección del catálogo compartido",
            ["RemoteOnlineCatalogProvider"] = "Tu servidor UMD",
            ["RemoteServerSettingsSaved"] =
                "Se guardó la configuración del servidor en línea.",
            ["RemoteServerSettingsSaveFailed"] =
                "No se pudo guardar la configuración del servidor: {0}",
            ["RemoteAccountSignedOutStatus"] =
                "Se cerró la sesión del servidor en línea.",
            ["RemoteSignOutFailed"] =
                "No se pudo cerrar la sesión del servidor: {0}",
            ["RemoteSnapshotRefreshed"] =
                "Se comprobó la copia en línea.",
            ["RemoteSnapshotNone"] =
                "Todavía no existe una copia en línea.",
            ["RemoteSnapshotCheckFailed"] =
                "No se pudo comprobar la copia en línea: {0}",
            ["RemoteSnapshotUploaded"] =
                "La sincronización terminó en la revisión {0}.",
            ["RemoteSnapshotConflict"] =
                "Otros dispositivos siguieron actualizando la cuenta hasta la revisión {0}; la conciliación automática no pudo terminar. Intenta sincronizar de nuevo.",
            ["RemoteSnapshotUploadFailed"] =
                "No se pudo subir este dispositivo: {0}",
            ["RemoteSnapshotRestored"] =
                "Cambios conciliados: {0} título(s) agregado(s), {1} registro(s) de progreso y {2} lista(s).",
            ["RemoteSnapshotRestoreFailed"] =
                "No se pudo restaurar la copia en línea: {0}",
            ["RemoteSharedListPublished"] =
                "La lista «{0}» ya está disponible mediante su enlace de catálogo.",
            ["RemoteSharedListPublishFailed"] =
                "No se pudo compartir la lista seleccionada: {0}",
            ["RemoteSharedListRevoked"] =
                "Se revocó el enlace de la lista compartida.",
            ["RemoteSharedListRevokeFailed"] =
                "No se pudo revocar el enlace compartido: {0}",
            ["RemoteServerSettingsLoadFailed"] =
                "No se pudo cargar la configuración del servidor: {0}",
            ["RemoteServerUrlRequired"] =
                "Primero ingresa la dirección de tu servidor UMD.",
            ["RemoteRegistrationDisabled"] =
                "Este servidor no permite crear cuentas públicamente. Pide al administrador que cree tu cuenta.",
            ["RemoteSignInSucceeded"] = "Sesión iniciada como {0}.",
            ["RemoteRegistrationFailed"] =
                "No se pudo crear la cuenta: {0}",
            ["RemoteSignInFailed"] = "No se pudo iniciar sesión: {0}",
            ["RemoteInitialSyncFailed"] =
                "Tu sesión está iniciada, pero la sincronización del dispositivo no pudo completarse: {0}",
            ["RemoteSessionExpired"] =
                "El servidor cerró esta sesión. Inicia sesión de nuevo: {0}",
            ["RemoteSessionRestoreFailed"] =
                "No se pudo restaurar la sesión guardada: {0}",
            ["RemoteSessionSaveFailed"] =
                "No se pudo guardar la sesión para el próximo inicio: {0}",
            ["RemoteServerIncompatible"] =
                "Este servidor no admite la API de cuentas y sincronización que requiere la aplicación.",
            ["RemoteSyncUnavailable"] =
                "La sincronización no está disponible en esta versión de la aplicación.",
            ["CatalogData"] = "Datos del catálogo",
            ["CatalogDataDescription"] =
                "Importa o exporta archivos portátiles del catálogo UMD.",
            ["ImportCatalog"] = "Importar catálogo",
            ["ExportCatalog"] = "Exportar catálogo",
            ["LocalFirst"] = "GUARDADO EN ESTE DISPOSITIVO",
            ["TotalLabel"] = "TOTAL",
            ["MoviesLabel"] = "PELÍCULAS",
            ["BooksLabel"] = "LIBROS",
            ["LiveTvLabel"] = "TV EN VIVO",
            ["LiveTvTitle"] = "TV en vivo",
            ["LiveTvSubtitle"] = "Canales en vivo de Argentina",
            ["LiveTvDescription"] =
                "Explora y mira canales argentinos en vivo de las listas públicas configuradas.",
            ["LiveTvRefresh"] = "Actualizar canales",
            ["LiveTvRefreshShort"] = "Actualizar",
            ["LiveTvSearchLabel"] = "Buscar un canal",
            ["LiveTvSearchPlaceholder"] =
                "Buscar por canal, categoría o proveedor",
            ["LiveTvProviderLabel"] = "Proveedor",
            ["LiveTvGroupLabel"] = "Categoría",
            ["LiveTvAllProviders"] = "Todos los proveedores",
            ["LiveTvAllGroups"] = "Todas las categorías",
            ["LiveTvOtherGroup"] = "Otros",
            ["LiveTvChannelSummary"] =
                "{0} de {1} canales · {2} proveedores cargados",
            ["LiveTvProviderFailures"] =
                "No se pudieron cargar algunos proveedores de canales: {0}",
            ["LiveTvLoaded"] =
                "Se cargaron {0} canales en vivo de {1} proveedores.",
            ["LiveTvLoadedPartial"] =
                "Se cargaron {0} canales en vivo de {1} proveedores; fallaron {2}.",
            ["LiveTvLoadFailed"] =
                "No se pudieron cargar los canales en vivo: {0}",
            ["LiveTvPlaybackStarted"] =
                "Reproduciendo {0} desde {1}.",
            ["LiveTvPlaybackFailed"] =
                "No se pudo reproducir {0}: {1}",
            ["LiveTvWatch"] = "Ver en vivo",
            ["LiveTvNoChannels"] = "No hay canales que coincidan",
            ["LiveTvNoChannelsHint"] =
                "Limpia los filtros o actualiza los proveedores de canales.",
            ["MetadataProviders"] = "Proveedores de metadatos y créditos",
            ["MetadataAvailabilityAll"] =
                "Actualiza películas y series de acción real desde TMDB, o anime, manga y novelas web desde MyAnimeList.",
            ["MetadataAvailabilityNative"] =
                "Actualiza películas y series de acción real desde TMDB. MyAnimeList bloquea el acceso directo desde el navegador, por lo que la actualización de anime, manga y novelas web está disponible en las aplicaciones nativas.",
            ["TmdbApiKey"] = "Clave de API TMDB v3",
            ["TmdbApiKeyPlaceholder"] = "Clave de API de TMDB",
            ["MalClientId"] = "ID de cliente de MyAnimeList",
            ["MalClientIdPlaceholder"] = "ID de cliente de MAL",
            ["PreferredLanguage"] = "Idioma preferido",
            ["PreferredLanguagePlaceholder"] = "es-AR",
            ["NativeOnly"] = "Solo aplicación nativa",
            ["SaveProviderSettings"] = "Guardar proveedores",
            ["CredentialDisclosure"] =
                "Las credenciales se guardan únicamente en los datos locales de la aplicación en este dispositivo. Se muestran ocultas, pero la aplicación no las cifra. La búsqueda exige una coincidencia exacta con el título actual o alternativo y usa el año de estreno para resolver duplicados.",
            ["MetadataAttribution"] =
                "Este producto usa la API de TMDB, pero TMDB no lo avala ni certifica. Los metadatos de anime, manga y novelas web son provistos por MyAnimeList.",
            ["OpenMagnetTitle"] = "Abrir un enlace magnet o archivo torrent",
            ["PublicByDefault"] = "PÚBLICO POR DEFECTO",
            ["OpenMagnet"] = "Abrir magnet",
            ["OpenTorrentFile"] = "Abrir archivo .torrent",
            ["OpenMagnetsAutomatically"] = "Abrir automáticamente los magnets recibidos",
            ["ImportInfoJson"] = "Importar info.json del torrent",
            ["ForcePrivate"] = "Forzar fuentes torrent sin marcar al modo privado",
            ["SaveOptions"] = "Guardar opciones",
            ["PendingMagnet"] = "Magnet recibido pendiente de confirmación",
            ["OpenReceivedMagnet"] = "Abrir magnet recibido",
            ["FullBackup"] = "Copia completa de datos locales",
            ["FullBackupDescription"] =
                "Conserva IDs, biblioteca, progreso, preferencias y descargas.",
            ["CreateBackup"] = "Crear copia",
            ["RestoreBackup"] = "Restaurar copia",
            ["ViewDetails"] = "Ver detalles",
            ["BackToCatalog"] = "Volver al catálogo",
            ["EntriesTitle"] = "Episodios, capítulos, pistas y versiones",
            ["ContinueCatalogEntry"] = "Continuar · {0}",
            ["SortNewestFirst"] = "Más recientes primero",
            ["SortOldestFirst"] = "Más antiguos primero",
            ["MarkUnitSeen"] = "Marcar como visto",
            ["MarkUnitUnseen"] = "Marcar como no visto",
            ["MarkAllUnitsSeen"] = "Marcar todo como visto",
            ["MarkAllUnitsUnseen"] = "Marcar todo como no visto",
            ["PlaybackAvailable"] = "Reproducción disponible",
            ["NoMappedSource"] = "No hay una fuente torrent asignada.",
            ["BackToDetails"] = "Volver a detalles",
            ["StreamingSource"] = "Transmitiendo desde el torrent seleccionado",
            ["OnePage"] = "Una página",
            ["Continuous"] = "Continuo",
            ["Previous"] = "Anterior",
            ["Next"] = "Siguiente",
            ["CatalogPageSummary"] = "Página {0} de {1} · {2} elementos",
            ["DecreaseFont"] = "Reducir tamaño de letra",
            ["IncreaseFont"] = "Aumentar tamaño de letra",
            ["ZoomOut"] = "Alejar",
            ["ResetZoom"] = "Restablecer zoom",
            ["ZoomIn"] = "Acercar",
            ["PreviousChapter"] = "Capítulo anterior",
            ["NextChapter"] = "Capítulo siguiente",
            ["PreviousEpisode"] = "Episodio anterior",
            ["NextEpisode"] = "Episodio siguiente",
            ["BackToManga"] = "Manga",
            ["GoToTop"] = "Ir arriba",
            ["BusyLabel"] = "Cargando",
            ["ErrorMessageLabel"] = "Mensaje de error",
            ["StatusMessageLabel"] = "Mensaje de estado",
            ["TmdbApiKeyLabel"] = "Clave de API de TMDB",
            ["MalClientIdLabel"] = "ID de cliente de MyAnimeList",
            ["MetadataLanguageLabel"] = "Idioma de metadatos",
            ["MagnetInputLabel"] = "Enlace magnet",
            ["ReaderPagesLabel"] = "Páginas del lector continuo",
            ["EpubContentLabel"] = "Contenido del capítulo EPUB",
            ["EpubIllustrationLabel"] = "Ilustración EPUB",
            ["ArtworkLabel"] = "Imagen de portada",
            ["CatalogJsonFileType"] = "Catálogo JSON de UMD",
            ["TorrentFileType"] = "Metadatos BitTorrent",
            ["FullBackupFileType"] = "Copia completa de UMD",
            ["ImportCatalogDialog"] = "Importar catálogo JSON",
            ["ExportCatalogDialog"] = "Exportar catálogo JSON",
            ["OpenTorrentFileDialog"] = "Abrir un archivo .torrent",
            ["CreateBackupDialog"] = "Crear copia completa de datos locales",
            ["RestoreBackupDialog"] = "Restaurar copia completa de datos locales",

            ["PlatformNativeDesktop"] = "Escritorio nativo",
            ["PlatformBrowser"] = "Navegador / WebAssembly",
            ["PlatformAndroid"] = "Android",
            ["PlatformAndroidTv"] = "Android TV",
            ["PlatformIos"] = "iOS / iPadOS",
            ["Unknown"] = "Desconocido",
            ["PrivacyMse"] = "Torrents públicos y privados opcionales · MSE",
            ["PrivacyDtls"] = "Torrents públicos y privados opcionales · DTLS",
            ["TrackerDetailsHidden"] = "detalles de trackers ocultos",
            ["MagnetDetailsHidden"] = "Detalles del magnet ocultos",
            ["TorrentAdapterConnected"] = "Adaptador {0} conectado",
            ["TorrentAdapterDisconnected"] = "El adaptador {0} aún no está conectado",
            ["CatalogRecovered"] =
                "Se reemplazó un catálogo local dañado por una base limpia. Los archivos originales se conservaron en {0}",
            ["MagnetOptionsSaved"] = "Opciones de apertura de magnets guardadas.",
            ["MagnetOptionsSaveFailed"] = "No se pudieron guardar las opciones de magnets: {0}",
            ["MetadataOptionsSaved"] = "Proveedores de metadatos guardados en este dispositivo.",
            ["MetadataOptionsSaveFailed"] = "No se pudieron guardar los proveedores de metadatos: {0}",
            ["MetadataUpdated"] = "Metadatos actualizados desde {0}.",
            ["MetadataUpdateFailed"] = "No se pudieron actualizar los metadatos: {0}",
            ["CatalogItemDeleted"] = "Se eliminó {0} del catálogo.",
            ["CatalogItemDeleteFailed"] =
                "No se pudo eliminar el elemento del catálogo: {0}",
            ["CatalogImported"] = "Catálogo importado: {0} creados, {1} actualizados.",
            ["CatalogInvalid"] = "El catálogo seleccionado no es válido: {0}",
            ["CatalogImportFailed"] = "No se pudo importar el catálogo: {0}",
            ["CatalogExported"] = "JSON del catálogo exportado sin IDs de base de datos ni datos de usuario.",
            ["CatalogExportFailed"] = "No se pudo exportar el catálogo: {0}",
            ["BackupExported"] = "Copia completa exportada con IDs del catálogo y datos locales.",
            ["BackupExportFailed"] = "No se pudo exportar la copia completa: {0}",
            ["BackupRestored"] = "Copia completa restaurada. La base anterior se conservó en {0}",
            ["BackupRestoreFailed"] = "No se pudo restaurar la copia completa: {0}",
            ["SaveTargetNotEmpty"] =
                "'{0}' ya contiene datos y esta plataforma no puede vaciarlo. " +
                "Elige otro nombre de archivo o borra ese archivo primero.",
            ["CatalogLoadFailed"] = "No se pudo cargar el catálogo local: {0}",
            ["SearchResultSources"] = "{0} · {1} fuentes",
            ["SearchSourcesLoading"] = "Agregando fuentes de {0}…",
            ["SearchSourcesLoaded"] = "Se agregaron {0} fuentes más.",
            ["SearchResultsTitle"] = "Búsqueda: {0} ({1})",
            ["SearchCompleted"] = "Se encontraron {0} resultados locales y en línea.",
            ["SearchCompletedWithFailures"] =
                "Se encontraron {0} resultados; {1} proveedores en línea no estuvieron disponibles.",
            ["SearchFailed"] = "Falló la búsqueda del catálogo: {0}",
            ["ContentSourceAdded"] = "Se agregó el proveedor de catálogo torrent.",
            ["ContentSourcesSaved"] =
                "Las fuentes de contenido se guardaron en este dispositivo.",
            ["ContentSourceRemoved"] = "Se quitó el proveedor de catálogo torrent.",
            ["ContentSourceSaveFailed"] =
                "No se pudieron guardar las fuentes de contenido: {0}",
            ["ContentSourceLoadFailed"] =
                "No se pudieron cargar las fuentes de contenido: {0}",
            ["DailyUpdatesChecking"] =
                "Comprobando fuentes diarias...",
            ["DailyUpdatesComplete"] =
                "Fuentes diarias sincronizadas: {0} título(s) nuevo(s), {1} actualizado(s).",
            ["DailyUpdatesPartial"] =
                "Fuentes diarias sincronizadas: {0} título(s) nuevo(s), {1} actualizado(s). {2} fuente(s) volverán a intentarlo al abrir Inicio.",
            ["DailyUpdatesFailed"] =
                "Las fuentes diarias volverán a intentarlo al abrir Inicio: {0}",
            ["OnlineItemLoaded"] =
                "Se cargó desde {0} y se combinó con el catálogo local: {1} creado, {2} actualizado.",
            ["OnlineItemLoadFailed"] =
                "No se pudo cargar el título en línea: {0}",
            ["CatalogItemMissing"] = "El elemento seleccionado ya no existe en el catálogo.",
            ["DetailsLoadFailed"] = "No se pudieron cargar los detalles del título: {0}",
            ["TorrentEngineMissing"] = "Este entorno no tiene un motor torrent configurado.",
            ["OrderedPagesOpened"] = "Se abrieron {0} páginas ordenadas.",
            ["SingleImageOpened"] = "Se abrió una página de imagen.",
            ["ReadOnlinePages"] = "Leer {0} páginas en línea",
            ["FindChapterOnline"] = "Buscar capítulo en las API habilitadas",
            ["WatchOnlineServers"] = "Ver en línea ({0} servidores)",
            ["FindEpisodeOnline"] = "Buscar episodio en las API habilitadas",
            ["ChooseContentProvider"] = "Elegir una fuente para {0}",
            ["MultipleContentProvidersFound"] =
                "{0} proveedores habilitados encontraron esta entrada. Elige cuál usar.",
            ["ContentProvidersFoundWhileSearching"] =
                "Fuentes encontradas hasta ahora: {0}. Puedes elegir mientras continúa la búsqueda.",
            ["SearchingContentProviders"] =
                "Buscando esta entrada en los proveedores habilitados…",
            ["ProviderTitleMatch"] = "{0:P0} de coincidencia del título",
            ["NoPlayableOnlineVideoServer"] =
                "No se pudo abrir ninguno de los servidores de video disponibles.",
            ["VideoServers"] = "Servidores de video",
            ["EmbeddedVideoServerShort"] = "Web",
            ["EmbeddedWebPlayerNotConfigured"] =
                "Esta plataforma no puede mostrar servidores de video integrados.",
            ["EmbeddedWebPlayerUiThreadRequired"] =
                "El reproductor de video integrado debe crearse en el hilo de la interfaz.",
            ["EmbeddedWebAddressInvalid"] =
                "El servidor de video integrado devolvió una dirección web inválida.",
            ["OnlinePagesOpened"] =
                "Se abrieron {0} páginas ordenadas desde el proveedor API.",
            ["OnlinePagesOpenFailed"] =
                "No se pudieron abrir las páginas del proveedor: {0}",
            ["OnlineChapterResolved"] =
                "Coincidencia en {0}; se sincronizaron el manga y sus capítulos locales y se abrieron {1} páginas (similitud del título {2:P0}).",
            ["OnlineChapterResolveFailed"] =
                "No se encontró el capítulo seleccionado en las API habilitadas: {0}",
            ["SavedMangaRefreshed"] =
                "Lista de capítulos actualizada desde {0}.",
            ["SavedAnimeRefreshed"] =
                "Lista de episodios actualizada desde {0}.",
            ["OnlineVideoOpened"] =
                "Reproduciendo desde {0}. Hay {1} servidores disponibles debajo del reproductor.",
            ["OnlineVideoOpenFailed"] =
                "No se pudo abrir el servidor de video en línea: {0}",
            ["OnlineEpisodeResolved"] =
                "Coincidencia en {0}; se sincronizó la lista local y se encontraron {1} servidores de video (similitud del título {2:P0}).",
            ["OnlineEpisodeResolveFailed"] =
                "No se encontró el episodio seleccionado en las API habilitadas: {0}",
            ["AdjacentEpisodeNeedsProvider"] =
                "No se pudo abrir {0} automáticamente. Elige una fuente abajo.",
            ["AdjacentEpisodeNotFound"] =
                "Ningún proveedor de API habilitado ofrece {0}.",
            ["VideoServerChanged"] =
                "La reproducción cambió a {0}.",
            ["RemoteImageTypeUnsupported"] =
                "El proveedor devolvió un tipo de imagen no compatible.",
            ["CbzPagesOpened"] = "Se abrieron {0} páginas CBZ.",
            ["EpubChaptersOpened"] = "Se abrieron {0} capítulos EPUB.",
            ["PdfRendererUnavailable"] = "Este entorno no pudo iniciar el lector de PDF.",
            ["PdfPagesOpened"] = "Se abrieron {0} páginas PDF.",
            ["SelectPrimarySource"] =
                "Selecciona una fuente de video principal; los subtítulos asignados se adjuntan automáticamente.",
            ["ReaderNotImplemented"] =
                "Este formato de lectura está catalogado, pero su lector aún no está implementado.",
            ["MediaPresenterUnavailable"] =
                "Este entorno no pudo iniciar su reproductor de video o audio integrado.",
            ["PlaybackResumed"] = "Se reanudó {0} desde {1}.",
            ["PlaybackStarted"] = "Reproduciendo {0}.",
            ["MediaOpenFailed"] = "No se pudo abrir la fuente multimedia: {0}",
            ["MagnetOptionsLoadFailed"] = "No se pudieron cargar las opciones de magnets recibidos: {0}",
            ["MetadataOptionsLoadFailed"] = "No se pudieron cargar los proveedores de metadatos: {0}",
            ["UnitsMarkedSeenThrough"] =
                "Se marcaron {0} entradas como vistas hasta {1}.",
            ["UnitMarkedSeen"] = "{0} se marcó como visto.",
            ["UnitMarkedUnseen"] = "{0} se marcó como no visto.",
            ["AllUnitsMarkedSeen"] =
                "Se marcaron {0} entradas como vistas.",
            ["AllUnitsMarkedUnseen"] =
                "Se marcaron {0} entradas como no vistas.",
            ["UnitSeenSaveFailed"] =
                "No se pudo guardar el estado de visualización: {0}",
            ["UserListsLoadFailed"] =
                "No se pudieron cargar tus listas: {0}",
            ["UserListNameRequired"] =
                "Escribe un nombre para la lista.",
            ["UserListNameExists"] =
                "Ya existe una lista con este nombre.",
            ["UserListCreated"] = "Se creó la lista {0}.",
            ["UserListRenamed"] = "La lista ahora se llama {0}.",
            ["UserListDeleted"] = "Se eliminó la lista {0}.",
            ["UserListSaveFailed"] =
                "No se pudo guardar la lista: {0}",
            ["ItemAddedToUserList"] = "Se agregó {0} a {1}.",
            ["ItemRemovedFromUserList"] = "Se quitó {0} de {1}.",
            ["UserListMembershipSaveFailed"] =
                "No se pudo cambiar la pertenencia a la lista: {0}",
            ["PasteMagnetFirst"] = "Primero pega un enlace magnet.",
            ["MagnetConfirmationRequired"] =
                "Se recibió un enlace magnet. Selecciona Abrir magnet recibido para continuar.",
            ["MagnetEngineUnavailable"] =
                "El enlace magnet es válido, pero el adaptador {0} aún no está conectado.",
            ["MagnetAlreadyOpen"] = "Este magnet ya está abierto.",
            ["MagnetInvalid"] = "El enlace magnet no es válido: {0}",
            ["MagnetIncompatible"] =
                "El enlace magnet es válido, pero esta versión no puede abrirlo: {0}",
            ["MagnetUnsafe"] = "No se pudo abrir el magnet de forma segura: {0}",
            ["MagnetOpenFailed"] = "No se pudo abrir el magnet: {0}",
            ["TorrentFileEngineUnavailable"] =
                "El archivo torrent es válido, pero el adaptador {0} aún no está conectado.",
            ["TorrentFileAlreadyOpen"] = "Este torrent ya está abierto.",
            ["TorrentFileInvalid"] = "El archivo torrent no es válido: {0}",
            ["TorrentFileIncompatible"] =
                "El archivo torrent es válido, pero esta versión no puede abrirlo: {0}",
            ["TorrentFileUnsafe"] =
                "No se pudo abrir el archivo torrent de forma segura: {0}",
            ["TorrentFileOpenFailed"] =
                "No se pudo abrir el archivo torrent: {0}",
            ["TorrentManifestImported"] =
                "Torrent abierto e info.json importado: {0} creados, {1} actualizados.",
            ["TorrentManifestNotFound"] =
                "Torrent abierto; no se encontró un manifiesto info.json del catálogo.",
            ["TorrentManifestPending"] =
                "Torrent abierto. info.json se comprobará en segundo plano.",
            ["TorrentOpened"] = "Torrent abierto.",
            ["TorrentActivityAdding"] = "Agregando torrent...",
            ["TorrentActivityMetadata"] = "Buscando metadatos del torrent...",
            ["TorrentActivityChecking"] = "Comprobando archivos descargados...",
            ["TorrentActivityFindingPeers"] = "Esperando pares...",
            ["TorrentActivityDownloading"] = "Descargando...",
            ["TorrentActivityReady"] = "Listo para abrir",
            ["TorrentActivitySeeding"] = "Descarga completa - compartiendo",
            ["TorrentActivityPaused"] = "Descarga pausada",
            ["TorrentActivityFailed"] = "El torrent falló",
            ["TorrentActivityRemoving"] = "Deteniendo torrent...",
            ["TorrentActivityPreparingContent"] =
                "Preparando el capítulo o episodio seleccionado",
            ["TorrentActivityTorrentFile"] = "Archivo torrent",
            ["TorrentActivityReadingFile"] = "Leyendo metadatos del torrent",
            ["TorrentActivityManualMagnet"] = "Magnet manual",
            ["TorrentActivityOpeningMagnet"] = "Abriendo enlace magnet",
            ["TorrentActivityProgressWithSize"] = "{0:0}% - {1} / {2}",
            ["TorrentActivityProgress"] = "{0:0}%",
            ["TorrentActivitySpeed"] = "{0}/s",
            ["TorrentActivityPeers"] = "{0} pares",
            ["TorrentActivityEta"] = "faltan {0}",
            ["TorrentActivityActionFailed"] =
                "Falló la acción del torrent: {0}",
            ["PauseTorrent"] = "Pausar",
            ["ResumeTorrent"] = "Reanudar",
            ["StopTorrent"] = "Detener",
            ["DismissTorrentActivity"] = "Descartar",
            ["ManifestWarning"] = "{0} Advertencia del manifiesto: {1}",
            ["Subtitles"] = "Subtítulos",
            ["OriginalUnspecified"] = "Original / sin especificar",
            ["PlaybackFailed"] = "Falló la reproducción: {0}",
            ["PlaybackProgressSaveFailed"] = "No se pudo guardar el progreso de reproducción: {0}",

            ["Uncategorized"] = "Sin categoría",
            ["MediaTypeMovie"] = "Película",
            ["MediaTypeBook"] = "Libro",
            ["MediaTypeAudio"] = "Audio",
            ["SubtypeMovie"] = "Película",
            ["SubtypeSeries"] = "Serie",
            ["SubtypeAnimeMovie"] = "Película de anime",
            ["SubtypeAnimeSeries"] = "Serie de anime",
            ["SubtypeDocumentary"] = "Documental",
            ["SubtypeShort"] = "Cortometraje",
            ["SubtypeBook"] = "Libro",
            ["SubtypeNovel"] = "Novela",
            ["SubtypeManga"] = "Manga",
            ["SubtypeComic"] = "Cómic",
            ["SubtypeLightNovel"] = "Novela ligera",
            ["SubtypeWebNovel"] = "Novela web",
            ["SubtypeAlbum"] = "Álbum musical",
            ["SubtypeSingle"] = "Sencillo",
            ["SubtypeAudiobook"] = "Audiolibro",
            ["SubtypePodcast"] = "Podcast",
            ["SubtypeRadioDrama"] = "Radioteatro",
            ["LanguageUnknown"] = "Idioma desconocido",
            ["NameKindOriginal"] = "original",
            ["NameKindTranslation"] = "traducción",
            ["NameKindAlternative"] = "alternativo",
            ["NameKindCanonical"] = "canónico",
            ["NameKindSynonym"] = "sinónimo",
            ["NameKindPrevious"] = "anterior",
            ["NameKindOther"] = "otro",
            ["Read"] = "Leer",
            ["Play"] = "Reproducir",
            ["PlayLanguage"] = "Reproducir {0}",
            ["Subtitle"] = "Subtítulo",
            ["SourceImageSequence"] = "Secuencia numerada de imágenes",
            ["SourceSingleImage"] = "Una imagen JPG, PNG o WebP",
            ["SourceCbz"] = "Imágenes empaquetadas en CBZ",
            ["SourceEpub"] = "Libro empaquetado en EPUB",
            ["SourcePdf"] = "Documento empaquetado en PDF",
            ["SourceTorrentFile"] = "Archivo torrent asignado",
            ["AssetRoleMedia"] = "Contenido",
            ["AssetRolePrimary"] = "Principal",
            ["AssetRoleSubtitle"] = "Subtítulo",
            ["AssetRoleCaption"] = "Subtitulado",
            ["AssetRoleCover"] = "Portada",
            ["AssetRoleThumbnail"] = "Miniatura",
            ["FullRecording"] = "Grabación completa",
            ["MainRelease"] = "Versión principal",
            ["SeasonNumber"] = "Temporada {0}",
            ["AllSeasons"] = "Todas las temporadas",
            ["SeasonSelectorLabel"] = "Temporada",
            ["UpdateFromProvider"] = "Actualizar desde {0}",
            ["DeleteChapter"] = "Eliminar capítulo",
            ["DeleteEpisode"] = "Eliminar episodio",
            ["ConfirmDeleteEntry"] = "¿Eliminar {0} de este catálogo local? Los archivos descargados se conservan. Una actualización del proveedor o sincronización puede restaurar esta entrada.",
            ["CatalogEntryMissing"] = "Este capítulo o episodio ya no está en el catálogo.",
            ["CatalogEntryDeleted"] = "La entrada se eliminó del catálogo local.",
            ["CatalogEntryDeleteFailed"] = "No se pudo eliminar esta entrada: {0}",
            ["DeleteCatalogItem"] = "Eliminar del catálogo",
            ["ConfirmDeleteCatalogItem"] =
                "¿Eliminar este título? También se quitarán su progreso guardado, sus favoritos y sus listas.",
            ["NoReadableSource"] =
                "No se asignó una fuente reproducible o legible a este título.",
            ["UnitEpisode"] = "Episodio",
            ["UnitChapter"] = "Capítulo",
            ["UnitTrack"] = "Pista",
            ["UnitAudioChapter"] = "Capítulo de audio",
            ["UnitPart"] = "Parte",
            ["ProgressPage"] = "página {0}",
            ["ProgressTime"] = "{0}",
            ["ContinueProgress"] = "Continuar en {0}",
            ["NewContentBadge"] = "Nuevo",
            ["SeasonUnitProgress"] = "Temporada {0} · {1} {2}",
            ["ProgressTargetMissing"] =
                "Ese capítulo o episodio guardado ya no está disponible.",
            ["ProgressSourceMissing"] =
                "No hay una fuente reproducible o legible para este progreso.",
            ["ProgressOpenFailed"] =
                "No se pudo abrir el progreso guardado: {0}",
            ["PageCount"] = "{0} páginas",
            ["DurationHoursMinutes"] = "{0} h {1} min",
            ["DurationMinutes"] = "{0} min",
            ["NoPages"] = "No hay páginas",
            ["PageOf"] = "Página {0} de {1}",
            ["PageNumber"] = "Página {0}",
            ["ImageStreamMissing"] = "El motor torrent no proporcionó el flujo de la imagen.",
            ["ImageTooLarge"] = "La imagen supera el límite de 96 MiB del lector.",
            ["PageLoadFailed"] = "No se pudo cargar esta página: {0}",
            ["RetryPageLoad"] = "Reintentar esta página",
            ["ProfilesTitle"] = "¿Quién está viendo?",
            ["ProfilesDescription"] =
                "Cada persona guarda sus favoritos, su progreso y sus listas. El catálogo es compartido.",
            ["ProfileAdded"] = "Perfil añadido.",
            ["ProfileRemoved"] = "Perfil eliminado.",
            ["AddProfile"] = "Añadir perfil",
            ["RemoveProfile"] = "Eliminar",
            ["NewProfileName"] = "Nombre del perfil",
            ["SwitchProfile"] = "Cambiar de perfil",
            ["CurrentProfile"] = "Usando {0}",
            ["InUseLabel"] = "En uso",
            ["NoChapters"] = "No hay capítulos",
            ["ChapterOf"] = "Capítulo {0} de {1}",
            ["EpubImageStreamMissing"] = "El lector EPUB no proporcionó el flujo de la imagen.",
            ["EpubImageLoadFailed"] = "No se pudo cargar esta imagen EPUB: {0}",

            ["PlayerAudio"] = "AUDIO",
            ["StreamingMonoTorrent"] = "Transmitiendo desde MonoTorrent",
            ["PlayerPlay"] = "Reproducir",
            ["CastAction"] = "Transmitir",
            ["CastLocal"] = "Esta PC",
            ["CastSearchHint"] = "Buscando dispositivos Chromecast en tu red local…",
            ["CastWirelessDisplay"] = "Pantalla inalámbrica de Windows (Win + K)",
            ["CastWirelessFailed"] = "No se pudo abrir pantalla inalámbrica. Presiona Win + K para abrirla en Windows.",
            ["CastChooseDevice"] = "Elige dónde reproducir.",
            ["CastConnected"] = "Reproduciendo en {0}",
            ["CastDiscoveryFailed"] = "No se pudieron buscar dispositivos Chromecast. Cierra y vuelve a abrir este menú para reintentar.",
            ["CastNoDevices"] = "Aún no se encontró un Chromecast. Comprueba que la PC y el dispositivo estén en la misma red.",
            ["CastDeviceLost"] = "El dispositivo ya no está disponible. Puedes continuar en esta PC.",
            ["CastConnecting"] = "Cambiando el destino de reproducción…",
            ["CastConnectFailed"] = "No se pudo conectar con el dispositivo elegido.",
            ["CastFailedResumedLocal"] = "No se pudo transmitir. La reproducción volvió a esta PC.",
            ["CastRestoreFailed"] = "No se pudo retomar la reproducción. Elige Esta PC para reintentar, o selecciona otra fuente.",
            ["PlayerReplay"] = "Repetir",
            ["PlayerPause"] = "Pausar",
            ["PlayerRewind10"] = "Retroceder 10 s",
            ["PlayerForward30"] = "Avanzar 30 s",
            ["PlayerTvRemoteHint"] = "OK: seleccionar  •  ↑: progreso  •  ↓: servidor / reproductor",
            ["PlayerTvLiveRemoteHint"] = "OK: seleccionar  •  ↓: servidor / reproductor",
            ["PlayerSeekPosition"] = "Posición de reproducción",
            ["PlayerLive"] = "EN DIRECTO",
            ["PlayerVolume"] = "Volumen",
            ["PlayerEnterFullscreen"] = "Pantalla completa",
            ["PlayerExitFullscreen"] = "Salir de pantalla completa",
            ["PlayerAudioRelease"] = "Audio / versión",
            ["PlayerServerQuality"] = "Servidor / calidad",
            ["PlayerAudioOrRelease"] = "Audio o versión",
            ["PlayerClose"] = "Cerrar reproductor",
            ["PlayingTitle"] = "Reproduciendo {0}",
            ["BrowserPlaybackFailed"] = "El navegador no pudo reproducir este archivo torrent.",
            ["BrowserStateInvalid"] = "El reproductor del navegador devolvió un estado no válido.",
            ["BrowserServiceWorkerUrlRequired"] =
                "La reproducción en el navegador requiere una URL del service worker para cada versión.",
            ["LibVlcInitializeFailed"] = "No se pudo iniciar LibVLC en este dispositivo.",
            ["NativeRenditionListInvalid"] =
                "La solicitud contiene una lista no válida de versiones de audio o edición.",
            ["NativeMediaStreamMissing"] =
                "El motor torrent nativo no proporcionó un flujo multimedia legible.",
            ["NativeMediaStreamUnreadable"] = "El flujo multimedia del torrent no se puede leer.",
            ["NativePlaybackSourceMissing"] =
                "La solicitud no contiene una fuente de reproducción nativa.",
            ["LibVlcStreamRejected"] = "LibVLC rechazó el flujo multimedia seleccionado.",
            ["LibVlcVideoSurfaceUnavailable"] =
                "La superficie de video de la aplicación no estaba lista, así que no se inició la reproducción fuera de la aplicación.",
            ["RenditionOpenFailed"] =
                "No se pudo abrir la versión de audio o edición seleccionada: {0}",
            ["RenditionRestoreFailed"] =
                "Falló el cambio de versión y no se pudo restaurar la fuente anterior.",
            ["LibVlcDecodeFailed"] =
                "LibVLC no pudo decodificar o leer la transmisión de video seleccionada.",
            ["LibVlcPlaybackStartTimedOut"] =
                "El servidor de video seleccionado no inició la reproducción a tiempo.",
            ["MediaRenditionListInvalid"] = "La lista de versiones multimedia no es válida.",
            ["MediaRenditionInvalid"] = "Una versión multimedia no es válida.",
            ["PlaybackUrlInvalid"] = "La URL de reproducción torrent no es válida.",
            ["PlaybackOriginInvalid"] =
                "La URL de reproducción torrent debe usar el origen de la aplicación.",
            ["SubtitleListInvalid"] = "La lista de pistas de subtítulos no es válida.",
            ["SubtitleTrackInvalid"] = "Una pista de subtítulos no es válida.",
            ["PdfEmptyOrTooLarge"] =
                "El PDF está vacío o supera el límite de 256 MiB del lector.",
            ["BrowserPdfUrlRequired"] =
                "La lectura de PDF en el navegador requiere una URL torrent del service worker.",
            ["BrowserPdfMetadataInvalid"] =
                "El lector de PDF del navegador devolvió metadatos no válidos.",
            ["BrowserPdfDimensionsInvalid"] =
                "El lector de PDF del navegador devolvió dimensiones de página no válidas.",
            ["PdfPageNotOwned"] = "La página no pertenece a este documento PDF.",
            ["RenderedPdfPageTooLarge"] =
                "La página PDF renderizada supera el límite de imagen de 64 MiB.",
            ["RenderedPdfPageInvalid"] =
                "El lector de PDF del navegador devolvió una página PNG no válida o demasiado grande.",
            ["PlayerBack"] = "Volver",
            ["PlayerBackDescription"] = "Cerrar el reproductor y volver a los detalles del título",
            ["AudioPlayerFor"] = "Reproductor de audio para {0}",
            ["VideoPlayerFor"] = "Reproductor de video para {0}",
            ["AndroidPlaybackLoading"] = "Cargando {0}…",
            ["AndroidPlaybackEngine"] = "Reproductor: {0}",
            ["AndroidPlaybackEncryptionUnsupported"] = "Este flujo cifrado requiere un reproductor compatible con DRM; ninguno de los motores Android incluidos puede abrirlo.",
            ["AndroidPlaybackFailed"] =
                "Android no pudo decodificar o leer el flujo multimedia ({0}).",
            ["IosPlaybackAddressRejected"] =
                "iOS rechazó la dirección local de reproducción torrent.",
            ["IosPlayerCreateFailed"] =
                "iOS no pudo crear un reproductor para el flujo torrent.",
            ["IosWindowUnavailable"] =
                "iOS no proporcionó una ventana activa para la reproducción.",
            ["InAppPlayerNotConfigured"] =
                "Este entorno no configuró un reproductor integrado de video o audio.",
            ["TvNavigationHint"] =
                "Usa el control direccional para moverte, Seleccionar para abrir y Atrás para volver."
        };

    private static CultureInfo _currentCulture = ResolveCulture();

    public static CultureInfo CurrentCulture => _currentCulture;

    public static IReadOnlyCollection<string> ResourceKeys => English.Keys.ToArray();

    public static bool HasCompleteTranslations =>
        English.Keys.All(Spanish.ContainsKey) &&
        Spanish.Keys.All(English.ContainsKey);

    public static string Get(string key) => Get(key, CurrentCulture);

    public static string Get(string key, CultureInfo culture)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(culture);
        var selected = IsSpanish(culture) ? Spanish : English;
        return selected.TryGetValue(key, out var value)
            ? value
            : English.GetValueOrDefault(key, key);
    }

    public static string Format(string key, params object?[] arguments) =>
        string.Format(CurrentCulture, Get(key), arguments);

    public static string Format(
        CultureInfo culture,
        string key,
        params object?[] arguments) =>
        string.Format(culture, Get(key, culture), arguments);

    public static string MediaTypeName(MediaType mediaType) => mediaType switch
    {
        MediaType.Movie => Get("MediaTypeMovie"),
        MediaType.Book => Get("MediaTypeBook"),
        MediaType.Audio => Get("MediaTypeAudio"),
        _ => Get("Unknown")
    };

    public static string SubtypeName(string subtypeCode) => subtypeCode switch
    {
        "movie.movie" => Get("SubtypeMovie"),
        "movie.series" => Get("SubtypeSeries"),
        "movie.anime_movie" => Get("SubtypeAnimeMovie"),
        "movie.anime_series" => Get("SubtypeAnimeSeries"),
        "movie.documentary" => Get("SubtypeDocumentary"),
        "movie.short" => Get("SubtypeShort"),
        "book.book" => Get("SubtypeBook"),
        "book.novel" => Get("SubtypeNovel"),
        "book.manga" => Get("SubtypeManga"),
        "book.comic" => Get("SubtypeComic"),
        "book.light_novel" => Get("SubtypeLightNovel"),
        "book.web_novel" => Get("SubtypeWebNovel"),
        "audio.album" => Get("SubtypeAlbum"),
        "audio.single" => Get("SubtypeSingle"),
        "audio.audiobook" => Get("SubtypeAudiobook"),
        "audio.podcast" => Get("SubtypePodcast"),
        "audio.radio_drama" => Get("SubtypeRadioDrama"),
        _ => subtypeCode
    };

    public static string PublicationStatusName(PublicationStatus status) =>
        status switch
        {
            PublicationStatus.Ongoing => Get("PublicationStatusOngoing"),
            PublicationStatus.Completed => Get("PublicationStatusFinished"),
            PublicationStatus.Paused => Get("PublicationStatusPaused"),
            PublicationStatus.Cancelled => Get("PublicationStatusCancelled"),
            PublicationStatus.Announced => Get("PublicationStatusAnnounced"),
            _ => Get("PublicationStatusUnknown")
        };

    public static string AlternativeNameKind(string kind) =>
        kind.Trim().ToLowerInvariant() switch
        {
            "original" => Get("NameKindOriginal"),
            "translation" or "translated" or "localized" => Get("NameKindTranslation"),
            "alternative" or "alias" => Get("NameKindAlternative"),
            "canonical" => Get("NameKindCanonical"),
            "synonym" => Get("NameKindSynonym"),
            "previous" => Get("NameKindPrevious"),
            "other" or "" => Get("NameKindOther"),
            _ => kind.Replace('_', ' ')
        };

    public static string LanguageName(string language)
    {
        if (string.IsNullOrWhiteSpace(language) ||
            string.Equals(language, "und", StringComparison.OrdinalIgnoreCase))
        {
            return Get("LanguageUnknown");
        }

        try
        {
            var culture = CultureInfo.GetCultureInfo(language);
            return IsSpanish(CurrentCulture)
                ? culture.NativeName
                : culture.DisplayName;
        }
        catch (CultureNotFoundException)
        {
            return language;
        }
    }

    public static string AssetRoleName(string role)
    {
        var normalized = role.Replace('_', ' ').Trim();
        return normalized.ToLowerInvariant() switch
        {
            "" => Get("AssetRoleMedia"),
            "primary" => Get("AssetRolePrimary"),
            "subtitle" or "subtitles" => Get("AssetRoleSubtitle"),
            "caption" or "captions" => Get("AssetRoleCaption"),
            "cover" => Get("AssetRoleCover"),
            "thumbnail" => Get("AssetRoleThumbnail"),
            _ => char.ToUpperInvariant(normalized[0]) + normalized[1..]
        };
    }

    public static void Apply(
        IResourceDictionary resources,
        CultureInfo? culture = null)
    {
        ArgumentNullException.ThrowIfNull(resources);
        _currentCulture = culture ?? ResolveCulture();
        foreach (var (key, fallback) in English)
        {
            resources[key] = IsSpanish(CurrentCulture)
                ? Spanish.GetValueOrDefault(key, fallback)
                : fallback;
        }
    }

    private static bool IsSpanish(CultureInfo culture) =>
        string.Equals(
            culture.TwoLetterISOLanguageName,
            "es",
            StringComparison.OrdinalIgnoreCase);

    private static CultureInfo ResolveCulture()
    {
        var configuredLanguage =
            Environment.GetEnvironmentVariable("UMD_UI_LANGUAGE");
        if (!string.IsNullOrWhiteSpace(configuredLanguage))
        {
            try
            {
                return CultureInfo.GetCultureInfo(configuredLanguage.Trim());
            }
            catch (CultureNotFoundException)
            {
                // An invalid override falls back to the operating-system UI culture.
            }
        }

        return CultureInfo.CurrentUICulture;
    }
}
