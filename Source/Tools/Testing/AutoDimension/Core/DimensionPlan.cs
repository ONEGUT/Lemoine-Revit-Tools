using System.Collections.Generic;

namespace LemoineTools.Tools.Testing.AutoDimension.Core
{
    /// <summary>Which destination a dimension measures out to. All are first-class peers.</summary>
    public enum TargetType { Grid, SlabEdge, ManualDatum }

    /// <summary>Per-segment text placement chosen by the layout core to manage density.</summary>
    public enum SegmentTextState { Inline, Flipped, Staggered, LeaderOut }

    /// <summary>Which side of the source run a dimension string is pulled toward.</summary>
    public enum DimSide { Positive, Negative }

    /// <summary>One measured span within a dimension string (between two adjacent references).</summary>
    public sealed class PlannedSegment
    {
        /// <summary>Model length of this segment in feet.</summary>
        public double LengthFt { get; set; }

        /// <summary>Paper-space width of the value text, expressed in model feet (already scaled).</summary>
        public double TextWidthFt { get; set; }

        /// <summary>Text placement decided by layout. Inline until the core moves it.</summary>
        public SegmentTextState TextState { get; set; } = SegmentTextState.Inline;

        /// <summary>True when this segment cannot fit its text inline at its current length.</summary>
        public bool IsCramped => TextWidthFt > LengthFt;
    }

    /// <summary>
    /// One dimension to place, fully described in abstract view/paper space. Holds stable
    /// string keys (not Revit References) so the plan stays serializable; the Revit I/O layer
    /// keeps a parallel map from <see cref="SourceKey"/> to the actual References for commit.
    /// </summary>
    public sealed class PlannedDimension
    {
        /// <summary>Identity of the source cross-line (its ElementId, stringified). Stable per run.</summary>
        public string SourceKey { get; set; } = "";

        /// <summary>Identity of the resolved target — used for stale-target cleanup across runs.</summary>
        public string TargetKey { get; set; } = "";

        public TargetType TargetType { get; set; }

        /// <summary>Anchor point on the source run (view coords).</summary>
        public Vec2 SourcePoint { get; set; }

        /// <summary>Anchor point on the resolved target (view coords).</summary>
        public Vec2 TargetPoint { get; set; }

        /// <summary>Unit measurement axis (default horizontal). Parameterised for future variants.</summary>
        public Vec2 AxisDir { get; set; } = new Vec2(1, 0);

        public DimSide Side { get; set; } = DimSide.Positive;

        /// <summary>Current paper-space offset of the dimension line from the source run (model ft).</summary>
        public double OffsetFt { get; set; }

        public List<PlannedSegment> Segments { get; set; } = new List<PlannedSegment>();

        /// <summary>Paper-space bounds of the whole string (text + line) for collision tests.</summary>
        public Box2 PaperBounds { get; set; }
    }

    /// <summary>A source line whose target could not be resolved. Reported, never silently dropped.</summary>
    public sealed class UnresolvedTarget
    {
        public string SourceKey { get; set; } = "";
        public TargetType TargetType { get; set; }
        public string Reason { get; set; } = "";
    }

    /// <summary>Two top candidates scored within the ambiguity threshold — user must resolve.</summary>
    public sealed class AmbiguousTarget
    {
        public string SourceKey { get; set; } = "";
        public TargetType TargetType { get; set; }
        public string CandidateA { get; set; } = "";
        public string CandidateB { get; set; } = "";
        public double ScoreA { get; set; }
        public double ScoreB { get; set; }
    }

    /// <summary>
    /// The serializable, inspectable boundary between the engine and commit. Contains every
    /// dimension to place, every unresolved target, every flagged ambiguity, and notes.
    /// Commit is a dumb consumer of an approved plan.
    /// </summary>
    public sealed class DimensionPlan
    {
        public int SchemaVersion { get; set; } = 1;

        /// <summary>Identity of this run, stamped onto every placed dimension's owner entity.</summary>
        public string RunId { get; set; } = "";

        public List<PlannedDimension> Dimensions { get; set; } = new List<PlannedDimension>();
        public List<UnresolvedTarget> Unresolved { get; set; } = new List<UnresolvedTarget>();
        public List<AmbiguousTarget>  Ambiguities { get; set; } = new List<AmbiguousTarget>();

        /// <summary>Link references that could not be obtained (reported, not fatal).</summary>
        public List<string> MissingLinkRefs { get; set; } = new List<string>();

        /// <summary>Free-form diagnostics for the report (unsatisfiable layout, plateau, etc.).</summary>
        public List<string> Notes { get; set; } = new List<string>();
    }
}
