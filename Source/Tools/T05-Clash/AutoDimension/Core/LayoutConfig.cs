namespace LemoineTools.Tools.Clash.AutoDimension.Core
{
    /// <summary>
    /// Revit-free layout + scoring configuration. All distances are in feet (model units);
    /// paper-space values have already been multiplied by the view scale before they reach
    /// the core. Versioned via <see cref="SchemaVersion"/> so persisted configs migrate.
    /// </summary>
    public sealed class LayoutConfig
    {
        /// <summary>Config schema version. v1 from the first release.</summary>
        public int SchemaVersion { get; set; } = 1;

        // ── Spacing / precision (paper-space, model-ft equivalent) ─────────────
        /// <summary>Paper-space gap between stacked dimension strings. Default 1/2".</summary>
        public double StringSpacingFt { get; set; } = (1.0 / 2.0) / 12.0;

        /// <summary>First string's offset from the source run. Default 1/4" — the dimension sits
        /// close to the clash (halved from 1/2" to match the hand drawing).</summary>
        public double FirstOffsetFt { get; set; } = (1.0 / 4.0) / 12.0;

        /// <summary>Rounding precision applied to displayed values. Default 1/8".</summary>
        public double PrecisionFt { get; set; } = (1.0 / 8.0) / 12.0;

        /// <summary>Paper-space text height used to size collision bands. Default 3/32".</summary>
        public double TextHeightFt { get; set; } = (3.0 / 32.0) / 12.0;

        // ── Witness (extension) line anatomy (paper-space) ─────────────────────
        /// <summary>Visible gap between the anchor and the start of its witness line.
        /// Default 1/16" (ASME Y14.5 §1.7.2).</summary>
        public double WitnessGapFt { get; set; } = (1.0 / 16.0) / 12.0;

        /// <summary>How far a witness line extends past the outermost dimension line.
        /// Default 1/8" (ASME Y14.5 §1.7.2).</summary>
        public double WitnessOvershootFt { get; set; } = (1.0 / 8.0) / 12.0;

        // ── Moved-tag column geometry (multiples of text height; shared by the
        //    plan-time TagColumnPlanner and the commit-time realizer) ────────────
        /// <summary>First tag's clearance above/below the dimension arc.</summary>
        public double TagColumnBaseHeights { get; set; } = 2.2;

        /// <summary>Perpendicular step between stacked tags.</summary>
        public double TagColumnStepHeights { get; set; } = 1.4;

        /// <summary>Column offset past the group's far text edge, along the axis.</summary>
        public double TagColumnAlongHeights { get; set; } = 0.75;

        // ── Scoring weights ────────────────────────────────────────────────────
        /// <summary>Penalty per unit of paper-space overlap area with an obstacle (hard).</summary>
        public double OverlapWeight { get; set; } = 1000.0;

        /// <summary>Penalty for a string crossing the crop boundary (hard).</summary>
        public double OffCropWeight { get; set; } = 1000.0;

        /// <summary>Penalty when a witness line crosses dimension text (hard).</summary>
        public double WitnessCrossWeight { get; set; } = 500.0;

        /// <summary>Penalty per genuine line crossing a drafting standard forbids: a dimension
        /// line crossing another dimension line or a witness line (hard, ASME Y14.5 §1.7.2 —
        /// witness lines may cross each other; dimension lines never cross anything).</summary>
        public double CrossingWeight { get; set; } = 800.0;

        /// <summary>Soft penalty per leader-leader crossing (drafting: leaders never cross).</summary>
        public double LeaderCrossWeight { get; set; } = 25.0;

        /// <summary>Soft penalty per leader crossing a dimension or witness line.</summary>
        public double LeaderLineCrossWeight { get; set; } = 10.0;

        /// <summary>Soft penalty per foot of leader length beyond the minimum useful reach —
        /// keeps moved tags near their segment instead of drifting.</summary>
        public double LeaderSlackWeight { get; set; } = 1.0;

        /// <summary>Soft penalty per foot of text overflow on a cramped segment. Low, so a drafter-
        /// style inline overhang on a short segment is cheap rather than forcing a move.</summary>
        public double CrampedWeight { get; set; } = 3.0;

        /// <summary>Soft penalty per foot of deviation from target string spacing.</summary>
        public double UnevenSpacingWeight { get; set; } = 5.0;

        /// <summary>Soft penalty per leadered segment. High, so leadering text out is a last resort
        /// (a drafter lets short text overhang instead) and rarely beats an inline/staggered tag.</summary>
        public double LeaderWeight { get; set; } = 40.0;

        // ── Stacking / refinement ──────────────────────────────────────────────
        /// <summary>Worst-first repair passes after the greedy passes (0 disables repair).</summary>
        public int MaxRepairPasses { get; set; } = 3;

        /// <summary>Pull near-level, span-disjoint same-axis strings onto one shared line
        /// (NCS: "align dimensions in one line").</summary>
        public bool AlignSharedRows { get; set; } = true;

        /// <summary>Penalise un-staggered value texts on adjacent rows (ASME staggering
        /// convention for stacked dimensions).</summary>
        public bool StaggerStackedText { get; set; } = true;

        /// <summary>Soft penalty per pair of along-axis-overlapping texts on adjacent rows.</summary>
        public double StaggerWeight { get; set; } = 2.0;

        // ── Convergence ────────────────────────────────────────────────────────
        /// <summary>Hard cap on layout iterations (determinism + no runaway).</summary>
        public int MaxIterations { get; set; } = 50;

        /// <summary>Wall-clock cap in milliseconds for the layout loop.</summary>
        public int TimeCapMs { get; set; } = 4000;

        /// <summary>Soft-score delta under which two consecutive iterations count as a plateau.</summary>
        public double PlateauEpsilon { get; set; } = 1e-4;

        /// <summary>Max distinct offset steps to try when shifting a string off a collision.</summary>
        public int MaxOffsetSteps { get; set; } = 8;
    }
}
