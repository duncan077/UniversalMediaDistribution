namespace UniversalMediaDistribution.Domain.Reading;

public sealed record ImageSequenceSourceFile(int FileIndex, string Path, long Length);

public sealed record ImageSequencePage(int Position, int FileIndex, string Path, long Length);

