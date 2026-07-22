using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Navisworks.Api;
using LemoineTools.Lemoine;

namespace LemoineNavisworks.FloorSplit
{
    // =========================================================================
    // NavisFloorSplit — the Navisworks API layer for the Floor Splitter.
    //
    //   • Gather every geometry item's vertical (Z) extent once.
    //   • Discover levels from the model's "Level" property, inferring each
    //     level's elevation from the lowest item carrying it.
    //   • Turn selected levels into floor bands (N levels → N−1 floors; ends open).
    //   • Per floor: hide out-of-band items, save a viewpoint, export an NWD that
    //     physically drops the hidden geometry (ExcludeHiddenItems — 2026 API),
    //     then restore visibility.
    //
    // Mirrors NavisSearchSets' traversal (RootItem.DescendantsAndSelf + HasGeometry)
    // and its ⚠-verify convention: this cannot be compiled on Linux, so calls that
    // still need a Windows/Navisworks-2026 check are tagged "⚠ verify".
    //
    // REQUIRES the Navisworks 2026 .NET API — NwdExportOptions / TryExportToNwd and
    // the ExcludeHiddenItems option do not exist before 2026.
    // =========================================================================
    internal static class NavisFloorSplit
    {
        // ── Gather vertical extents of all geometry items ────────────────────────
        public static List<ItemZ> GatherItemZ(Document doc, out double overallMin, out double overallMax)
        {
            var list = new List<ItemZ>();
            overallMin = double.PositiveInfinity;
            overallMax = double.NegativeInfinity;
            if (doc == null || doc.IsClear) return list;

            for (int i = 0; i < doc.Models.Count; i++)
            {
                Model model = doc.Models[i];
                foreach (ModelItem item in model.RootItem.DescendantsAndSelf)        // ⚠ verify (traversal)
                {
                    if (!SafeHasGeometry(item)) continue;
                    if (!TryZExtent(item, out double lo, out double hi)) continue;

                    list.Add(new ItemZ { Item = item, MinZ = lo, MaxZ = hi });
                    if (lo < overallMin) overallMin = lo;
                    if (hi > overallMax) overallMax = hi;
                }
            }
            return list;
        }

        private static bool SafeHasGeometry(ModelItem item)
        {
            try { return item.HasGeometry; }                                         // ⚠ verify (ModelItem.HasGeometry)
            catch { return false; }
        }

        private static bool TryZExtent(ModelItem item, out double minZ, out double maxZ)
        {
            minZ = 0; maxZ = 0;
            try
            {
                BoundingBox3D bb = item.BoundingBox();                               // ⚠ verify (ModelItem.BoundingBox())
                if (bb == null) return false;
                minZ = bb.Min.Z;                                                     // ⚠ verify (BoundingBox3D.Min/Max -> Point3D.Z)
                maxZ = bb.Max.Z;
                return true;
            }
            catch (Exception ex) { LemoineLog.Swallowed("NavisFloorSplit: bbox", ex); return false; }
        }

        // ── Discover levels from the "Level" property (elevation = lowest item) ───
        // Capped so opening the tool on a huge federated model does not freeze the
        // UI thread; a full re-scan is offered via a button.
        public static List<LevelDef> DiscoverLevels(Document doc, int cap = 40000)
        {
            var minZByLevel = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            if (doc == null || doc.IsClear) return new List<LevelDef>();

            int seen = 0;
            for (int i = 0; i < doc.Models.Count && seen < cap; i++)
            {
                Model model = doc.Models[i];
                foreach (ModelItem item in model.RootItem.DescendantsAndSelf)        // ⚠ verify
                {
                    if (seen >= cap) break;
                    if (!SafeHasGeometry(item)) continue;
                    seen++;

                    string level = ReadLevelName(item);
                    if (string.IsNullOrWhiteSpace(level)) continue;
                    if (!TryZExtent(item, out double lo, out _)) continue;

                    if (!minZByLevel.TryGetValue(level, out double cur) || lo < cur)
                        minZByLevel[level] = lo;
                }
            }

            return minZByLevel
                .OrderBy(kv => kv.Value)
                .Select(kv => new LevelDef(kv.Key, kv.Value))
                .ToList();
        }

        private static string ReadLevelName(ModelItem item)
        {
            try
            {
                foreach (PropertyCategory cat in item.PropertyCategories)
                {
                    foreach (DataProperty p in cat.Properties)
                    {
                        string pd = p.DisplayName ?? p.Name?.Name ?? "";            // ⚠ verify (DataProperty.DisplayName / Name.Name)
                        if (pd.Equals("Level", StringComparison.OrdinalIgnoreCase))
                        {
                            string v = p.Value?.ToDisplayString();                   // ⚠ verify (VariantData.ToDisplayString())
                            if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
                        }
                    }
                }
            }
            catch (Exception ex) { LemoineLog.Swallowed("NavisFloorSplit: read level", ex); }
            return null;
        }

        // ── Selected levels → floor bands (named by lower level; ends open) ───────
        public static List<FloorBand> BuildBands(IEnumerable<LevelDef> levels)
        {
            var sel = levels.Where(l => l.UseAsBoundary)
                            .OrderBy(l => l.Elevation)
                            .ToList();

            var bands = new List<FloorBand>();
            int n = sel.Count;
            for (int i = 0; i < n - 1; i++)
            {
                double low  = (i == 0)     ? double.NegativeInfinity : sel[i].Elevation;
                double high = (i == n - 2) ? double.PositiveInfinity : sel[i + 1].Elevation;
                bands.Add(new FloorBand(sel[i].Name, low, high));
            }
            return bands;
        }

        // ── Which items fall OUTSIDE a band (i.e. must be hidden) ─────────────────
        public static List<ModelItem> HideSetFor(IReadOnlyList<ItemZ> items, FloorBand band, StraddleRule rule)
        {
            var hide = new List<ModelItem>(items.Count);
            foreach (var z in items)
            {
                bool keep = rule == StraddleRule.ByCentroid
                    ? (z.CentreZ >= band.Low && z.CentreZ < band.High)
                    : !(z.MaxZ < band.Low || z.MinZ > band.High);   // keep anything overlapping
                if (!keep) hide.Add(z.Item);
            }
            return hide;
        }

        // ── Visibility ───────────────────────────────────────────────────────────
        public static void SetHidden(Document doc, IReadOnlyList<ModelItem> items, bool hidden)
        {
            if (items == null || items.Count == 0) return;
            var col = new ModelItemCollection();                                     // ⚠ verify
            col.AddRange(items);
            doc.Models.SetHidden(col, hidden);                                       // ⚠ verify (DocumentModels.SetHidden)
        }

        public static List<ModelItem> CurrentlyHidden(IEnumerable<ItemZ> items)
        {
            var hidden = new List<ModelItem>();
            foreach (var z in items)
            {
                try { if (z.Item.IsHidden) hidden.Add(z.Item); }                     // ⚠ verify (ModelItem.IsHidden)
                catch (Exception ex) { LemoineLog.Swallowed("NavisFloorSplit: IsHidden", ex); }
            }
            return hidden;
        }

        // ── Save a viewpoint for the current (per-floor) hide state ──────────────
        // NOTE: a saved viewpoint only REMEMBERS the hide state when the global
        // "Save Hide/Required Attributes" viewpoint option is enabled (off by
        // default). Enable it once in Options > Interface > Viewpoint Defaults, or
        // the viewpoint will restore geometry the export dropped. The NWD export
        // itself does not depend on this — it reads live visibility.
        public static void SaveFloorViewpoint(Document doc, string name, Action<string, string> log)
        {
            try
            {
                Viewpoint vp = doc.CurrentViewpoint.ToViewpoint();                   // ⚠ verify
                var sv = new SavedViewpoint(vp) { DisplayName = name };              // ⚠ verify
                doc.SavedViewpoints.AddCopy(sv);                                     // ⚠ verify
            }
            catch (Exception ex)
            {
                LemoineLog.Swallowed("NavisFloorSplit: save viewpoint", ex);
                log?.Invoke($"Could not save viewpoint “{name}” (see diagnostics).", "info");
            }
        }

        // ── Export the current visible model to an NWD (hidden geometry dropped) ──
        public static bool ExportNwd(Document doc, string path, bool embedXrefs, bool keepProps,
                                     Action<string, string> log)
        {
            try
            {
                var opts = new NwdExportOptions                                      // ⚠ 2026-only API
                {
                    ExcludeHiddenItems          = true,
                    EmbedXrefs                  = embedXrefs,
                    PreventObjectPropertyExport = !keepProps,
                };
                return doc.TryExportToNwd(path, opts);                               // ⚠ 2026-only API
            }
            catch (Exception ex)
            {
                LemoineLog.Error("NavisFloorSplit: export nwd", ex);
                log?.Invoke("Export error: " + ex.Message, "fail");
                return false;
            }
        }

        // ── Unit suffix for elevation display ────────────────────────────────────
        public static string UnitSuffix(Document doc)
        {
            try
            {
                switch (doc.Units)                                                   // ⚠ verify (Document.Units enum)
                {
                    case Units.Feet:        return "ft";
                    case Units.Inches:      return "in";
                    case Units.Meters:      return "m";
                    case Units.Centimeters: return "cm";
                    case Units.Millimeters: return "mm";
                    default:                return "";
                }
            }
            catch { return ""; }
        }
    }
}
