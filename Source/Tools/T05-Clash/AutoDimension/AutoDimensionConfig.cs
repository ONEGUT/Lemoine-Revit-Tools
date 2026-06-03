using System;
using System.IO;
using System.Xml.Serialization;
using LemoineTools.Lemoine;
using CoreLayout = LemoineTools.Tools.Testing.AutoDimension.Core.LayoutConfig;

namespace LemoineTools.Tools.Testing.AutoDimension
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
        /// the clash (Load() migrates older files).</summary>
        public int SchemaVersion { get; set; } = 3;

        /// <summary>Destination type for this run: "Grid", "SlabEdge", or "ManualDatum".</summary>
        public string TargetType { get; set; } = "Grid";

        /// <summary>Include loaded Revit links as target sources (coordination slabs/grids).</summary>
        public bool IncludeLinks { get; set; } = true;

        /// <summary>Measure each clash in both the view's X and Y directions (two dimensions/clash).</summary>
        public bool DimensionBothAxes { get; set; } = true;

        /// <summary>Merge collinear, adjacent clashes that share a target into one chained string.</summary>
        public bool ChainAligned { get; set; } = true;

        /// <summary>Max along-axis gap between adjacent clashes that still chain (mm). Wide so
        /// penetrations spread across a bay consolidate into one run to the edge, like the manual.</summary>
        public double ChainMaxGapMm { get; set; } = 3000.0;

        /// <summary>How far a clash may sit off the shared baseline and still count as "in line" (mm).</summary>
        public double ChainCollinearToleranceMm { get; set; } = 250.0;

        /// <summary>Collapse parallel dimensions on the same axis whose witness lines coincide within
        /// this tolerance into a single dimension (mm). 0 disables the merge.</summary>
        public double DuplicateToleranceMm { get; set; } = 25.0;

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
            c.ChainMaxGapMm            = def.ChainMaxGapMm;
            c.ChainCollinearToleranceMm = def.ChainCollinearToleranceMm;
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
    }
}
