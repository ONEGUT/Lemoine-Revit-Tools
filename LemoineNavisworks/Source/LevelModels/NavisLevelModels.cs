using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Navisworks.Api;
using LemoineTools.Framework;

namespace LemoineNavisworks.LevelModels
{
    // =========================================================================
    // NavisLevelModels — the Navisworks API layer for the Level Models tool.
    //
    //   • List the appended models so each level can be assigned some of them.
    //   • Hide every model NOT assigned to the level being exported, so the NWD
    //     cannot carry another level's models (ExcludeHiddenItems drops them).
    //   • Optionally trim WITHIN the assigned models by elevation band.
    //   • Optionally save a clipped viewpoint per level.
    //   • Export the NWD and restore the model's original visibility.
    //
    // WHY HIDE AND NOT CLIP. Navisworks has no cut/boolean API — scene geometry is
    // baked and read-only — and Autodesk documents that ExcludeHiddenItems does NOT
    // drop items that are merely section-clipped: they stay in the tree, whole, in
    // the written file. So hiding is the only thing that keeps geometry OUT of an
    // NWD; the clipped viewpoint below is presentation only, and is described that
    // way everywhere the user can see it.
    //
    // The trim is element-granular, not geometry-granular: a riser modelled as
    // per-storey segments distributes correctly, a one-piece full-height riser is
    // kept or dropped whole per StraddleRule. Nothing here can halve a solid.
    //
    // REQUIRES the Navisworks 2026 .NET API — NwdExportOptions / TryExportToNwd and
    // ExcludeHiddenItems do not exist before 2026.
    //
    // This project cannot be built or run on Linux and no Navisworks API DLL is
    // vendored, so calls that still need a Windows/Navisworks-2026 check carry a
    // "⚠ verify" tag. Every one of them is wrapped so a wrong guess degrades to a
    // logged warning rather than an unhandled throw.
    // =========================================================================
    internal static class NavisLevelModels
    {
        // Per-item geometry probes (HasGeometry / BoundingBox) can fail on an individual item
        // without the scan being wrong. Logging each failure would flood the log on a federation
        // with hundreds of thousands of items, but discarding them outright would make a
        // systematically broken probe look exactly like "this model has no geometry". So they are
        // counted here and reported ONCE by the caller; the first is also written to diagnostics
        // so there is a stack trace to work from.
        private static int  _probeFailures;
        private static bool _probeFirstLogged;

        /// <summary>Number of items whose geometry could not be probed during the last scan.</summary>
        public static int ProbeFailures => _probeFailures;

        private static void ResetProbeFailures() { _probeFailures = 0; _probeFirstLogged = false; }

        private static void NoteProbeFailure(string context, Exception ex)
        {
            _probeFailures++;
            if (_probeFirstLogged) return;
            _probeFirstLogged = true;
            DiagnosticsLog.Swallowed(context + " (first of possibly many this scan)", ex);
        }

        // ── Appended models ──────────────────────────────────────────────────

        /// <summary>Lists every appended model. Keys are made unique, because two models sharing
        /// a display name would otherwise collapse into one picker row meaning both.</summary>
        public static List<ModelRef> ListModels(Document doc)
        {
            var list = new List<ModelRef>();
            if (doc == null || doc.IsClear) return list;

            var used = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < doc.Models.Count; i++)
            {
                var mr = new ModelRef { Index = i };
                try
                {
                    Model m = doc.Models[i];
                    string file = "";
                    try { file = m.FileName ?? ""; }                                  // ⚠ verify (Model.FileName)
                    catch (Exception ex) { DiagnosticsLog.Swallowed("LevelModels: model filename", ex); }

                    mr.SourceFile  = string.IsNullOrWhiteSpace(file) ? "" : Path.GetFileName(file);
                    mr.DisplayName = SafeModelName(m, mr.SourceFile, i);
                }
                catch (Exception ex)
                {
                    DiagnosticsLog.Swallowed($"LevelModels: read model {i}", ex);
                    mr.DisplayName = $"Model {i + 1}";
                }

                string key = mr.DisplayName;
                if (used.TryGetValue(key, out int n))
                {
                    used[key] = n + 1;
                    key = $"{mr.DisplayName} ({n + 1})";
                }
                else used[key] = 1;

                mr.Key = key;
                list.Add(mr);
            }
            return list;
        }

        private static string SafeModelName(Model m, string sourceFile, int index)
        {
            try
            {
                string rn = m.RootItem?.DisplayName ?? "";                             // ⚠ verify (ModelItem.DisplayName)
                if (!string.IsNullOrWhiteSpace(rn)) return rn.Trim();
            }
            catch (Exception ex) { DiagnosticsLog.Swallowed("LevelModels: root item name", ex); }

            if (!string.IsNullOrWhiteSpace(sourceFile))
                return Path.GetFileNameWithoutExtension(sourceFile);
            return $"Model {index + 1}";
        }

        // ── Level discovery (names + elevations, both editable afterwards) ────

        /// <summary>Reads distinct "Level" property values, taking each level's elevation from the
        /// lowest item carrying it. Capped so opening the tool on a large federation cannot hang
        /// the UI thread. Returns an empty list — and says so at the call site — when the models
        /// carry no Level property at all.</summary>
        public static List<DiscoveredLevel> DiscoverLevels(Document doc, int cap = LevelDefaults.DiscoverScanCap)
        {
            ResetProbeFailures();
            var minZ = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            if (doc == null || doc.IsClear) return new List<DiscoveredLevel>();

            int seen = 0;
            for (int i = 0; i < doc.Models.Count && seen < cap; i++)
            {
                foreach (ModelItem item in Descendants(doc.Models[i]))
                {
                    if (seen >= cap) break;
                    if (!SafeHasGeometry(item)) continue;
                    seen++;

                    string level = ReadLevelName(item);
                    if (string.IsNullOrWhiteSpace(level)) continue;
                    if (!TryZExtent(item, out double lo, out _)) continue;

                    if (!minZ.TryGetValue(level, out double cur) || lo < cur) minZ[level] = lo;
                }
            }

            return minZ.OrderBy(kv => kv.Value)
                       .Select(kv => new DiscoveredLevel { Name = kv.Key, Elevation = kv.Value })
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
                        string pd = p.DisplayName ?? p.Name?.Name ?? "";               // ⚠ verify (DataProperty.DisplayName / Name.Name)
                        if (pd.Equals("Level", StringComparison.OrdinalIgnoreCase))
                        {
                            string v = p.Value?.ToDisplayString();                     // ⚠ verify (VariantData.ToDisplayString())
                            if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
                        }
                    }
                }
            }
            catch (Exception ex) { DiagnosticsLog.Swallowed("LevelModels: read level property", ex); }
            return "";
        }

        // ── Geometry extents (only gathered when some level trims) ────────────

        public static List<ItemZ> GatherItemZ(Document doc)
        {
            ResetProbeFailures();
            var list = new List<ItemZ>();
            if (doc == null || doc.IsClear) return list;

            for (int i = 0; i < doc.Models.Count; i++)
                foreach (ModelItem item in Descendants(doc.Models[i]))
                {
                    if (!SafeHasGeometry(item)) continue;
                    if (!TryZExtent(item, out double lo, out double hi)) continue;
                    list.Add(new ItemZ { Item = item, ModelIndex = i, MinZ = lo, MaxZ = hi });
                }
            return list;
        }

        private static IEnumerable<ModelItem> Descendants(Model model)
        {
            IEnumerable<ModelItem> seq;
            try { seq = model.RootItem.DescendantsAndSelf; }                            // ⚠ verify (traversal)
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed("LevelModels: descend model", ex);
                yield break;
            }
            foreach (var it in seq) yield return it;
        }

        private static bool SafeHasGeometry(ModelItem item)
        {
            try { return item.HasGeometry; }                                            // ⚠ verify (ModelItem.HasGeometry)
            catch (Exception ex) { NoteProbeFailure("LevelModels: HasGeometry", ex); return false; }
        }

        private static bool TryZExtent(ModelItem item, out double minZ, out double maxZ)
        {
            minZ = 0; maxZ = 0;
            try
            {
                BoundingBox3D bb = item.BoundingBox();                                  // ⚠ verify (ModelItem.BoundingBox())
                if (bb == null) return false;
                minZ = bb.Min.Z; maxZ = bb.Max.Z;
                return true;
            }
            catch (Exception ex) { NoteProbeFailure("LevelModels: bounding box", ex); return false; }
        }

        // ── Visibility ───────────────────────────────────────────────────────

        /// <summary>Root items of every appended model — the cheap handle for whole-model hiding.</summary>
        public static List<ModelItem> RootItems(Document doc)
        {
            var roots = new List<ModelItem>();
            if (doc == null || doc.IsClear) return roots;
            for (int i = 0; i < doc.Models.Count; i++)
            {
                try { roots.Add(doc.Models[i].RootItem); }
                catch (Exception ex) { DiagnosticsLog.Swallowed($"LevelModels: root item {i}", ex); }
            }
            return roots;
        }

        public static void SetHidden(Document doc, IReadOnlyCollection<ModelItem> items, bool hidden)
        {
            if (doc == null || items == null || items.Count == 0) return;
            try
            {
                var col = new ModelItemCollection();                                     // ⚠ verify
                col.AddRange(items);
                doc.Models.SetHidden(col, hidden);                                       // ⚠ verify (DocumentModels.SetHidden) — assumed to cascade to descendants
            }
            catch (Exception ex) { DiagnosticsLog.Error("LevelModels: SetHidden", ex); throw; }
        }

        /// <summary>Which of <paramref name="items"/> are hidden right now — the snapshot restored
        /// after the run so the live model is left exactly as the user had it.</summary>
        public static List<ModelItem> CurrentlyHidden(IEnumerable<ModelItem> items)
        {
            var hidden = new List<ModelItem>();
            foreach (var it in items.OrEmpty())
            {
                try { if (it.IsHidden) hidden.Add(it); }                                 // ⚠ verify (ModelItem.IsHidden)
                catch (Exception ex) { DiagnosticsLog.Swallowed("LevelModels: IsHidden", ex); }
            }
            return hidden;
        }

        /// <summary>Items to hide for one level: everything in a model the level does not own,
        /// plus — when the level trims — items in an owned model that fall outside its band.</summary>
        // ownedModelIndices is a HashSet, not IReadOnlyCollection, on purpose: through the
        // interface `Contains` would bind to the LINQ extension and run a LINEAR scan for every
        // one of potentially hundreds of thousands of items. The concrete type binds to the O(1)
        // member instead.
        public static List<ModelItem> HideSetFor(
            LevelDef level,
            HashSet<int> ownedModelIndices,
            IReadOnlyList<ModelItem> allRoots,
            IReadOnlyList<ItemZ> items,
            StraddleRule rule)
        {
            var hide = new List<ModelItem>();

            // 1. Whole models this level does not own — one entry per model, not per element.
            for (int i = 0; i < allRoots.Count; i++)
                if (!ownedModelIndices.Contains(i)) hide.Add(allRoots[i]);

            // 2. Within the owned models, elements outside the band.
            if (level.Trim && level.HasBand && items != null)
            {
                foreach (var z in items)
                {
                    if (!ownedModelIndices.Contains(z.ModelIndex)) continue;   // already hidden wholesale
                    bool keep = rule == StraddleRule.ByCentroid
                        ? (z.CentreZ >= level.Bottom && z.CentreZ < level.Top)
                        : !(z.MaxZ < level.Bottom || z.MinZ > level.Top);      // keep anything overlapping
                    if (!keep) hide.Add(z.Item);
                }
            }
            return hide;
        }

        // ── Clipped viewpoint ────────────────────────────────────────────────

        /// <summary>Saves a viewpoint named after the level, optionally carrying clipping planes at
        /// its band. PRESENTATION ONLY — clipped geometry still ships inside the NWD; only the
        /// hide/trim above keeps anything out of the file.
        ///
        /// A saved viewpoint records the hide state only when Options ▸ Interface ▸ Viewpoint
        /// Defaults ▸ "Save Hide/Required Attributes" is enabled (off by default). The export does
        /// not depend on that — it reads live visibility — so a failure here is logged and the run
        /// continues.</summary>
        public static bool SaveViewpoint(Document doc, string name, LevelDef level, bool clip,
                                         Action<string, string> log)
        {
            try
            {
                if (clip && level.HasBand) ApplyClip(doc, level.Bottom, level.Top);

                Viewpoint vp = doc.CurrentViewpoint.ToViewpoint();                       // ⚠ verify
                var sv = new SavedViewpoint(vp) { DisplayName = name };                  // ⚠ verify
                doc.SavedViewpoints.AddCopy(sv);                                         // ⚠ verify
                return true;
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed("LevelModels: save viewpoint", ex);
                log?.Invoke(AppStrings.T("navis.levelModels.log.viewpointFailed", name), "warn");
                return false;
            }
        }

        /// <summary>Enables a pair of horizontal clipping planes at the band edges. Wrapped
        /// separately from the viewpoint save so a clipping-API mismatch costs the clip only, not
        /// the viewpoint. ⚠ The whole body needs a Windows/Navisworks-2026 check.</summary>
        private static void ApplyClip(Document doc, double bottom, double top)
        {
            try
            {
                Viewpoint vp = doc.CurrentViewpoint.ToViewpoint();                       // ⚠ verify
                var planes = vp.GetClippingPlanes();                                     // ⚠ verify (Viewpoint.GetClippingPlanes)
                if (planes == null || planes.Count < 2) return;

                // Plane 0 cuts away everything below the band, plane 1 everything above it.
                planes[0].Alignment = ClippingPlaneAlignment.AlignZUp;                   // ⚠ verify (enum + member)
                planes[0].Distance  = bottom;
                planes[0].State     = ClippingPlaneState.Enabled;                        // ⚠ verify
                planes[1].Alignment = ClippingPlaneAlignment.AlignZDown;                 // ⚠ verify
                planes[1].Distance  = -top;
                planes[1].State     = ClippingPlaneState.Enabled;
                vp.SetClippingPlanes(planes);                                            // ⚠ verify
                doc.CurrentViewpoint.CopyFrom(vp);                                       // ⚠ verify
            }
            catch (Exception ex) { DiagnosticsLog.Swallowed("LevelModels: apply clip planes", ex); }
        }

        /// <summary>Turns every clipping plane off again, so the run leaves the live view alone.</summary>
        public static void ClearClip(Document doc)
        {
            try
            {
                Viewpoint vp = doc.CurrentViewpoint.ToViewpoint();
                var planes = vp.GetClippingPlanes();
                if (planes == null) return;
                foreach (var p in planes) p.State = ClippingPlaneState.Disabled;         // ⚠ verify
                vp.SetClippingPlanes(planes);
                doc.CurrentViewpoint.CopyFrom(vp);
            }
            catch (Exception ex) { DiagnosticsLog.Swallowed("LevelModels: clear clip planes", ex); }
        }

        // ── Export ───────────────────────────────────────────────────────────

        /// <summary>Writes the currently-visible model to an NWD, physically dropping hidden
        /// geometry. Returns the failure reason, or "" on success.</summary>
        public static string ExportNwd(Document doc, string path, bool embedXrefs, bool keepProps)
        {
            try
            {
                var opts = new NwdExportOptions                                          // ⚠ 2026-only API
                {
                    ExcludeHiddenItems          = true,
                    EmbedXrefs                  = embedXrefs,
                    PreventObjectPropertyExport = !keepProps,
                };
                bool ok = doc.TryExportToNwd(path, opts);                                // ⚠ 2026-only API
                return ok ? "" : AppStrings.T("navis.levelModels.log.exportRefused");
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("LevelModels: export nwd", ex);
                return ex.Message;
            }
        }

        // ── Units ────────────────────────────────────────────────────────────

        public static string UnitSuffix(Document doc)
        {
            try
            {
                switch (doc.Units)                                                       // ⚠ verify (Document.Units enum)
                {
                    case Units.Feet:        return "ft";
                    case Units.Inches:      return "in";
                    case Units.Meters:      return "m";
                    case Units.Centimeters: return "cm";
                    case Units.Millimeters: return "mm";
                    default:                return "";
                }
            }
            catch (Exception ex)
            {
                // Cosmetic only — the band still works, the elevations just render unitless.
                DiagnosticsLog.Swallowed("LevelModels: read document units", ex);
                return "";
            }
        }
    }
}
