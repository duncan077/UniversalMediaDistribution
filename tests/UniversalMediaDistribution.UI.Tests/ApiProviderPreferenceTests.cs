using UniversalMediaDistribution.UI.ViewModels;

namespace UniversalMediaDistribution.UI.Tests;

public sealed class ApiProviderPreferenceTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public void ManualLookupShowsThePickerForEveryNonEmptyResult(
        int candidateCount,
        bool expected)
    {
        Assert.Equal(
            expected,
            ApiProviderPreference.ShouldShowManualPicker(candidateCount));
    }

    [Fact]
    public void FindsTheCurrentChapterProvider()
    {
        string[] candidates = ["first", "current", "third"];

        var index = ApiProviderPreference.FindPreferredProviderIndex(
            candidates,
            "current",
            static providerId => providerId);

        Assert.Equal(1, index);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("missing")]
    [InlineData("CURRENT")]
    public void ReturnsNoPreferenceWhenTheExactProviderIsUnavailable(
        string? preferredProviderId)
    {
        string[] candidates = ["first", "current"];

        var index = ApiProviderPreference.FindPreferredProviderIndex(
            candidates,
            preferredProviderId,
            static providerId => providerId);

        Assert.Equal(-1, index);
    }

    [Theory]
    [InlineData(-1, new[] { 0, 1, 2 })]
    [InlineData(0, new[] { 0, 1, 2 })]
    [InlineData(1, new[] { 1, 0, 2 })]
    [InlineData(2, new[] { 2, 0, 1 })]
    public void TriesThePreferredProviderBeforeFallbacks(
        int preferredIndex,
        int[] expectedOrder)
    {
        var actualOrder = Enumerable.Range(0, expectedOrder.Length)
            .Select(attempt => ApiProviderPreference.GetAttemptIndex(
                attempt,
                expectedOrder.Length,
                preferredIndex));

        Assert.Equal(expectedOrder, actualOrder);
    }
}
