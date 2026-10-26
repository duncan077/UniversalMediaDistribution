using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using UniversalMediaDistribution.Application.Catalog;
using UniversalMediaDistribution.Application.UserState;
using UniversalMediaDistribution.Reading;
using UniversalMediaDistribution.Torrenting;
using UniversalMediaDistribution.UI.Localization;
using UniversalMediaDistribution.UI.ViewModels;
using UniversalMediaDistribution.UI.Views;

namespace UniversalMediaDistribution.UI.Tests;

[Collection("Player surface transitions")]
public sealed class ReaderAutoScrollTests
{
    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(144)]
    public void SmoothMovementHasTheSameSpeedAcrossRefreshRates(int frames)
    {
        double offset = 0;
        for (var i = 0; i < frames; i++) offset = ReaderScrollMotion.Advance(offset, 10000, 60, 1d / frames);
        Assert.Equal(60, offset, 6);
    }

    [Fact]
    public void StallsAndChapterBoundariesNeverCauseALargeJump()
    {
        Assert.Equal(103, ReaderScrollMotion.Advance(100, 1000, 60, 5));
        Assert.Equal(1000, ReaderScrollMotion.Advance(999, 1000, 180, 0.05));
        Assert.Equal(0, ReaderScrollMotion.Advance(1, 1000, -1200, 0.05));
        Assert.Equal(0, ReaderScrollMotion.Advance(0, 0, 60, 0.05));
    }

    [Fact]
    public void MiddleScrollHasADeadZoneTwoDirectionsAndABoundedSpeed()
    {
        Assert.Equal(0, ReaderScrollMotion.PointerVelocity(12));
        Assert.Equal(0, ReaderScrollMotion.PointerVelocity(-8));
        Assert.Equal(-ReaderScrollMotion.PointerVelocity(70), ReaderScrollMotion.PointerVelocity(-70));
        Assert.InRange(ReaderScrollMotion.PointerVelocity(100000), 1, 1200);
        Assert.Equal(0, ReaderScrollMotion.PointerVelocity(double.NaN));
    }

    [Fact]
    public async Task ModeChangesAndDisposalStopMovementWithoutLosingSpeed()
    {
        using var reader = await CreateReaderAsync();
        reader.ToggleAutoScrollCommand.Execute(null);
        Assert.False(reader.IsAutoScrollEnabled);
        reader.UseContinuousModeCommand.Execute(null);
        reader.AutoScrollSpeed = 1.75;
        reader.ToggleAutoScrollCommand.Execute(null);
        Assert.True(reader.IsAutoScrollEnabled);
        reader.UsePagedModeCommand.Execute(null);
        Assert.False(reader.IsAutoScrollEnabled);
        reader.UseContinuousModeCommand.Execute(null);
        Assert.False(reader.IsAutoScrollEnabled);
        Assert.Equal(1.75, reader.AutoScrollSpeed);
        reader.IsAutoScrollEnabled = true;
        reader.Dispose();
        Assert.False(reader.IsAutoScrollEnabled);
        reader.IsAutoScrollEnabled = true;
        Assert.False(reader.IsAutoScrollEnabled);
    }

    [Theory]
    [InlineData(280)]
    [InlineData(640)]
    public async Task SpanishControlsFitAndTheSliderChangesTheReaderSpeed(double width)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(PlayerFullscreenLayoutTests.TestApp));
        await session.Dispatch<bool>(async () =>
        {
            UiTextCatalog.Apply(Avalonia.Application.Current!.Resources, System.Globalization.CultureInfo.GetCultureInfo("es"));
            using var reader = await CreateReaderAsync();
            var controls = new ReaderAutoScrollControls { DataContext = reader };
            var window = new Window { Width = width, Height = 300, Content = controls };
            window.Show();
            try
            {
                window.UpdateLayout();
                foreach (var child in controls.GetVisualDescendants().Where(v => v is Button or Slider))
                {
                    var position = child.TranslatePoint(default, controls)!.Value;
                    Assert.InRange(position.X, 0, width);
                    Assert.InRange(position.X + child.Bounds.Width, 0, width + 1);
                }
                controls.FindControl<Slider>("SpeedSlider")!.Value = 2.5;
                Assert.Equal(2.5, reader.AutoScrollSpeed);
                Assert.Equal("2.5×", reader.AutoScrollSpeedLabel);
                controls.FindControl<Slider>("SpeedSlider")!.Value = 10;
                Assert.Equal(10, reader.AutoScrollSpeed);
                Assert.Equal("10×", reader.AutoScrollSpeedLabel);
                reader.SetAutoScrollSpeedCommand.Execute("5");
                Assert.Equal(5, reader.AutoScrollSpeed);
                reader.AutoScrollSpeed = 25;
                Assert.Equal(10, reader.AutoScrollSpeed);
            }
            finally { window.Close(); }
            return true;
        }, CancellationToken.None);
    }

    internal static ValueTask<ImageReaderViewModel> CreateReaderAsync()
    {
        var id = Guid.NewGuid();
        var identity = new CatalogContentIdentity(id, $"book:{id:N}");
        return ImageReaderViewModel.CreateAsync("Test chapter", identity, identity,
            new TestDocument(), new InMemoryUserMediaStateStore());
    }

    private sealed class TestDocument : IPdfDocument
    {
        public IReadOnlyList<PdfPageDescriptor> Pages { get; } = [new(0, 800, 1600), new(1, 800, 1600)];
        public ValueTask<IMediaReadHandle> OpenPageReadAsync(PdfPageDescriptor page, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<IMediaReadHandle>(new IOException("No external image requests in this test."));
        public void Dispose() { }
    }
}
