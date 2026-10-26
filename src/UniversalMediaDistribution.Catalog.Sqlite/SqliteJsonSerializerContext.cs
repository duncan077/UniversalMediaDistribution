using System.Text.Json.Serialization;
using UniversalMediaDistribution.Application.ContentSources;
using UniversalMediaDistribution.Application.Metadata;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Torrenting.Ingestion;

namespace UniversalMediaDistribution.Catalog.Sqlite;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(MagnetOpenPreferences))]
[JsonSerializable(typeof(MetadataProviderSettings))]
[JsonSerializable(typeof(ContentSourceProviderSettings))]
[JsonSerializable(typeof(Dictionary<string, StoredApiTitleBinding>))]
[JsonSerializable(typeof(ApiProviderUnitRoute))]
// Serialized only to be hashed, never stored: the hash is how a re-import of
// the same payload is told apart from one that carries something new.
[JsonSerializable(typeof(CatalogTransferItem))]
internal sealed partial class SqliteJsonSerializerContext :
    JsonSerializerContext;
