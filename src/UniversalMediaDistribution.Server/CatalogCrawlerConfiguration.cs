using System.Globalization;
using UniversalMediaDistribution.Catalog.ContentSources;

namespace UniversalMediaDistribution.Server;

internal static class CatalogCrawlerConfiguration
{
    internal static CatalogCrawlerOptions Read(IReadOnlyList<ApiContentProviderRegistration> providers, ServerDatabase database)
    {
        var overrides = new Dictionary<string, Uri>(StringComparer.Ordinal);
        foreach (var provider in providers)
        {
            var value = Environment.GetEnvironmentVariable("UMD_CRAWLER_BASE_" + provider.Descriptor.Id.Replace('-', '_').ToUpperInvariant());
            if (string.IsNullOrWhiteSpace(value)) continue;
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !CatalogCrawlerHttpClient.IsPublicUri(uri))
                throw new ArgumentException($"Invalid public base URL for {provider.Descriptor.Id}.");
            overrides.Add(provider.Descriptor.Id, uri);
        }
        var disabled = (Environment.GetEnvironmentVariable("UMD_CRAWLER_DISABLED_PROVIDERS") ?? "")
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        if (disabled.Any(id => providers.All(provider => provider.Descriptor.Id != id)))
            throw new ArgumentException("Unknown disabled crawler provider.");
        return new CatalogCrawlerOptions
        {
            PagesPerProvider = ReadInt("UMD_CRAWLER_PAGES_PER_PROVIDER", 2, 1, 100),
            MaximumRunTime = TimeSpan.FromMinutes(ReadInt("UMD_CRAWLER_MAX_MINUTES", 45, 1, 50)),
            OperationTimeout = TimeSpan.FromSeconds(ReadInt("UMD_CRAWLER_OPERATION_SECONDS", 180, 10, 1200)),
            BaseUrls = overrides,
            ProviderIntervals = new CatalogCrawlerSettingsStore(database).ReadIntervals()
                .Where(pair => providers.Any(provider => provider.Descriptor.Id == pair.Key))
                .ToDictionary(pair => pair.Key, pair => TimeSpan.FromSeconds(pair.Value), StringComparer.Ordinal),
            DisabledProviders = disabled
        };
    }

    private static int ReadInt(string name, int fallback, int minimum, int maximum)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < minimum || number > maximum)
            throw new ArgumentException($"Invalid value for {name}.");
        return number;
    }
}
