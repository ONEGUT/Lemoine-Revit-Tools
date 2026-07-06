using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Helpers;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.ScopeBoxes
{
    // ── Manager payload types (plain data — safe across threads) ─────────────

    public sealed class ManagerViewRef
    {
        public ElementId Id = ElementId.InvalidElementId;
        public string Name = "";
        public string TypeLabel = "";
    }

    public sealed class ManagerDatumRef
    {
        public ElementId Id = ElementId.InvalidElementId;
        public string Name = "";
        /// <summary>Kind token: "Grid" | "Level" | "RefPlane" (logic identifier).</summary>
        public string Kind = "Grid";
        /// <summary>Scope box currently governing this datum's extents (InvalidElementId = none).</summary>
        public ElementId CurrentBoxId = ElementId.InvalidElementId;
        // Geometry for intersection testing (Revit only allows a scope box on a datum whose
        // plane crosses the box). Levels use Elevation; grids/ref planes use the XY bbox.
        public bool   IsLevel;
        public double Elevation;
        public double MinX, MinY, MaxX, MaxY;
        public bool   HasBounds;

        /// <summary>True when this datum's plane intersects the given box bounds — mirrors
        /// Revit's own "datum plane must intersect the scope box" rule closely enough to keep
        /// non-intersecting datums out of the picker (so the assignment never errors).</summary>
        public bool IntersectsBox(ScopeBoxUsage box)
        {
            if (IsLevel)
                return Elevation >= box.MinZ - 0.01 && Elevation <= box.MaxZ + 0.01;
            if (!HasBounds) return true; // unknown extent — don't hide it
            return !(MaxX < box.MinX || MinX > box.MaxX || MaxY < box.MinY || MinY > box.MaxY);
        }
    }

    public sealed class ScopeBoxUsage
    {
        public ElementId Id = ElementId.InvalidElementId;
        public string Name = "";
        public double WidthFt, DepthFt, HeightFt;
        public double MinX, MinY, MinZ, MaxX, MaxY, MaxZ;
        public List<ManagerViewRef>  Views  = new List<ManagerViewRef>();
        public List<ManagerDatumRef> Datums = new List<ManagerDatumRef>();
        public bool IsUnused => Views.Count == 0 && Datums.Count == 0;
    }

    public sealed class ManagerScanResult
    {
        public List<ScopeBoxUsage>   Boxes         = new List<ScopeBoxUsage>();
        /// <summary>Every non-template view with a writable Scope Box parameter.</summary>
        public List<ManagerViewRef>  EligibleViews = new List<ManagerViewRef>();
        /// <summary>Every datum (grid / level / named reference plane) with a writable scope-box parameter.</summary>
        public List<ManagerDatumRef> Datums        = new List<ManagerDatumRef>();
        /// <summary>Project-browser tree for the assign-views picker.</summary>
        public LemoineBrowserTree?   Tree;
    }

    /// <summary>
    /// Read-only scan for the Scope Box Manager: every scope box with the views and
    /// datums using it, the assignable-view universe, and the browser tree for the
    /// view picker. Shared by the scan event and the post-action refresh.
    /// </summary>
    public sealed class ScopeBoxManagerScanHandler : IExternalEventHandler
    {
        public Action<ManagerScanResult>? OnScanComplete { get; set; }
        public Action<string>?            OnError        { get; set; }

        public string GetName() => "LemoineTools.Tools.ScopeBoxes.ScopeBoxManagerScanHandler";

        public void Execute(UIApplication app)
        {
            var doc = app.ActiveUIDocument.Document;
            try   { OnScanComplete?.Invoke(Collect(doc)); }
            catch (Exception ex) { OnError?.Invoke(ex.Message); }
        }

        /// <summary>Runs the full usage scan. Must be called on Revit's main thread.</summary>
        internal static ManagerScanResult Collect(Document doc)
        {
            var result = new ManagerScanResult();

            // Boxes
            var usageById = new Dictionary<long, ScopeBoxUsage>();
            foreach (var b in ScopeBoxCreatorScanHandler.CollectScopeBoxes(doc))
            {
                var u = new ScopeBoxUsage
                {
                    Id = b.Id, Name = b.Name,
                    WidthFt = b.WidthFt, DepthFt = b.DepthFt, HeightFt = b.HeightFt,
                    MinX = b.MinX, MinY = b.MinY, MinZ = b.MinZ,
                    MaxX = b.MaxX, MaxY = b.MaxY, MaxZ = b.MaxZ,
                };
                result.Boxes.Add(u);
                usageById[b.Id.Value] = u;
            }

            // Views — eligibility and usage from the same parameter
            foreach (var v in new FilteredElementCollector(doc)
                         .OfClass(typeof(View)).Cast<View>()
                         .Where(v => !v.IsTemplate))
            {
                Parameter? p;
                try { p = v.get_Parameter(BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP); }
                catch (Exception ex) { LemoineLog.Swallowed($"ScopeBoxManager scan: read view {v.Id.Value} scope-box param", ex); continue; }
                if (p == null || p.IsReadOnly) continue;

                var vref = new ManagerViewRef
                {
                    Id = v.Id, Name = v.Name,
                    TypeLabel = LemoineTools.Tools.LinkViews.ViewsByTemplateRunHandler.ViewTypeLabel(v.ViewType),
                };
                result.EligibleViews.Add(vref);

                var boxId = p.AsElementId();
                if (boxId != null && usageById.TryGetValue(boxId.Value, out var usage))
                    usage.Views.Add(vref);
            }

            // Datums — grids, levels, named reference planes
            void CollectDatums<T>(string kind) where T : Element
            {
                foreach (var e in new FilteredElementCollector(doc).OfClass(typeof(T)).Cast<T>())
                {
                    Parameter? p;
                    try { p = e.get_Parameter(BuiltInParameter.DATUM_VOLUME_OF_INTEREST); }
                    catch (Exception ex) { LemoineLog.Swallowed($"ScopeBoxManager scan: read datum {e.Id.Value} scope-box param", ex); continue; }
                    if (p == null || p.IsReadOnly) continue;
                    if (kind == "RefPlane" && string.IsNullOrWhiteSpace(e.Name)) continue;

                    var boxId = p.AsElementId() ?? ElementId.InvalidElementId;
                    var dref  = new ManagerDatumRef
                    {
                        Id = e.Id, Name = e.Name, Kind = kind, CurrentBoxId = boxId,
                    };

                    // Geometry for the intersection filter.
                    if (e is Level lvl) { dref.IsLevel = true; dref.Elevation = lvl.Elevation; }
                    else
                    {
                        var bb = e.get_BoundingBox(null);
                        if (bb != null)
                        {
                            dref.HasBounds = true;
                            dref.MinX = Math.Min(bb.Min.X, bb.Max.X);
                            dref.MinY = Math.Min(bb.Min.Y, bb.Max.Y);
                            dref.MaxX = Math.Max(bb.Min.X, bb.Max.X);
                            dref.MaxY = Math.Max(bb.Min.Y, bb.Max.Y);
                        }
                    }
                    result.Datums.Add(dref);

                    if (usageById.TryGetValue(boxId.Value, out var usage))
                        usage.Datums.Add(dref);
                }
            }
            CollectDatums<Autodesk.Revit.DB.Grid>("Grid");
            CollectDatums<Level>("Level");
            CollectDatums<ReferencePlane>("RefPlane");

            result.Tree = BrowserTreeCapture.Capture(doc);
            return result;
        }
    }

    /// <summary>
    /// Executes one Scope Box Manager action inside a transaction, then re-runs the
    /// usage scan so the window refreshes from authoritative state. Action tokens:
    /// "SetViews" (make ViewIds the authoritative set of views carrying BoxId),
    /// "SetDatums" (same for datums), "Rename" (RenamePairs), "Delete" (BoxIds).
    /// </summary>
    public sealed class ScopeBoxManagerRunHandler : IExternalEventHandler
    {
        // ── Inputs set before Raise() ─────────────────────────────────
        public string          Action      { get; set; } = "";
        public ElementId       BoxId       { get; set; } = ElementId.InvalidElementId;
        public List<ElementId> ViewIds     { get; set; } = new List<ElementId>();
        public List<ElementId> DatumIds    { get; set; } = new List<ElementId>();
        public List<ElementId> BoxIds      { get; set; } = new List<ElementId>();
        public List<(ElementId BoxId, string NewName)> RenamePairs { get; set; }
            = new List<(ElementId, string)>();

        // ── Callbacks ─────────────────────────────────────────────────
        /// <summary>(succeededCount, failedCount, summaryLine, refreshed scan)</summary>
        public Action<int, int, string, ManagerScanResult?>? OnActionComplete { get; set; }

        public string GetName() => "LemoineTools.Tools.ScopeBoxes.ScopeBoxManagerRunHandler";

        public void Execute(UIApplication app)
        {
            var doc = app.ActiveUIDocument.Document;
            int ok = 0, failed = 0;
            string summary;
            try
            {
                summary = RunAction(doc, ref ok, ref failed);
            }
            catch (Exception ex)
            {
                LemoineLog.Error($"ScopeBoxManager: action '{Action}' aborted", ex);
                summary = LemoineStrings.T("scopeBoxes.manager.status.aborted", ex.Message);
                failed++;
            }

            ManagerScanResult? refreshed = null;
            try { refreshed = ScopeBoxManagerScanHandler.Collect(doc); }
            catch (Exception ex) { LemoineLog.Error("ScopeBoxManager: post-action rescan failed", ex); }

            try { OnActionComplete?.Invoke(ok, failed, summary, refreshed); }
            finally
            {
                // Session-long static handler — drop the action's payload.
                Action      = "";
                BoxId       = ElementId.InvalidElementId;
                ViewIds     = new List<ElementId>();
                DatumIds    = new List<ElementId>();
                BoxIds      = new List<ElementId>();
                RenamePairs = new List<(ElementId, string)>();
            }
        }

        private string RunAction(Document doc, ref int ok, ref int failed)
        {
            switch (Action)
            {
                case "SetViews":  return SetParamTargets(doc, ok: ref ok, failed: ref failed,
                    targetIds: ViewIds, isView: true);
                case "SetDatums": return SetParamTargets(doc, ok: ref ok, failed: ref failed,
                    targetIds: DatumIds, isView: false);
                case "Rename":    return Rename(doc, ref ok, ref failed);
                case "Delete":    return Delete(doc, ref ok, ref failed);
                default:
                    failed++;
                    return LemoineStrings.T("scopeBoxes.manager.status.unknownAction", Action);
            }
        }

        // Makes `targetIds` the authoritative carrier set for BoxId: assigns the box to
        // every listed element, clears it from every element currently carrying it that
        // is not listed. Per-element failures are counted and logged, never fatal.
        private string SetParamTargets(Document doc, ref int ok, ref int failed,
                                       List<ElementId> targetIds, bool isView)
        {
            var box = doc.GetElement(BoxId);
            if (box == null)
            {
                failed++;
                return LemoineStrings.T("scopeBoxes.manager.status.boxGone");
            }

            var bip = isView
                ? BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP
                : BuiltInParameter.DATUM_VOLUME_OF_INTEREST;

            var wanted = new HashSet<long>(targetIds.Select(id => id.Value));
            int assigned = 0, cleared = 0;

            using (var tx = new Transaction(doc, "Scope Box Manager — assign"))
            {
                // Swallow warning modals on datum assignment (the overlay already filters to
                // intersecting datums, so this only guards edge cases).
                ConfigureFailures(tx, swallowWarnings: !isView);
                tx.Start();

                // Current carriers (so unselected ones get cleared)
                IEnumerable<Element> universe = isView
                    ? new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                        .Where(v => !v.IsTemplate).Cast<Element>()
                    : new FilteredElementCollector(doc)
                        .WherePasses(new LogicalOrFilter(new List<ElementFilter>
                        {
                            new ElementClassFilter(typeof(Autodesk.Revit.DB.Grid)),
                            new ElementClassFilter(typeof(Level)),
                            new ElementClassFilter(typeof(ReferencePlane)),
                        }))
                        .WhereElementIsNotElementType();

                foreach (var e in universe)
                {
                    Parameter? p;
                    try { p = e.get_Parameter(bip); }
                    catch (Exception ex) { LemoineLog.Swallowed($"ScopeBoxManager assign: read param on {e.Id.Value}", ex); continue; }
                    if (p == null || p.IsReadOnly) continue;

                    bool carries = p.AsElementId()?.Value == BoxId.Value;
                    bool want    = wanted.Contains(e.Id.Value);
                    if (carries == want) { if (want) ok++; continue; }

                    try
                    {
                        p.Set(want ? BoxId : ElementId.InvalidElementId);
                        if (want) { assigned++; ok++; } else cleared++;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        LemoineLog.Swallowed($"ScopeBoxManager assign: set param on {e.Id.Value}", ex);
                    }
                }

                tx.Commit();
            }

            return LemoineStrings.T(
                isView ? "scopeBoxes.manager.status.viewsSet" : "scopeBoxes.manager.status.datumsSet",
                box.Name, assigned, cleared, failed);
        }

        private string Rename(Document doc, ref int ok, ref int failed)
        {
            // Pre-check collisions against existing names AND earlier batch entries.
            var taken = new HashSet<string>(
                ScopeBoxCreatorScanHandler.CollectScopeBoxes(doc).Select(b => b.Name),
                StringComparer.OrdinalIgnoreCase);

            int skipped = 0;
            using (var tx = new Transaction(doc, "Scope Box Manager — rename"))
            {
                ConfigureFailures(tx);
                tx.Start();

                foreach (var (boxId, newName) in RenamePairs)
                {
                    var el = doc.GetElement(boxId);
                    if (el == null || string.IsNullOrWhiteSpace(newName)) { skipped++; continue; }
                    if (string.Equals(el.Name, newName, StringComparison.Ordinal)) { skipped++; continue; }
                    if (taken.Contains(newName)) { skipped++; continue; }

                    try
                    {
                        taken.Remove(el.Name);
                        el.Name = newName;
                        taken.Add(newName);
                        ok++;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        LemoineLog.Swallowed($"ScopeBoxManager rename: box {boxId.Value} → '{newName}'", ex);
                    }
                }

                tx.Commit();
            }

            return LemoineStrings.T("scopeBoxes.manager.status.renamed", ok, skipped, failed);
        }

        private string Delete(Document doc, ref int ok, ref int failed)
        {
            var names = new List<string>();
            using (var tx = new Transaction(doc, "Scope Box Manager — delete"))
            {
                ConfigureFailures(tx);
                tx.Start();

                foreach (var id in BoxIds)
                {
                    var el = doc.GetElement(id);
                    if (el == null) continue;
                    string name = el.Name;
                    try { doc.Delete(id); names.Add(name); ok++; }
                    catch (Exception ex)
                    {
                        failed++;
                        LemoineLog.Swallowed($"ScopeBoxManager delete: box {id.Value} '{name}'", ex);
                    }
                }

                tx.Commit();
            }

            return names.Count > 0
                ? LemoineStrings.T("scopeBoxes.manager.status.deleted", ok, string.Join(", ", names), failed)
                : LemoineStrings.T("scopeBoxes.manager.status.deletedNone");
        }

        private static void ConfigureFailures(Transaction tx, bool swallowWarnings = false)
        {
            var opts = tx.GetFailureHandlingOptions();
            opts.SetClearAfterRollback(true);
            opts.SetDelayedMiniWarnings(true);
            if (swallowWarnings)
                opts.SetFailuresPreprocessor(new SwallowWarningsPreprocessor());
            tx.SetFailureHandlingOptions(opts);
        }

        /// <summary>
        /// Dismisses warning-level failures so no modal appears during a datum-assign pass.
        /// The overlay already filters to datums that intersect the box, so this is only a
        /// belt-and-suspenders guard; errors are left for Revit to roll back normally.
        /// </summary>
        private sealed class SwallowWarningsPreprocessor : IFailuresPreprocessor
        {
            public FailureProcessingResult PreprocessFailures(FailuresAccessor a)
            {
                a.DeleteAllWarnings();
                return FailureProcessingResult.Continue;
            }
        }
    }
}
