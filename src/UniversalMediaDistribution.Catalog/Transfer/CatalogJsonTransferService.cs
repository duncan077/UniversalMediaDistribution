using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.Catalog.Transfer;

public sealed class CatalogJsonTransferService(ICatalogTransferStore store)
{
    public const int MaximumDocumentBytes = 32 * 1024 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 64
    };

    private static readonly CatalogTransferJsonContext SerializerContext = new(SerializerOptions);

    public async ValueTask ExportAsync(
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);

        if (!destination.CanWrite)
        {
            throw new ArgumentException("Destination stream is not writable.", nameof(destination));
        }

        var document = await store.ExportCatalogAsync(cancellationToken);
        CatalogTransferValidator.Validate(document);

        await JsonSerializer.SerializeAsync(
            destination,
            document,
            SerializerContext.CatalogTransferDocument,
            cancellationToken);
    }

    internal async ValueTask<CatalogTransferDocument> ExportDocumentAsync(
        CancellationToken cancellationToken = default)
    {
        var document = await store.ExportCatalogAsync(cancellationToken);
        CatalogTransferValidator.Validate(document);
        return document;
    }

    internal async ValueTask<CatalogTransferDocument> ExportItemDocumentAsync(
        Guid catalogItemId, CancellationToken cancellationToken)
    {
        if (store is not ICatalogItemTransferStore targeted)
            return await ExportDocumentAsync(cancellationToken);
        var document = await targeted.ExportCatalogItemAsync(catalogItemId, cancellationToken);
        CatalogTransferValidator.Validate(document);
        return document;
    }

    internal async ValueTask<CatalogTransferDocument> ExportMatchingDocumentAsync(
        IReadOnlyList<CatalogTransferItem> candidates, CancellationToken cancellationToken)
    {
        if (store is not ICatalogItemTransferStore targeted)
            return await ExportDocumentAsync(cancellationToken);
        var document = await targeted.ExportMatchingCatalogItemsAsync(candidates, cancellationToken);
        CatalogTransferValidator.Validate(document);
        return document;
    }

    public async ValueTask<CatalogTransferDocument> ReadAsync(
        Stream source,
        CancellationToken cancellationToken = default)
    {
        var bytes = await ReadDocumentBytesAsync(source, cancellationToken);
        return DeserializeAndValidate(bytes);
    }

    public async ValueTask<CatalogTransferDocument> ReadUnboundAsync(
        Stream source,
        CancellationToken cancellationToken = default)
    {
        var bytes = await ReadDocumentBytesAsync(source, cancellationToken);
        return DeserializeAndValidate(bytes, allowMissingTorrentIdentity: true);
    }

    public async ValueTask<CatalogImportPreview> PreviewAsync(
        CatalogTransferDocument document,
        CancellationToken cancellationToken = default)
    {
        CatalogTransferValidator.Validate(document);

        var existingItems = await store.ListAsync(cancellationToken: cancellationToken);
        var existingKeys = existingItems.ToDictionary(
            static item => ContentItemKey.Create(item.MediaType, item.Name, item.SubtypeCode),
            static item => item.Name,
            StringComparer.Ordinal);

        var creates = new List<string>();
        var updates = new List<string>();

        foreach (var item in document.Items)
        {
            var mediaType = CatalogSubtypeResolver.ParseMediaType(item.MediaType);
            var key = CatalogTransferIdentity.GetKey(item);

            if (existingKeys.ContainsKey(key))
            {
                updates.Add(item.Name);
            }
            else
            {
                creates.Add(item.Name);
            }
        }

        return new CatalogImportPreview(creates, updates);
    }

    public async ValueTask<CatalogImportResult> ImportAsync(
        Stream source,
        string? sourceName = null,
        CancellationToken cancellationToken = default)
    {
        var bytes = await ReadDocumentBytesAsync(source, cancellationToken);
        var document = DeserializeAndValidate(bytes);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));

        return await store.MergeCatalogAsync(document, sourceName, hash, cancellationToken);
    }

    public ValueTask<CatalogImportResult> ImportAsync(
        CatalogTransferDocument document,
        string? sourceName = null,
        CancellationToken cancellationToken = default)
    {
        CatalogTransferValidator.Validate(document);
        return store.MergeCatalogAsync(document, sourceName, documentHash: null, cancellationToken);
    }

    internal ValueTask<CatalogImportResult> ImportSelectedItemAsync(
        CatalogTransferItem item,
        string? sourceName,
        CancellationToken cancellationToken)
    {
        item = item with { Deleted = false };
        CatalogTransferValidator.Validate(new CatalogTransferDocument { Items = [item] });
        return store.ImportSelectedItemAsync(item, sourceName, cancellationToken);
    }

    internal static CatalogTransferDocument DeserializeAndValidate(
        ReadOnlySpan<byte> bytes,
        bool allowMissingTorrentIdentity = false)
    {
        CatalogTransferDocument? document;

        try
        {
            document = JsonSerializer.Deserialize(bytes, SerializerContext.CatalogTransferDocument);
        }
        catch (JsonException exception)
        {
            throw new CatalogTransferValidationException([$"Invalid JSON: {exception.Message}"]);
        }

        CatalogTransferValidator.Validate(document, allowMissingTorrentIdentity);
        return document!;
    }

    internal static async ValueTask<byte[]> ReadDocumentBytesAsync(
        Stream source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (!source.CanRead)
        {
            throw new ArgumentException("Source stream is not readable.", nameof(source));
        }

        if (source.CanSeek && source.Length > MaximumDocumentBytes)
        {
            throw new CatalogTransferValidationException([$"Catalog JSON exceeds the {MaximumDocumentBytes / 1024 / 1024} MiB limit."]);
        }

        using var destination = new MemoryStream();
        var buffer = new byte[64 * 1024];

        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (destination.Length + read > MaximumDocumentBytes)
            {
                throw new CatalogTransferValidationException([$"Catalog JSON exceeds the {MaximumDocumentBytes / 1024 / 1024} MiB limit."]);
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        return destination.ToArray();
    }
}
