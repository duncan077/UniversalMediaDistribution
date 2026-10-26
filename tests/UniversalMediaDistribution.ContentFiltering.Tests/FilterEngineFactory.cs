using UniversalMediaDistribution.ContentFiltering;
using UniversalMediaDistribution.ContentFiltering.Compilation;
using UniversalMediaDistribution.ContentFiltering.Parsing;

namespace UniversalMediaDistribution.ContentFiltering.Tests;

internal static class FilterEngineFactory
{
    public static ContentFilterEngine Build(params string[] lines) =>
        Build(RulesetCompilerOptions.Standard, lines);

    public static ContentFilterEngine Build(
        RulesetCompilerOptions options,
        params string[] lines)
    {
        var blob = Compile(out _, options, lines);
        return new ContentFilterEngine(CompiledRuleset.Load(blob));
    }

    public static byte[] Compile(
        out RulesetCompilationReport report,
        RulesetCompilerOptions? options = null,
        params string[] lines)
    {
        var parsed = FilterListParser.Parse(string.Join('\n', lines));
        return RulesetCompiler.Compile(parsed, out report, options);
    }

    public static FilterAction Act(
        this ContentFilterEngine engine,
        string url,
        string? documentUrl = null,
        ResourceType type = ResourceType.Other) =>
        engine.Match(new FilterRequest(
            url.AsSpan(),
            documentUrl.AsSpan(),
            type)).Action;

    public static string? Redirect(
        this ContentFilterEngine engine,
        string url,
        string? documentUrl = null,
        ResourceType type = ResourceType.Other) =>
        engine.Match(new FilterRequest(
            url.AsSpan(),
            documentUrl.AsSpan(),
            type)).RedirectResource;
}
