using System.Globalization;
using System.Text;

namespace UniversalMediaDistribution.Domain.Catalog;

public static class NameNormalizer
{
    public static string Normalize(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // Browser WebAssembly supports canonical Form C but rejects the
        // compatibility Form KC at runtime. Use one deterministic form on all
        // hosts so imported catalog keys compare identically.
        var normalized = name.Normalize(NormalizationForm.FormC);
        var builder = new StringBuilder(normalized.Length);
        var pendingSpace = false;

        foreach (var character in normalized)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
        }

        if (builder.Length == 0)
        {
            throw new ArgumentException("A name must contain at least one visible character.", nameof(name));
        }

        return builder.ToString().ToUpper(CultureInfo.InvariantCulture);
    }
}
