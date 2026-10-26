namespace UniversalMediaDistribution.Domain.Catalog;

public sealed record CatalogContributorCredit
{
    public CatalogContributorCredit(
        string role,
        string? characterName = null,
        int sortOrder = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        ArgumentOutOfRangeException.ThrowIfNegative(sortOrder);

        Role = role.Trim();
        CharacterName = string.IsNullOrWhiteSpace(characterName)
            ? null
            : characterName.Trim();
        SortOrder = sortOrder;
    }

    public string Role { get; }

    public string? CharacterName { get; }

    public int SortOrder { get; }
}

public sealed record CatalogContributor
{
    public CatalogContributor(
        string name,
        string? kind = null,
        string? url = null,
        string? thumbnailRef = null,
        IEnumerable<CatalogContributorCredit>? credits = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var normalizedKind = string.IsNullOrWhiteSpace(kind)
            ? "person"
            : kind.Trim().ToLowerInvariant();
        if (normalizedKind is not ("person" or "organization"))
        {
            throw new ArgumentException(
                "A contributor kind must be 'person' or 'organization'.",
                nameof(kind));
        }

        Name = name.Trim();
        NameKey = NameNormalizer.Normalize(Name);
        Kind = normalizedKind;
        StableKey = $"{Kind}:{NameKey}";
        Url = NormalizeUrl(url);
        ThumbnailRef = string.IsNullOrWhiteSpace(thumbnailRef)
            ? null
            : thumbnailRef.Trim();
        Credits = credits?
            .Where(static credit => credit is not null)
            .Distinct()
            .OrderBy(static credit => credit.SortOrder)
            .ThenBy(static credit => credit.Role, StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];
    }

    public string Name { get; }

    public string NameKey { get; }

    public string Kind { get; }

    public string StableKey { get; }

    public Uri? Url { get; }

    public string? ThumbnailRef { get; }

    public IReadOnlyList<CatalogContributorCredit> Credits { get; }

    public bool IsDistributor =>
        Kind == "organization" &&
        Credits.Any(static credit => IsDistributorRole(credit.Role));

    private static bool IsDistributorRole(string role) =>
        role.Trim().ToLowerInvariant() is
            "distributor" or
            "distribution" or
            "publisher" or
            "licensor" or
            "provider" or
            "distribuidor" or
            "distribuidora" or
            "editorial" or
            "licenciante" or
            "proveedor";

    private static Uri? NormalizeUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ArgumentException(
                "A contributor URL must be an absolute HTTP or HTTPS URL without embedded credentials.",
                nameof(value));
        }

        return uri;
    }
}
