using System.Collections.Generic;

namespace LemoineTools.Lemoine
{
    /// <summary>
    /// A single labelled result figure shown in the run strip, e.g. "120 markers".
    /// <see cref="ColorKey"/> is a theme resource key (e.g. "LemoineGreen",
    /// "LemoineAccent", "LemoineTextDim") so chips match the run's colour language.
    /// </summary>
    public struct ResultChip
    {
        public string Label;
        public int    Count;
        public string ColorKey;

        public ResultChip(string label, int count, string colorKey)
        {
            Label    = label;
            Count    = count;
            ColorKey = colorKey;
        }
    }

    /// <summary>
    /// Optional: lets a tool describe its run outcome so the result strip is self-describing
    /// instead of a bare "N pass · N fail · N skip".
    ///
    /// <list type="bullet">
    /// <item><see cref="ResultNoun"/> renames the primary (pass) label — e.g. "segments" so
    /// the strip reads "42 segments · 2 skipped · 0 failed". Read once when the strip is built,
    /// so it can be a constant.</item>
    /// <item><see cref="ResultChips"/> replaces the bare pass number with a set of labelled
    /// figures for multi-output tools (Clash → markers + dimensions; Bulk Export → per-format
    /// file counts). It is populated by the tool at completion and read by
    /// <c>StepFlowWindow</c> when the run finishes, so it returns null until then.</item>
    /// </list>
    ///
    /// Tools that don't implement this interface keep the generic pass/fail/skip labels.
    /// </summary>
    public interface ILemoineRunResult
    {
        /// <summary>Noun for the primary count, e.g. "segments". Null/empty → keep "pass".</summary>
        string? ResultNoun { get; }

        /// <summary>Per-output breakdown shown as chips, or null to keep the bare pass number.</summary>
        IReadOnlyList<ResultChip>? ResultChips { get; }
    }
}
