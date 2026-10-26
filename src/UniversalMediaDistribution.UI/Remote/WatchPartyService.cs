using System.Globalization;
using UniversalMediaDistribution.UI.Diagnostics;
using UniversalMediaDistribution.UI.Playback;

namespace UniversalMediaDistribution.UI.Remote;

/// <summary>
/// Keeps this device's playback and a watch party's shared timeline pointing
/// at the same moment.
/// <para>
/// The platform players have no way to report "the viewer pressed pause", so
/// local intent is inferred the only way that works on every backend: the
/// 2-second playback monitor hands each state sample here, and a sample that
/// disagrees with where the shared timeline says we should be — paused when
/// the party plays, or several seconds away from the expected position — is
/// read as the viewer acting, and becomes a command to the party. Commands
/// arriving from the party are applied through the player's transport
/// controls, with a short grace period so an applied command is not
/// re-detected as a fresh local action and echoed back.
/// </para>
/// </summary>
public sealed class WatchPartyService : IAsyncDisposable
{
    /// <summary>
    /// How far a sample may sit from the expected position before it reads as
    /// a deliberate seek. Below this it is timer jitter, buffering, and the
    /// 2-second sampling grid.
    /// </summary>
    private const double SeekDeviationSeconds = 5;

    /// <summary>
    /// How much drift an incoming update must show before the player is
    /// repositioned. Tighter than the outgoing threshold so members converge
    /// instead of ping-ponging.
    /// </summary>
    private const double DriftCorrectionSeconds = 3;

    private static readonly TimeSpan RemoteApplyGrace =
        TimeSpan.FromSeconds(4);
    private static readonly TimeSpan CommandRepeatDelay =
        TimeSpan.FromSeconds(3);
    private static readonly TimeSpan HostHeartbeatInterval =
        TimeSpan.FromSeconds(10);

    /// <summary>
    /// Backoff between reconnection attempts, holding at the last value for
    /// as long as the application is open. A party outlives a lift ride or a
    /// sleeping laptop, so giving up after a fixed number of tries would end
    /// parties the server still holds.
    /// </summary>
    private static readonly TimeSpan[] ReconnectDelays =
    [
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30)
    ];

    private readonly IRemoteMediaServerClient _client;
    private readonly IPlatformMediaPlayer _player;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _gate = new();

    private bool _supportsWatchParties;
    private RemoteWatchPartyState? _current;
    private long _lastSequence = -1;
    private RemoteWatchPartyContent? _activePlayback;
    private IWatchPartyConnection? _connection;
    private CancellationTokenSource? _receiveCancellation;
    private Task? _receiveTask;
    private DateTimeOffset _lastRemoteApplyAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastCommandSentAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastHeartbeatAt = DateTimeOffset.MinValue;
    private string? _lastEventDescription;
    private bool _reportedBuffering;
    private int _pendingPlaybackApplies;
    private bool _disposed;

    public WatchPartyService(
        IRemoteMediaServerClient client,
        IPlatformMediaPlayer player,
        TimeProvider? timeProvider = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Raised from background threads whenever the party changed in any way,
    /// including ending. Subscribers marshal to their own thread.
    /// </summary>
    public event Action? StateChanged;

    public bool IsAvailable
    {
        get
        {
            lock (_gate)
            {
                return _supportsWatchParties && _client.Session is not null;
            }
        }
    }

    public RemoteWatchPartyState? Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public bool IsInParty => Current is not null;

    public bool IsConnected
    {
        get
        {
            lock (_gate)
            {
                return _connection is not null;
            }
        }
    }

    public bool IsHost
    {
        get
        {
            lock (_gate)
            {
                return _current is not null &&
                       _client.Session is { } session &&
                       _current.HostAccountId == session.AccountId;
            }
        }
    }

    /// <summary>
    /// Whether what this device is playing is what the party is watching —
    /// the condition for commands to flow in either direction.
    /// </summary>
    public bool IsSynchronized
    {
        get
        {
            lock (_gate)
            {
                return _current?.Content is { } partyContent &&
                       _activePlayback is { } local &&
                       ContentMatches(partyContent, local);
            }
        }
    }

    /// <summary>
    /// The most recent party happening, phrased for a status line:
    /// who paused, who joined.
    /// </summary>
    public string? LastEventDescription
    {
        get
        {
            lock (_gate)
            {
                return _lastEventDescription;
            }
        }
    }

    public void ApplyCapabilities(RemoteServerCapabilities? capabilities)
    {
        lock (_gate)
        {
            _supportsWatchParties = capabilities?.WatchParties is true;
        }
    }

    public async ValueTask<RemoteWatchPartyState?> CreateAsync(
        RemoteWatchPartyContent? content,
        double positionSeconds,
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            return null;
        }

        var state = await _client.CreateWatchPartyAsync(
            content,
            positionSeconds,
            cancellationToken);
        await DisconnectAsync();
        await AdoptPartyAsync(state, cancellationToken);
        return state;
    }

    public async ValueTask<RemoteWatchPartyState?> JoinAsync(
        string joinCode,
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(joinCode))
        {
            return null;
        }

        var state = await _client.JoinWatchPartyAsync(
            joinCode,
            cancellationToken);
        if (state is null)
        {
            return null;
        }

        await DisconnectAsync();
        await AdoptPartyAsync(state, cancellationToken);
        return state;
    }

    /// <summary>
    /// Picks the party this account was already in back up — after a restart,
    /// or a sign-in that happened while friends kept a party open.
    /// </summary>
    public async ValueTask<bool> TryResumeAsync(
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable || IsInParty)
        {
            return IsInParty;
        }

        try
        {
            var state = await _client.GetCurrentWatchPartyAsync(
                cancellationToken);
            if (state is null)
            {
                return false;
            }

            await AdoptPartyAsync(state, cancellationToken);
            return true;
        }
        catch (Exception exception)
        {
            ApplicationDiagnosticLog.Warning(
                "watch-party.resume-failed",
                exception.Message);
            return false;
        }
    }

    /// <summary>
    /// Whether the party takes transport only from its host.
    /// </summary>
    public bool HostControlsOnly
    {
        get
        {
            lock (_gate)
            {
                return _current?.HostControlsOnly is true;
            }
        }
    }

    /// <summary>
    /// Whether this device's own pauses and seeks reach the party. False for
    /// a member of a host-locked party, and for a player whose transport
    /// cannot be driven at all — an embedded page owns its own controls.
    /// </summary>
    public bool CanControlParty
    {
        get
        {
            lock (_gate)
            {
                if (_current is null)
                {
                    return false;
                }

                var isHost = _client.Session is { } session &&
                    _current.HostAccountId == session.AccountId;
                return isHost || !_current.HostControlsOnly;
            }
        }
    }

    /// <summary>
    /// Whether the active player can be driven by the party at all. A page
    /// playing in the embedded browser cannot, so a member watching there is
    /// told rather than left wondering why nothing happens.
    /// </summary>
    public bool CanFollowParty => _player.SupportsTransportControl;

    /// <summary>Aligns only this viewer; never publishes a seek to the room.</summary>
    public async ValueTask ResynchronizeAsync(CancellationToken cancellationToken = default)
    {
        if (IsConnected && IsSynchronized && CanFollowParty)
        {
            await ApplyPartyPlaybackAsync(forceSeek: true, cancellationToken);
        }
    }

    /// <summary>
    /// Locks transport to the host, or unlocks it. Only the host may.
    /// </summary>
    public async ValueTask<bool> SetHostControlsOnlyAsync(
        bool hostControlsOnly,
        CancellationToken cancellationToken = default)
    {
        if (!IsInParty || !IsHost)
        {
            return false;
        }

        var updated = await _client.SetWatchPartyOptionsAsync(
            hostControlsOnly,
            cancellationToken);
        if (updated is null)
        {
            return false;
        }

        lock (_gate)
        {
            _current = updated;
        }

        RaiseStateChanged();
        return true;
    }

    public async ValueTask LeaveAsync(
        CancellationToken cancellationToken = default)
    {
        await DisconnectAsync();
        var hadParty = false;
        lock (_gate)
        {
            hadParty = _current is not null;
            _current = null;
            _lastSequence = -1;
            _lastEventDescription = null;
        }

        try
        {
            if (_client.Session is not null)
            {
                await _client.LeaveWatchPartyAsync(cancellationToken);
            }
        }
        catch (Exception exception)
        {
            // Leaving locally succeeded either way; the server ends the empty
            // membership when the socket stays gone.
            ApplicationDiagnosticLog.Warning(
                "watch-party.leave-failed",
                exception.Message);
        }

        if (hadParty)
        {
            RaiseStateChanged();
        }
    }

    /// <summary>
    /// Forgets the party without telling the server — the sign-out and
    /// profile-switch path, where the session that could talk to the server
    /// is going away.
    /// </summary>
    public async ValueTask ResetAsync()
    {
        await DisconnectAsync();
        var hadParty = false;
        lock (_gate)
        {
            hadParty = _current is not null;
            _current = null;
            _lastSequence = -1;
            _activePlayback = null;
            _lastEventDescription = null;
        }

        if (hadParty)
        {
            RaiseStateChanged();
        }
    }

    /// <summary>
    /// Records what this device is playing now (or null when playback
    /// closed). When the host starts something, the party follows it.
    /// </summary>
    public async ValueTask NotifyPlaybackStartedAsync(
        RemoteWatchPartyContent? localContent,
        double positionSeconds,
        CancellationToken cancellationToken = default)
    {
        IWatchPartyConnection? connection;
        bool isHost;
        bool synchronizedNow;
        bool clearBuffering;
        lock (_gate)
        {
            clearBuffering = _reportedBuffering;
            _activePlayback = localContent;
            connection = _connection;
            isHost = _current is not null &&
                _client.Session is { } session &&
                _current.HostAccountId == session.AccountId;
            synchronizedNow = _current?.Content is { } partyContent &&
                localContent is not null &&
                ContentMatches(partyContent, localContent);
        }

        if (connection is not null && clearBuffering)
        {
            await SendCommandAsync(
                connection,
                new RemoteWatchPartyCommand("buffering", IsBuffering: false),
                _timeProvider.GetUtcNow(),
                cancellationToken);
        }

        if (connection is null || localContent is null)
        {
            // What this device plays is part of the observable state — the
            // synchronized flag just changed even though nothing was sent.
            RaiseStateChanged();
            return;
        }

        try
        {
            if (isHost)
            {
                await connection.SendAsync(
                    new RemoteWatchPartyCommand(
                        "content",
                        positionSeconds,
                        localContent),
                    cancellationToken);
            }
            else if (synchronizedNow)
            {
                // A member arriving on the party's title aligns to the shared
                // timeline instead of dragging everyone to their own resume
                // position.
                await AlignToPartyAsync(cancellationToken);
            }
        }
        catch (Exception exception)
        {
            ApplicationDiagnosticLog.Warning(
                "watch-party.playback-start",
                exception.Message);
        }

        RaiseStateChanged();
    }

    /// <summary>
    /// Whether opening this title should stop at its first frame: the party
    /// is watching it and is not playing. A member watching something else
    /// of their own is never held.
    /// </summary>
    public bool ShouldOpenPaused(RemoteWatchPartyContent localContent)
    {
        ArgumentNullException.ThrowIfNull(localContent);
        lock (_gate)
        {
            return _current?.Content is { } partyContent &&
                   ContentMatches(partyContent, localContent) &&
                   !_current.Playback.IsPlaying;
        }
    }

    /// <summary>
    /// The position a member should open the party's title at, or null when
    /// this device is not in a party watching that title.
    /// </summary>
    public double? GetJoinPositionFor(RemoteWatchPartyContent localContent)
    {
        ArgumentNullException.ThrowIfNull(localContent);
        lock (_gate)
        {
            return _current?.Content is { } partyContent &&
                   ContentMatches(partyContent, localContent)
                ? _current.Playback.PositionAt(_timeProvider.GetUtcNow())
                : null;
        }
    }

    /// <summary>
    /// One sample from the playback monitor. Detects viewer actions and
    /// publishes them, and keeps the host's heartbeat flowing.
    /// </summary>
    public async ValueTask OnPlaybackTickAsync(
        PlatformMediaState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        IWatchPartyConnection? connection;
        RemoteWatchPartyPlayback? playback;
        bool isHost;
        bool hostControlsOnly;
        bool bufferingChanged;
        var now = _timeProvider.GetUtcNow();
        lock (_gate)
        {
            // A player whose transport cannot be driven must not drive the
            // party either. The embedded web player reports a placeholder
            // state — position zero, always playing — which would otherwise
            // read as a seek to the start and drag everyone back with it.
            if (_current?.Content is not { } partyContent ||
                _activePlayback is not { } local ||
                !ContentMatches(partyContent, local) ||
                _connection is null ||
                !_player.SupportsTransportControl ||
                state.IsClosed ||
                state.IsEnded)
            {
                return;
            }

            connection = _connection;
            playback = _current.Playback;
            isHost = _client.Session is { } session &&
                _current.HostAccountId == session.AccountId;
            hostControlsOnly = _current.HostControlsOnly;

            // Reported before the quiet windows below: a stall is exactly
            // what happens while a remote command is still settling, and the
            // party is waiting to hear about it.
            bufferingChanged = state.IsBuffering != _reportedBuffering;
        }

        if (bufferingChanged)
        {
            await SendCommandAsync(
                connection,
                new RemoteWatchPartyCommand(
                    "buffering",
                    state.PositionSeconds,
                    Content: null,
                    IsBuffering: state.IsBuffering),
                now,
                cancellationToken);
            return;
        }

        lock (_gate)
        {
            if (_pendingPlaybackApplies > 0 ||
                now - _lastRemoteApplyAt < RemoteApplyGrace ||
                now - _lastCommandSentAt < CommandRepeatDelay)
            {
                return;
            }
        }

        // A stalled player is not making choices, and the party has already
        // been told why it is not moving.
        if (state.IsBuffering)
        {
            return;
        }

        var expectedPosition = playback.PositionAt(now);
        RemoteWatchPartyCommand? command = null;
        var canControlParty = isHost || !hostControlsOnly;
        if (canControlParty && state.IsPlaying != playback.IsPlaying)
        {
            command = new RemoteWatchPartyCommand(
                state.IsPlaying ? "play" : "pause",
                state.PositionSeconds);
        }
        else if (canControlParty &&
                 Math.Abs(state.PositionSeconds - expectedPosition) >
                 SeekDeviationSeconds)
        {
            command = new RemoteWatchPartyCommand(
                "seek",
                state.PositionSeconds);
        }
        else if (isHost &&
                 playback.IsPlaying &&
                 now - _lastHeartbeatAt > HostHeartbeatInterval)
        {
            command = new RemoteWatchPartyCommand(
                "position",
                state.PositionSeconds);
        }
        else if (!canControlParty &&
                 (state.IsPlaying != playback.IsPlaying ||
                  Math.Abs(state.PositionSeconds - expectedPosition) >
                  SeekDeviationSeconds))
        {
            // A locked party ignores this member's transport, so what they
            // did locally is undone by putting them back on the timeline.
            await ApplyPartyPlaybackAsync(
                forceSeek: true,
                cancellationToken);
            return;
        }

        if (command is null)
        {
            return;
        }

        await SendCommandAsync(connection, command, now, cancellationToken);
    }

    private async ValueTask SendCommandAsync(
        IWatchPartyConnection connection,
        RemoteWatchPartyCommand command,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        try
        {
            await connection.SendAsync(command, cancellationToken);
            lock (_gate)
            {
                switch (command.Type)
                {
                    case "position":
                        _lastHeartbeatAt = now;
                        break;
                    case "buffering":
                        // Nothing local to assume: the server decides whether
                        // a stall pauses the party, and says so in its reply.
                        _reportedBuffering = command.IsBuffering is true;
                        break;
                    default:
                        _lastCommandSentAt = now;

                        // Assume the command lands so the next tick measures
                        // against it; the server's echo confirms or corrects.
                        if (_current is not null)
                        {
                            _current = _current with
                            {
                                Playback = new RemoteWatchPartyPlayback(
                                    command.Type is not "pause" &&
                                    (command.Type is "play" ||
                                     _current.Playback.IsPlaying),
                                    command.PositionSeconds ?? 0,
                                    now,
                                    _client.Session?.DisplayName ??
                                    string.Empty)
                            };
                        }

                        break;
                }
            }
        }
        catch (Exception exception)
        {
            ApplicationDiagnosticLog.Warning(
                "watch-party.command-failed",
                exception.Message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await DisconnectAsync();
    }

    private async ValueTask AdoptPartyAsync(
        RemoteWatchPartyState state,
        CancellationToken cancellationToken)
    {
        var connection = await _client.ConnectWatchPartyAsync(
            cancellationToken);
        var receiveCancellation = new CancellationTokenSource();
        lock (_gate)
        {
            _current = state;
            _lastSequence = -1;
            _connection = connection;
            _receiveCancellation = receiveCancellation;
            _lastRemoteApplyAt = _timeProvider.GetUtcNow();
            _lastEventDescription = null;
            _reportedBuffering = false;
            _lastCommandSentAt = DateTimeOffset.MinValue;
            _lastHeartbeatAt = DateTimeOffset.MinValue;
        }

        _receiveTask = ReceiveLoopAsync(
            connection,
            receiveCancellation.Token);
        RaiseStateChanged();
    }

    private async Task ReceiveLoopAsync(
        IWatchPartyConnection connection,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                while (!cancellationToken.IsCancellationRequested)
                {
                    var partyEvent = await connection.ReceiveAsync(cancellationToken);
                    if (partyEvent is null)
                    {
                        break;
                    }

                    await HandleEventAsync(partyEvent, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                ApplicationDiagnosticLog.Warning("watch-party.socket-lost", exception.Message);
            }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_connection, connection))
                    {
                        _connection = null;
                    }
                }

                try
                {
                    await connection.DisposeAsync();
                }
                catch (Exception exception)
                {
                    ApplicationDiagnosticLog.Warning("watch-party.socket-close-failed", exception.Message);
                }
                RaiseStateChanged();
            }

            var reconnected = await ReconnectAsync(cancellationToken);
            if (reconnected is null)
            {
                return;
            }

            connection = reconnected;
        }
    }

    private async Task<IWatchPartyConnection?> ReconnectAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var delay = ReconnectDelays[
                Math.Min(attempt, ReconnectDelays.Length - 1)];
            try
            {
                await Task.Delay(delay, cancellationToken);
                if (!IsAvailable)
                {
                    break;
                }

                var state = await _client.GetCurrentWatchPartyAsync(
                    cancellationToken);
                if (state is null)
                {
                    // The party genuinely ended while we were away.
                    break;
                }

                var connection = await _client.ConnectWatchPartyAsync(
                    cancellationToken);
                lock (_gate)
                {
                    if (_current?.PartyId != state.PartyId)
                    {
                        _lastSequence = -1;
                    }
                    _current = state;
                    _connection = connection;
                    _lastRemoteApplyAt = _timeProvider.GetUtcNow();

                    // The server dropped this member's stall when the socket
                    // died, so the next tick has to report it afresh.
                    _reportedBuffering = false;
                }

                RaiseStateChanged();
                if (IsSynchronized)
                {
                    await AlignToPartyAsync(cancellationToken);
                }
                return connection;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception exception)
            {
                ApplicationDiagnosticLog.Warning(
                    "watch-party.reconnect-failed",
                    exception.Message);
            }
        }

        lock (_gate)
        {
            _current = null;
            _connection = null;
            _lastEventDescription = null;
        }

        RaiseStateChanged();
        return null;
    }

    private async ValueTask HandleEventAsync(
        RemoteWatchPartyEvent partyEvent,
        CancellationToken cancellationToken)
    {
        if (partyEvent.State is null)
        {
            return;
        }

        bool isOwnAction;
        bool synchronized;
        lock (_gate)
        {
            if (partyEvent.Sequence <= _lastSequence)
            {
                return;
            }

            _lastSequence = partyEvent.Sequence;
            _current = partyEvent.State;
            _lastEventDescription = DescribeEvent(partyEvent);
            isOwnAction = partyEvent.InitiatedByAccountId is { } initiator &&
                _client.Session is { } session &&
                initiator == session.AccountId;
            synchronized = partyEvent.State.Content is { } partyContent &&
                _activePlayback is { } local &&
                ContentMatches(partyContent, local);
        }

        // Every event carries a complete snapshot. Membership changes can
        // resume a buffering pause, and our own buffering report can pause
        // or resume us. Only explicit local transport echoes are redundant.
        var isLocalTransportEcho = isOwnAction &&
            partyEvent.Reason is "play" or "pause" or "seek" or "content" or "position";
        if (!isLocalTransportEcho && synchronized)
        {
            await ApplyPartyPlaybackAsync(
                forceSeek: partyEvent.Reason is "seek",
                cancellationToken);
        }

        RaiseStateChanged();
    }

    /// <summary>
    /// Brings the local player to the shared timeline: pause state exactly,
    /// position only when it is far enough off to matter.
    /// </summary>
    private async ValueTask ApplyPartyPlaybackAsync(
        bool forceSeek,
        CancellationToken cancellationToken)
    {
        RemoteWatchPartyPlayback? playback;
        lock (_gate)
        {
            playback = _current?.Playback;
            _lastRemoteApplyAt = _timeProvider.GetUtcNow();
        }

        if (playback is null ||
            !_player.SupportsTransportControl ||
            !_player.IsAvailable)
        {
            return;
        }

        try
        {
            lock (_gate)
            {
                _pendingPlaybackApplies++;
            }

            var state = await _player.GetStateAsync(cancellationToken);
            if (state.IsClosed || state.IsEnded)
            {
                return;
            }

            var target = playback.PositionAt(_timeProvider.GetUtcNow());
            if (forceSeek ||
                Math.Abs(state.PositionSeconds - target) >
                DriftCorrectionSeconds)
            {
                await _player.SeekAsync(target, cancellationToken);
            }

            if (state.IsPlaying != playback.IsPlaying)
            {
                await _player.SetPlayingAsync(
                    playback.IsPlaying,
                    cancellationToken);
            }
        }
        catch (Exception exception)
        {
            ApplicationDiagnosticLog.Warning(
                "watch-party.apply-failed",
                exception.Message);
        }
        finally
        {
            lock (_gate)
            {
                _pendingPlaybackApplies--;
                // Allow a slow seek to settle before inferring local intent.
                _lastRemoteApplyAt = _timeProvider.GetUtcNow();
            }
        }
    }

    private ValueTask AlignToPartyAsync(
        CancellationToken cancellationToken) =>
        ApplyPartyPlaybackAsync(forceSeek: false, cancellationToken);

    private async ValueTask DisconnectAsync()
    {
        IWatchPartyConnection? connection;
        CancellationTokenSource? receiveCancellation;
        Task? receiveTask;
        lock (_gate)
        {
            connection = _connection;
            receiveCancellation = _receiveCancellation;
            receiveTask = _receiveTask;
            _connection = null;
            _receiveCancellation = null;
            _receiveTask = null;
        }

        if (receiveCancellation is not null)
        {
            try
            {
                await receiveCancellation.CancelAsync();
            }
            finally
            {
                receiveCancellation.Dispose();
            }
        }

        if (receiveTask is not null)
        {
            try
            {
                await receiveTask;
            }
            catch (Exception)
            {
                // The loop's own logging already said what went wrong.
            }
        }
        else if (connection is not null)
        {
            await connection.DisposeAsync();
        }
    }

    private void RaiseStateChanged() => StateChanged?.Invoke();

    private static string? DescribeEvent(RemoteWatchPartyEvent partyEvent)
    {
        var who = partyEvent.InitiatedByDisplayName;
        if (string.IsNullOrWhiteSpace(who))
        {
            return null;
        }

        return partyEvent.Reason switch
        {
            "play" or "pause" or "seek" or "joined" or "left" or "content" =>
                $"{who} · {partyEvent.Reason}",
            _ => null
        };
    }

    internal static bool ContentMatches(
        RemoteWatchPartyContent partyContent,
        RemoteWatchPartyContent localContent) =>
        string.Equals(
            partyContent.ItemKey,
            localContent.ItemKey,
            StringComparison.Ordinal) &&
        string.Equals(
            partyContent.UnitKind ?? string.Empty,
            localContent.UnitKind ?? string.Empty,
            StringComparison.OrdinalIgnoreCase) &&
        (partyContent.SeasonNumber ?? 0) ==
        (localContent.SeasonNumber ?? 0) &&
        DisplayNumbersMatch(
            partyContent.DisplayNumber,
            localContent.DisplayNumber);

    /// <summary>
    /// Chapter/episode numbers compare numerically when both sides are
    /// numbers, so <c>1</c> and <c>01</c> are the same episode — the rule the
    /// dynamic chapter matcher already applies elsewhere.
    /// </summary>
    private static bool DisplayNumbersMatch(string? left, string? right)
    {
        var normalizedLeft = (left ?? string.Empty).Trim();
        var normalizedRight = (right ?? string.Empty).Trim();
        if (decimal.TryParse(
                normalizedLeft,
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out var leftNumber) &&
            decimal.TryParse(
                normalizedRight,
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out var rightNumber))
        {
            return leftNumber == rightNumber;
        }

        return string.Equals(
            normalizedLeft,
            normalizedRight,
            StringComparison.OrdinalIgnoreCase);
    }
}
