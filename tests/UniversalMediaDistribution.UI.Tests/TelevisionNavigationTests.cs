using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using UniversalMediaDistribution.Application.Catalog;
using UniversalMediaDistribution.Domain.Catalog;
using UniversalMediaDistribution.UI.ViewModels;
using UniversalMediaDistribution.UI.Views;

namespace UniversalMediaDistribution.UI.Tests;

public sealed class TelevisionNavigationTests
{
    [Fact]
    public void EnteringCatalogPrefersCardOverHeaderTools()
    {
        var card = Card(Guid.NewGuid());
        Assert.Same(card, MainView.SelectCatalogFocusTarget([new Button(), card], null));
    }

    [Fact]
    public void BackRestoresTheSameTitleEvenAfterCardsAreRecreated()
    {
        var rememberedId = Guid.NewGuid();
        var recreated = Card(rememberedId);
        Assert.Same(recreated, MainView.SelectCatalogFocusTarget(
            [Card(Guid.NewGuid()), recreated], rememberedId));
    }

    [Fact]
    public void RemovedTitleFallsBackToFirstAvailableCard()
    {
        var first = Card(Guid.NewGuid());
        Assert.Same(first, MainView.SelectCatalogFocusTarget([first], Guid.NewGuid()));
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void UnavailableCardsCannotReceiveRestoredFocus(bool enabled, bool visible, bool focusable)
    {
        var unavailable = Card(Guid.NewGuid());
        unavailable.IsEnabled = enabled;
        unavailable.IsVisible = visible;
        unavailable.Focusable = focusable;
        var available = Card(Guid.NewGuid());
        Assert.Same(available, MainView.SelectCatalogFocusTarget(
            [unavailable, available], ((CatalogCardViewModel)unavailable.DataContext!).Id));
    }

    [Fact]
    public void EmptyCatalogDoesNotInventACardTarget() =>
        Assert.Null(MainView.SelectCatalogFocusTarget([new Button()], null));

    [Theory]
    [InlineData(NavigationDirection.Left, 210, 100)]
    [InlineData(NavigationDirection.Right, 0, 100)]
    [InlineData(NavigationDirection.Up, 100, 210)]
    [InlineData(NavigationDirection.Down, 100, 0)]
    public void DirectionNeverChoosesAControlBehindTheViewer(
        NavigationDirection direction, double x, double y) =>
        Assert.False(MainView.IsBetterTelevisionCandidate(
            direction, new Rect(100, 100, 80, 80), new Rect(x, y, 80, 80), null));

    [Fact]
    public void HorizontalMovementStaysOnTheCurrentRow() =>
        Assert.True(MainView.IsBetterTelevisionCandidate(
            NavigationDirection.Right,
            new Rect(100, 100, 80, 80),
            new Rect(300, 100, 80, 80),
            new Rect(190, 0, 80, 80)));

    [Fact]
    public void EqualCandidatesKeepStableTraversalOrder() =>
        Assert.False(MainView.IsBetterTelevisionCandidate(
            NavigationDirection.Down,
            new Rect(100, 100, 80, 80),
            new Rect(100, 200, 80, 80),
            new Rect(100, 200, 80, 80)));

    private static Button Card(Guid id)
    {
        var card = new CatalogCard(id, MediaType.Movie, "movie.movie", "Test title",
            string.Empty, null, [], [], [], []);
        var button = new Button
        {
            DataContext = new CatalogCardViewModel(card, null!, null,
                _ => Task.CompletedTask, false, null,
                (_, _) => Task.CompletedTask, _ => Task.CompletedTask)
        };
        button.Classes.Add("catalog-card-button");
        return button;
    }
}
