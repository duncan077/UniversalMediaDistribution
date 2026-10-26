using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization.Metadata;

namespace UniversalMediaDistribution.Server;

internal sealed record AccountRegistrationRequest(
    string Username,
    string Password,
    string DisplayName,
    string DeviceName);

internal sealed record AccountLoginRequest(
    string Username,
    string Password,
    string DeviceName);

internal sealed record AdminAccountRegistrationRequest(
    string Username,
    string Password,
    string DisplayName);

internal sealed record AccountSessionResponse(
    Guid AccountId,
    string Username,
    string DisplayName,
    string AccessToken,
    DateTimeOffset ExpiresAt);

internal sealed record AccountCreatedResponse(
    Guid AccountId,
    string Username,
    string DisplayName,
    DateTimeOffset CreatedAt);

internal sealed record AccountProfileResponse(
    Guid AccountId,
    string Username,
    string DisplayName,
    DateTimeOffset CreatedAt,
    DateTimeOffset SessionExpiresAt);

internal sealed record AccountIndexRecord(Guid AccountId);

internal sealed record AccountRecord(
    Guid AccountId,
    string Username,
    string DisplayName,
    string PasswordSalt,
    string PasswordHash,
    int PasswordIterations,
    DateTimeOffset CreatedAt);

internal sealed record AccountSessionRecord(
    Guid AccountId,
    string TokenHash,
    string DeviceName,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

internal sealed record AuthenticatedAccount(
    AccountRecord Account,
    AccountSessionRecord Session);

internal sealed class AccountConflictException(string message) :
    Exception(message);

internal sealed class SqliteAccountStore
{
    public const int DefaultPasswordIterations = 310_000;
    public static readonly TimeSpan DefaultSessionLifetime =
        TimeSpan.FromDays(30);

    private static readonly byte[] DummySalt =
        SHA256.HashData("umd-account-dummy-salt"u8);

    private readonly string _accountRoot;
    private readonly string _sessionRoot;
    private readonly int _passwordIterations;
    private readonly TimeSpan _sessionLifetime;
    private readonly ServerDatabase _storage;
    private SemaphoreSlim _writeGate => _storage.WriteGate;

    public SqliteAccountStore(
        string dataDirectory,
        int passwordIterations = DefaultPasswordIterations,
        TimeSpan? sessionLifetime = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _storage = new ServerDatabase(dataDirectory);
        if (passwordIterations < 100_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(passwordIterations),
                "Password hashing must use at least 100,000 iterations.");
        }

        _accountRoot = Path.GetFullPath(
            Path.Combine(dataDirectory, "accounts"));
        _sessionRoot = Path.GetFullPath(
            Path.Combine(dataDirectory, "sessions"));
        _passwordIterations = passwordIterations;
        _sessionLifetime = sessionLifetime ?? DefaultSessionLifetime;
        if (_sessionLifetime <= TimeSpan.Zero ||
            _sessionLifetime > TimeSpan.FromDays(365))
        {
            throw new ArgumentOutOfRangeException(nameof(sessionLifetime));
        }
    }

    public async ValueTask<AccountSessionResponse> RegisterAsync(
        AccountRegistrationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var username = AccountRules.NormalizeUsername(request.Username);
        var displayName = AccountRules.NormalizeDisplayName(
            request.DisplayName);
        var deviceName = AccountRules.NormalizeDeviceName(
            request.DeviceName);
        AccountRules.ValidatePassword(request.Password);

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            using var write = _storage.BeginWrite();
            var account = await CreateAccountCoreAsync(
                username,
                request.Password,
                displayName,
                cancellationToken);
            return write.CommitAndReturn(await CreateSessionCoreAsync(
                account,
                deviceName,
                cancellationToken));
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask<AccountRecord> CreateAsync(
        AdminAccountRegistrationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var username = AccountRules.NormalizeUsername(request.Username);
        var displayName = AccountRules.NormalizeDisplayName(
            request.DisplayName);
        AccountRules.ValidatePassword(request.Password);

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            using var write = _storage.BeginWrite();
            return write.CommitAndReturn(await CreateAccountCoreAsync(
                username,
                request.Password,
                displayName,
                cancellationToken));
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask<AccountSessionResponse?> LoginAsync(
        AccountLoginRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var username = AccountRules.NormalizeUsername(request.Username);
        var deviceName = AccountRules.NormalizeDeviceName(
            request.DeviceName);
        AccountRules.ValidatePasswordShape(request.Password);

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            using var write = _storage.BeginWrite();
            var index = await _storage.ReadJsonAsync(
                GetUsernameIndexPath(username),
                ServerJsonSerializerContext.Default.AccountIndexRecord,
                cancellationToken);
            var account = index is null
                ? null
                : await _storage.ReadJsonAsync(
                    GetAccountPath(index.AccountId),
                    ServerJsonSerializerContext.Default.AccountRecord,
                    cancellationToken);
            if (account is null)
            {
                PerformDummyPasswordHash(request.Password);
                return null;
            }

            if (!VerifyPassword(account, request.Password))
            {
                return null;
            }

            return write.CommitAndReturn(await CreateSessionCoreAsync(
                account,
                deviceName,
                cancellationToken));
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask<AuthenticatedAccount?> AuthenticateAsync(
        string? accessToken,
        CancellationToken cancellationToken)
    {
        if (!AccountRules.IsAccessTokenShapeValid(accessToken))
        {
            return null;
        }

        var tokenHash = HashToken(accessToken!);
        var sessionPath = GetSessionPath(tokenHash);
        var session = await _storage.ReadJsonAsync(
            sessionPath,
            ServerJsonSerializerContext.Default.AccountSessionRecord,
            cancellationToken);
        if (session is null ||
            !string.Equals(
                session.TokenHash,
                tokenHash,
                StringComparison.Ordinal))
        {
            return null;
        }

        if (session.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            _storage.TryDelete(sessionPath);
            return null;
        }

        var account = await _storage.ReadJsonAsync(
            GetAccountPath(session.AccountId),
            ServerJsonSerializerContext.Default.AccountRecord,
            cancellationToken);
        return account is null
            ? null
            : new AuthenticatedAccount(account, session);
    }

    /// <summary>
    /// Finds an account by its exact name.
    /// <para>
    /// Exact only, over the index login already keeps. There is no prefix or
    /// substring form on purpose: adding one would turn this into a way to walk
    /// the whole account list, where exact match reveals no more than
    /// registration already does by refusing a name that is taken.
    /// </para>
    /// </summary>
    public async ValueTask<AccountRecord?> FindByUsernameAsync(
        string username,
        CancellationToken cancellationToken)
    {
        string normalized;
        try
        {
            normalized = AccountRules.NormalizeUsername(username);
        }
        catch (ArgumentException)
        {
            return null;
        }

        var index = await _storage.ReadJsonAsync(
            GetUsernameIndexPath(normalized),
            ServerJsonSerializerContext.Default.AccountIndexRecord,
            cancellationToken);
        return index is null
            ? null
            : await _storage.ReadJsonAsync(
                GetAccountPath(index.AccountId),
                ServerJsonSerializerContext.Default.AccountRecord,
                cancellationToken);
    }

    public ValueTask<AccountRecord?> FindByIdAsync(
        Guid accountId,
        CancellationToken cancellationToken) =>
        accountId == Guid.Empty
            ? ValueTask.FromResult<AccountRecord?>(null)
            : _storage.ReadJsonAsync(
                GetAccountPath(accountId),
                ServerJsonSerializerContext.Default.AccountRecord,
                cancellationToken);

    public ValueTask<AuthenticatedAccount?> AuthenticateAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return AuthenticateAsync(
            AccountRules.GetBearerToken(context),
            cancellationToken);
    }

    public async ValueTask RevokeAsync(
        string? accessToken,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!AccountRules.IsAccessTokenShapeValid(accessToken))
        {
            return;
        }

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            using var write = _storage.BeginWrite();
            _storage.TryDelete(
                GetSessionPath(HashToken(accessToken!)));
            write.Commit();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async ValueTask<AccountSessionResponse> CreateSessionCoreAsync(
        AccountRecord account,
        string deviceName,
        CancellationToken cancellationToken)
    {
        var token = AccountRules.CreateAccessToken();
        var tokenHash = HashToken(token);
        var createdAt = DateTimeOffset.UtcNow;
        var session = new AccountSessionRecord(
            account.AccountId,
            tokenHash,
            deviceName,
            createdAt,
            createdAt.Add(_sessionLifetime));
        await _storage.WriteJsonAtomicAsync(
            GetSessionPath(tokenHash),
            session,
            ServerJsonSerializerContext.Default.AccountSessionRecord,
            cancellationToken);
        return new AccountSessionResponse(
            account.AccountId,
            account.Username,
            account.DisplayName,
            token,
            session.ExpiresAt);
    }

    private async ValueTask<AccountRecord> CreateAccountCoreAsync(
        string username,
        string password,
        string displayName,
        CancellationToken cancellationToken)
    {
        var indexPath = GetUsernameIndexPath(username);
        if (_storage.Exists(indexPath))
        {
            throw new AccountConflictException(
                "That account name is already in use.");
        }

        var accountId = Guid.CreateVersion7();
        var salt = RandomNumberGenerator.GetBytes(32);
        var passwordHash = HashPassword(
            password,
            salt,
            _passwordIterations);
        try
        {
            var account = new AccountRecord(
                accountId,
                username,
                displayName,
                Convert.ToBase64String(salt),
                Convert.ToBase64String(passwordHash),
                _passwordIterations,
                DateTimeOffset.UtcNow);
            await _storage.WriteJsonAtomicAsync(
                GetAccountPath(accountId),
                account,
                ServerJsonSerializerContext.Default.AccountRecord,
                cancellationToken);
            await _storage.WriteJsonAtomicAsync(
                indexPath,
                new AccountIndexRecord(accountId),
                ServerJsonSerializerContext.Default.AccountIndexRecord,
                cancellationToken);
            return account;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordHash);
            CryptographicOperations.ZeroMemory(salt);
        }
    }

    private static bool VerifyPassword(
        AccountRecord account,
        string password)
    {
        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(account.PasswordSalt);
            expected = Convert.FromBase64String(account.PasswordHash);
        }
        catch (FormatException)
        {
            return false;
        }

        if (salt.Length != 32 ||
            expected.Length != 32 ||
            account.PasswordIterations < 100_000)
        {
            CryptographicOperations.ZeroMemory(salt);
            CryptographicOperations.ZeroMemory(expected);
            return false;
        }

        var actual = HashPassword(
            password,
            salt,
            account.PasswordIterations);
        var valid = CryptographicOperations.FixedTimeEquals(
            expected,
            actual);
        CryptographicOperations.ZeroMemory(salt);
        CryptographicOperations.ZeroMemory(expected);
        CryptographicOperations.ZeroMemory(actual);
        return valid;
    }

    private static byte[] HashPassword(
        string password,
        byte[] salt,
        int iterations)
    {
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        try
        {
            return Rfc2898DeriveBytes.Pbkdf2(
                passwordBytes,
                salt,
                iterations,
                HashAlgorithmName.SHA256,
                32);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    private void PerformDummyPasswordHash(string password)
    {
        var value = HashPassword(
            password,
            DummySalt,
            _passwordIterations);
        CryptographicOperations.ZeroMemory(value);
    }

    private string GetUsernameIndexPath(string username)
    {
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(username)));
        return GetSafePath(
            _accountRoot,
            "by-username",
            hash[..2],
            hash + ".json");
    }

    private string GetAccountPath(Guid accountId) =>
        GetSafePath(
            _accountRoot,
            "by-id",
            accountId.ToString("N") + ".json");

    private string GetSessionPath(string tokenHash) =>
        GetSafePath(
            _sessionRoot,
            tokenHash[..2],
            tokenHash + ".json");

    private static string HashToken(string token) =>
        Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string GetSafePath(
        string root,
        params string[] segments)
    {
        var path = Path.GetFullPath(
            segments.Aggregate(
                root,
                Path.Combine));
        if (!path.StartsWith(
                root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The account storage path is outside its data root.");
        }

        return path;
    }
}

internal static class AccountRules
{
    public const int MinimumPasswordLength = 10;
    public const int MaximumPasswordLength = 256;

    public static string NormalizeUsername(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > 128)
        {
            throw new ArgumentException(
                "Account names must not exceed 64 characters.",
                nameof(value));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length is < 3 or > 64 ||
            !char.IsAsciiLetterOrDigit(normalized[0]) ||
            normalized.Any(static character =>
                !char.IsAsciiLetterOrDigit(character) &&
                character is not ('.' or '_' or '-')))
        {
            throw new ArgumentException(
                "Account names must contain 3 to 64 ASCII letters, digits, dots, underscores, or hyphens and start with a letter or digit.",
                nameof(value));
        }

        return normalized;
    }

    public static string NormalizeDisplayName(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > 200)
        {
            throw new ArgumentException(
                "Display names must not exceed 100 characters.",
                nameof(value));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = string.Join(
            ' ',
            value.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries));
        if (normalized.Length is < 1 or > 100 ||
            normalized.Any(char.IsControl))
        {
            throw new ArgumentException(
                "Display names must contain 1 to 100 visible characters.",
                nameof(value));
        }

        return normalized;
    }

    public static string NormalizeDeviceName(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > 200)
        {
            throw new ArgumentException(
                "Device names must not exceed 100 characters.",
                nameof(value));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim();
        if (normalized.Length is < 1 or > 100 ||
            normalized.Any(char.IsControl))
        {
            throw new ArgumentException(
                "Device names must contain 1 to 100 visible characters.",
                nameof(value));
        }

        return normalized;
    }

    public static void ValidatePassword(string value)
    {
        ValidatePasswordShape(value);
        if (value.Length < MinimumPasswordLength)
        {
            throw new ArgumentException(
                $"Passwords must contain at least {MinimumPasswordLength} characters.",
                nameof(value));
        }
    }

    public static void ValidatePasswordShape(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > MaximumPasswordLength ||
            value.Any(static character =>
                character is '\0' or '\r' or '\n'))
        {
            throw new ArgumentException(
                "The supplied password has an unsupported shape.",
                nameof(value));
        }
    }

    public static string? GetBearerToken(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        return header.StartsWith(
            prefix,
            StringComparison.OrdinalIgnoreCase)
            ? header[prefix.Length..].Trim()
            : null;
    }

    public static bool IsAccessTokenShapeValid(string? token) =>
        !string.IsNullOrWhiteSpace(token) &&
        token.Length == 43 &&
        token.All(static character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '-' or '_');

    public static string CreateAccessToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}

internal static class ServerFileStorage
{
    public static async ValueTask<T?> ReadJsonAsync<T>(
        string path,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
        where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await System.Text.Json.JsonSerializer.DeserializeAsync(
            stream,
            typeInfo,
            cancellationToken);
    }

    public static async ValueTask WriteJsonAtomicAsync<T>(
        string path,
        T value,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path) ??
            throw new InvalidOperationException(
                "The server data file has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = path + "." +
            Guid.CreateVersion7().ToString("N") +
            ".tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 16 * 1024,
                             FileOptions.Asynchronous |
                             FileOptions.SequentialScan))
            {
                await System.Text.Json.JsonSerializer.SerializeAsync(
                    stream,
                    value,
                    typeInfo,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    public static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Expired or temporary file cleanup is best effort.
        }
    }

    /// <summary>
    /// Deletes and reports whether anything was there, for callers that answer
    /// 404 when there was not.
    /// </summary>
    public static bool TryDeleteReporting(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
