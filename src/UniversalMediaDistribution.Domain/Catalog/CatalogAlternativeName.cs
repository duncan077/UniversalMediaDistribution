namespace UniversalMediaDistribution.Domain.Catalog;

public sealed record CatalogAlternativeName
{
    public CatalogAlternativeName(
        string name,
        string? language = null,
        string? kind = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Name = name.Trim();
        NameKey = NameNormalizer.Normalize(Name);
        Language = LanguageTag.Normalize(language);
        Kind = string.IsNullOrWhiteSpace(kind)
            ? "other"
            : kind.Trim().ToLowerInvariant();
    }

    public string Name { get; }

    public string NameKey { get; }

    public string Language { get; }

    public string Kind { get; }

}
