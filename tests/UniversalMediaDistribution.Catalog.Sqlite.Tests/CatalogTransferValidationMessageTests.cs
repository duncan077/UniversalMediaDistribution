using UniversalMediaDistribution.Catalog.Transfer;

namespace UniversalMediaDistribution.Catalog.Sqlite.Tests;

/// <summary>
/// A rejected sync or import has to say which rule failed. The message is the
/// only part that reaches the screen, so the paths the validator collected
/// have to survive into it.
/// </summary>
public sealed class CatalogTransferValidationMessageTests
{
    [Fact]
    public void TheMessageNamesTheFailingRule()
    {
        var exception = new CatalogTransferValidationException(
            ["items[3].sources[0].torrent is required."]);

        Assert.Contains(
            "items[3].sources[0].torrent is required.",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ManyFailuresAreSummarisedRatherThanListedInFull()
    {
        var errors = Enumerable.Range(0, 300)
            .Select(index => $"items[{index}].name is required.")
            .ToArray();

        var exception = new CatalogTransferValidationException(errors);

        Assert.Contains("items[0].name is required.", exception.Message);
        Assert.Contains("297 more", exception.Message);
        Assert.DoesNotContain("items[299]", exception.Message);
        Assert.Equal(300, exception.Errors.Count);
    }

    [Fact]
    public void EveryRuleIsStillAvailableOnTheException()
    {
        // The message is trimmed for a status bar; a log or a test needs all
        // of them.
        var exception = new CatalogTransferValidationException(
            ["a", "b", "c", "d"]);

        Assert.Equal(["a", "b", "c", "d"], exception.Errors);
    }
}
