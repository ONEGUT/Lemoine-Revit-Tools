using System;
using System.IO;
using System.Xml.Serialization;
using LemoineTools.Lemoine;
using CoreLayout = LemoineTools.Tools.Clash.AutoDimension.Core.LayoutConfig;

namespace LemoineTools.Tools.Clash.AutoDimension
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
        /// ASME Y14.5 spacing defaults (first offset 3/8", row spacing 1/4"). Load() migrates older
        /// files.</summary>
        public int SchemaVersion { get; set; } = 6;

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

        /// <summary>Max along-run gap between adjacent clashes that still belong to one run (mm).
        /// Wide enough that penetrations spread across a bay stay one run to the edge, like the
        /// manual; tight enough that a separate run further along starts its own dimension. Default
        /// 5 ft (1524 mm) — the UI edits this in feet.</summary>
        public double RunGapMm { get; set; } = 1524.0;

        /// <summary>How far a clash may sit off the run's line and still belong to it (mm). Also the
        /// across-run snap: members within this of the run offset share one dimension (a pipe a hair
        /// off the line is treated as in line, not dimensioned separately). Default 0.5 ft (152.4 mm)
        /// — the UI edits this in feet.</summary>
        public double RunCrossToleranceMm { get; set; } = 152.4;

        /// <summary>Feet view of <see cref="RunGapMm"/> — every consumer works in feet; the mm
        /// field stays the persisted storage so existing XML round-trips unchanged.</summary>
        [XmlIgnore]
        public double RunGapFt
        {
            get => RunGapMm / 304.8;
            set => RunGapMm = value * 304.8;
        }

        /// <summary>Feet view of <see cref="RunCrossToleranceMm"/> (see <see cref="RunGapFt"/>).</summary>
        [XmlIgnore]
        public double RunCrossToleranceFt
        {
            get => RunCrossToleranceMm / 304.8;
            set => RunCrossToleranceMm = value * 304.8;
        }

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
                catch (Exception ex) { LemoineLog.Swallowed("AutoDimensionConfig: ensure settings directory", ex); }
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
            catch (Exception ex) { LemoineLog.Error("AutoDimensionConfig: save", ex); }
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
                        return c;
                    }
                }
            }
            catch (Exception ex) { LemoineLog.Swallowed("AutoDimensionConfig: load (using defaults)", ex); }
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
            var def = new AutoDimensionConfig();
            c.RunGapMm            = def.RunGapMm;
            c.RunCrossToleranceMm = def.RunCrossToleranceMm;
            c.SchemaVersion = 4;
        }

        /// <summary>v4 → v5: the run gap / cross tolerance are now edited in feet and seeded from clean
        /// feet-based defaults (5 ft / 0.5 ft). Reset both to the new defaults so existing files pick up
        /// the rounded values rather than the old 1500 / 100 mm. Leaves target, links, dimension-type,
        /// and layout numbers untouched — same shape as the v2/v3/v4 resets above.</summary>
        private static void MigrateToV5(AutoDimensionConfig c)
        {
            var def = new AutoDimensionConfig();
            c.RunGapMm            = def.RunGapMm;
            c.RunCrossToleranceMm = def.RunCrossToleranceMm;
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
    }
}
