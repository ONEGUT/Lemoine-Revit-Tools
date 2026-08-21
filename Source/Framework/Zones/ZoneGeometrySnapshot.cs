using System;
using System.Collections.Generic;
using System.Linq;

namespace LemoineTools.Framework.Zones
{
    // =========================================================================
    // ZoneGeometrySnapshot — what the building looks like, handed to a window
    // that has no Revit API access.
    //
    // The Zone Manager runs on its own STA thread and can never read the model,
    // so the launching command captures this on Revit's main thread before the
    // window is shown. It is IMMUTABLE for the window's lifetime: the canvas
    // draws from this plus the zone library, and nothing else.
    //
    // Deliberately Revit-free — no XYZ, no Document, no Element. The command
    // converts; everything downstream sees plain numbers. That is what lets the
    // canvas be unit-tested and what keeps this type loadable on the UI thread.
    //
    // The outlines come from ZoneSlabOutline, the SAME path the key plan draws
    // from, so a plan on screen and a plan in a title block cannot disagree.
    // =========================================================================
    public sealed class ZoneGeometrySnapshot
    {
        /// <summary>One entry per zone level that had a resolvable host level. Keyed by level id.</summary>
        public Dictionary<string, LevelOutline> Levels { get; }
            = new Dictionary<string, LevelOutline>(StringComparer.Ordinal);

        /// <summary>Counts of what the capture read, for the empty state's "read from the model" card.</summary>
        public ModelCounts Counts { get; set; } = new ModelCounts();

        public DateTime CapturedAt { get; set; } = DateTime.Now;

        /// <summary>True when at least one level carries a drawable outline.</summary>
        public bool HasAnyOutline => Levels.Values.Any(l => l.HasOutline);

        public LevelOutline? ForLevel(string? levelId)
        {
            if (string.IsNullOrEmpty(levelId)) return null;
            return Levels.TryGetValue(levelId!, out var o) ? o : null;
        }

        // ── Per-level outline ────────────────────────────────────────────────

        /// <summary>
        /// One level's slab union, as closed loops in model feet.
        ///
        /// A level with no outline is NOT an error: the canvas draws that level's areas over an
        /// empty surface rather than blocking, and <see cref="From"/> records why so the reason
        /// can be surfaced instead of guessed at.
        /// </summary>
        public sealed class LevelOutline
        {
            public string LevelId       { get; set; } = "";
            public string HostLevelName { get; set; } = "";

            /// <summary>Closed rings in model feet. Ring 0 is the outer boundary; the rest are holes.</summary>
            public List<List<PlanPoint>> Rings { get; } = new List<List<PlanPoint>>();

            /// <summary>Which collector produced this — "SlabEdges", "Roofs", "ZoneExtents" or "None".</summary>
            public string From { get; set; } = "None";

            public double MinX { get; set; }
            public double MinY { get; set; }
            public double MaxX { get; set; }
            public double MaxY { get; set; }

            public bool HasOutline => Rings.Count > 0 && Rings.Any(r => r.Count >= 3);

            public double WidthFt => MaxX - MinX;
            public double DepthFt => MaxY - MinY;
        }

        /// <summary>A point in model feet. A struct so a ring of a few hundred costs nothing.</summary>
        public struct PlanPoint
        {
            public double X;
            public double Y;

            public PlanPoint(double x, double y) { X = x; Y = y; }
        }

        // ── Model counts ─────────────────────────────────────────────────────

        /// <summary>
        /// What the launch capture found. Shown verbatim in the empty state so a user looking at
        /// a blank window can tell "Discover has nothing to work with" from "Discover has not run".
        /// </summary>
        public sealed class ModelCounts
        {
            public int ScopeBoxes     { get; set; }
            public int TitleBlockTypes{ get; set; }
            public int HostLevels     { get; set; }
            public int LinkedModels   { get; set; }
        }
    }
}
