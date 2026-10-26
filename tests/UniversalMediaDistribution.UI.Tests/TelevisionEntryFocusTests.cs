using Avalonia.Controls;
using UniversalMediaDistribution.UI.Views;

namespace UniversalMediaDistribution.UI.Tests;

/// <summary>
/// Guards the rule that decides which action of a chapter the television
/// remote lands on when the pad arrives at that chapter.
/// </summary>
public sealed class TelevisionEntryFocusTests
{
    private static Button Action(string? styleClass = null)
    {
        var button = new Button();
        if (styleClass is not null)
        {
            button.Classes.Add(styleClass);
        }

        return button;
    }

    [Fact]
    public void EntryWithoutAPickerIsEnteredOnItsFirstAction()
    {
        var search = Action();
        var markSeen = Action();

        var leading = MainView.SelectLeadingEntryAction([search, markSeen]);

        Assert.Same(search, leading);
    }

    /// <summary>
    /// The regression: the source picker renders below the entry's own
    /// actions, so taking the first action outright bounced the pad straight
    /// back onto "search the enabled APIs" every time it arrived. That made
    /// the provider buttons reachable only by the one-shot focus fired when
    /// the picker first appeared — leave the entry once and no amount of
    /// up/down ever got back to them.
    /// </summary>
    [Fact]
    public void EntryWithAnOpenPickerIsEnteredOnTheProviderChoice()
    {
        var search = Action();
        var markSeen = Action();
        var cancel = Action();
        var provider = Action(MainView.ProviderChoiceClass);

        var leading = MainView.SelectLeadingEntryAction(
            [search, markSeen, cancel, provider]);

        Assert.Same(provider, leading);
    }

    [Fact]
    public void TheFirstProviderChoiceWinsWhenSeveralProvidersMatched()
    {
        var search = Action();
        var first = Action(MainView.ProviderChoiceClass);
        var second = Action(MainView.ProviderChoiceClass);

        var leading = MainView.SelectLeadingEntryAction([search, first, second]);

        Assert.Same(first, leading);
    }

    [Fact]
    public void AProviderChoiceAlreadyLeadingIsKept()
    {
        var provider = Action(MainView.ProviderChoiceClass);
        var search = Action();

        var leading = MainView.SelectLeadingEntryAction([provider, search]);

        Assert.Same(provider, leading);
    }

    /// <summary>
    /// Callers filter to what is visible in the viewport, so an entry that is
    /// scrolled out contributes nothing and focus must be left alone rather
    /// than snapped onto the list container.
    /// </summary>
    [Fact]
    public void AnEntryWithNoVisibleActionsSelectsNothing()
    {
        Assert.Null(MainView.SelectLeadingEntryAction([]));
    }
}
