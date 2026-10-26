using System.Text.Json;
using Microsoft.Data.Sqlite;
using UniversalMediaDistribution.Application.ContentSources;

namespace UniversalMediaDistribution.Catalog.Sqlite;

/// <summary>
/// Stores the provider title chosen for each catalog item as one settings row
/// per item, so a lookup reads a single key and no schema change is needed.
/// </summary>
public sealed class SqliteApiTitleBindingStore(SqliteCatalogDatabase database) :
    IApiTitleBindingStore
{
    private const string KeyPrefix = "api_title_binding:";

    public async ValueTask<IReadOnlyDictionary<string, ApiTitleBinding>>
        LoadAsync(
            Guid catalogItemId,
            CancellationToken cancellationToken = default)
    {
        var empty = new Dictionary<string, ApiTitleBinding>(
            StringComparer.Ordinal);
        if (catalogItemId == Guid.Empty)
        {
            return empty;
        }

        var stored = await ReadAsync(catalogItemId, cancellationToken);
        if (stored is null)
        {
            return empty;
        }

        foreach (var (providerId, entry) in stored)
        {
            if (string.IsNullOrWhiteSpace(entry.ExternalId))
            {
                continue;
            }

            empty[providerId] = new ApiTitleBinding(
                catalogItemId,
                providerId,
                entry.ExternalId,
                entry.Title ?? string.Empty,
                entry.UnitRoutes,
                entry.LastUsedAt);
        }

        return empty;
    }

    public async ValueTask SaveAsync(
        ApiTitleBinding binding,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        var stored =
            await ReadAsync(binding.CatalogItemId, cancellationToken) ??
            new Dictionary<string, StoredApiTitleBinding>(
                StringComparer.Ordinal);
        stored[binding.ProviderId] = new StoredApiTitleBinding
        {
            ExternalId = binding.ExternalId,
            Title = binding.Title,
            UnitRoutes = binding.UnitRoutes.ToList(),
            LastUsedAt = binding.LastUsedAt
        };
        await WriteAsync(binding.CatalogItemId, stored, cancellationToken);
    }

    public async ValueTask RemoveAsync(
        Guid catalogItemId,
        string providerId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        var stored = await ReadAsync(catalogItemId, cancellationToken);
        if (stored is null || !stored.Remove(providerId.Trim()))
        {
            return;
        }

        await WriteAsync(catalogItemId, stored, cancellationToken);
    }

    private async ValueTask<Dictionary<string, StoredApiTitleBinding>?>
        ReadAsync(
            Guid catalogItemId,
            CancellationToken cancellationToken)
    {
        await database.InitializeAsync(cancellationToken);
        await database.OperationGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection =
                await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT value
                FROM application_settings
                WHERE key = $key;
                """;
            command.Parameters.AddWithValue("$key", CreateKey(catalogItemId));
            if (await command.ExecuteScalarAsync(cancellationToken)
                is not string json ||
                string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize(
                    json,
                    SqliteJsonSerializerContext.Default
                        .DictionaryStringStoredApiTitleBinding);
            }
            catch (JsonException)
            {
                // A corrupt row is treated as no binding: the lookup falls back
                // to searching by name.
                return null;
            }
        }
        finally
        {
            database.OperationGate.Release();
        }
    }

    private async ValueTask WriteAsync(
        Guid catalogItemId,
        Dictionary<string, StoredApiTitleBinding> bindings,
        CancellationToken cancellationToken)
    {
        await database.InitializeAsync(cancellationToken);
        await database.OperationGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection =
                await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            if (bindings.Count == 0)
            {
                command.CommandText = """
                    DELETE FROM application_settings
                    WHERE key = $key;
                    """;
                command.Parameters.AddWithValue(
                    "$key",
                    CreateKey(catalogItemId));
                await command.ExecuteNonQueryAsync(cancellationToken);
                return;
            }

            command.CommandText = """
                INSERT INTO application_settings (key, value, updated_at)
                VALUES ($key, $value, $updated_at)
                ON CONFLICT (key)
                DO UPDATE SET
                    value = excluded.value,
                    updated_at = excluded.updated_at;
                """;
            command.Parameters.AddWithValue("$key", CreateKey(catalogItemId));
            command.Parameters.AddWithValue(
                "$value",
                JsonSerializer.Serialize(
                    bindings,
                    SqliteJsonSerializerContext.Default
                        .DictionaryStringStoredApiTitleBinding));
            command.Parameters.AddWithValue(
                "$updated_at",
                DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            database.OperationGate.Release();
        }
    }

    private static string CreateKey(Guid catalogItemId) =>
        KeyPrefix + catalogItemId.ToString("N");

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

internal sealed class StoredApiTitleBinding
{
    public string ExternalId { get; set; } = string.Empty;

    public string? Title { get; set; }

    public List<ApiProviderUnitRoute>? UnitRoutes { get; set; }

    public DateTimeOffset? LastUsedAt { get; set; }
}
