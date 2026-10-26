using Microsoft.Data.Sqlite;
using UniversalMediaDistribution.Catalog.Sync;

namespace UniversalMediaDistribution.Catalog.Sqlite;

/// <summary>
/// Keeps the signed-in session in the local database so a restart does not ask
/// for the password again. The row holds a bearer token, so it is deleted on
/// sign-out and whenever the server rejects it, and it is left out of catalog
/// exports and sync snapshots.
/// <para>
/// Stored against a profile, not the installation: each profile signs into its
/// own account, and switching must never hand one person's token to another.
/// </para>
/// </summary>
public sealed class SqliteRemoteSessionStore(
    SqliteCatalogDatabase database) : IRemoteSessionStore
{
    private const string ServiceUrlKey = "remote_session_service_url";
    private const string AccessTokenKey = "remote_session_access_token";
    private const string AccountIdKey = "remote_session_account_id";
    private const string UsernameKey = "remote_session_username";
    private const string DisplayNameKey = "remote_session_display_name";
    private const string ExpiresAtKey = "remote_session_expires_at";

    private static readonly string[] Keys =
    [
        ServiceUrlKey,
        AccessTokenKey,
        AccountIdKey,
        UsernameKey,
        DisplayNameKey,
        ExpiresAtKey
    ];

    public async ValueTask<RemoteSessionState?> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync(cancellationToken);
        await database.OperationGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection =
                await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT key, value
                FROM profile_settings
                WHERE profile_id = $profile
                  AND key IN (
                    $service_url,
                    $access_token,
                    $account_id,
                    $username,
                    $display_name,
                    $expires_at);
                """;
            command.Parameters.AddWithValue(
                "$profile",
                database.ActiveProfileId.ToString("D"));
            command.Parameters.AddWithValue("$service_url", ServiceUrlKey);
            command.Parameters.AddWithValue("$access_token", AccessTokenKey);
            command.Parameters.AddWithValue("$account_id", AccountIdKey);
            command.Parameters.AddWithValue("$username", UsernameKey);
            command.Parameters.AddWithValue("$display_name", DisplayNameKey);
            command.Parameters.AddWithValue("$expires_at", ExpiresAtKey);

            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            await using (var reader =
                         await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    values[reader.GetString(0)] = reader.GetString(1);
                }
            }

            if (!values.TryGetValue(AccessTokenKey, out var accessToken) ||
                !values.TryGetValue(ServiceUrlKey, out var serviceUrl) ||
                !values.TryGetValue(AccountIdKey, out var accountIdText) ||
                !Guid.TryParse(accountIdText, out var accountId) ||
                !values.TryGetValue(ExpiresAtKey, out var expiresAtText) ||
                !DateTimeOffset.TryParse(
                    expiresAtText,
                    null,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out var expiresAt))
            {
                return null;
            }

            return new RemoteSessionState
            {
                ServiceUrl = serviceUrl,
                AccessToken = accessToken,
                AccountId = accountId,
                Username = values.GetValueOrDefault(UsernameKey) ??
                    string.Empty,
                DisplayName = values.GetValueOrDefault(DisplayNameKey) ??
                    string.Empty,
                ExpiresAt = expiresAt
            };
        }
        finally
        {
            database.OperationGate.Release();
        }
    }

    public async ValueTask SaveAsync(
        RemoteSessionState? session,
        CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync(cancellationToken);
        await database.OperationGate.WaitAsync(cancellationToken);
        try
        {
            var profileId = database.ActiveProfileId.ToString("D");
            await using var connection =
                await OpenConnectionAsync(cancellationToken);
            await using var transaction =
                (SqliteTransaction)await connection.BeginTransactionAsync(
                    cancellationToken);
            if (session is null)
            {
                await using var delete = connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = """
                    DELETE FROM profile_settings
                    WHERE profile_id = $profile
                      AND key LIKE 'remote_session_%';
                    """;
                delete.Parameters.AddWithValue("$profile", profileId);
                await delete.ExecuteNonQueryAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return;
            }

            var values = new[]
            {
                (ServiceUrlKey, session.ServiceUrl),
                (AccessTokenKey, session.AccessToken),
                (AccountIdKey, session.AccountId.ToString("D")),
                (UsernameKey, session.Username),
                (DisplayNameKey, session.DisplayName),
                (ExpiresAtKey, session.ExpiresAt.ToString("O"))
            };
            foreach (var (key, value) in values)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO profile_settings
                        (profile_id, key, value, updated_at)
                    VALUES ($profile, $key, $value, $updated_at)
                    ON CONFLICT (profile_id, key)
                    DO UPDATE SET
                        value = excluded.value,
                        updated_at = excluded.updated_at;
                    """;
                command.Parameters.AddWithValue("$profile", profileId);
                command.Parameters.AddWithValue("$key", key);
                command.Parameters.AddWithValue("$value", value);
                command.Parameters.AddWithValue(
                    "$updated_at",
                    DateTimeOffset.UtcNow.ToString("O"));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            database.OperationGate.Release();
        }
    }

    internal static IReadOnlyList<string> SettingKeys => Keys;

    private async ValueTask<SqliteConnection> OpenConnectionAsync(
        CancellationToken cancellationToken)
    {
        var connection = database.CreateConnection();
        try
        {
            await connection.OpenAsync(cancellationToken);
            await SqliteCatalogDatabase.ConfigureConnectionAsync(
                connection,
                cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
