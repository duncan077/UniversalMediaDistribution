using UniversalMediaDistribution.Catalog.Sqlite;
using UniversalMediaDistribution.Catalog.Sync;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.UI.Remote;

namespace UniversalMediaDistribution.UI.Tests;

/// <summary>
/// Provisions a throwaway desktop profile that is already signed in, already
/// has a friend, and already has something to show on a title, so the friend
/// surfaces can be looked at without clicking through registration.
/// <para>
/// A tool rather than a test. It does nothing unless both
/// <c>UMD_TEST_SERVER_URL</c> and <c>UMD_TEST_SEED_PROFILE</c> are set, and it
/// writes only into the directory the second names — pass one under a
/// temporary path and start the application with
/// <c>UMD_APPLICATION_DATA_DIRECTORY</c> pointing at the same place, so the
/// real installation is never touched.
/// </para>
/// <para>
/// The friend's progress has to name a title this profile actually holds, or
/// the item page has nothing to match it against. Titles come from the
/// profile's own catalog, which means a fresh profile has none yet: seed, start
/// the application once so it fills its catalog, then seed again to attach
/// progress to a real title.
/// </para>
/// </summary>
public sealed class FriendProfileSeeder
{
    private const string Password = "correct horse battery staple";

    [Fact]
    public async Task SeedASignedInProfileWithAFriend()
    {
        var serverUrl = Environment.GetEnvironmentVariable(
            "UMD_TEST_SERVER_URL");
        var profileDirectory = Environment.GetEnvironmentVariable(
            "UMD_TEST_SEED_PROFILE");
        if (string.IsNullOrWhiteSpace(serverUrl) ||
            string.IsNullOrWhiteSpace(profileDirectory))
        {
            // Does nothing without both, so an ordinary run is unaffected.
            return;
        }

        var serverUri = new Uri(serverUrl);
        var username = Environment.GetEnvironmentVariable(
            "UMD_TEST_SEED_USERNAME") ?? "desktop";
        var friendUsername = Environment.GetEnvironmentVariable(
            "UMD_TEST_SEED_FRIEND") ?? "ana";
        Directory.CreateDirectory(profileDirectory);

        using var owner = new HttpRemoteMediaServerClient();
        using var friend = new HttpRemoteMediaServerClient();
        var ownerSession = await SignInAsync(owner, serverUri, username);
        var friendSession = await SignInAsync(
            friend,
            serverUri,
            friendUsername);

        await BefriendAsync(owner, friend, ownerSession, friendSession);

        var database = new SqliteCatalogDatabase(
            Path.Combine(profileDirectory, "catalog.db"));
        var seededTitle = await PublishFriendProgressAsync(
            friend,
            database);

        await StoreSessionAsync(database, owner, serverUri, ownerSession);

        // Whether a title was found decides whether the item-page strip has
        // anything to show, and that depends on the profile having been filled
        // by an earlier run, so say which happened rather than finishing
        // silently either way.
        Console.WriteLine(
            seededTitle is null
                ? "Seeded a friend, but the profile catalog is empty, so no " +
                  "progress was attached. Start the application once to fill " +
                  "it, then seed again."
                : $"Seeded a friend reading '{seededTitle}'.");
    }

    /// <summary>
    /// Makes the two accounts friends whichever way round they already stand:
    /// a request from one side completes immediately when the other has
    /// already asked, and an existing friendship simply stays.
    /// </summary>
    private static async Task BefriendAsync(
        HttpRemoteMediaServerClient owner,
        HttpRemoteMediaServerClient friend,
        RemoteAccountSessionInfo ownerSession,
        RemoteAccountSessionInfo friendSession)
    {
        if ((await owner.GetFriendsAsync()).Any(
                candidate => candidate.AccountId == friendSession.AccountId))
        {
            return;
        }

        await friend.SendFriendRequestAsync(ownerSession.Username);
        await owner.AcceptFriendRequestAsync(friendSession.AccountId);
    }

    /// <summary>
    /// Turns the friend's sharing on and gives them progress in a title this
    /// profile holds.
    /// </summary>
    /// <returns>
    /// The title used, or <see langword="null"/> when the profile's catalog has
    /// nothing with units to point at.
    /// </returns>
    private static async Task<string?> PublishFriendProgressAsync(
        HttpRemoteMediaServerClient friend,
        SqliteCatalogDatabase database)
    {
        await friend.SetFriendVisibilityAsync(
            new RemoteFriendVisibility(true, true, []));

        var catalog = await new SqliteCatalogRepository(database)
            .ExportCatalogAsync();
        var candidates = catalog.Items
            .SelectMany(EnumerateCandidates)
            .ToArray();

        // One of each, so both a manga and a series carry progress. A series
        // whose episodes hang off a season is preferred over one without,
        // because that is the shape nothing else here reaches.
        var chosen = new[]
        {
            PickCandidate(candidates, "chapter"),
            PickCandidate(candidates, "episode")
        }
            .OfType<ProgressCandidate>()
            .ToArray();
        if (chosen.Length == 0)
        {
            return null;
        }

        var snapshot = await friend.GetSnapshotAsync();
        await friend.UploadSnapshotAsync(
            BuildProgressDocument(chosen),
            snapshot?.Revision ?? 0);
        return string.Join(
            "', '",
            chosen.Select(static candidate => candidate.Item.Name));
    }

    /// <summary>
    /// Every unit a title offers, whether listed directly or under a season.
    /// A series organised into seasons keeps its episodes on the season, so
    /// looking only at the title's own units misses them entirely.
    /// </summary>
    private static IEnumerable<ProgressCandidate> EnumerateCandidates(
        CatalogTransferItem item) =>
        item.Units
            .Select(unit => new ProgressCandidate(item, null, unit))
            .Concat(item.Seasons.SelectMany(
                season => season.Units.Select(
                    unit => new ProgressCandidate(item, season, unit))));

    private static ProgressCandidate? PickCandidate(
        IReadOnlyList<ProgressCandidate> candidates,
        string unitKind)
    {
        var matching = candidates
            .Where(candidate => string.Equals(
                candidate.Unit.Kind,
                unitKind,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();

        // Past the beginning where possible, so the line reads as real progress
        // rather than as having only just started.
        return matching
            .Where(static candidate => candidate.Season is not null)
            .Concat(matching)
            .Skip(Math.Min(2, Math.Max(0, matching.Length - 1)))
            .FirstOrDefault();
    }

    private sealed record ProgressCandidate(
        CatalogTransferItem Item,
        CatalogTransferSeason? Season,
        CatalogTransferUnit Unit);

    private static UserSyncDocument BuildProgressDocument(
        IReadOnlyList<ProgressCandidate> candidates) =>
        new()
        {
            // Version one on purpose: the upgrade path fills in the per-record
            // sync versions a current-version document is required to carry.
            Version = 1,
            ExportedAt = DateTimeOffset.UtcNow,
            DeviceId = "seeded-friend",
            DeviceName = "Seeded friend",

            // Only the chosen titles. The validator refuses progress that
            // points at a unit the document's own catalog does not carry, and a
            // whole catalog would be a large upload for two lines of text.
            Catalog = new CatalogTransferDocument
            {
                SourceLabel = "Seeded friend progress",
                Items = candidates
                    .Select(static candidate => candidate.Item)
                    .DistinctBy(static item => item.Name, StringComparer.Ordinal)
                    .ToArray()
            },
            Progress = candidates
                .Select(static candidate => new UserSyncProgressEntry
                {
                    Content = new UserSyncContentIdentity
                    {
                        Item = new UserSyncCatalogItemIdentity
                        {
                            MediaType = candidate.Item.MediaType,
                            Name = candidate.Item.Name
                        },
                        UnitKind = candidate.Unit.Kind,
                        DisplayNumber = candidate.Unit.DisplayNumber,
                        SeasonNumber = candidate.Season?.Number
                    },
                    Position = 1,
                    PositionKind = string.Equals(
                        candidate.Unit.Kind,
                        "episode",
                        StringComparison.OrdinalIgnoreCase)
                        ? "seconds"
                        : "page",
                    IsCompleted = false,
                    UpdatedAt = DateTimeOffset.UtcNow
                })
                .ToArray(),
            ReaderPreferences = [],
            FavoriteContributorKeys = [],
            FavoriteStates = [],
            FavoriteItemStates = [],
            Lists = []
        };

    private static async Task StoreSessionAsync(
        SqliteCatalogDatabase database,
        HttpRemoteMediaServerClient owner,
        Uri serverUri,
        RemoteAccountSessionInfo session)
    {
        await new SqliteRemoteServerSettingsStore(database).SaveAsync(
            new RemoteServerSettings
            {
                ServiceUrl = serverUri.AbsoluteUri,
                Username = session.Username,
                DeviceName = "Seeded desktop",
                OnlineCatalogEnabled = false
            });
        await new SqliteRemoteSessionStore(database).SaveAsync(
            new RemoteSessionState
            {
                ServiceUrl = serverUri.AbsoluteUri,
                AccessToken = owner.AccessToken ??
                    throw new InvalidOperationException(
                        "The client reported no access token after signing in."),
                AccountId = session.AccountId,
                Username = session.Username,
                DisplayName = session.DisplayName,
                ExpiresAt = session.ExpiresAt
            });
    }

    /// <summary>
    /// Registers, or signs in when the name is already taken, so the seeder can
    /// be run more than once against the same server.
    /// </summary>
    private static async Task<RemoteAccountSessionInfo> SignInAsync(
        HttpRemoteMediaServerClient client,
        Uri serverUri,
        string username)
    {
        try
        {
            return await client.RegisterAsync(
                serverUri,
                username,
                Password,
                username,
                "Seeded desktop");
        }
        catch (Exception)
        {
            return await client.LoginAsync(
                serverUri,
                username,
                Password,
                "Seeded desktop");
        }
    }
}
