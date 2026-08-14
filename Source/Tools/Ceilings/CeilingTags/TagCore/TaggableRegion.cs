using System;
using System.Collections.Generic;

namespace LemoineTools.Tools.Ceilings.CeilingTags.TagCore
{
    /// <summary>
    /// Minimal 2D point/vector in world feet. Revit-free so the whole placement core runs on
    /// plain fixtures — this is deliberately NOT the dimensioning tool's <c>AutoDimension.Core.Vec2</c>,
    /// which belongs to that tool's layout model; ceiling tagging should not depend on it.
    /// </summary>
    public readonly struct Pt2
    {
        public readonly double X;
        public readonly double Y;

        public Pt2(double x, double y) { X = x; Y = y; }

        public static Pt2 operator +(Pt2 a, Pt2 b) => new Pt2(a.X + b.X, a.Y + b.Y);
        public static Pt2 operator -(Pt2 a, Pt2 b) => new Pt2(a.X - b.X, a.Y - b.Y);
        public static Pt2 operator *(Pt2 a, double s) => new Pt2(a.X * s, a.Y * s);

        public double Length => Math.Sqrt(X * X + Y * Y);

        public Pt2 Normalized()
        {
            double len = Length;
            return len < 1e-9 ? new Pt2(0, 0) : new Pt2(X / len, Y / len);
        }

        public override string ToString() => $"({X:0.###},{Y:0.###})";
    }

    /// <summary>A closed 2D loop in world feet. Not assumed to be wound any particular way —
    /// the rasterizer uses an even-odd fill, so outer/hole orientation is irrelevant.</summary>
    public sealed class Loop2
    {
        public List<Pt2> Points { get; } = new List<Pt2>();

        public Loop2() { }
        public Loop2(IEnumerable<Pt2> pts) { Points.AddRange(pts); }

        /// <summary>Enclosed area in square feet, sign-free (shoelace). Used to pick the outer
        /// loop and to tell a light-fixture opening from a real architectural one.</summary>
        public double AbsArea
        {
            get
            {
                if (Points.Count < 3) return 0;
                double a = 0;
                for (int i = 0; i < Points.Count; i++)
                {
                    Pt2 p = Points[i], q = Points[(i + 1) % Points.Count];
                    a += p.X * q.Y - q.X * p.Y;
                }
                return Math.Abs(a) * 0.5;
            }
        }
    }

    /// <summary>
    /// One thing that can carry tags, expressed with no Revit types at all.
    ///
    /// This is the room-ready abstraction: a ceiling and (later) a room differ only in how
    /// their loops are read out of the model and how a tag is finally created, never in how
    /// the tag POSITIONS are computed. See <see cref="OcclusionLayer"/> for why kinds can't
    /// hide each other.
    /// </summary>
    public sealed class TaggableRegion
    {
        /// <summary>Stable key for this region within a run — used to tie the plan back to the
        /// Revit reference bundle without the core ever seeing an ElementId.</summary>
        public string Id { get; set; } = "";

        /// <summary>Human-readable name for logs/diagnostics (e.g. the ceiling's type name).</summary>
        public string DisplayName { get; set; } = "";

        /// <summary>Outer boundary loops. Usually one; a ceiling sketched as several disjoint
        /// islands can supply more.</summary>
        public List<Loop2> Outers { get; } = new List<Loop2>();

        /// <summary>Openings (light wells, shafts) subtracted from the outers.</summary>
        public List<Loop2> Holes { get; } = new List<Loop2>();

        /// <summary>
        /// Regions only ever occlude other regions on the SAME layer. Ceilings share a layer
        /// (a lower ceiling hides a higher one in an RCP); rooms would form their own layer and
        /// could never be clipped by ceiling coverage, which is why occlusion is layered rather
        /// than global.
        /// </summary>
        public int OcclusionLayer { get; set; }

        /// <summary>Within a layer, the region with the SMALLER depth wins where two overlap.
        /// For ceilings this is the bottom-face elevation: an RCP looks up, so the lower
        /// ceiling is the one you see.</summary>
        public double SortDepth { get; set; }
    }

    /// <summary>One planned tag: where it goes, and which region it belongs to.</summary>
    public sealed class TagPlacement
    {
        public string RegionId { get; set; } = "";

        /// <summary>Tag point in world feet (X/Y). The Z used at commit time comes from the
        /// region's own source geometry, not from the core.</summary>
        public Pt2 Point { get; set; }
    }

    /// <summary>Why a region produced the tag count it did — surfaced in the run log so a
    /// surprising result is explainable rather than silent.</summary>
    public sealed class RegionDiagnostic
    {
        public string RegionId { get; set; } = "";
        public string DisplayName { get; set; } = "";

        /// <summary>Set when the region produced no tags at all, with the reason.</summary>
        public string? SkipReason { get; set; }

        public int    TagCount      { get; set; }
        public int    IslandCount   { get; set; }
        public bool   WasCorridor   { get; set; }

        /// <summary>Openings treated as solid because they are too small to be architectural —
        /// recessed lights, diffusers, sprinklers. Reported so a ceiling that behaves oddly can
        /// be traced back to its openings.</summary>
        public int    IgnoredOpenings { get; set; }
        /// <summary>Fraction of the region's own footprint left visible after occlusion (1 = untouched).</summary>
        public double VisibleFraction { get; set; } = 1.0;
    }

    /// <summary>The complete Revit-free output of the planner.</summary>
    public sealed class TagPlan
    {
        public List<TagPlacement>    Placements  { get; } = new List<TagPlacement>();
        public List<RegionDiagnostic> Diagnostics { get; } = new List<RegionDiagnostic>();

        /// <summary>Regions fully hidden by lower regions on their layer.</summary>
        public int FullyHiddenCount { get; set; }
    }
}
