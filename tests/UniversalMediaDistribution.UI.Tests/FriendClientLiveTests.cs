using UniversalMediaDistribution.Domain.Catalog;
using UniversalMediaDistribution.UI.Remote;

namespace UniversalMediaDistribution.UI.Tests;

/// <summary>
/// Drives the real HTTP client against a real server.
/// <para>
/// Everything else about friends is tested against a stub or by calling the
/// store directly, which leaves the one seam nothing covers: the URLs this
/// client builds and the JSON it expects back. A wrong route or a renamed
/// field shows up here and nowhere else.
/// </para>
/// <para>
/// Skipped unless <c>UMD_TEST_SERVER_URL</c> names a reachable server, so an
/// ordinary run is unaffected. Start one with
/// <c>UMD_SERVER_ALLOW_REGISTRATION=true</c> and point the variable at it.
/// </para>
/// </summary>
public sealed class FriendClientLiveTests
{
    private static readonly Uri? ServerUri = ReadServerUri();

    [Fact]
    public async Task TheClientDrivesAWholeFriendshipAgainstAServer()
    {
        if (ServerUri is null)
        {
            // Passes without testing anything. Nothing here can run without a
            // server, and the alternative is a red suite on every machine that
            // has not started one.
            return;
        }

        // Unique names: the server keeps accounts, so a rerun against the same
        // data directory must not collide with the previous one.
        var suffix = Guid.CreateVersion7().ToString("N")[..12];
        using var owner = new HttpRemoteMediaServerClient();
        using var viewer = new HttpRemoteMediaServerClient();

        var ownerSession = await RegisterAsync(owner, $"owner{suffix}");
        var viewerSession = await RegisterAsync(viewer, $"viewer{suffix}");

        var capabilities = await owner.GetCapabilitiesAsync(ServerUri!);
        Assert.True(
            capabilities.Friends,
            "The server under test does not report the friends capability.");

        // Ask, appear on both sides, accept.
        var outcome = await viewer.SendFriendRequestAsync($"owner{suffix}");
        Assert.Equal(RemoteFriendRequestOutcome.Sent, outcome);

        var incoming = Assert.Single(await owner.GetFriendRequestsAsync());
        Assert.True(incoming.IsIncoming);
        Assert.Equal(viewerSession.AccountId, incoming.AccountId);

        var outgoing = Assert.Single(await viewer.GetFriendRequestsAsync());
        Assert.False(outgoing.IsIncoming);

        Assert.True(
            await owner.AcceptFriendRequestAsync(viewerSession.AccountId));
        Assert.Empty(await owner.GetFriendRequestsAsync());

        var friend = Assert.Single(await viewer.GetFriendsAsync());
        Assert.Equal(ownerSession.AccountId, friend.AccountId);
        Assert.True(
            friend.SharesProgress,
            "A new friend should start included.");

        // Sharing is off until asked for, and the switch round-trips.
        var visibility = await owner.GetFriendVisibilityAsync();
        Assert.False(
            visibility.ShareProgress,
            "Sharing should be off until it is turned on.");
        var saved = await owner.SetFriendVisibilityAsync(
            new RemoteFriendVisibility(true, true, []));
        Assert.True(saved.ShareProgress);

        // Nothing to see yet: the owner has never synced.
        var itemKey = ContentItemKey.Create(MediaType.Book, "Copper Skies");
        Assert.Empty(await viewer.LookupFriendProgressAsync([itemKey]));

        // The per-friend switch and the removal both answer over the wire.
        Assert.True(
            await owner.SetFriendSharingAsync(
                viewerSession.AccountId,
                shareProgress: false));
        Assert.False(
            Assert.Single(await owner.GetFriendsAsync()).SharesProgress);

        Assert.True(
            await viewer.RemoveFriendAsync(ownerSession.AccountId));
        Assert.Empty(await viewer.GetFriendsAsync());
        Assert.Empty(await owner.GetFriendsAsync());

        await owner.SignOutAsync();
        await viewer.SignOutAsync();
    }

    /// <summary>
    /// A name nobody holds answers the same way as one whose owner does not
    /// accept requests, and the client reports both as nothing found rather
    /// than raising.
    /// </summary>
    [Fact]
    public async Task AnUnknownNameIsReportedAsNothingFound()
    {
        if (ServerUri is null)
        {
            // Passes without testing anything. Nothing here can run without a
            // server, and the alternative is a red suite on every machine that
            // has not started one.
            return;
        }

        var suffix = Guid.CreateVersion7().ToString("N")[..12];
        using var client = new HttpRemoteMediaServerClient();
        await RegisterAsync(client, $"seeker{suffix}");

        Assert.Null(
            await client.SendFriendRequestAsync($"absent{suffix}"));

        await client.SignOutAsync();
    }

    private static async Task<RemoteAccountSessionInfo> RegisterAsync(
        HttpRemoteMediaServerClient client,
        string username) =>
        await client.RegisterAsync(
            ServerUri!,
            username,
            "correct horse battery staple",
            username,
            "Integration test");

    private static Uri? ReadServerUri()
    {
        var configured = Environment.GetEnvironmentVariable(
            "UMD_TEST_SERVER_URL");
        return !string.IsNullOrWhiteSpace(configured) &&
            Uri.TryCreate(configured, UriKind.Absolute, out var uri)
            ? uri
            : null;
    }
}
