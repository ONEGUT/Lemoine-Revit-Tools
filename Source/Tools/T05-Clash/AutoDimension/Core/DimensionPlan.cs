using System.Collections.Generic;
using System.Xml.Serialization;

namespace LemoineTools.Tools.Clash.AutoDimension.Core
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

        /// <summary>Planned view-2D centre of this segment's relocated value text when the
        /// layout moved it out of line (see <see cref="TagColumnPlanner"/>); null while inline.
        /// Derived — recomputed whenever side/offset/states change.</summary>
        [XmlIgnore]
        public Vec2? TagPos { get; set; }

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

        /// <summary>Which way along the axis the moved-tag column hangs off its group:
        /// +1 past the far edge (right for an x-string), -1 before the near edge (left).
        /// Searched by the layout exactly like <see cref="Side"/>, so a busy right side
        /// flips the column left as easily as a busy top flips the string below.</summary>
        public int TagColumnDir { get; set; } = 1;

        /// <summary>Which side of the dimension LINE the moved-tag column stacks toward:
        /// +1 the offset side (above a positive-side string), -1 the opposite side (dragged
        /// below the dimension). Searched by the layout like <see cref="TagColumnDir"/> — the
        /// below side is what clears a column fouled by its own witness lines (witnesses from
        /// scattered cluster anchors can cross the above-line column zone).</summary>
        public int TagStackDir { get; set; } = 1;

        /// <summary>Id of the clash cluster this dimension belongs to ("" when unclustered).
        /// Dimensions are laid out cluster by cluster — each cluster's group is optimized
        /// jointly inside its working region.</summary>
        public string ClusterId { get; set; } = "";

        /// <summary>The cluster's working region in view-2D: the tight box around its clashes
        /// and their dimension targets, ballooned outward until it meets the neighbouring
        /// clusters' regions (equal spacing). The layout softly keeps this dimension's lines
        /// and texts inside it, so each group fills its own empty space instead of spilling
        /// into the next group's. Valid only when <see cref="HasRegion"/>.</summary>
        public Box2 Region { get; set; }
        public bool HasRegion { get; set; }

        /// <summary>Current paper-space offset of the dimension line from the source run (model ft).</summary>
        public double OffsetFt { get; set; }

        public List<PlannedSegment> Segments { get; set; } = new List<PlannedSegment>();

        /// <summary>View-2D anchor of every reference along the line (sources + target),
        /// sorted along the measurement axis. Segment k spans anchors k → k+1; each anchor
        /// drops one witness line. Filled by the chainer.</summary>
        public List<Vec2> RefAnchors { get; set; } = new List<Vec2>();

        /// <summary>Paper-space bounds of the whole string (text + line) for collision tests.</summary>
        public Box2 PaperBounds { get; set; }

        /// <summary>Drawn anatomy (lines/witnesses/text/leaders) at the current placement.
        /// Derived — rebuilt by <see cref="DimGeometry.RecomputeBounds"/>, never serialized.</summary>
        [XmlIgnore]
        public DimAnatomy? Anatomy { get; set; }
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
