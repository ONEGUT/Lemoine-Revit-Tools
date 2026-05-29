using System.Collections.Generic;

namespace LemoineTools.Lemoine
{
    /// <summary>
    /// Optional contract for a tool whose FINAL step is a review summary.
    ///
    /// When a tool implements this, <see cref="StepFlowWindow"/> renders the last step
    /// automatically as a <c>LemoineReviewSummary</c> — the tool no longer hand-rolls a
    /// 2×2 card grid in <c>GetStepContent</c> for that step (it can return null/empty for
    /// the final step id). The framework reads these members each time the review step is
    /// shown (and on every ValidationChanged), so the values always reflect the latest
    /// input. The result: the last step is review-only by construction, forever.
    ///
    /// All members are read on the WPF UI thread.
    /// </summary>
    public interface ILemoineReviewable
    {
        /// <summary>Ordered (id, label) pairs — one summary card per item.</summary>
        IList<(string id, string label)> ReviewItems { get; }

        /// <summary>Current display value for each item id (missing ids render as "—").</summary>
        IDictionary<string, string> ReviewValues { get; }

        /// <summary>Optional chips/tags shown below the cards (null or empty = none).</summary>
        IList<string>? ReviewChips { get; }

        /// <summary>Optional italic note shown beneath the summary (null = none).</summary>
        string? ReviewNote { get; }

        /// <summary>Optional warning banner shown above the summary (null = none).</summary>
        string? ReviewWarning { get; }
    }
}
