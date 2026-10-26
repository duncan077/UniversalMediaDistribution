using System.Reflection;
using System.Text;
using System.Text.Json;

namespace UniversalMediaDistribution.ContentFiltering.Injection;

public sealed record InjectionOptions
{
    /// <summary>
    /// How long one procedural pass may hold the main thread. A frame is
    /// sixteen milliseconds; anything approaching that is a dropped one.
    /// </summary>
    public int ProceduralBudgetMilliseconds { get; init; } = 12;

    /// <summary>
    /// How long the DOM must stay still before the observer disconnects. An
    /// embed page settles quickly, and watching it for the rest of a two-hour
    /// film buys nothing.
    /// </summary>
    public int ObserverIdleMilliseconds { get; init; } = 5000;

    public int MaxElementsPerPass { get; init; } = 2000;

    /// <summary>
    /// Popup suppression and click-trap removal. On by default at every tier:
    /// these are what the player hosts actually do, and no filter list
    /// describes them.
    /// </summary>
    public bool BuiltInDefenses { get; init; } = true;

    public bool IncludeProcedural { get; init; } = true;

    /// <summary>
    /// A sanity ceiling on hiding selectors, not a throttle.
    ///
    /// It used to be four thousand, which silently dropped roughly seven out
    /// of ten of the selectors EasyList offers — and since neither of the
    /// hosts this application embeds has any host-specific rules, generic
    /// hiding is the entire cosmetic defence for them. Truncating it is why
    /// advertisement boxes survived. The whole set costs about a quarter of a
    /// megabyte, which is not a cost worth trading correctness for; a device
    /// that genuinely cannot afford it declines generic hiding altogether
    /// rather than taking an arbitrary prefix of it.
    /// </summary>
    public int MaxSelectors { get; init; } = 50_000;

    public static InjectionOptions Standard { get; } = new();

    /// <summary>
    /// What a low-memory television box gets: no procedural selectors, a
    /// smaller sweep, and a shorter observer life. The built-in defences stay.
    /// </summary>
    public static InjectionOptions Constrained { get; } = new()
    {
        IncludeProcedural = false,
        MaxElementsPerPass = 500,
        MaxSelectors = 500,
        ObserverIdleMilliseconds = 3000,
        ProceduralBudgetMilliseconds = 6
    };
}

/// <summary>
/// Assembles the script a page gets at document start: its configuration, the
/// scriptlet library, and the cosmetic runtime.
/// </summary>
public static class InjectionPayloadBuilder
{
    /// <summary>
    /// The scriptlets this build implements. A filter list naming anything
    /// else is dropped here rather than shipped and ignored in the page.
    /// Kept deliberately in step with the registry in <c>scriptlets.js</c>.
    /// </summary>
    public static IReadOnlySet<string> SupportedScriptlets { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "abort-current-script",
            "abort-on-property-read",
            "abort-on-property-write",
            "json-prune",
            "no-fetch-if",
            "no-xhr-if",
            "noeval-if",
            "nowebrtc",
            "prevent-setInterval",
            "prevent-setTimeout",
            "prevent-window-open",
            "remove-attr",
            "remove-class",
            "set-constant"
        };

    private static readonly string ScriptletLibrary = ReadResource("scriptlets.js");

    private static readonly string Runtime = ReadResource("runtime.js");

    public static string Build(
        CosmeticPayload payload,
        InjectionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var settings = options ?? InjectionOptions.Standard;
        var config = new InjectionConfig
        {
            Selectors = Take(payload.Selectors, settings.MaxSelectors),
            GenericSelectors = Take(
                payload.GenericSelectors,
                settings.MaxSelectors),
            Procedural = settings.IncludeProcedural
                ? Take(payload.ProceduralSelectors, settings.MaxSelectors)
                : [],
            Scriptlets = ReadScriptlets(payload.Scriptlets),
            ProceduralBudgetMs = settings.ProceduralBudgetMilliseconds,
            ObserverIdleMs = settings.ObserverIdleMilliseconds,
            MaxElementsPerPass = settings.MaxElementsPerPass,
            BuiltInDefenses = settings.BuiltInDefenses
        };

        var builder = new StringBuilder(
            ScriptletLibrary.Length + Runtime.Length + 1024);
        builder.Append("globalThis.__umdFilterConfig=")
            .Append(JsonSerializer.Serialize(
                config,
                InjectionJsonContext.Default.InjectionConfig))
            .AppendLine(";")
            .AppendLine(ScriptletLibrary)
            .AppendLine(Runtime);
        return builder.ToString();
    }

    private static string[] Take(IReadOnlyList<string> values, int limit)
    {
        var count = Math.Min(values.Count, limit);
        if (values.Count > count)
        {
            // Worth saying out loud: past the ceiling a host gets an arbitrary
            // prefix of its hiding rules, and the advertisement boxes whose
            // selectors fell off the end simply stay on screen.
            System.Diagnostics.Trace.TraceWarning(
                $"Cosmetic filtering dropped {values.Count - count} of " +
                $"{values.Count} selectors to stay within the ceiling.");
        }

        var result = new string[count];
        for (var index = 0; index < count; index++)
        {
            result[index] = values[index];
        }

        return result;
    }

    private static ScriptletConfig[] ReadScriptlets(
        IReadOnlyList<string> bodies)
    {
        var result = new List<ScriptletConfig>(bodies.Count);
        foreach (var body in bodies)
        {
            if (ScriptletInvocation.TryParse(body, out var invocation) &&
                SupportedScriptlets.Contains(invocation.Name))
            {
                result.Add(new ScriptletConfig
                {
                    Name = invocation.Name,
                    Args = [.. invocation.Arguments]
                });
            }
        }

        return [.. result];
    }

    private static string ReadResource(string name)
    {
        var assembly = typeof(InjectionPayloadBuilder).Assembly;
        var path =
            $"{assembly.GetName().Name}.Resources.{name}";
        using var stream = assembly.GetManifestResourceStream(path) ??
            throw new InvalidOperationException(
                $"The embedded resource '{path}' is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
