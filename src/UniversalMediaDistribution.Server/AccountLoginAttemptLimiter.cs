using System.Security.Cryptography;
using System.Text;

namespace UniversalMediaDistribution.Server;

internal sealed class AccountLoginAttemptLimiter
{
    public const int DefaultAccountFailureLimit = 8;
    public const int DefaultAddressFailureLimit = 40;
    public static readonly TimeSpan DefaultWindow =
        TimeSpan.FromMinutes(10);

    private const int MaximumTrackedKeys = 50_000;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, FailureWindow> _failures =
        new(StringComparer.Ordinal);
    private readonly int _accountFailureLimit;
    private readonly int _addressFailureLimit;
    private readonly TimeSpan _window;
    private readonly TimeProvider _timeProvider;
    private int _operations;

    public AccountLoginAttemptLimiter(
        int accountFailureLimit = DefaultAccountFailureLimit,
        int addressFailureLimit = DefaultAddressFailureLimit,
        TimeSpan? window = null,
        TimeProvider? timeProvider = null)
    {
        if (accountFailureLimit < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(accountFailureLimit));
        }

        if (addressFailureLimit < accountFailureLimit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(addressFailureLimit));
        }

        _window = window ?? DefaultWindow;
        if (_window <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(window));
        }

        _accountFailureLimit = accountFailureLimit;
        _addressFailureLimit = addressFailureLimit;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool IsBlocked(
        string address,
        string normalizedUsername,
        out TimeSpan retryAfter)
    {
        var now = _timeProvider.GetUtcNow();
        var addressKey = CreateKey("address", address);
        var accountKey = CreateKey(
            "account",
            address + "\n" + normalizedUsername);

        lock (_gate)
        {
            PruneIfNeeded(now);
            var addressRetry = GetRetryAfter(
                addressKey,
                _addressFailureLimit,
                now);
            var accountRetry = GetRetryAfter(
                accountKey,
                _accountFailureLimit,
                now);
            retryAfter = addressRetry > accountRetry
                ? addressRetry
                : accountRetry;
            return retryAfter > TimeSpan.Zero;
        }
    }

    public void RecordFailure(
        string address,
        string normalizedUsername)
    {
        var now = _timeProvider.GetUtcNow();
        lock (_gate)
        {
            PruneIfNeeded(now);
            Increment(
                CreateKey("address", address),
                now);
            Increment(
                CreateKey(
                    "account",
                    address + "\n" + normalizedUsername),
                now);
        }
    }

    public void RecordSuccess(
        string address,
        string normalizedUsername)
    {
        lock (_gate)
        {
            _failures.Remove(
                CreateKey(
                    "account",
                    address + "\n" + normalizedUsername));
        }
    }

    private TimeSpan GetRetryAfter(
        string key,
        int failureLimit,
        DateTimeOffset now)
    {
        if (!_failures.TryGetValue(key, out var state))
        {
            return TimeSpan.Zero;
        }

        if (now - state.StartedAt >= _window)
        {
            _failures.Remove(key);
            return TimeSpan.Zero;
        }

        return state.Failures >= failureLimit
            ? state.StartedAt + _window - now
            : TimeSpan.Zero;
    }

    private void Increment(
        string key,
        DateTimeOffset now)
    {
        if (_failures.TryGetValue(key, out var state) &&
            now - state.StartedAt < _window)
        {
            _failures[key] = state with
            {
                Failures = state.Failures + 1
            };
            return;
        }

        if (_failures.Count >= MaximumTrackedKeys)
        {
            RemoveOldest();
        }

        _failures[key] = new FailureWindow(1, now);
    }

    private void PruneIfNeeded(DateTimeOffset now)
    {
        _operations++;
        if ((_operations & 255) != 0 &&
            _failures.Count < MaximumTrackedKeys)
        {
            return;
        }

        foreach (var key in _failures
                     .Where(pair =>
                         now - pair.Value.StartedAt >= _window)
                     .Select(static pair => pair.Key)
                     .ToArray())
        {
            _failures.Remove(key);
        }
    }

    private void RemoveOldest()
    {
        var oldest = _failures.MinBy(
            static pair => pair.Value.StartedAt);
        if (!string.IsNullOrEmpty(oldest.Key))
        {
            _failures.Remove(oldest.Key);
        }
    }

    private static string CreateKey(
        string scope,
        string value)
    {
        var bytes = Encoding.UTF8.GetBytes(
            scope + "\n" + value);
        try
        {
            return Convert.ToHexString(
                SHA256.HashData(bytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private sealed record FailureWindow(
        int Failures,
        DateTimeOffset StartedAt);
}
