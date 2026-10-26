using System.Security.Cryptography;
using System.Text;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.Catalog.Transfer;

/// <summary>
/// Mints the sync identity of a catalog item: a GUID derived from the media
/// type and the normalized title the item was born under.
/// </summary>
/// <remarks>
/// Deterministic on purpose. Identity cannot wait for a server round trip —
/// the server is optional — and two devices that each add the same title must
/// arrive at the same identity without ever talking to each other. Hashing
/// the birth name gives both, and lets every existing database back-fill
/// identical IDs for identical titles with no coordination at all.
/// <para>
/// The ID is a birth certificate, not a current-name hash: it is derived once,
/// when the item first exists, and stored. Renames leave it untouched — that
/// is the entire point, an identity that survives what display names cannot.
/// </para>
/// </remarks>
public static class CatalogStableId
{
    /// <summary>
    /// Fixed namespace for catalog identity. Never change it: every ID every
    /// device has ever minted is a hash under this value.
    /// </summary>
    private static readonly Guid Namespace =
        new("c2f1d7a4-9b63-4d1e-8a6f-3e5d0b7c9a21");

    public static Guid Create(MediaType mediaType, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Create(
            $"{mediaType.ToString().ToLowerInvariant()}:" +
            NameNormalizer.Normalize(name));
    }

    public static Guid Create(MediaType mediaType, string name, string subtype) =>
        QualifyBookId(Create(mediaType, name), mediaType, subtype);

    private static Guid QualifyBookId(Guid id, MediaType mediaType, string subtype)
    {
        var code = CatalogSubtypeResolver.ResolveCode(mediaType, subtype);
        return mediaType != MediaType.Book || code == "book.manga"
            ? id : Create($"{id:N}:{code}");
    }

    public static IReadOnlyList<CatalogTransferItem> NormalizeLegacyIdentities(IEnumerable<CatalogTransferItem> items)
    {
        var normalized = items.Select(NormalizeLegacyIdentity).ToArray();
        var collisions = normalized.Where(item => item.Id is not null &&
                CatalogSubtypeResolver.ParseMediaType(item.MediaType) == MediaType.Book)
            .GroupBy(item => item.Id!.Value)
            .Where(group => group.Select(item => CatalogSubtypeResolver.ResolveCode(MediaType.Book, item.Subtype))
                .Distinct(StringComparer.Ordinal).Skip(1).Any())
            .Select(group => group.Key).ToHashSet();
        return normalized.Select(item => item.Id is { } id && collisions.Contains(id)
            ? item with { Id = QualifyBookId(id, CatalogSubtypeResolver.ParseMediaType(item.MediaType), item.Subtype) }
            : item).ToArray();
    }

    public static CatalogTransferItem NormalizeLegacyIdentity(CatalogTransferItem item)
    {
        var media = CatalogSubtypeResolver.ParseMediaType(item.MediaType);
        if (item.Id != Create(media, item.Name)) return item;
        var qualifiedId = Create(media, item.Name, item.Subtype);
        return item.Id == qualifiedId ? item : item with { Id = qualifiedId };
    }

    private static Guid Create(string value)
    {
        var namespaceBytes = Namespace.ToByteArray();
        var valueBytes = Encoding.UTF8.GetBytes(value);
        var input = new byte[namespaceBytes.Length + valueBytes.Length];
        namespaceBytes.CopyTo(input, 0);
        valueBytes.CopyTo(input, namespaceBytes.Length);
        var hash = SHA256.HashData(input);
        return new Guid(hash.AsSpan(0, 16));
    }
}
