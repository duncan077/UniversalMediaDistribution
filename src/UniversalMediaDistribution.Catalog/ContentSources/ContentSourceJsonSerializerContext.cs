using System.Text.Json.Serialization;

namespace UniversalMediaDistribution.Catalog.ContentSources;

[JsonSerializable(typeof(string))]
internal sealed partial class ContentSourceJsonSerializerContext :
    JsonSerializerContext;
