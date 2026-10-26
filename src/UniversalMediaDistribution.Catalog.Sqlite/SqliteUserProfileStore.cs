using System.Globalization;
using Microsoft.Data.Sqlite;
using UniversalMediaDistribution.Application.UserState;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.Catalog.Sqlite;

public sealed class SqliteUserProfileStore(SqliteCatalogDatabase database)
    : IUserProfileStore
{
    private const int MaximumNameLength = 40;

    public async ValueTask<IReadOnlyList<UserProfile>> ListAsync(
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
                SELECT id, name, accent_key, sort_order, created_at, last_used_at
                FROM profiles
                ORDER BY sort_order, created_at, name_key;
                """;
            var profiles = new List<UserProfile>();
            await using var reader =
                await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                profiles.Add(new UserProfile(
                    Guid.Parse(reader.GetString(0)),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetInt32(3),
                    ParseTimestamp(reader.GetString(4)),
                    reader.IsDBNull(5)
                        ? null
                        : ParseTimestamp(reader.GetString(5))));
            }

            return profiles;
        }
        finally
        {
            database.OperationGate.Release();
        }
    }

    public async ValueTask<UserProfile> CreateAsync(
        string name,
        string? accentKey = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = NormalizeName(name);
        var nameKey = NameNormalizer.Normalize(normalizedName);
        var profile = new UserProfile(
            Guid.CreateVersion7(),
            normalizedName,
            accentKey,
            SortOrder: 0,
            DateTimeOffset.UtcNow,
            LastUsedAt: null);

        await database.InitializeAsync(cancellationToken);
        await database.OperationGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection =
                await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO profiles (
                    id, name, name_key, accent_key, sort_order,
                    created_at, last_used_at)
                VALUES (
                    $id, $name, $name_key, $accent_key,
                    (SELECT COALESCE(MAX(sort_order), -1) + 1 FROM profiles),
                    $created_at, NULL);
                """;
            command.Parameters.AddWithValue("$id", profile.Id.ToString("D"));
            command.Parameters.AddWithValue("$name", profile.Name);
            command.Parameters.AddWithValue("$name_key", nameKey);
            command.Parameters.AddWithValue(
                "$accent_key",
                (object?)accentKey ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "$created_at",
                profile.CreatedAt.ToString("O"));
            try
            {
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (SqliteException exception)
                when (exception.SqliteErrorCode == 19)
            {
                throw new InvalidOperationException(
                    "A profile with this name already exists.",
                    exception);
            }

            return profile;
        }
        finally
        {
            database.OperationGate.Release();
        }
    }

    public async ValueTask<UserProfile> RenameAsync(
        Guid profileId,
        string name,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = NormalizeName(name);
        await database.InitializeAsync(cancellationToken);
        await database.OperationGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection =
                await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE profiles
                SET name = $name, name_key = $name_key
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", profileId.ToString("D"));
            command.Parameters.AddWithValue("$name", normalizedName);
            command.Parameters.AddWithValue(
                "$name_key",
                NameNormalizer.Normalize(normalizedName));
            try
            {
                if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
                {
                    throw new KeyNotFoundException(
                        $"Profile '{profileId}' no longer exists.");
                }
            }
            catch (SqliteException exception)
                when (exception.SqliteErrorCode == 19)
            {
                throw new InvalidOperationException(
                    "A profile with this name already exists.",
                    exception);
            }
        }
        finally
        {
            database.OperationGate.Release();
        }

        return (await ListAsync(cancellationToken))
            .First(profile => profile.Id == profileId);
    }

    public async ValueTask DeleteAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync(cancellationToken);
        await database.OperationGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection =
                await OpenConnectionAsync(cancellationToken);
            await using var transaction =
                (SqliteTransaction)await connection.BeginTransactionAsync(
                    cancellationToken);

            // Somebody has to be using the application. Removing the last
            // profile would leave a library nobody owns and a picker with
            // nothing to pick.
            await using (var count = connection.CreateCommand())
            {
                count.Transaction = transaction;
                count.CommandText = "SELECT COUNT(*) FROM profiles;";
                if (Convert.ToInt32(
                        await count.ExecuteScalarAsync(cancellationToken),
                        CultureInfo.InvariantCulture) <= 1)
                {
                    throw new InvalidOperationException(
                        "The last profile cannot be removed.");
                }
            }

            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;

                // Progress is deleted by hand because its profile column
                // carries no foreign key: SQLite cannot add a column that is
                // both non-null and a reference, so the cascade that clears
                // every other table does not reach it.
                command.CommandText = """
                    DELETE FROM playback_progress WHERE profile_id = $id;
                    DELETE FROM user_media_list_items
                    WHERE list_id IN (
                        SELECT id FROM user_media_lists WHERE profile_id = $id);
                    DELETE FROM profiles WHERE id = $id;
                    """;
                command.Parameters.AddWithValue("$id", profileId.ToString("D"));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            database.OperationGate.Release();
        }
    }

    public async ValueTask MarkUsedAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync(cancellationToken);
        await database.OperationGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection =
                await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText =
                "UPDATE profiles SET last_used_at = $now WHERE id = $id;";
            command.Parameters.AddWithValue("$id", profileId.ToString("D"));
            command.Parameters.AddWithValue(
                "$now",
                DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            database.OperationGate.Release();
        }
    }

    private static string NormalizeName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var trimmed = name.Trim();
        return trimmed.Length > MaximumNameLength
            ? trimmed[..MaximumNameLength]
            : trimmed;
    }

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed
            // Rows written by the migration use SQLite's own datetime(), which
            // has no offset. They are UTC by construction.
            : DateTime.SpecifyKind(
                DateTime.Parse(value, CultureInfo.InvariantCulture),
                DateTimeKind.Utc);

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
