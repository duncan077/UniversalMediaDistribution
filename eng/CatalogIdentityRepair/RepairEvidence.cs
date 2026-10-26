using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;

internal static class RepairEvidence
{
    // Only punctuation/diacritics of the complete primary title are folded;
    // this is not fuzzy containment (sequels must remain distinct).
    internal static bool Matches(CatalogTransferItem left, CatalogTransferItem right)
    {
        if (CatalogTransferIdentity.GetMatchScore(left, right) > 0) return true;
        if (!CatalogTransferIdentity.AreCompatible(left, right) || !CatalogTransferIdentity.IsUsableName(left.Name) || !CatalogTransferIdentity.IsUsableName(right.Name)) return false;
        var a = CatalogReleaseYear.Resolve(left.ReleaseYear, left.ReleaseDate);
        var b = CatalogReleaseYear.Resolve(right.ReleaseYear, right.ReleaseDate);
        return (a is null || b is null || a == b) && Fold(left.Name) == Fold(right.Name);
    }
    private static string Fold(string name) => string.Concat(NameNormalizer.Normalize(name).Normalize(NormalizationForm.FormD).Where(char.IsLetterOrDigit));

    internal static void Regroup(RepairPlan plan)
    {
        plan.Groups.Clear();
        foreach (var source in plan.Sources.Where(s => s.FreshItem is not null))
        {
            var matches = plan.Groups.Where(g => g.SourceIndexes.All(index => Matches(plan.Sources[index].FreshItem!, source.FreshItem!))).ToArray();
            var group = matches.Length == 1 ? matches[0] : new RepairGroup { Item = source.FreshItem! };
            if (matches.Length != 1) plan.Groups.Add(group);
            else group.Item = CatalogTransferIdentity.CombineConverged(group.Item, CatalogTransferIdentity.ConvergeIdentity(group.Item, source.FreshItem!));
            group.SourceIndexes.Add(plan.Sources.IndexOf(source));
        }
    }

    internal static async Task RefreshFailuresAsync(string path)
    {
        path = Path.GetFullPath(path);
        var plan = JsonSerializer.Deserialize(await File.ReadAllTextAsync(path), RepairJsonContext.Default.RepairPlan)!;
        if (!plan.FetchCompleted) throw new InvalidDataException("The first fetch is still running.");
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(Path.GetDirectoryName(path)!, "backup.sqlite"),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        db.Open();
        using var http = CatalogCrawlerHttpClient.Create();
        var registry = BuiltInApiContentProviders.Create(http).ToDictionary(p => p.Descriptor.Id);
        foreach (var source in plan.Sources.Where(s => s.Failure is not null).ToArray())
        {
            var registration = registry[source.ProviderId];
            if (!registration.Descriptor.ProvidesUnitContent) continue;
            using var interval = db.CreateCommand();
            interval.CommandText = "SELECT interval_seconds FROM crawler_provider_settings WHERE provider_id=$id;";
            interval.Parameters.AddWithValue("$id", source.ProviderId);
            using var pacing = CatalogCrawlerHttpClient.UseProviderInterval(TimeSpan.FromSeconds(interval.ExecuteScalar() is long seconds ? seconds : 2));
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            try
            {
                var provider = registration.GetProvider();
                var overrideUrl = Environment.GetEnvironmentVariable("UMD_CRAWLER_BASE_" + source.ProviderId.Replace('-', '_').ToUpperInvariant());
                var root = new Uri(string.IsNullOrWhiteSpace(overrideUrl) ? registration.Descriptor.DefaultBaseUrl : overrideUrl);
                if (!CatalogCrawlerHttpClient.IsPublicUri(root)) throw new InvalidDataException("Nonpublic provider root.");
                var hits = await provider.SearchAsync(root, source.PreviewName, cancellationToken: timeout.Token);
                var candidates = hits.Where(hit => Matches(hit.PreviewItem, new CatalogTransferItem
                { MediaType = hit.PreviewItem.MediaType, Subtype = hit.PreviewItem.Subtype, Name = source.PreviewName })).ToArray();
                if (candidates.Length != 1) throw new InvalidDataException("Search did not verify exactly one canonical title.");
                var selected = new ApiContentSearchItem(candidates[0].ExternalId, candidates[0].PreviewItem, RequireProviderTitle: true);
                Console.WriteLine($"Retry verified: {source.ProviderId}: {selected.ExternalId}");
                var fresh = await provider.LoadItemAsync(root, selected, timeout.Token);
                CatalogTransferValidator.Validate(new CatalogTransferDocument { Items = [fresh.CatalogItem] });
                source.FreshItem = fresh.CatalogItem with { Id = null, SyncVersion = null };
                source.ResolvedExternalId = selected.ExternalId == source.ExternalId ? null : selected.ExternalId;
                source.Failure = null;
                Console.WriteLine($"Recovered: {source.FreshItem.Name}; chapters={source.FreshItem.Units.Count}");
            }
            catch (Exception exception)
            {
                source.FreshItem = null;
                source.Failure = exception.GetType().Name;
                Console.WriteLine($"Still unresolved: {source.ProviderId}: {source.PreviewName}; {source.Failure}");
            }
            Regroup(plan);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(plan, RepairJsonContext.Default.RepairPlan));
        }
    }
}
