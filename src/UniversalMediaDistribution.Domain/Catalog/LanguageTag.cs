namespace UniversalMediaDistribution.Domain.Catalog;

public static class LanguageTag
{
    public const string Undetermined = "und";

    public static string Normalize(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return Undetermined;
        }

        var value = language.Trim();
        if (value.Length > 35 ||
            value.StartsWith('-') ||
            value.EndsWith('-') ||
            value.Split('-').Any(static segment =>
                segment.Length is < 1 or > 8 ||
                !segment.All(char.IsAsciiLetterOrDigit)))
        {
            throw new ArgumentException(
                $"Language '{language}' is not a valid language tag.",
                nameof(language));
        }

        return value.ToLowerInvariant();
    }
}
