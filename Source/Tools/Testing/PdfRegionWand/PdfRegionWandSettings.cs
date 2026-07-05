using System;
using System.IO;
using System.Xml.Serialization;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.Testing.PdfRegionWand
{
    /// <summary>
    /// Persistent settings for the PDF Region Wand palette. XML singleton in
    /// %AppData%\LemoineTools, same pattern as ClashDimensionSettings. Must be
    /// public — XmlSerializer throws on non-public root types and the failure
    /// would be silently swallowed by the load/save try/catch.
    /// </summary>
    [XmlRoot("PdfRegionWandSettings")]
    public sealed class PdfRegionWandSettings
    {
        private static readonly Lazy<PdfRegionWandSettings> _lazy =
            new Lazy<PdfRegionWandSettings>(Load);

        public static PdfRegionWandSettings Instance => _lazy.Value;

        // ── Raster extraction ────────────────────────────────────────────────
        public int RasterDpi { get; set; } = 300;
        public double BinarizeThreshold { get; set; } = 0.5;

        // ── Simplification / snapping (model units) ──────────────────────────
        /// <summary>Douglas-Peucker tolerance as model inches at drawing scale.</summary>
        public double SimplifyModelInches { get; set; } = 1.0;
        public double OrthoSnapAngleDeg { get; set; } = 2.0;

        /// <summary>Default drawing scale as paper inches per model foot (0.25 = 1/4" = 1'-0").</summary>
        public double DefaultScaleInchesPerFoot { get; set; } = 0.25;

        /// <summary>Leak guard: regions larger than this are treated as unenclosed.</summary>
        public double MaxRegionAreaFt2 { get; set; } = 20000;

        // ── Split lines ──────────────────────────────────────────────────────
        /// <summary>Keep the user's drawn split lines in the model after capture (default: delete).</summary>
        public bool KeepSplitLines { get; set; }

        /// <summary>Draw split lines with the model-line tool instead of detail lines.</summary>
        public bool UseModelLines { get; set; }

        // ── Vector mode ──────────────────────────────────────────────────────
        /// <summary>Pages with fewer path commands than this auto-detect as raster.</summary>
        public int VectorDetectThreshold { get; set; } = 200;

        // ── Output ───────────────────────────────────────────────────────────
        /// <summary>Holes smaller than this are ignored when sketching floors.</summary>
        public double MinHoleAreaFt2 { get; set; } = 0.25;

        /// <summary>Create the floor immediately when a preview is confirmed.</summary>
        public bool CreateOnConfirm { get; set; } = true;

        // ── Persistence ──────────────────────────────────────────────────────
        private static string FilePath
        {
            get
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "LemoineTools");
                try { Directory.CreateDirectory(dir); }
                catch (Exception ex) { LemoineLog.Swallowed("PdfRegionWandSettings: ensure settings directory", ex); }
                return Path.Combine(dir, "PdfRegionWandSettings.xml");
            }
        }

        public void Save()
        {
            try
            {
                var xs = new XmlSerializer(typeof(PdfRegionWandSettings));
                using (var w = new StreamWriter(FilePath))
                    xs.Serialize(w, this);
            }
            catch (Exception ex)
            {
                LemoineLog.Error("PdfRegionWandSettings: save", ex);
            }
        }

        private static PdfRegionWandSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var xs = new XmlSerializer(typeof(PdfRegionWandSettings));
                    using (var r = new StreamReader(FilePath))
                        return (PdfRegionWandSettings)xs.Deserialize(r)!;
                }
            }
            catch (Exception ex)
            {
                LemoineLog.Error("PdfRegionWandSettings: load — using defaults", ex);
            }
            return new PdfRegionWandSettings();
        }
    }
}
