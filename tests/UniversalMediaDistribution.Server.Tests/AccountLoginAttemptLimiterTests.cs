using UniversalMediaDistribution.Server;

namespace UniversalMediaDistribution.Server.Tests;

public sealed class AccountLoginAttemptLimiterTests
{
    [Fact]
    public void RepeatedAccountFailuresAreBlockedAndSuccessClearsOnlyThatAccount()
    {
        var clock = new ManualTimeProvider(
            new DateTimeOffset(
                2026,
                7,
                24,
                12,
                0,
                0,
                TimeSpan.Zero));
        var limiter = new AccountLoginAttemptLimiter(
            accountFailureLimit: 3,
            addressFailureLimit: 10,
            window: TimeSpan.FromMinutes(5),
            timeProvider: clock);

        for (var index = 0; index < 3; index++)
        {
            Assert.False(limiter.IsBlocked(
                "2001:db8::1",
                "alice",
                out _));
            limiter.RecordFailure("2001:db8::1", "alice");
        }

        Assert.True(limiter.IsBlocked(
            "2001:db8::1",
            "alice",
            out var retryAfter));
        Assert.Equal(TimeSpan.FromMinutes(5), retryAfter);
        Assert.False(limiter.IsBlocked(
            "2001:db8::1",
            "bob",
            out _));

        limiter.RecordSuccess("2001:db8::1", "alice");

        Assert.False(limiter.IsBlocked(
            "2001:db8::1",
            "alice",
            out _));
    }

    [Fact]
    public void AddressLimitCannotBeResetByAValidLoginAndExpires()
    {
        var clock = new ManualTimeProvider(
            DateTimeOffset.Parse("2026-07-24T12:00:00Z"));
        var limiter = new AccountLoginAttemptLimiter(
            accountFailureLimit: 3,
            addressFailureLimit: 5,
            window: TimeSpan.FromMinutes(5),
            timeProvider: clock);

        for (var index = 0; index < 5; index++)
        {
            limiter.RecordFailure(
                "192.0.2.10",
                $"account-{index}");
        }

        limiter.RecordSuccess("192.0.2.10", "account-0");

        Assert.True(limiter.IsBlocked(
            "192.0.2.10",
            "unrelated",
            out _));

        clock.Advance(TimeSpan.FromMinutes(5));

        Assert.False(limiter.IsBlocked(
            "192.0.2.10",
            "unrelated",
            out _));
    }

    private sealed class ManualTimeProvider(
        DateTimeOffset current) : TimeProvider
    {
        private DateTimeOffset _current = current;

        public override DateTimeOffset GetUtcNow() => _current;

        public void Advance(TimeSpan value) =>
            _current = _current.Add(value);
    }
}
