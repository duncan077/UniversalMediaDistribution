using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.Server;

internal sealed record WatchPartyContentRequest(
    string MediaType,
    string Name,
    string? Title,
    string? UnitKind,
    int? SeasonNumber,
    string? DisplayNumber);

internal sealed record WatchPartyCreateRequest(
    WatchPartyContentRequest? Content,
    double PositionSeconds);

internal sealed record WatchPartyJoinRequest(string JoinCode);

internal sealed record WatchPartyContentResponse(
    string ItemKey,
    string MediaType,
    string Name,
    string Title,
    string? UnitKind,
    int? SeasonNumber,
    string? DisplayNumber);

internal sealed record WatchPartyPlaybackResponse(
    bool IsPlaying,
    double PositionSeconds,
    DateTimeOffset UpdatedAt,
    string UpdatedByDisplayName);

internal sealed record WatchPartyMemberResponse(
    Guid AccountId,
    string DisplayName,
    bool IsHost,
    bool IsConnected,
    bool IsBuffering);

internal sealed record WatchPartyStateResponse(
    Guid PartyId,
    string JoinCode,
    Guid HostAccountId,
    WatchPartyMemberResponse[] Members,
    WatchPartyContentResponse? Content,
    WatchPartyPlaybackResponse Playback,
    DateTimeOffset CreatedAt,
    bool HostControlsOnly);

internal sealed record WatchPartyOptionsRequest(bool HostControlsOnly);

/// <summary>
/// One realtime message from a member. <c>play</c>, <c>pause</c>, and
/// <c>seek</c> are transport commands any member may send; <c>position</c> is
/// the host's periodic heartbeat that keeps the shared timeline honest; and
/// <c>content</c> is the host declaring what the party is watching now.
/// </summary>
internal sealed record WatchPartyClientMessage(
    string Type,
    double? PositionSeconds,
    WatchPartyContentRequest? Content,
    bool? IsBuffering);

/// <summary>
/// Every server-to-member message is the complete party state plus the reason
/// it changed, so a member that misses one message is fully caught up by the
/// next rather than replaying a command log.
/// </summary>
internal sealed record WatchPartyServerMessage(
    string Reason,
    long Sequence,
    Guid? InitiatedByAccountId,
    string? InitiatedByDisplayName,
    WatchPartyStateResponse? State);

internal sealed class WatchPartyConflictException(string message) :
    Exception(message);

/// <summary>
/// Live watch-party rooms. Deliberately in-memory: a party is a session
/// between people who are present, not durable state, so a server restart
/// ending every party is the honest outcome; each client notices its socket
/// close and tells its viewer.
/// </summary>
internal sealed class WatchPartyRegistry
{
    /// <summary>
    /// Unambiguous join-code alphabet: no 0/O, 1/I/L pairs to read out loud
    /// or type from a television remote.
    /// </summary>
    private const string JoinCodeAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    private const int JoinCodeLength = 6;
    private const int MaximumMembers = 16;
    private const int MaximumMessageBytes = 16 * 1024;
    internal static readonly TimeSpan IdlePartyLifetime =
        TimeSpan.FromHours(6);

    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, PartyRoom> _partiesById = [];
    private readonly Dictionary<string, Guid> _partyIdsByJoinCode =
        new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, Guid> _partyIdsByAccountId = [];
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _sendTimeout;

    public WatchPartyRegistry(TimeProvider? timeProvider = null, TimeSpan? sendTimeout = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _sendTimeout = sendTimeout ?? TimeSpan.FromSeconds(10);
        if (_sendTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(sendTimeout));
    }

    public WatchPartyStateResponse Create(
        AccountRecord account,
        WatchPartyCreateRequest request)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(request);
        var content = ValidateContent(request.Content);
        var now = _timeProvider.GetUtcNow();
        lock (_gate)
        {
            RemoveExpiredParties(now);
            LeaveCore(account.AccountId, now);
            var party = new PartyRoom(
                Guid.CreateVersion7(),
                GenerateJoinCode(),
                account.AccountId,
                now)
            {
                Content = content,
                Playback = new PlaybackState(
                    IsPlaying: false,
                    Math.Max(0, request.PositionSeconds),
                    now,
                    account.DisplayName)
            };
            party.Members[account.AccountId] = new PartyMember(
                account.AccountId,
                account.DisplayName);
            _partiesById[party.PartyId] = party;
            _partyIdsByJoinCode[party.JoinCode] = party.PartyId;
            _partyIdsByAccountId[account.AccountId] = party.PartyId;
            return ToState(party);
        }
    }

    public WatchPartyStateResponse? Join(
        AccountRecord account,
        string joinCode)
    {
        ArgumentNullException.ThrowIfNull(account);
        var normalizedCode = NormalizeJoinCode(joinCode);
        if (normalizedCode is null)
        {
            return null;
        }

        var now = _timeProvider.GetUtcNow();
        PartyRoom party;
        lock (_gate)
        {
            RemoveExpiredParties(now);
            if (!_partyIdsByJoinCode.TryGetValue(
                    normalizedCode,
                    out var partyId) ||
                !_partiesById.TryGetValue(partyId, out party!))
            {
                return null;
            }

            if (!party.Members.ContainsKey(account.AccountId))
            {
                if (party.Members.Count >= MaximumMembers)
                {
                    throw new WatchPartyConflictException(
                        "The watch party is full.");
                }

                LeaveCore(account.AccountId, now);
                party.Members[account.AccountId] = new PartyMember(
                    account.AccountId,
                    account.DisplayName);
            }

            _partyIdsByAccountId[account.AccountId] = party.PartyId;
            party.LastActivityAt = now;
            party.Sequence++;
        }

        BroadcastState(party, "joined", account.AccountId, account.DisplayName);
        return SnapshotState(party);
    }

    public WatchPartyStateResponse? Get(Guid accountId)
    {
        lock (_gate)
        {
            RemoveExpiredParties(_timeProvider.GetUtcNow());
            return TryGetParty(accountId, out var party)
                ? ToState(party)
                : null;
        }
    }

    /// <summary>
    /// Locks transport control to the host, or unlocks it. Only the host may
    /// change it; null when this account is not in a party, and a conflict
    /// when it is not the host's to set.
    /// </summary>
    public WatchPartyStateResponse? SetOptions(
        Guid accountId,
        WatchPartyOptionsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        PartyRoom party;
        lock (_gate)
        {
            if (!TryGetParty(accountId, out party!))
            {
                return null;
            }

            if (party.HostAccountId != accountId)
            {
                throw new WatchPartyConflictException(
                    "Only the host can change how the watch party is " +
                    "controlled.");
            }

            if (party.HostControlsOnly == request.HostControlsOnly)
            {
                return ToState(party);
            }

            party.HostControlsOnly = request.HostControlsOnly;
            party.LastActivityAt = _timeProvider.GetUtcNow();
            party.Sequence++;
        }

        BroadcastState(party, "options", accountId, null);
        return SnapshotState(party);
    }

    public bool Leave(Guid accountId)
    {
        PartyRoom? party;
        string? displayName;
        lock (_gate)
        {
            if (!TryGetParty(accountId, out party))
            {
                return false;
            }

            displayName = party.Members.TryGetValue(
                accountId,
                out var member)
                ? member.DisplayName
                : null;
            LeaveCore(accountId, _timeProvider.GetUtcNow());
            if (!_partiesById.ContainsKey(party.PartyId))
            {
                party = null;
            }
        }

        if (party is not null)
        {
            BroadcastState(party, "left", accountId, displayName);
        }

        return true;
    }

    /// <summary>
    /// Owns one member socket from accept to close: registers it, replays the
    /// current state so the member starts synchronized, then relays messages
    /// until either side hangs up.
    /// </summary>
    public async Task ConnectAsync(
        AccountRecord account,
        WebSocket socket,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(socket);
        PartyRoom? party;
        PartyConnection connection;
        lock (_gate)
        {
            if (!TryGetParty(account.AccountId, out party))
            {
                party = null;
            }
        }

        if (party is null)
        {
            await socket.CloseAsync(
                WebSocketCloseStatus.PolicyViolation,
                "Join a watch party before connecting.",
                cancellationToken);
            return;
        }

        lock (_gate)
        {
            connection = new PartyConnection(account.AccountId, socket,
                cancellationToken, _sendTimeout);
            party.Connections.Add(connection);
            party.Sequence++;
            party.LastActivityAt = _timeProvider.GetUtcNow();
        }

        BroadcastState(party, "connected", account.AccountId, account.DisplayName);
        try
        {
            await ReceiveLoopAsync(
                party,
                account,
                connection,
                connection.CancellationToken);
        }
        catch (OperationCanceledException) when (connection.CancellationToken.IsCancellationRequested)
        {
        }
        catch (WebSocketException) when (connection.CancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            await connection.DisposeAsync();
            bool stillMember;
            lock (_gate)
            {
                party.Connections.Remove(connection);
                stillMember = party.Members.ContainsKey(account.AccountId) &&
                    _partiesById.ContainsKey(party.PartyId);
                if (stillMember)
                {
                    // A member whose socket dropped cannot report that their
                    // buffer refilled, so their stall must not outlive the
                    // connection and hold the party paused.
                    if (party.Members.TryGetValue(
                            account.AccountId,
                            out var member) &&
                        member.IsBuffering)
                    {
                        member.IsBuffering = false;
                        ResumeIfNobodyIsBuffering(
                            party,
                            _timeProvider.GetUtcNow());
                    }

                    party.Sequence++;
                }
            }

            if (stillMember)
            {
                BroadcastState(party, "disconnected", account.AccountId, account.DisplayName);
            }
        }
    }

    private async Task ReceiveLoopAsync(
        PartyRoom party,
        AccountRecord account,
        PartyConnection connection,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[8 * 1024];
        using var message = new MemoryStream();
        while (connection.Socket.State is WebSocketState.Open &&
               !cancellationToken.IsCancellationRequested)
        {
            message.SetLength(0);
            ValueWebSocketReceiveResult received;
            do
            {
                received = await connection.Socket.ReceiveAsync(
                    buffer.AsMemory(),
                    cancellationToken);
                if (received.MessageType is WebSocketMessageType.Close)
                {
                    return;
                }

                message.Write(buffer.AsSpan(0, received.Count));
                if (message.Length > MaximumMessageBytes)
                {
                    await connection.Socket.CloseAsync(
                        WebSocketCloseStatus.MessageTooBig,
                        "The watch-party message is too large.",
                        cancellationToken);
                    return;
                }
            }
            while (!received.EndOfMessage);

            WatchPartyClientMessage? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize(
                    message.GetBuffer().AsSpan(0, (int)message.Length),
                    ServerJsonSerializerContext.Default
                        .WatchPartyClientMessage);
            }
            catch (JsonException)
            {
                continue;
            }

            if (parsed is not null)
            {
                HandleMessage(party, account, parsed);
            }
        }
    }

    private void HandleMessage(
        PartyRoom party,
        AccountRecord account,
        WatchPartyClientMessage message)
    {
        var now = _timeProvider.GetUtcNow();
        string reason;
        lock (_gate)
        {
            if (!_partiesById.ContainsKey(party.PartyId) ||
                !party.Members.ContainsKey(account.AccountId))
            {
                return;
            }

            var isHost = party.HostAccountId == account.AccountId;

            // A locked party takes transport only from its host; everything
            // else a member sends still applies to themselves.
            if (party.HostControlsOnly &&
                !isHost &&
                message.Type is "play" or "pause" or "seek")
            {
                return;
            }

            switch (message.Type)
            {
                case "play":
                case "pause":
                    // An explicit transport action replaces the automatic
                    // buffering pause; recovery must not undo a manual pause.
                    party.PausedForBuffering = false;
                    party.Playback = new PlaybackState(
                        message.Type is "play",
                        ClampPosition(
                            message.PositionSeconds ??
                            ExpectedPosition(party.Playback, now)),
                        now,
                        account.DisplayName);
                    reason = message.Type;
                    break;
                case "seek" when message.PositionSeconds is not null:
                    party.Playback = party.Playback with
                    {
                        PositionSeconds =
                            ClampPosition(message.PositionSeconds.Value),
                        UpdatedAt = now,
                        UpdatedByDisplayName = account.DisplayName
                    };
                    reason = "seek";
                    break;
                case "buffering" when message.IsBuffering is { } buffering:
                    if (!party.Members.TryGetValue(
                            account.AccountId,
                            out var bufferingMember) ||
                        bufferingMember.IsBuffering == buffering)
                    {
                        return;
                    }

                    bufferingMember.IsBuffering = buffering;

                    // Everyone waits for the member who stalled, and resumes
                    // when they are ready — but only from a pause this
                    // stalling caused, so a party deliberately paused stays
                    // paused when their buffer refills.
                    if (buffering && party.Playback.IsPlaying)
                    {
                        party.PausedForBuffering = true;
                        party.Playback = new PlaybackState(
                            IsPlaying: false,
                            ClampPosition(
                                ExpectedPosition(party.Playback, now)),
                            now,
                            account.DisplayName);
                    }
                    else if (!buffering &&
                             party.PausedForBuffering &&
                             !party.Members.Values.Any(
                                 member => member.IsBuffering))
                    {
                        party.PausedForBuffering = false;
                        party.Playback = party.Playback with
                        {
                            IsPlaying = true,
                            UpdatedAt = now,
                            UpdatedByDisplayName = account.DisplayName
                        };
                    }

                    reason = "buffering";
                    break;
                case "position" when
                    isHost && message.PositionSeconds is not null:
                    party.Playback = party.Playback with
                    {
                        PositionSeconds =
                            ClampPosition(message.PositionSeconds.Value),
                        UpdatedAt = now
                    };
                    reason = "position";
                    break;
                case "content" when isHost && message.Content is not null:
                    ContentState content;
                    try
                    {
                        content = ValidateContent(message.Content)!;
                    }
                    catch (ArgumentException)
                    {
                        return;
                    }

                    party.Content = content;
                    party.Playback = new PlaybackState(
                        IsPlaying: true,
                        ClampPosition(message.PositionSeconds ?? 0),
                        now,
                        account.DisplayName);
                    reason = "content";
                    break;
                default:
                    return;
            }

            party.Sequence++;
            party.LastActivityAt = now;
        }

        BroadcastState(party, reason, account.AccountId, account.DisplayName);
    }

    /// <summary>
    /// Where the shared timeline stands right now: the stored position plus
    /// however long it has been running since the position was stored.
    /// </summary>
    internal static double ExpectedPosition(
        PlaybackState playback,
        DateTimeOffset now) =>
        playback.IsPlaying
            ? playback.PositionSeconds +
              Math.Max(0, (now - playback.UpdatedAt).TotalSeconds)
            : playback.PositionSeconds;

    private void BroadcastState(
        PartyRoom party,
        string reason,
        Guid? initiatedByAccountId,
        string? initiatedByDisplayName)
    {
        WatchPartyServerMessage payload;
        PartyConnection[] connections;
        lock (_gate)
        {
            payload = new WatchPartyServerMessage(
                reason,
                party.Sequence,
                initiatedByAccountId,
                initiatedByDisplayName,
                ToState(party));
            connections = [.. party.Connections];
        }

        foreach (var connection in connections)
        {
            connection.Queue(payload);
        }
    }

    private WatchPartyStateResponse SnapshotState(PartyRoom party)
    {
        lock (_gate)
        {
            return ToState(party);
        }
    }

    /// <summary>Callers must hold <see cref="_gate"/>.</summary>
    private WatchPartyStateResponse ToState(PartyRoom party) =>
        new(
            party.PartyId,
            party.JoinCode,
            party.HostAccountId,
            party.Members.Values
                .OrderBy(member => member.JoinedAtOrder)
                .Select(member => new WatchPartyMemberResponse(
                    member.AccountId,
                    member.DisplayName,
                    member.AccountId == party.HostAccountId,
                    party.Connections.Any(connection =>
                        connection.AccountId == member.AccountId),
                    member.IsBuffering))
                .ToArray(),
            party.Content is { } content
                ? new WatchPartyContentResponse(
                    content.ItemKey,
                    content.MediaType,
                    content.Name,
                    content.Title,
                    content.UnitKind,
                    content.SeasonNumber,
                    content.DisplayNumber)
                : null,
            new WatchPartyPlaybackResponse(
                party.Playback.IsPlaying,
                party.Playback.PositionSeconds,
                party.Playback.UpdatedAt,
                party.Playback.UpdatedByDisplayName),
            party.CreatedAt,
            party.HostControlsOnly);

    /// <summary>Callers must hold <see cref="_gate"/>.</summary>
    private void LeaveCore(Guid accountId, DateTimeOffset now)
    {
        if (!_partyIdsByAccountId.Remove(accountId, out var partyId) ||
            !_partiesById.TryGetValue(partyId, out var party))
        {
            return;
        }

        party.Members.Remove(accountId);
        foreach (var connection in party.Connections
                     .Where(connection => connection.AccountId == accountId)
                     .ToArray())
        {
            party.Connections.Remove(connection);
            _ = CloseAsync(connection);
        }

        // Whoever is left must not be held by a member who is gone: leaving
        // while stalled would otherwise pause the party permanently.
        ResumeIfNobodyIsBuffering(party, now);

        if (party.Members.Count == 0)
        {
            _partiesById.Remove(party.PartyId);
            _partyIdsByJoinCode.Remove(party.JoinCode);
            return;
        }

        if (party.HostAccountId == accountId)
        {
            // The longest-standing remaining member inherits the room, so the
            // party survives its creator stepping out.
            party.HostAccountId = party.Members.Values
                .OrderBy(member => member.JoinedAtOrder)
                .First()
                .AccountId;
        }

        party.Sequence++;
        party.LastActivityAt = now;
    }

    private static async Task CloseAsync(PartyConnection connection)
    {
        try
        {
            connection.Stop();
            await connection.SendTask;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await connection.Socket.CloseAsync(
                WebSocketCloseStatus.NormalClosure,
                "You left the watch party.",
                timeout.Token);
        }
        catch (Exception)
        {
            connection.Socket.Abort();
        }
    }

    /// <summary>
    /// Lifts a buffering pause once nobody is stalled any more.
    /// Callers must hold <see cref="_gate"/>.
    /// </summary>
    private static void ResumeIfNobodyIsBuffering(
        PartyRoom party,
        DateTimeOffset now)
    {
        if (!party.PausedForBuffering ||
            party.Members.Values.Any(member => member.IsBuffering))
        {
            return;
        }

        party.PausedForBuffering = false;
        party.Playback = party.Playback with
        {
            IsPlaying = true,
            UpdatedAt = now
        };
    }

    /// <summary>Callers must hold <see cref="_gate"/>.</summary>
    private bool TryGetParty(Guid accountId, out PartyRoom party)
    {
        party = null!;
        return _partyIdsByAccountId.TryGetValue(accountId, out var partyId) &&
               _partiesById.TryGetValue(partyId, out party!);
    }

    /// <summary>Callers must hold <see cref="_gate"/>.</summary>
    private void RemoveExpiredParties(DateTimeOffset now)
    {
        foreach (var party in _partiesById.Values
                     .Where(party =>
                         party.Connections.Count == 0 &&
                         now - party.LastActivityAt > IdlePartyLifetime)
                     .ToArray())
        {
            _partiesById.Remove(party.PartyId);
            _partyIdsByJoinCode.Remove(party.JoinCode);
            foreach (var member in party.Members.Keys)
            {
                _partyIdsByAccountId.Remove(member);
            }
        }
    }

    private string GenerateJoinCode()
    {
        Span<char> code = stackalloc char[JoinCodeLength];
        while (true)
        {
            for (var index = 0; index < code.Length; index++)
            {
                code[index] = JoinCodeAlphabet[
                    RandomNumberGenerator.GetInt32(JoinCodeAlphabet.Length)];
            }

            var candidate = new string(code);
            if (!_partyIdsByJoinCode.ContainsKey(candidate))
            {
                return candidate;
            }
        }
    }

    private static string? NormalizeJoinCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim().ToUpperInvariant();
        return normalized.Length == JoinCodeLength &&
               normalized.All(JoinCodeAlphabet.Contains)
            ? normalized
            : null;
    }

    private static double ClampPosition(double value) =>
        double.IsFinite(value)
            ? Math.Clamp(value, 0, TimeSpan.FromDays(2).TotalSeconds)
            : 0;

    private static ContentState? ValidateContent(
        WatchPartyContentRequest? content)
    {
        if (content is null)
        {
            return null;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(content.MediaType);
        ArgumentException.ThrowIfNullOrWhiteSpace(content.Name);
        if (content.Name.Length > 512 ||
            content.Title?.Length > 512 ||
            content.UnitKind?.Length > 32 ||
            content.DisplayNumber?.Length > 32 ||
            content.SeasonNumber is < 0 or > 10_000)
        {
            throw new ArgumentException(
                "The watch-party content is invalid.",
                nameof(content));
        }

        // The same key rule both installations already agree on for friend
        // progress, so two members' catalogs identify the title identically.
        var itemKey = ContentItemKey.Create(
            content.MediaType,
            content.Name);
        return new ContentState(
            itemKey,
            content.MediaType.Trim().ToLowerInvariant(),
            content.Name.Trim(),
            string.IsNullOrWhiteSpace(content.Title)
                ? content.Name.Trim()
                : content.Title.Trim(),
            NormalizeOptional(content.UnitKind),
            content.SeasonNumber,
            NormalizeOptional(content.DisplayNumber));
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    internal sealed record ContentState(
        string ItemKey,
        string MediaType,
        string Name,
        string Title,
        string? UnitKind,
        int? SeasonNumber,
        string? DisplayNumber);

    internal sealed record PlaybackState(
        bool IsPlaying,
        double PositionSeconds,
        DateTimeOffset UpdatedAt,
        string UpdatedByDisplayName);

    private sealed class PartyMember(Guid accountId, string displayName)
    {
        private static long _joinCounter;

        public Guid AccountId { get; } = accountId;

        public string DisplayName { get; } = displayName;

        /// <summary>
        /// Host succession needs "who has been here longest", and a counter
        /// orders two members who joined within one clock tick.
        /// </summary>
        public long JoinedAtOrder { get; } =
            Interlocked.Increment(ref _joinCounter);

        /// <summary>
        /// Whether this member's stream is refilling. The party waits for a
        /// member who stalls rather than running on without them.
        /// </summary>
        public bool IsBuffering { get; set; }
    }

    private sealed class PartyConnection : IAsyncDisposable
    {
        private readonly Lock _queueGate = new();
        private readonly Channel<WatchPartyServerMessage> _pending = Channel.CreateBounded<WatchPartyServerMessage>(
            new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                AllowSynchronousContinuations = false
            });
        private readonly CancellationTokenSource _lifetime;
        private long _latestSequence = -1;
        private bool _stopped;

        public PartyConnection(Guid accountId, WebSocket socket,
            CancellationToken cancellationToken, TimeSpan sendTimeout)
        {
            AccountId = accountId;
            Socket = socket;
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            CancellationToken = _lifetime.Token;
            SendTask = SendLoopAsync(sendTimeout);
        }

        public Guid AccountId { get; }
        public WebSocket Socket { get; }
        public CancellationToken CancellationToken { get; }
        public Task SendTask { get; }

        public void Queue(WatchPartyServerMessage payload)
        {
            lock (_queueGate)
            {
                // Concurrent broadcasts can reach a connection out of order.
                if (_stopped || payload.Sequence <= _latestSequence) return;
                _latestSequence = payload.Sequence;
                _pending.Writer.TryWrite(payload);
            }
        }

        public void Stop()
        {
            lock (_queueGate)
            {
                if (_stopped) return;
                _stopped = true;
                _pending.Writer.TryComplete();
                _lifetime.Cancel();
            }
        }

        private async Task SendLoopAsync(TimeSpan sendTimeout)
        {
            var lastSentSequence = -1L;
            try
            {
                await foreach (var payload in _pending.Reader.ReadAllAsync(CancellationToken))
                {
                    if (Socket.State != WebSocketState.Open) break;
                    // Older clients apply transport only for playback events.
                    // A replaced pause followed by "joined" must still pause
                    // them, even if that latest event was their own action.
                    var outgoing = lastSentSequence < 0 || payload.Sequence > lastSentSequence + 1
                        ? payload with
                        {
                            Reason = "position",
                            InitiatedByAccountId = null,
                            InitiatedByDisplayName = null
                        }
                        : payload;
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(outgoing,
                        ServerJsonSerializerContext.Default.WatchPartyServerMessage);
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
                    deadline.CancelAfter(sendTimeout);
                    await Socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text,
                        endOfMessage: true, deadline.Token);
                    lastSentSequence = payload.Sequence;
                }
            }
            catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception)
            {
                // A timed-out send must also unblock the receive loop, so the
                // connection is removed and its buffering state is cleared.
                Stop();
                Socket.Abort();
            }
            finally
            {
                Stop();
                while (_pending.Reader.TryRead(out _)) { }
            }
        }

        public async ValueTask DisposeAsync()
        {
            Stop();
            await SendTask;
            lock (_queueGate) _lifetime.Dispose();
        }
    }

    private sealed class PartyRoom(
        Guid partyId,
        string joinCode,
        Guid hostAccountId,
        DateTimeOffset createdAt)
    {
        public Guid PartyId { get; } = partyId;

        public string JoinCode { get; } = joinCode;

        public Guid HostAccountId { get; set; } = hostAccountId;

        public DateTimeOffset CreatedAt { get; } = createdAt;

        public DateTimeOffset LastActivityAt { get; set; } = createdAt;

        public long Sequence { get; set; }

        /// <summary>
        /// Whether only the host may pause, resume, and seek for everyone.
        /// </summary>
        public bool HostControlsOnly { get; set; }

        /// <summary>
        /// Whether the current pause came from a member stalling rather than
        /// from someone choosing it. Only such a pause resumes on its own.
        /// </summary>
        public bool PausedForBuffering { get; set; }

        public ContentState? Content { get; set; }

        public PlaybackState Playback { get; set; } = new(
            IsPlaying: false,
            PositionSeconds: 0,
            createdAt,
            UpdatedByDisplayName: string.Empty);

        public Dictionary<Guid, PartyMember> Members { get; } = [];

        public List<PartyConnection> Connections { get; } = [];
    }
}
