using System.Diagnostics;
using UniversalMediaDistribution.UI.Diagnostics;
using UniversalMediaDistribution.Catalog.Sync;

namespace UniversalMediaDistribution.UI.Remote;

internal sealed record RemoteUserSyncReconciliationResult(
    RemoteSyncWriteResult WriteResult,
    UserSyncRestoreResult? RestoreResult,
    int AttemptCount,
    UserSyncDocument? NewBaseline = null,
    long? BaselineRevision = null);

/// <summary>
/// What the delta path needs to know: the last server revision this device
/// proved it holds, and the document it held when it proved it. The baseline
/// is what local changes are measured against — no baseline, no way to say
/// "only this moved", so the full snapshot goes instead and seeds one.
/// </summary>
internal sealed record RemoteSyncDeltaContext(
    long LastSyncedRevision,
    UserSyncDocument? Baseline);

internal static class RemoteUserSyncReconciler
{
    public const int MaximumAttempts = 5;

    public static async ValueTask<RemoteUserSyncReconciliationResult>
        SynchronizeAsync(
            IRemoteMediaServerClient client,
            IUserSyncStore store,
            string deviceId,
            string deviceName,
            RemoteSyncDeltaContext? delta = null,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(store);
        UserSyncDocumentValidator.ValidateDeviceId(deviceId);
        UserSyncDocumentValidator.ValidateDeviceName(deviceName);

        // The whole reconciliation runs off the caller's thread, not just the
        // requests. Restoring a snapshot converges every incoming title
        // against the catalog and writes the result, which is seconds of work
        // on a real library — awaited from a command it would resume on the
        // UI thread between every step and the window would stop drawing
        // until it finished. Nothing below touches the view models, so there
        // is no thread it has to come back to; the callers await this and
        // resume on their own context.
        return await Task.Run(
            () => SynchronizeCoreAsync(
                client,
                store,
                deviceId,
                deviceName,
                delta,
                cancellationToken),
            cancellationToken);
    }

    private static async Task<RemoteUserSyncReconciliationResult>
        SynchronizeCoreAsync(
            IRemoteMediaServerClient client,
            IUserSyncStore store,
            string deviceId,
            string deviceName,
            RemoteSyncDeltaContext? delta,
            CancellationToken cancellationToken)
    {
        if (delta is { LastSyncedRevision: > 0, Baseline: not null })
        {
            var result = await TrySynchronizeByDeltaAsync(
                client,
                store,
                deviceId,
                deviceName,
                delta,
                cancellationToken);
            if (result is not null)
            {
                return result;
            }
        }

        return await SynchronizeSnapshotsAsync(client, store, deviceId, deviceName, cancellationToken);
    }

    private static async Task<RemoteUserSyncReconciliationResult> SynchronizeSnapshotsAsync(
        IRemoteMediaServerClient client, IUserSyncStore store, string deviceId, string deviceName,
        CancellationToken cancellationToken, RemoteSyncSnapshot? prefetchedSnapshot = null)
    {
        for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = attempt == 1 && prefetchedSnapshot is not null
                ? prefetchedSnapshot
                : await MeasureAsync("download-snapshot", () => client.GetSnapshotAsync(cancellationToken));
            UserSyncRestoreResult? restore = null;
            if (snapshot is not null)
            {
                restore = await MeasureAsync("restore-snapshot", () => store.RestoreAsync(snapshot.Document, cancellationToken));
            }

            var document = await MeasureAsync("export", () => store.ExportAsync(deviceId, deviceName, cancellationToken));
            if (snapshot is not null &&
                UserSyncDocumentDelta.ComputeChangedKeys(snapshot.Document, document).Count == 0)
            {
                return new RemoteUserSyncReconciliationResult(
                    new RemoteSyncWriteResult(snapshot.Revision, snapshot.UpdatedAt, snapshot.UpdatedByDeviceId),
                    restore, attempt, document, snapshot.Revision);
            }
            try
            {
                var write = await MeasureAsync("upload-snapshot", () => client.UploadSnapshotAsync(document, snapshot?.Revision ?? 0, cancellationToken));
                return new RemoteUserSyncReconciliationResult(
                    write,
                    restore,
                    attempt,
                    document,
                    // A concurrent upload may have been merged by the server.
                    // Do not claim to have downloaded those unseen records.
                    write.Revision == (snapshot?.Revision ?? 0) + 1
                        ? write.Revision : snapshot?.Revision ?? 0);
            }
            catch (RemoteSyncConflictException)
                when (attempt < MaximumAttempts)
            {
                // Pull the winning snapshot, merge it locally, and retry.
            }
        }

        throw new InvalidOperationException(
            "Device synchronization could not converge after repeated concurrent updates.");
    }

    /// <summary>
    /// The routine sync: pull only what changed on the server, push only what
    /// changed here. Null when the server cannot answer incrementally —
    /// unknown account, an index-less stored state, or a client that does not
    /// implement the call — in which case the full snapshot path runs and
    /// re-seeds the baseline.
    /// </summary>
    private static async Task<RemoteUserSyncReconciliationResult?>
        TrySynchronizeByDeltaAsync(
            IRemoteMediaServerClient client,
            IUserSyncStore store,
            string deviceId,
            string deviceName,
            RemoteSyncDeltaContext delta,
            CancellationToken cancellationToken)
    {
        var changes = await MeasureAsync("download-changes", () => client.GetChangesAsync(delta.LastSyncedRevision, cancellationToken));
        if (changes is { Full: true })
        {
            return await SynchronizeSnapshotsAsync(client, store, deviceId, deviceName, cancellationToken,
                new RemoteSyncSnapshot(changes.ToRevision, changes.Document.ExportedAt, changes.Document.DeviceId, changes.Document));
        }
        if (changes is null || changes.ToRevision < delta.LastSyncedRevision)
        {
            return null;
        }

        UserSyncRestoreResult? restore = null;
        if (!UserSyncDocumentDelta.IsEmpty(changes.Document))
        {
            restore = await MeasureAsync("restore-changes", () => store.RestoreAsync(changes.Document, cancellationToken));
        }

        var export = await MeasureAsync("export", () => store.ExportAsync(deviceId, deviceName, cancellationToken));

        // Local changes are what differs from the baseline — minus what the
        // server itself just sent, which would otherwise echo straight back.
        var localKeys = new HashSet<string>(
            UserSyncDocumentDelta.ComputeChangedKeys(delta.Baseline, export),
            StringComparer.Ordinal);
        localKeys.IntersectWith(
            UserSyncDocumentDelta.ComputeChangedKeys(changes.Document, export));

        if (localKeys.Count == 0)
        {
            return new RemoteUserSyncReconciliationResult(
                new RemoteSyncWriteResult(
                    changes.ToRevision,
                    DateTimeOffset.UtcNow,
                    deviceId),
                restore,
                AttemptCount: 1,
                export,
                changes.ToRevision);
        }

        var partial = UserSyncDocumentDelta.Extract(export, localKeys);
        try
        {
            var write = await MeasureAsync("upload-changes", () => client.PatchProgressAsync(partial, cancellationToken));
            return new RemoteUserSyncReconciliationResult(
                write,
                restore,
                AttemptCount: 1,
                export,
                changes.ToRevision);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException)
        {
            // Whatever refused the patch — a server that lost its snapshot,
            // an incompatibility — the full path answers it, at the cost the
            // full path always had.
            return null;
        }
    }
    private static async ValueTask<T> MeasureAsync<T>(string stage, Func<ValueTask<T>> action)
    {
        var timer = Stopwatch.StartNew();
        var outcome = "failed";
        try
        {
            var result = await action();
            outcome = "ok";
            return result;
        }
        finally
        {
            ApplicationDiagnosticLog.Information("sync.stage",
                $"stage={stage}; elapsedMs={timer.ElapsedMilliseconds}; outcome={outcome}");
        }
    }

}
