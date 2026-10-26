using System.Globalization;
using System.Text.RegularExpressions;

namespace UniversalMediaDistribution.Domain.Reading;

public static partial class ImageSequenceParser
{
    private static readonly HashSet<string> SupportedExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg",
            ".jpeg",
            ".png",
            ".webp",
            ".avif"
        };

    public static IReadOnlyList<ImageSequencePage> Parse(IEnumerable<ImageSequenceSourceFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        var pagesByPosition = new Dictionary<int, ImageSequencePage>();

        foreach (var file in files)
        {
            if (file.FileIndex < 0)
            {
                throw new ImageSequenceValidationException($"File '{file.Path}' has an invalid negative torrent index.");
            }

            if (file.Length < 0)
            {
                throw new ImageSequenceValidationException($"File '{file.Path}' has an invalid negative length.");
            }

            if (string.IsNullOrWhiteSpace(file.Path))
            {
                continue;
            }

            var extension = Path.GetExtension(file.Path);
            if (!SupportedExtensions.Contains(extension))
            {
                continue;
            }

            var fileName = Path.GetFileNameWithoutExtension(file.Path);
            var match = PositionSuffixRegex().Match(fileName);
            if (!match.Success)
            {
                continue;
            }

            if (!int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var position))
            {
                throw new ImageSequenceValidationException($"File '{file.Path}' has a page position outside the supported range.");
            }

            var page = new ImageSequencePage(position, file.FileIndex, file.Path, file.Length);
            if (!pagesByPosition.TryAdd(position, page))
            {
                throw new ImageSequenceValidationException($"Image sequence contains duplicate position {position}.");
            }
        }

        if (pagesByPosition.Count == 0)
        {
            throw new ImageSequenceValidationException("Image sequence does not contain any supported files ending in _<position>.");
        }

        var pages = pagesByPosition.Values.OrderBy(static page => page.Position).ToArray();

        for (var expectedPosition = 0; expectedPosition < pages.Length; expectedPosition++)
        {
            if (pages[expectedPosition].Position != expectedPosition)
            {
                throw new ImageSequenceValidationException(
                    $"Image sequence must be contiguous and start at 0. Expected position {expectedPosition}, found {pages[expectedPosition].Position}.");
            }
        }

        return pages;
    }

    [GeneratedRegex("_([0-9]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex PositionSuffixRegex();
}

