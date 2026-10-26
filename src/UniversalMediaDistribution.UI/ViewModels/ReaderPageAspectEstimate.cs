namespace UniversalMediaDistribution.UI.ViewModels;

/// <summary>
/// The chapter's running idea of how tall a page is, shared by every page in
/// it so the ones not yet decoded reserve a box the same shape as the ones
/// that are.
/// <para>
/// The reserved height is what the virtualizing panel averages to estimate the
/// height of everything it has not realized. With one fixed guess for every
/// undecoded page, scrolling into a run of them dropped that average sharply —
/// a chapter of tall strips being guessed at an ordinary page's proportions —
/// and the estimated extent shrank below the offset already scrolled to. The
/// viewport clamp then resolved that by moving to the largest offset the new
/// extent allowed, which is the end of the chapter.
/// </para>
/// <para>
/// Averaging what this chapter's own pages actually measured keeps the guess
/// close to the truth, so the estimate stops swinging as pages load and unload.
/// </para>
/// </summary>
internal sealed class ReaderPageAspectEstimate
{
    /// <summary>
    /// Height over width for a typical comic or manga page, used until this
    /// chapter has measured one of its own.
    /// </summary>
    public const double TypicalPageAspectRatio = 1.4;

    /// <summary>
    /// How far the average must move before the pages waiting on it are told.
    /// Every decoded page reports, and remeasuring the undecoded ones for a
    /// fraction of a percent would cost more layout than the accuracy is worth.
    /// </summary>
    private const double MeaningfulChange = 0.02;

    private double _total;
    private int _count;

    public double Ratio { get; private set; } = TypicalPageAspectRatio;

    /// <summary>
    /// Raised when the estimate has moved enough that pages reserving space
    /// from it should measure again.
    /// </summary>
    public event EventHandler? Changed;

    public void Report(double ratio)
    {
        if (!double.IsFinite(ratio) || ratio <= 0)
        {
            return;
        }

        _total += ratio;
        _count++;

        var updated = _total / _count;
        var moved = Math.Abs(updated - Ratio) > Ratio * MeaningfulChange;
        Ratio = updated;
        if (moved)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
