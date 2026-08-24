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

        // ── Units ────────────────────────────────────────────────────────────

        /// <summary>Multiplier from the document's own units into FEET. Bands and every Z value
        /// the tool compares are held in feet, because that is what the UI states — a model
        /// authored in millimetres must not silently make a "12.00" band mean 12 mm.</summary>
        public static double ToFeet(Document doc)
        {
            try
            {
                return UnitConversion.ScaleFactor(doc.Units, Units.Feet);   // confirmed member
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed("LevelModels: unit scale factor", ex);
                return 1.0;   // treat as already-feet rather than scaling by a guess
            }
        }

        // ── Level discovery — from the source model's TREE, not from elements ──

        /// <summary>What the last <see cref="DiscoverLevels"/> call saw, so a zero result can say
        /// why instead of leaving the user guessing.</summary>
        public sealed class DiscoveryReport
        {
            public string SourceModel = "";
            public int    GroupsSeen;
            /// <summary>Names of the child nodes under the model root, whether or not they looked
            /// like levels — the diagnostic that makes an empty result actionable.</summary>
            public List<string> ChildNames = new List<string>();
        }

        public static DiscoveryReport LastDiscovery { get; private set; } = new DiscoveryReport();

        /// <summary>
        /// Reads the levels of ONE model from its tree: the nodes directly under the model's root.
        /// An NWC exported from Revit "divided by level" carries exactly that — LEVEL 0, LEVEL 01,
        /// … ROOF BEARING — which is what the Navisworks selection tree shows.
        ///
        /// <para>This replaces v1's federation-wide element-property sweep. That scan was capped,
        /// walked every geometry item in every model, and still found nothing; this reads a
        /// handful of nodes from a single model and is effectively instant.</para>
        ///
        /// Elevations come from each group's own bounding box and are returned in FEET.
        /// </summary>
        public static List<DiscoveredLevel> DiscoverLevels(Document doc, int modelIndex)
        {
            ResetProbeFailures();
            var report = new DiscoveryReport();
            LastDiscovery = report;

            var found = new List<DiscoveredLevel>();
            if (doc == null || doc.IsClear) return found;
            if (modelIndex < 0 || modelIndex >= doc.Models.Count) return found;

            double toFeet = ToFeet(doc);

            try
            {
                Model model = doc.Models[modelIndex];
                report.SourceModel = SafeModelName(model, "", modelIndex);

                foreach (ModelItem child in model.RootItem.Children)
                {
                    string name = SafeDisplayName(child).Trim();
                    if (name.Length == 0) continue;
                    report.ChildNames.Add(name);
                    report.GroupsSeen++;

                    // Every child is offered — the tree of a level-divided export contains levels
                    // and little else, and filtering by a name pattern here would silently drop a
                    // level whose name does not fit the pattern (ROOF BEARING, PODIUM…). The user
                    // deletes any row that is not a level.
                    if (!TryZExtent(child, out double lo, out double hi)) { lo = 0; hi = 0; }
                    found.Add(new DiscoveredLevel
                    {
                        Name      = name,
                        Elevation = lo * toFeet,
                        Top       = hi * toFeet,
                    });
                }
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("LevelModels: read level groups", ex);
            }

            return found.OrderBy(l => l.Elevation).ToList();
        }

        private static string SafeDisplayName(ModelItem item)
        {
            try { return item.DisplayName ?? ""; }
            catch (Exception ex) { DiagnosticsLog.Swallowed("LevelModels: item display name", ex); return ""; }
        }

        // ── Auto-assign models to levels by file name ────────────────────────

        /// <summary>
        /// Matches each model to the levels its FILE NAME mentions. "DT-Arch-L02.nwc" lands on
        /// "LEVEL 02"; a model naming no level at all is left unassigned rather than guessed at.
        ///
        /// <para>Matching is done on a squashed form of both strings (letters and digits only,
        /// upper-cased) so "LEVEL 02" / "Level02" / "L02" / "L2" all reconcile. The longest level
        /// token that matches wins, so "LEVEL 01" is never mistaken for "LEVEL 0" when both
        /// exist — the single most likely way a name match goes quietly wrong.</para>
        /// </summary>
        public static void AutoAssign(IReadOnlyList<ModelRef> models, IReadOnlyList<LevelDef> levels)
        {
            if (models == null || levels == null) return;

            // Longest first: "LEVEL01" must be tested before "LEVEL0".
            var tokens = levels
                .Select(lv => new { Level = lv, Keys = LevelTokens(lv.Name) })
                .Where(x => x.Keys.Count > 0)
                .ToList();

            foreach (var m in models)
            {
                string hay = Squash(Path.GetFileNameWithoutExtension(m.SourceFile ?? "") + " " + m.DisplayName);
                if (hay.Length == 0) continue;

                var best = tokens
                    .Select(x => new { x.Level, Hit = x.Keys.Where(k => hay.Contains(k)).OrderByDescending(k => k.Length).FirstOrDefault() })
                    .Where(x => x.Hit != null)
                    .OrderByDescending(x => x.Hit!.Length)
                    .FirstOrDefault();

                if (best == null) continue;
                if (!best.Level.Models.Contains(m.Key)) best.Level.Models.Add(m.Key);
            }
        }

        /// <summary>The forms a level name might take inside a file name, longest first.</summary>
        private static List<string> LevelTokens(string levelName)
        {
            var keys = new List<string>();
            string squashed = Squash(levelName);
            if (squashed.Length == 0) return keys;

            keys.Add(squashed);                                   // LEVEL02

            // A trailing number gives the short forms people actually use in file names.
            var m = System.Text.RegularExpressions.Regex.Match(squashed, @"^(?:LEVEL|LVL|FLOOR|STOREY|STORY|L|B)(\d{1,3})$",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success)
            {
                string digits = m.Groups[1].Value;               // "02"
                string bare   = digits.TrimStart('0');
                if (bare.Length == 0) bare = "0";
                keys.Add("LEVEL" + digits);
                keys.Add("L" + digits);                          // L02
                if (bare != digits) { keys.Add("LEVEL" + bare); keys.Add("L" + bare); }   // L2
            }
            return keys.Distinct().OrderByDescending(k => k.Length).ToList();
        }

        /// <summary>Letters and digits only, upper-cased — so spaces, dashes and underscores in
        /// either the level name or the file name stop mattering.</summary>
        private static string Squash(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s)
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToUpperInvariant(c));
            return sb.ToString();
        }

        // ── Geometry extents (only gathered when some level trims) ────────────

        /// <summary>Vertical extents of every geometry item, in FEET so they compare directly
        /// against the levels' bands.</summary>
        public static List<ItemZ> GatherItemZ(Document doc)
        {
            ResetProbeFailures();
            var list = new List<ItemZ>();
            if (doc == null || doc.IsClear) return list;

            double toFeet = ToFeet(doc);
            for (int i = 0; i < doc.Models.Count; i++)
                foreach (ModelItem item in Descendants(doc.Models[i]))
                {
                    if (!SafeHasGeometry(item)) continue;
                    if (!TryZExtent(item, out double lo, out double hi)) continue;
                    list.Add(new ItemZ
                    {
                        Item = item, ModelIndex = i,
                        MinZ = lo * toFeet, MaxZ = hi * toFeet,
                    });
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

            // 2. Within the owned models, elements outside the band. Trim is always on; a level
            //    with no real band (top not above bottom) simply has nothing to trim against.
            if (level.HasBand && items != null)
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
                AimAtBand(vp, doc, level);
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

        /// <summary>Lifts the camera to the MIDDLE of the level's band so the saved viewpoint opens
        /// looking at that level rather than wherever the user happened to be standing. Only the
        /// height is changed — the direction the user is facing is left alone, since that is a
        /// preference and the band is the thing this tool actually knows about.</summary>
        private static void AimAtBand(Viewpoint vp, Document doc, LevelDef level)
        {
            try
            {
                if (!level.HasBand) return;
                double toFeet = ToFeet(doc);
                double scale  = Math.Abs(toFeet) > 1e-9 ? 1.0 / toFeet : 1.0;
                double midZ   = ((level.Bottom + level.Top) * 0.5) * scale;   // band is in feet

                Point3D p = vp.Position;                                                  // confirmed get/set
                vp.Position = new Point3D(p.X, p.Y, midZ);
            }
            catch (Exception ex) { DiagnosticsLog.Swallowed("LevelModels: aim viewpoint at band", ex); }
        }

        // ── Viewpoint isolation for the export ───────────────────────────────

        /// <summary>
        /// Takes every saved viewpoint out of the document and hands them back for restoring, so
        /// the NWD written next contains ONLY the viewpoint this run creates. Returns null when
        /// there was nothing to remove (or the read failed), which the caller treats as "nothing
        /// to restore".
        ///
        /// <para>This mutates the LIVE document. It is only ever safe because the caller restores
        /// in a finally — and even a hard failure costs the session's viewpoints, not the file's,
        /// since nothing here saves the document.</para>
        /// </summary>
        public static IList<SavedItem>? TakeViewpoints(Document doc)
        {
            try
            {
                var snapshot = doc.SavedViewpoints.CreateCopy();                         // confirmed
                if (snapshot == null || snapshot.Count == 0) return null;
                doc.SavedViewpoints.Clear();                                             // confirmed
                return snapshot;
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("LevelModels: take saved viewpoints", ex);
                return null;
            }
        }

        /// <summary>Puts the document's own viewpoints back, dropping whatever the run added.</summary>
        public static void RestoreViewpoints(Document doc, IList<SavedItem>? snapshot)
        {
            try
            {
                doc.SavedViewpoints.Clear();
                if (snapshot != null && snapshot.Count > 0)
                    doc.SavedViewpoints.CopyFrom(snapshot);                              // CopyFrom(IEnumerable<SavedItem>) — confirmed
            }
            catch (Exception ex) { DiagnosticsLog.Error("LevelModels: restore saved viewpoints", ex); }
        }

        /// <summary>Removes just the viewpoints added since the document was emptied, so the next
        /// level does not inherit the previous level's viewpoint.</summary>
        public static void ClearViewpoints(Document doc)
        {
            try { doc.SavedViewpoints.Clear(); }
            catch (Exception ex) { DiagnosticsLog.Swallowed("LevelModels: clear saved viewpoints", ex); }
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

                // bottom/top arrive in FEET; the clip box is expressed in the document's own
                // units, so convert back or the band lands at the wrong height in a metric model.
                double toFeet = ToFeet(doc);
                double scale  = Math.Abs(toFeet) > 1e-9 ? 1.0 / toFeet : 1.0;
                double lo = bottom * scale, hi = top * scale;

                Viewpoint vp = doc.CurrentViewpoint.ToViewpoint();
                ClipPlaneSet clip = vp.ClipPlanes;
                clip.Box = new BoundingBox3D(
                    new Point3D(-ClipPlaneHorizontalExtent, -ClipPlaneHorizontalExtent, lo),
                    new Point3D( ClipPlaneHorizontalExtent,  ClipPlaneHorizontalExtent, hi));
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
                    // Stamp the version explicitly rather than leaving it at the default, so the
                    // NWD always opens as the same Navisworks version it was created from.
                    FileVersion                 = (int)DocumentFileVersion.Navisworks2026,
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

    }
}
