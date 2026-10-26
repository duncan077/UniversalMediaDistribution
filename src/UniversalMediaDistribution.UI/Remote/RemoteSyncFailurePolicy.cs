using System.Net;

namespace UniversalMediaDistribution.UI.Remote;

public enum RemoteSyncFailureKind
{
    /// <summary>
    /// The session is still valid; only this synchronization attempt failed.
    /// </summary>
    Recoverable,

    /// <summary>
    /// The server refused the session, so the account is no longer signed in.
    /// </summary>
    SessionRejected
}

/// <summary>
/// Decides whether a synchronization failure invalidates the signed-in session.
/// Sign-in succeeds long before device state is reconciled, so a failed
/// snapshot download, merge, or upload must not be reported as a rejected
/// login - only a refused session may sign the account out.
/// </summary>
public static class RemoteSyncFailurePolicy
{
    public static RemoteSyncFailureKind Classify(Exception? exception) =>
        exception switch
        {
            HttpRequestException
            {
                StatusCode: HttpStatusCode.Unauthorized or
                    HttpStatusCode.Forbidden
            } => RemoteSyncFailureKind.SessionRejected,
            _ => RemoteSyncFailureKind.Recoverable
        };
}
