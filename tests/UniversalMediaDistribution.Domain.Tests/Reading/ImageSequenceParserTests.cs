using UniversalMediaDistribution.Domain.Reading;

namespace UniversalMediaDistribution.Domain.Tests.Reading;

public sealed class ImageSequenceParserTests
{
    [Fact]
    public void Parse_OrdersPagesByNumericSuffix()
    {
        var files = Enumerable.Range(0, 11)
            .Reverse()
            .Select(position => new ImageSequenceSourceFile(position + 20, $"chapter/page_{position}.webp", 1_000 + position))
            .ToArray();

        var pages = ImageSequenceParser.Parse(files);

        Assert.Equal(Enumerable.Range(0, 11), pages.Select(static page => page.Position));
        Assert.Equal("chapter/page_10.webp", pages[10].Path);
    }

    [Fact]
    public void Parse_IgnoresUnsupportedAndUnnumberedFiles()
    {
        ImageSequenceSourceFile[] files =
        [
            new(0, "chapter/cover.webp", 100),
            new(1, "chapter/notes.txt", 100),
            new(2, "chapter/page_0.png", 100)
        ];

        var pages = ImageSequenceParser.Parse(files);

        var page = Assert.Single(pages);
        Assert.Equal(0, page.Position);
        Assert.Equal(2, page.FileIndex);
    }

    [Fact]
    public void Parse_RejectsMissingZeroPosition()
    {
        ImageSequenceSourceFile[] files = [new(0, "chapter/page_1.jpg", 100)];

        var exception = Assert.Throws<ImageSequenceValidationException>(() => ImageSequenceParser.Parse(files));

        Assert.Contains("Expected position 0", exception.Message);
    }

    [Fact]
    public void Parse_RejectsGap()
    {
        ImageSequenceSourceFile[] files =
        [
            new(0, "chapter/page_0.jpg", 100),
            new(1, "chapter/page_2.jpg", 100)
        ];

        var exception = Assert.Throws<ImageSequenceValidationException>(() => ImageSequenceParser.Parse(files));

        Assert.Contains("Expected position 1", exception.Message);
    }

    [Fact]
    public void Parse_RejectsDuplicatePosition()
    {
        ImageSequenceSourceFile[] files =
        [
            new(0, "chapter/page_0.jpg", 100),
            new(1, "chapter/scan_0.png", 100)
        ];

        var exception = Assert.Throws<ImageSequenceValidationException>(() => ImageSequenceParser.Parse(files));

        Assert.Contains("duplicate position 0", exception.Message);
    }
}

