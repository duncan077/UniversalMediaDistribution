using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using UniversalMediaDistribution.UI.Localization;
using UniversalMediaDistribution.UI.Views;

namespace UniversalMediaDistribution.UI.Tests;

[Collection("Player surface transitions")]
public sealed class WatchPartyPanelTests
{
    [Theory]
    [InlineData(300)]
    [InlineData(640)]
    public async Task RoomControlsFitTheAvailableWidth(double width)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(PlayerFullscreenLayoutTests.TestApp));
        await session.Dispatch(() =>
        {
            UiTextCatalog.Apply(Avalonia.Application.Current!.Resources, System.Globalization.CultureInfo.GetCultureInfo("es"));
            var panel = new WatchPartyPanel();
            // Unbound states are all visible, covering both create/join and room actions.
            var window = new Window { Width = width, Height = 800, Content = panel };
            window.Show();
            try
            {
                window.UpdateLayout();
                var buttons = panel.GetVisualDescendants().OfType<Button>()
                    .Where(b => b.IsEffectivelyVisible && b.MinHeight >= 40).ToArray();
                Assert.NotEmpty(buttons);
                foreach (var button in buttons)
                {
                    var origin = button.TranslatePoint(default, panel)!.Value;
                    Assert.InRange(origin.X, 0, width);
                    Assert.InRange(origin.X + button.Bounds.Width, 0, width + 1);
                    Assert.True(button.Bounds.Height >= 40);
                }
                Assert.NotNull(panel.FindControl<TextBox>("JoinCodeInput"));
                Assert.NotNull(panel.FindControl<Button>("ResyncPartyButton"));
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }
}
