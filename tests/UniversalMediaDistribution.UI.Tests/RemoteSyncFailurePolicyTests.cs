using System.Net;
using UniversalMediaDistribution.Catalog.Sync;
using UniversalMediaDistribution.UI.Localization;
using UniversalMediaDistribution.UI.Remote;

namespace UniversalMediaDistribution.UI.Tests;

public sealed class RemoteSyncFailurePolicyTests
{
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public void RefusedSession_SignsTheAccountOut(HttpStatusCode status)
    {
        var failure = new HttpRequestException(
            "rejected",
            inner: null,
            status);

        Assert.Equal(
            RemoteSyncFailureKind.SessionRejected,
            RemoteSyncFailurePolicy.Classify(failure));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public void ServerSideSyncErrors_KeepTheSession(HttpStatusCode status)
    {
        var failure = new HttpRequestException(
            "snapshot rejected",
            inner: null,
            status);

        Assert.Equal(
            RemoteSyncFailureKind.Recoverable,
            RemoteSyncFailurePolicy.Classify(failure));
    }

    [Fact]
    public void LocalSyncErrors_KeepTheSession()
    {
        // The failures a phone actually hits while reconciling: an invalid
        // snapshot, a transport drop, or a timeout. None of them mean the
        // credentials were wrong.
        Assert.Equal(
            RemoteSyncFailureKind.Recoverable,
            RemoteSyncFailurePolicy.Classify(
                new InvalidDataException(
                    "Synchronized progress target 'book' is not in the catalog.")));
        Assert.Equal(
            RemoteSyncFailureKind.Recoverable,
            RemoteSyncFailurePolicy.Classify(
                new HttpRequestException("connection reset")));
        Assert.Equal(
            RemoteSyncFailureKind.Recoverable,
            RemoteSyncFailurePolicy.Classify(
                new TaskCanceledException("sync timed out")));
        Assert.Equal(
            RemoteSyncFailureKind.Recoverable,
            RemoteSyncFailurePolicy.Classify(
                new RemoteSyncConflictException(4, "conflict")));
        Assert.Equal(
            RemoteSyncFailureKind.Recoverable,
            RemoteSyncFailurePolicy.Classify(exception: null));
    }

    [Fact]
    public void SyncFailureMessages_AreDistinctFromSignInFailure()
    {
        Assert.Contains("RemoteInitialSyncFailed", UiTextCatalog.ResourceKeys);
        Assert.Contains("RemoteSessionExpired", UiTextCatalog.ResourceKeys);
        Assert.NotEqual(
            UiTextCatalog.Get("RemoteSignInFailed"),
            UiTextCatalog.Get("RemoteInitialSyncFailed"));
    }
}
