using System.Text.Json.Serialization;

namespace UniversalMediaDistribution.ContentFiltering.Injection;

/// <summary>
/// The configuration object the injected runtime reads. Shapes and names have
/// to match <c>runtime.js</c>.
/// </summary>
internal sealed record InjectionConfig
{
    public string[] Selectors { get; init; } = [];

    /// <summary>Injected into the top document only. See the runtime.</summary>
    public string[] GenericSelectors { get; init; } = [];

    public string[] Procedural { get; init; } = [];

    public ScriptletConfig[] Scriptlets { get; init; } = [];

    public int ProceduralBudgetMs { get; init; }

    public int ObserverIdleMs { get; init; }

    public int MaxElementsPerPass { get; init; }

    public bool BuiltInDefenses { get; init; }
}

internal sealed record ScriptletConfig
{
    public string Name { get; init; } = string.Empty;

    public string[] Args { get; init; } = [];
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(InjectionConfig))]
internal sealed partial class InjectionJsonContext : JsonSerializerContext;
