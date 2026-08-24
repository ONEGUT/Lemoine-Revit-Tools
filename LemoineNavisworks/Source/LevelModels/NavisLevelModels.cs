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
    // Every member this file calls has been confirmed against a real
    // Autodesk.Navisworks.Api.dll (2026) by decoding its metadata tables with dnfile —
    // the same technique CLAUDE.md's Research Discipline prescribes for RevitAPI.dll,
    // via devtools/navis_dump.py. This project still cannot be BUILT or RUN on Linux
    // (no Navisworks host, no dnfile-confirmable runtime behavior), so two things
    // remain genuinely unverified despite every name/signature being real, and stay
    // tagged "⚠ verify" at their call sites rather than the whole file:
    //   • whether DocumentModels.SetHidden on a model ROOT item cascades to its
    //     descendants (the whole-model hide optimization depends on it);
    //   • whether ClipPlaneSet.Mode = Box actually renders as "clip outside the box,
    //     unclipped inside" (see ApplyClip).
    // Both degrade to a logged warning rather than an unhandled throw if wrong.
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
                    try { file = m.FileName ?? ""; }                                  // Model.FileName — confirmed
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
                string rn = m.RootItem?.DisplayName ?? "";                             // ModelItem.DisplayName — confirmed
                if (!string.IsNullOrWhiteSpace(rn)) return rn.Trim();
            }
            catch (Exception ex) { DiagnosticsLog.Swallowed("LevelModels: root item name", ex); }

            if (!string.IsNullOrWhiteSpace(sourceFile))
                return Path.GetFileNameWithoutExtension(sourceFile);
            return $"Model {index + 1}";
        }

        // ── Level discovery (names + elevations, both editable afterwards) ────

        /// <summary>What the last <see cref="DiscoverLevels"/> call actually saw. A scan that
        /// returns nothing is useless without this — the user needs to know WHETHER the models
        /// carry a level property at all and, if they do, what it is called.</summary>
        public sealed class DiscoveryReport
        {
            public int    ItemsScanned;
            public bool   HitCap;
            public string MatchedBy = "";              // which pass produced the result
            /// <summary>Distinct property names seen, most common first — the diagnostic that
            /// makes a zero result actionable instead of a mystery.</summary>
            public List<string> PropertyNames = new List<string>();
        }

        public static DiscoveryReport LastDiscovery { get; private set; } = new DiscoveryReport();

        // Property names that carry a level, in preference order. Revit exports usually surface
        // "Level"; MEP/structural families often only carry a Base/Reference/Schedule level.
        private static readonly string[] LevelPropertyNames =
        {
            "Level", "Base Level", "Reference Level", "Schedule Level",
            "Base Constraint", "Home Level", "Level Name", "Story", "Storey",
        };

        /// <summary>
        /// Finds the levels present in the federation, taking each level's elevation from the
        /// lowest item carrying it. Three passes, stopping at the first that finds anything:
        ///   1. a property whose name is one of <see cref="LevelPropertyNames"/> (exact);
        ///   2. any property whose name CONTAINS "level" with a non-empty value;
        ///   3. item / ancestor names that look like a level ("L01", "B1", "Ground", "Roof"…),
        ///      which is how a federation that groups by level in the TREE rather than by
        ///      property presents itself.
        /// Every pass records <see cref="LastDiscovery"/> so a zero result can say why.
        /// </summary>
        public static List<DiscoveredLevel> DiscoverLevels(Document doc, int cap = LevelDefaults.DiscoverScanCap)
        {
            ResetProbeFailures();
            var report = new DiscoveryReport();
            LastDiscovery = report;
            if (doc == null || doc.IsClear) return new List<DiscoveredLevel>();

            // One traversal, collecting everything each pass needs, so a big federation is walked
            // once rather than three times.
            var byExact   = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var byLoose   = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var byName    = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var propSeen  = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            int scanned = 0;
            for (int i = 0; i < doc.Models.Count && scanned < cap; i++)
            {
                foreach (ModelItem item in Descendants(doc.Models[i]))
                {
                    if (scanned >= cap) { report.HitCap = true; break; }

                    // The old scan counted (and required) geometry items only. A level is often
                    // carried on a non-geometry group/layer node, and burning the cap on one
                    // model's geometry could exhaust the budget before ever reaching a model that
                    // does carry levels — which is exactly a scan that "finds nothing".
                    bool hasGeom = SafeHasGeometry(item);
                    scanned++;

                    double lo = 0;
                    bool haveZ = hasGeom && TryZExtent(item, out lo, out _);

                    ReadLevelCandidates(item, propSeen, out string exact, out string loose);

                    if (!string.IsNullOrWhiteSpace(exact)) Record(byExact, exact, haveZ, lo);
                    if (!string.IsNullOrWhiteSpace(loose)) Record(byLoose, loose, haveZ, lo);

                    string treeName = LevelLikeName(SafeDisplayName(item));
                    if (!string.IsNullOrWhiteSpace(treeName)) Record(byName, treeName, haveZ, lo);
                }
            }

            report.ItemsScanned  = scanned;
            report.PropertyNames = propSeen.OrderByDescending(kv => kv.Value)
                                           .Select(kv => kv.Key)
                                           .Take(25)
                                           .ToList();

            var chosen = byExact.Count > 0 ? byExact
                       : byLoose.Count > 0 ? byLoose
                       : byName;
            report.MatchedBy = byExact.Count > 0 ? "property"
                             : byLoose.Count > 0 ? "property-loose"
                             : byName.Count  > 0 ? "name"
                             : "";

            return chosen.OrderBy(kv => kv.Value)
                         .Select(kv => new DiscoveredLevel { Name = kv.Key, Elevation = kv.Value })
                         .ToList();
        }

        // An item with no readable Z still proves the level EXISTS — record it at +inf so it is
        // kept but sorts last, rather than dropping the level entirely (the old scan's `continue`
        // on a failed bbox read silently discarded levels carried by non-geometry nodes).
        private static void Record(Dictionary<string, double> into, string level, bool haveZ, double z)
        {
            double v = haveZ ? z : double.PositiveInfinity;
            if (!into.TryGetValue(level, out double cur) || v < cur) into[level] = v;
        }

        private static string SafeDisplayName(ModelItem item)
        {
            try { return item.DisplayName ?? ""; }
            catch (Exception ex) { DiagnosticsLog.Swallowed("LevelModels: item display name", ex); return ""; }
        }

        /// <summary>Reads both an exact-match and a loose-match level value off one item, and
        /// tallies every property name seen for the diagnostic report — all in a single pass over
        /// the item's categories, which is the expensive part.</summary>
        private static void ReadLevelCandidates(
            ModelItem item, Dictionary<string, int> propSeen, out string exact, out string loose)
        {
            exact = ""; loose = "";
            try
            {
                foreach (PropertyCategory cat in item.PropertyCategories)
                {
                    foreach (DataProperty p in cat.Properties)
                    {
                        string pd = (p.DisplayName ?? p.Name ?? "").Trim();
                        if (pd.Length == 0) continue;

                        if (propSeen.TryGetValue(pd, out int n)) propSeen[pd] = n + 1;
                        else                                     propSeen[pd] = 1;

                        bool isExact = exact.Length == 0 &&
                                       LevelPropertyNames.Any(k => pd.Equals(k, StringComparison.OrdinalIgnoreCase));
                        bool isLoose = loose.Length == 0 &&
                                       pd.IndexOf("level", StringComparison.OrdinalIgnoreCase) >= 0;
                        if (!isExact && !isLoose) continue;

                        string v = "";
                        try { v = p.Value?.ToDisplayString() ?? ""; }
                        catch (Exception ex) { DiagnosticsLog.Swallowed("LevelModels: read property value", ex); }
                        v = v.Trim();
                        if (v.Length == 0) continue;

                        if (isExact) exact = v;
                        if (isLoose) loose = v;
                    }
                }
            }
            catch (Exception ex) { DiagnosticsLog.Swallowed("LevelModels: read level property", ex); }
        }

        /// <summary>Returns the trimmed name when it reads like a level label, else "". Covers the
        /// conventions actually in use here — L1 / L01 for storeys, L0 / Underground for below
        /// grade — plus the usual B1 / Level 2 / Ground / Roof variants.</summary>
        private static string LevelLikeName(string raw)
        {
            string name = (raw ?? "").Trim();
            if (name.Length == 0 || name.Length > 40) return "";

            // L1, L01, L-01, LVL 2, Level 3, B1, Storey 4 …
            if (System.Text.RegularExpressions.Regex.IsMatch(
                    name, @"^(L|B|LVL|LEVEL|FLOOR|STOR(E)?Y)\s*[-_]?\s*\d{1,3}([A-Za-z]|\.\d+)?$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                return name;

            // Named levels with no number.
            string[] named = { "underground", "basement", "ground", "roof", "mezzanine", "podium", "plant" };
            foreach (var w in named)
                if (name.Equals(w, StringComparison.OrdinalIgnoreCase)) return name;

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
            try { seq = model.RootItem.DescendantsAndSelf; }                            // ModelItem.DescendantsAndSelf : IEnumerable<ModelItem> — confirmed
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed("LevelModels: descend model", ex);
                yield break;
            }
            foreach (var it in seq) yield return it;
        }

        private static bool SafeHasGeometry(ModelItem item)
        {
            try { return item.HasGeometry; }                                            // ModelItem.HasGeometry — confirmed
            catch (Exception ex) { NoteProbeFailure("LevelModels: HasGeometry", ex); return false; }
        }

        private static bool TryZExtent(ModelItem item, out double minZ, out double maxZ)
        {
            minZ = 0; maxZ = 0;
            try
            {
                BoundingBox3D bb = item.BoundingBox();                                  // ModelItem.BoundingBox() — confirmed
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
                // DocumentModels.SetHidden(IEnumerable<ModelItem>, bool) — confirmed via
                // libs-navis dnfile decode, no ModelItemCollection wrapper needed. ⚠ verify:
                // whether hiding a model's ROOT item (the whole-model hide optimization this file
                // relies on) cascades to its descendants is scene-graph BEHAVIOR, not something a
                // metadata decode can confirm — needs a real Navisworks run.
                doc.Models.SetHidden(items, hidden);
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
                try { if (it.IsHidden) hidden.Add(it); }                                 // ModelItem.IsHidden — confirmed
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
                if (clip && level.HasBand) ApplyClip(doc, level.Bottom, level.Top, log);

                Viewpoint vp = doc.CurrentViewpoint.ToViewpoint();                       // confirmed
                var sv = new SavedViewpoint(vp) { DisplayName = name };                  // SavedViewpoint(Viewpoint) + inherited SavedItem.DisplayName — confirmed
                doc.SavedViewpoints.AddCopy(sv);                                         // AddCopy(SavedItem) — confirmed (SavedViewpoint : SavedItem)
                return true;
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed("LevelModels: save viewpoint", ex);
                log?.Invoke(AppStrings.T("navis.levelModels.log.viewpointFailed", name), "warn");
                return false;
            }
        }

        // Clipping planes — real API confirmed by decoding libs-navis\Autodesk.Navisworks.Api.dll
        // with the same dnfile metadata-table approach CLAUDE.md prescribes for RevitAPI.dll (see
        // Research Discipline). An earlier draft guessed Viewpoint.GetClippingPlanes() /
        // SetClippingPlanes() and a ClippingPlaneAlignment/ClippingPlaneState enum pair; none of
        // that exists. The real surface is a single mutable object:
        //
        //   Viewpoint.ClipPlanes : ClipPlaneSet
        //     .Mode          : ClipPlaneSetMode   (Planes | Box)         — get/set, confirmed
        //     .Box           : BoundingBox3D      (the active volume when Mode == Box) — get/set, confirmed
        //     .BoxTransform  : Transform3D        (frame the Box is expressed in)      — GET-ONLY, confirmed
        //     .Enabled       : bool                                     — get/set, confirmed
        //
        // A first pass also tried to assign BoxTransform to force identity (no rotation/offset).
        // That does not compile — CS0200, "cannot be assigned to, it is read only" — confirmed by
        // decoding ClipPlaneSet's methods: only get_BoxTransform/GetBoxTransform exist, no setter
        // anywhere. So BoxTransform is left alone entirely; whatever frame a fresh ClipPlaneSet
        // reports is out of this code's control, and Box's coordinates are supplied assuming that
        // frame is identity (world space). Box mode is used with a box tight on Z (the level's
        // band) and enormous on X/Y, so the visible effect is a horizontal band cut without
        // needing the model's real X/Y extent. This compiles against confirmed members throughout;
        // what is NOT independently confirmed (no live Navisworks to render it) is that Box mode
        // reads exactly as "clip only outside the box" AND that BoxTransform is in fact identity —
        // treat the visual result as ⚠ verify on a real Windows run even though every call in it
        // is real. If the clip renders skewed/rotated, that read-only transform is the first thing
        // to inspect (log its Translation/Linear via CurrentPlane or a diagnostic dump).
        //
        // Bottom/Top being in the wrong order would silently produce an inverted or empty box —
        // guard it rather than pass whatever the caller has.
        private const double ClipPlaneHorizontalExtent = 1_000_000; // matches the level-band stepper's own ±range

        private static void ApplyClip(Document doc, double bottom, double top, Action<string, string> log)
        {
            try
            {
                if (top <= bottom)
                {
                    log?.Invoke(AppStrings.T("navis.levelModels.log.clipBadBand"), "warn");
                    return;
                }

                Viewpoint vp = doc.CurrentViewpoint.ToViewpoint();
                ClipPlaneSet clip = vp.ClipPlanes;
                clip.Box = new BoundingBox3D(
                    new Point3D(-ClipPlaneHorizontalExtent, -ClipPlaneHorizontalExtent, bottom),
                    new Point3D( ClipPlaneHorizontalExtent,  ClipPlaneHorizontalExtent, top));
                clip.Mode    = ClipPlaneSetMode.Box;
                clip.Enabled = true;
                doc.CurrentViewpoint.CopyFrom(vp);
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("LevelModels: apply clip planes", ex);
                log?.Invoke(AppStrings.T("navis.levelModels.log.clipFailed"), "warn");
            }
        }

        /// <summary>Disables the clip so the run leaves the live view unclipped afterwards.</summary>
        public static void ClearClip(Document doc)
        {
            try
            {
                Viewpoint vp = doc.CurrentViewpoint.ToViewpoint();
                vp.ClipPlanes.Enabled = false;
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
                var opts = new NwdExportOptions                                          // confirmed — 2026-only API per Autodesk's own docs, not present before
                {
                    ExcludeHiddenItems          = true,
                    EmbedXrefs                  = embedXrefs,
                    PreventObjectPropertyExport = !keepProps,
                };
                bool ok = doc.TryExportToNwd(path, opts);                                // confirmed — 2026-only API per Autodesk's own docs, not present before
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
                switch (doc.Units)                                                       // Document.Units — confirmed (member names match; underlying ints differ from an earlier guess but C# switches by name)
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
