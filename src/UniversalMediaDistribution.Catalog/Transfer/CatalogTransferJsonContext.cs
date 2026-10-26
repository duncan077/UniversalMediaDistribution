using System.Text.Json.Serialization;

namespace UniversalMediaDistribution.Catalog.Transfer;

[JsonSerializable(typeof(CatalogTransferDocument))]
internal sealed partial class CatalogTransferJsonContext : JsonSerializerContext;
