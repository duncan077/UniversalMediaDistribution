using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.Catalog.Transfer;

public static class CatalogSubtypeResolver
{
    public static MediaType ParseMediaType(string value)
    {
        if (!Enum.TryParse<MediaType>(value, ignoreCase: true, out var mediaType) ||
            !Enum.IsDefined(mediaType))
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown media type.");
        }

        return mediaType;
    }

    public static string ResolveCode(MediaType mediaType, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        if (MediaSubtypeDefinition.IsCompatible(mediaType, value))
        {
            return MediaSubtypeDefinition.GetRequired(value).Code;
        }

        var normalizedValue = NormalizeSubtypeValue(value);
        var matches = MediaSubtypeDefinition.All
            .Where(definition => definition.MediaType == mediaType)
            .Where(definition =>
                string.Equals(NormalizeSubtypeValue(definition.DisplayName), normalizedValue, StringComparison.Ordinal) ||
                string.Equals(NormalizeSubtypeValue(definition.Code[(definition.Code.IndexOf('.') + 1)..]), normalizedValue, StringComparison.Ordinal))
            .ToArray();

        return matches.Length switch
        {
            1 => matches[0].Code,
            0 => throw new ArgumentOutOfRangeException(nameof(value), value, $"Unknown subtype for media type '{mediaType}'."),
            _ => throw new ArgumentException($"Subtype '{value}' is ambiguous for media type '{mediaType}'.", nameof(value))
        };
    }

    private static string NormalizeSubtypeValue(string value) =>
        NameNormalizer.Normalize(value.Replace('-', ' ').Replace('_', ' '));
}

