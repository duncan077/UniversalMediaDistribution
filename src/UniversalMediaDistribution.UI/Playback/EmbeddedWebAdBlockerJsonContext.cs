using System.Text.Json.Serialization;

namespace UniversalMediaDistribution.UI.Playback;

[JsonSerializable(typeof(string[]))]
internal sealed partial class EmbeddedWebAdBlockerJsonContext :
    JsonSerializerContext;
