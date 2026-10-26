using UniversalMediaDistribution.UI.Views;

namespace UniversalMediaDistribution.UI.Tests;

public sealed class TelevisionTextInputTests
{
    [Theory]
    [InlineData(false, false, false, 0, false)] // PC without an IME: no trap
    [InlineData(false, true, false, 100, false)] // stale Open, no new notification
    [InlineData(false, false, true, 100, true)] // Android IME starting
    [InlineData(false, false, true, 500, false)] // absent IME: bounded wait
    [InlineData(false, true, true, 2000, false)] // stale Android Open expires
    [InlineData(true, true, true, 5000, true)] // actual keyboard owns D-pad
    [InlineData(true, false, true, 100, false)] // keyboard already dismissed
    [InlineData(true, false, false, 1000, false)]
    public void DirectionsAreReservedOnlyForAnOpeningOrConfirmedKeyboard(
        bool confirmed, bool open, bool android, long elapsed, bool expected) =>
        Assert.Equal(expected, MainView.ShouldReserveTelevisionTextDirections(
            confirmed, open, android, elapsed));
}
