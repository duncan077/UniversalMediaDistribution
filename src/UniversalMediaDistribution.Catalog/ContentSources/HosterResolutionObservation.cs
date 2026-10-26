namespace UniversalMediaDistribution.Catalog.ContentSources;

/// <summary>Redacted diagnostic for one extractor hop, including delegated hosts.</summary>
public sealed record HosterResolutionObservation(string EmbedHost, string? Extractor, string Outcome);
