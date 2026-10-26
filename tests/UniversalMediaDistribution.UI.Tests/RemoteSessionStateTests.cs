using UniversalMediaDistribution.Catalog.Sync;

namespace UniversalMediaDistribution.UI.Tests;

public sealed class RemoteSessionStateTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AFreshSessionIsUsable() =>
        Assert.True(Create().IsUsable(Now));

    [Fact]
    public void AnExpiredSessionIsNotUsedAndCostsNoRequest() =>
        Assert.False(
            (Create() with { ExpiresAt = Now.AddSeconds(-1) }).IsUsable(Now));

    [Fact]
    public void ASessionWithoutAnAccountIsRejected() =>
        Assert.False(
            (Create() with { AccountId = Guid.Empty }).IsUsable(Now));

    [Fact]
    public void ASuspiciouslyShortTokenIsRejected() =>
        Assert.False((Create() with { AccessToken = "abc" }).IsUsable(Now));

    [Theory]
    [InlineData("http://media.example.com/")]
    [InlineData("ftp://media.example.com/")]
    [InlineData("not a url")]
    public void AnInsecureServiceUrlIsRejected(string serviceUrl) =>
        Assert.False((Create() with { ServiceUrl = serviceUrl }).IsUsable(Now));

    [Fact]
    public void LoopbackHttpStaysUsableForLocalDevelopment() =>
        Assert.True(
            (Create() with { ServiceUrl = "http://127.0.0.1:5123/" })
                .IsUsable(Now));

    [Fact]
    public async Task TheInMemoryStoreForgetsASessionWhenGivenNull()
    {
        var store = new InMemoryRemoteSessionStore();

        await store.SaveAsync(Create());
        Assert.NotNull(await store.LoadAsync());

        await store.SaveAsync(null);
        Assert.Null(await store.LoadAsync());
    }

    private static RemoteSessionState Create() =>
        new()
        {
            ServiceUrl = "https://media.example.com/",
            AccessToken = new string('t', 43),
            AccountId = Guid.CreateVersion7(),
            Username = "alice",
            DisplayName = "Alice",
            ExpiresAt = Now.AddDays(30)
        };
}
