namespace UniversalMediaDistribution.Desktop.Tests;

/// <summary>
/// Guards the quoting bug that silently broke every desktop update from 0.7.11
/// to 0.8.2. The generated script contained
/// <c>robocopy "C:\...\UMD\" "C:\...\backup" /e ...</c>; cmd reads the
/// backslash before the closing quote as an escape, so the argument never ends
/// and robocopy took the destination and every switch as part of one source
/// path. It reported no destination and copied nothing, and because the exit
/// code was not a failure the script carried on and reported success.
/// </summary>
public sealed class DesktopUpdateStagingQuotingTests
{
    [Theory]
    // A path that already ends in a separator is the case that shipped broken.
    [InlineData("C:\\Users\\someone\\OneDrive\\Escritorio\\UMD\\")]
    // A drive root keeps its separator even after TrimEndingDirectorySeparator,
    // so trimming alone never fixed this one.
    [InlineData("C:\\")]
    [InlineData("C:\\Program Files\\UMD\\")]
    public void AQuotedDirectoryNeverEndsWithABackslashBeforeTheQuote(
        string directory)
    {
        var quoted = DesktopApplicationUpdateService.QuoteForCommand(directory);

        Assert.StartsWith("\"", quoted, StringComparison.Ordinal);
        Assert.EndsWith("\"", quoted, StringComparison.Ordinal);
        Assert.DoesNotContain("\\\"", quoted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("C:\\Users\\someone\\UMD")]
    [InlineData("C:\\Program Files\\UMD")]
    public void ADirectoryWithoutATrailingSeparatorIsQuotedUnchanged(
        string directory)
    {
        var quoted = DesktopApplicationUpdateService.QuoteForCommand(directory);

        Assert.Equal($"\"{directory}\"", quoted);
        Assert.DoesNotContain("\\\"", quoted, StringComparison.Ordinal);
    }

    /// <summary>
    /// The stand-in for the removed separator still has to name the same
    /// directory, or robocopy would copy the wrong tree.
    /// </summary>
    [Fact]
    public void TheQuotedFormStillResolvesToTheSameDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "umd-quote-probe");
        Directory.CreateDirectory(directory);
        try
        {
            var quoted = DesktopApplicationUpdateService.QuoteForCommand(
                directory + Path.DirectorySeparatorChar);
            var unquoted = quoted.Trim('"');

            Assert.Equal(
                Path.GetFullPath(directory),
                Path.GetFullPath(unquoted));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
