using System;
using System.IO;
using System.Xml.Serialization;
using LemoineTools.Framework;
using CoreLayout = LemoineTools.Tools.Dimensioning.AutoDimension.Core.LayoutConfig;

namespace LemoineTools.Tools.Dimensioning.AutoDimension
{
    /// <summary>
    /// Versioned, XML-backed configuration for the auto-dimension engine. Wraps the Revit-free
    /// <see cref="CoreLayout"/> (layout/scoring) and adds the target-resolution knobs and the
    /// dimension-type name. Stored in <c>%AppData%\LemoineTools\AutoDimension.xml</c>.
    /// Must be <c>public</c> for <see cref="XmlSerializer"/> (an internal root fails silently).
    /// </summary>
    [XmlRoot("AutoDimension")]
    public sealed class AutoDimensionConfig
    {
        /// <summary>Config schema version. v1 first release; v2 refreshes the layout/chaining
        /// numbers to match hand-drafted output; v3 halves FirstOffset so the string sits closer to
        /// the clash; v4 replaces per-axis chaining tolerances with run-based grouping; v5 resets the
        /// run gap / cross tolerance to the clean feet-based defaults (5 ft / 0.5 ft); v6 adopts the
        /// ASME Y14.5 spacing defaults (first offset 3/8", row spacing 1/4"); v7 replaces the
        /// model-feet grouping tolerances with paper-space (sheet inch) values so grouping reads
        /// identically at every view scale — including enlarged callouts — and adds the callout
        /// minimum-clash threshold; v8 adds the finest-callout-scale cap (<see cref="MaxCalloutScale"/>).
        /// Load() migrates older files.</summary>
        public int SchemaVersion { get; set; } = 8;

        /// <summary>Destination type for this run: "Grid", "SlabEdge", or "ManualDatum".</summary>
        public string TargetType { get; set; } = "Grid";

        /// <summary>Include loaded Revit links as target sources (coordination slabs/grids).</summary>
        public bool IncludeLinks { get; set; } = true;

        /// <summary>Measure each clash in both the view's X and Y directions (two dimensions/clash).</summary>
        public bool DimensionBothAxes { get; set; } = true;

        /// <summary>Group clashes into physical runs, then chain along each run and emit a single
        /// dimension across it. Off = one dimension per clash per axis.</summary>
        public bool ChainAligned { get; set; } = true;

        /// <summary>Collapse oversaturated areas (clashes packed tighter than their value texts)
        /// into one chained string per axis, split by nearest reference — instead of a forest of
        /// solo dimensions whose witness lines cross everything. Default on.</summary>
        public bool DensityChaining { get; set; } = true;

        /// <summary>Callout tier: areas too dense even for chained strings get an enlarged-plan
        /// callout at a computed scale and are dimensioned THERE instead of the parent view.
        /// Used by the Clash Finder (which can create the views); default on.</summary>
        public bool DenseCalloutsEnabled { get; set; } = true;

        /// <summary>Cluster grouping distance in PAPER inches: clashes whose anchors sit within
        /// this distance on the printed sheet group into one cluster (single-link), and it is also
        /// the max along-run gap between adjacent members of one chained run. Paper-space, so the
        /// same value groups identically at 1:96 and inside a 1:16 callout — the model-feet knob it
        /// replaces grouped callouts wildly differently from their parents. Default 5/8" (= 5 ft at
        /// 1:96). Settings-only; no per-run override.</summary>
        public double ClusterLinkPaperIn { get; set; } = 0.625;

        /// <summary>How far a clash may sit off a run's fitted line and still belong to it, in
        /// PAPER inches (see <see cref="ClusterLinkPaperIn"/> for why paper-space). Also the
        /// across-run snap: members within this share one dimension. Default 1/16" (= 0.5 ft at
        /// 1:96). Settings-only; no per-run override.</summary>
        public double RunCrossPaperIn { get; set; } = 0.0625;

        /// <summary>A dense area only becomes an enlarged callout when at least this many clash
        /// markers fall inside it — smaller pockets stay on the chain tier in the parent view.</summary>
        public int CalloutMinClashes { get; set; } = 8;

        /// <summary>Finest (smallest-denominator) scale the callout auto-pick may reach. The pick can
        /// still choose a COARSER callout when the text demand allows; it just never zooms in past
        /// 1:<see cref="MaxCalloutScale"/>. Default 12 preserves the prior behaviour (callouts could go
        /// to 1:12); raise it (e.g. 24) to keep callouts smaller. Set per run from the Clash Finder's
        /// Dimensioning step.</summary>
        public int MaxCalloutScale { get; set; } = 12;

        /// <summary>Model-feet cluster link distance at a view scale (1:<paramref name="scale"/>).</summary>
        public double ClusterLinkFt(double scale) => ClusterLinkPaperIn / 12.0 * System.Math.Max(scale, 1.0);

        /// <summary>Model-feet off-line run tolerance at a view scale (see <see cref="ClusterLinkFt"/>).</summary>
        public double RunCrossFt(double scale) => RunCrossPaperIn / 12.0 * System.Math.Max(scale, 1.0);

        /// <summary>Name of the DimensionType to place with; empty = the document default.</summary>
        public string DimensionTypeName { get; set; } = "";

        // ── Target-resolution knobs ─────────────────────────────────────────────
        /// <summary>Reject targets whose projected horizontal distance exceeds this (feet).</summary>
        public double MaxDistanceFt { get; set; } = 50.0;

        /// <summary>Axis-match tolerance: keep faces/grids within this many degrees of the axis.</summary>
        public double AxisToleranceDeg { get; set; } = 15.0;

        /// <summary>If the top two slab faces score within this (feet), flag ambiguity — don't guess.</summary>
        public double AmbiguityThresholdFt { get; set; } = (0.5 / 12.0);

        /// <summary>Slab-face scoring weight on axis deviation (degrees → feet-equivalent).</summary>
        public double SlabAxisWeight { get; set; } = 0.02;

        /// <summary>Slab-face scoring credit for a larger / more primary boundary face.</summary>
        public double SlabLengthWeight { get; set; } = 0.05;

        /// <summary>Diagnostic only: when true, every dimensioned view writes a complete layout
        /// snapshot XML (config, obstacles, every dimension's placement + per-constraint score
        /// breakdown) to %AppData%\LemoineTools\LayoutSnapshots — the data harvester for layout
        /// tuning. Off by default; no behaviour change.</summary>
        public bool DumpLayoutSnapshots { get; set; } = false;

        /// <summary>Diagnostic only: when true, a SlabEdge run logs a per-source-doc face tally
        /// (host vs each link: floors, vertical faces kept, drops) and a per-clash candidate ranking
        /// so the "auto mode never picks the linked slab" path can be pinpointed from the run log.
        /// Off by default; XML-persisted (absent in older files → false). No behaviour change.</summary>
        public bool DiagnoseSlabEdge { get; set; } = false;

        // ── Layout (Revit-free core config) ─────────────────────────────────────
        public CoreLayout Layout { get; set; } = new CoreLayout();

        /// <summary>Parameterless ctor required by <see cref="XmlSerializer"/>.</summary>
        public AutoDimensionConfig() { }

        /// <summary>
        /// Copy for a single run, so per-run overrides (TargetType, MaxCalloutScale) never touch
        /// the shared singleton — a concurrent <see cref="Save"/> (e.g. the Dimensions settings
        /// window auto-saving mid-run) used to persist the temporary override. MemberwiseClone
        /// covers every value-typed property, present and future; Layout (all value fields) is
        /// cloned the same way.
        /// </summary>
        public AutoDimensionConfig CloneForRun()
        {
            var c = (AutoDimensionConfig)MemberwiseClone();
            c.Layout = Layout != null ? Layout.CloneShallow() : new CoreLayout();
            return c;
        }

        // ── Persistence (lazy singleton, mirrors ClashDefinitionsSettings) ──────
        private static readonly Lazy<AutoDimensionConfig> _lazy = new Lazy<AutoDimensionConfig>(Load);
        public static AutoDimensionConfig Instance => _lazy.Value;

        private static string FilePath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LemoineTools");
                try { Directory.CreateDirectory(dir); }
                catch (Exception ex) { DiagnosticsLog.Swallowed("AutoDimensionConfig: ensure settings directory", ex); }
                return Path.Combine(dir, "AutoDimension.xml");
            }
        }

        public void Save()
        {
            try
            {
                var xs = new XmlSerializer(typeof(AutoDimensionConfig));
                using (var w = new StreamWriter(FilePath)) xs.Serialize(w, this);
            }
            catch (Exception ex) { DiagnosticsLog.Error("AutoDimensionConfig: save", ex); }
        }

        private static AutoDimensionConfig Load()
        {
            try
            {
                string path = FilePath;
                if (File.Exists(path))
                {
                    var xs = new XmlSerializer(typeof(AutoDimensionConfig));
                    using (var r = new StreamReader(path))
                    {
                        var c = (AutoDimensionConfig)xs.Deserialize(r)!;
                        if (c.Layout == null) c.Layout = new CoreLayout();
                        if (c.SchemaVersion < 2) MigrateToV2(c);
                        if (c.SchemaVersion < 3) MigrateToV3(c);
                        if (c.SchemaVersion < 4) MigrateToV4(c);
                        if (c.SchemaVersion < 5) MigrateToV5(c);
                        if (c.SchemaVersion < 6) MigrateToV6(c);
                        if (c.SchemaVersion < 7) MigrateToV7(c);
                        if (c.SchemaVersion < 8) MigrateToV8(c);
                        return c;
                    }
                }
            }
            catch (Exception ex) { DiagnosticsLog.Swallowed("AutoDimensionConfig: load (using defaults)", ex); }
            return new AutoDimensionConfig();
        }

        /// <summary>v1 → v2: refresh the layout + chaining numbers a v1 file still carries to the new
        /// manual-matching defaults so the tuning reaches existing users. Leaves user-chosen target,
        /// links, and dimension-type alone.</summary>
        private static void MigrateToV2(AutoDimensionConfig c)
        {
            var def = new AutoDimensionConfig();
            c.Layout.LeaderWeight      = def.Layout.LeaderWeight;
            c.Layout.CrampedWeight     = def.Layout.CrampedWeight;
            c.Layout.FirstOffsetFt     = def.Layout.FirstOffsetFt;
            c.Layout.StringSpacingFt   = def.Layout.StringSpacingFt;
            // The v1→v2 chaining-number refresh targeted the old per-axis tolerances, which v4
            // replaces with run grouping; MigrateToV4 sets the run knobs, so nothing to do here.
            c.SchemaVersion = 2;
        }

        /// <summary>v2 → v3: halve FirstOffset so the dimension string sits closer to the clash, the
        /// way the hand drawing does. Leaves every other persisted value alone.</summary>
        private static void MigrateToV3(AutoDimensionConfig c)
        {
            var def = new AutoDimensionConfig();
            c.Layout.FirstOffsetFt = def.Layout.FirstOffsetFt;
            c.SchemaVersion = 3;
        }

        /// <summary>v3 → v4: the per-axis chaining tolerances (ChainMaxGapMm,
        /// ChainCollinearToleranceMm) and the duplicate-merge tolerance (DuplicateToleranceMm) are
        /// gone — grouping is now run-based. Those old XML elements are ignored on load; the new
        /// RunGapMm / RunCrossToleranceMm take their (semantically different) defaults. Leaves the
        /// user's target, links, dimension-type, and layout numbers untouched.</summary>
        private static void MigrateToV4(AutoDimensionConfig c)
        {
            // The old run knobs this migration used to seed were themselves replaced by the
            // paper-space fields in v7 (which take their ctor defaults when absent), so the
            // version bump is all that remains.
            c.SchemaVersion = 4;
        }

        /// <summary>v4 → v5: the run gap / cross tolerance are now edited in feet and seeded from clean
        /// feet-based defaults (5 ft / 0.5 ft). Reset both to the new defaults so existing files pick up
        /// the rounded values rather than the old 1500 / 100 mm. Leaves target, links, dimension-type,
        /// and layout numbers untouched — same shape as the v2/v3/v4 resets above.</summary>
        private static void MigrateToV5(AutoDimensionConfig c)
        {
            // Same as v4: the feet-based run knobs are gone in v7 — version bump only.
            c.SchemaVersion = 5;
        }

        /// <summary>v5 → v6: adopt the ASME Y14.5 spacing defaults — first dimension line 3/8"
        /// off the object, successive rows 1/4" apart. Resets only those two layout values (the
        /// new anatomy/refinement fields take their ctor defaults when absent from older XML);
        /// target, links, dimension-type, and run-grouping knobs stay untouched.</summary>
        private static void MigrateToV6(AutoDimensionConfig c)
        {
            var def = new AutoDimensionConfig();
            c.Layout.FirstOffsetFt   = def.Layout.FirstOffsetFt;
            c.Layout.StringSpacingFt = def.Layout.StringSpacingFt;
            c.SchemaVersion = 6;
        }

        /// <summary>v6 → v7: grouping tolerances move from model feet (RunGapMm /
        /// RunCrossToleranceMm — old XML elements now ignored on load) to paper-space inches
        /// (<see cref="ClusterLinkPaperIn"/> / <see cref="RunCrossPaperIn"/>), seeded with the
        /// defaults equivalent to the old 5 ft / 0.5 ft at 1:96. Adds
        /// <see cref="CalloutMinClashes"/>. Everything else untouched.</summary>
        private static void MigrateToV7(AutoDimensionConfig c)
        {
            var def = new AutoDimensionConfig();
            c.ClusterLinkPaperIn = def.ClusterLinkPaperIn;
            c.RunCrossPaperIn    = def.RunCrossPaperIn;
            c.CalloutMinClashes  = def.CalloutMinClashes;
            c.SchemaVersion = 7;
        }

        /// <summary>v7 → v8: adds <see cref="MaxCalloutScale"/> (finest callout scale). Absent in
        /// older XML, so it takes the ctor default (12 = prior behaviour). Version bump only.</summary>
        private static void MigrateToV8(AutoDimensionConfig c)
        {
            c.SchemaVersion = 8;
        }
    }
}
