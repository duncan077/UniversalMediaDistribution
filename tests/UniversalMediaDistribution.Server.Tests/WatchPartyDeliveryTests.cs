using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;

namespace UniversalMediaDistribution.Server.Tests;

public sealed class WatchPartyDeliveryTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(3);

    [Fact]
    public async Task SlowClientGetsOnlyTheLatestPendingSnapshot()
    {
        var registry = new WatchPartyRegistry();
        var account = CreateAccount();
        registry.Create(account, new WatchPartyCreateRequest(null, 0));
        using var socket = new TestSocket();
        using var lifetime = new CancellationTokenSource(TestTimeout);
        var connection = registry.ConnectAsync(account, socket, lifetime.Token);
        await socket.FirstSendStarted.Task.WaitAsync(TestTimeout);
        for (var index = 0; index < 100; index++)
            registry.SetOptions(account.AccountId, new WatchPartyOptionsRequest(index % 2 == 0));

        socket.ReleaseSend.TrySetResult();
        var first = await socket.ReadSentAsync();
        var latest = await socket.ReadSentAsync();
        Assert.Equal(1, first.Sequence);
        Assert.Equal(101, latest.Sequence);
        Assert.False(latest.State!.HostControlsOnly);
        Assert.Equal("position", latest.Reason);
        Assert.Null(latest.InitiatedByAccountId);
        socket.CloseIncoming();
        await connection.WaitAsync(TestTimeout);
        Assert.Equal(2, socket.CompletedSends);
        Assert.False(Assert.Single(registry.Get(account.AccountId)!.Members).IsConnected);
    }

    [Fact]
    public async Task SendTimeoutAbortsSocketAndRemovesConnection()
    {
        var registry = new WatchPartyRegistry(sendTimeout: TimeSpan.FromMilliseconds(50));
        var account = CreateAccount();
        registry.Create(account, new WatchPartyCreateRequest(null, 0));
        using var socket = new TestSocket();
        using var lifetime = new CancellationTokenSource(TestTimeout);
        await registry.ConnectAsync(account, socket, lifetime.Token).WaitAsync(TestTimeout);
        Assert.True(socket.WasAborted);
        Assert.Equal(0, socket.ActiveSends);
        Assert.False(Assert.Single(registry.Get(account.AccountId)!.Members).IsConnected);
    }

    [Fact]
    public async Task DisconnectCancelsSendAndDiscardsPendingSnapshots()
    {
        var registry = new WatchPartyRegistry();
        var account = CreateAccount();
        registry.Create(account, new WatchPartyCreateRequest(null, 0));
        using var socket = new TestSocket();
        using var lifetime = new CancellationTokenSource(TestTimeout);
        var connection = registry.ConnectAsync(account, socket, lifetime.Token);
        await socket.FirstSendStarted.Task.WaitAsync(TestTimeout);
        for (var index = 0; index < 10; index++)
            registry.SetOptions(account.AccountId, new WatchPartyOptionsRequest(index % 2 == 0));
        socket.CloseIncoming();
        await connection.WaitAsync(TestTimeout);
        Assert.Equal(0, socket.ActiveSends);
        Assert.Equal(0, socket.CompletedSends);
    }

    [Fact]
    public async Task SlowClientDoesNotDelayOtherMembers()
    {
        var registry = new WatchPartyRegistry();
        var host = CreateAccount();
        var guest = CreateAccount();
        var party = registry.Create(host, new WatchPartyCreateRequest(null, 0));
        registry.Join(guest, party.JoinCode);
        using var slow = new TestSocket();
        using var fast = new TestSocket();
        fast.ReleaseSend.TrySetResult();
        using var lifetime = new CancellationTokenSource(TestTimeout);
        var slowConnection = registry.ConnectAsync(host, slow, lifetime.Token);
        await slow.FirstSendStarted.Task.WaitAsync(TestTimeout);
        var fastConnection = registry.ConnectAsync(guest, fast, lifetime.Token);
        registry.SetOptions(host.AccountId, new WatchPartyOptionsRequest(true));
        WatchPartyServerMessage latest;
        do { latest = await fast.ReadSentAsync(); } while (!latest.State!.HostControlsOnly);
        Assert.Equal(0, slow.CompletedSends);
        slow.CloseIncoming();
        fast.CloseIncoming();
        await Task.WhenAll(slowConnection, fastConnection).WaitAsync(TestTimeout);
    }

    private static AccountRecord CreateAccount() => new(Guid.NewGuid(), "review", "Review",
        "", "", 100_000, DateTimeOffset.UtcNow);

    private sealed class TestSocket : WebSocket
    {
        private readonly Channel<byte[]> _sent = Channel.CreateUnbounded<byte[]>();
        private readonly TaskCompletionSource<WebSocketReceiveResult> _incoming =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _activeSends;
        private int _completedSends;
        private int _state = (int)WebSocketState.Open;
        public TaskCompletionSource FirstSendStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSend { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ActiveSends => Volatile.Read(ref _activeSends);
        public int CompletedSends => Volatile.Read(ref _completedSends);
        public bool WasAborted { get; private set; }
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => (WebSocketState)Volatile.Read(ref _state);
        public override string? SubProtocol => null;
        public void CloseIncoming() => _incoming.TrySetResult(new(0, WebSocketMessageType.Close, true));
        public async Task<WatchPartyServerMessage> ReadSentAsync()
        {
            var bytes = await _sent.Reader.ReadAsync().AsTask().WaitAsync(TestTimeout);
            return JsonSerializer.Deserialize(bytes, ServerJsonSerializerContext.Default.WatchPartyServerMessage)!;
        }
        public override void Abort()
        {
            WasAborted = true;
            Interlocked.Exchange(ref _state, (int)WebSocketState.Aborted);
        }
        public override void Dispose() => CloseIncoming();
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken token)
        {
            Interlocked.Exchange(ref _state, (int)WebSocketState.Closed);
            CloseIncoming();
            return Task.CompletedTask;
        }
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken token) =>
            CloseAsync(status, description, token);
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken token) =>
            _incoming.Task.WaitAsync(token);
        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken token)
        {
            Interlocked.Increment(ref _activeSends);
            FirstSendStarted.TrySetResult();
            try
            {
                await ReleaseSend.Task.WaitAsync(token);
                Interlocked.Increment(ref _completedSends);
                _sent.Writer.TryWrite(buffer.ToArray());
            }
            finally { Interlocked.Decrement(ref _activeSends); }
        }
    }
}
