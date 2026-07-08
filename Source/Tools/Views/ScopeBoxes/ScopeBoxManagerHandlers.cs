using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Helpers;
using LemoineTools.Framework;

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
        public BrowserTree?   Tree;
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
                catch (Exception ex) { DiagnosticsLog.Swallowed($"ScopeBoxManager scan: read view {v.Id.Value} scope-box param", ex); continue; }
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
                    catch (Exception ex) { DiagnosticsLog.Swallowed($"ScopeBoxManager scan: read datum {e.Id.Value} scope-box param", ex); continue; }
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

        // ── "Bind sides to grids" inputs — InvalidElementId keeps that edge unchanged ──
        public ElementId NorthGridId { get; set; } = ElementId.InvalidElementId;
        public ElementId SouthGridId { get; set; } = ElementId.InvalidElementId;
        public ElementId EastGridId  { get; set; } = ElementId.InvalidElementId;
        public ElementId WestGridId  { get; set; } = ElementId.InvalidElementId;

        // ── "Split" inputs ──────────────────────────────────────────────
        /// <summary>"Gridline" | "Middle" (logic tokens).</summary>
        public string    SplitMode           { get; set; } = "Gridline";
        public ElementId SplitGridId         { get; set; } = ElementId.InvalidElementId;
        /// <summary>Middle mode only — "NS" (a north-south line, splits West/East) or
        /// "EW" (an east-west line, splits South/North). Gridline mode infers this from
        /// the chosen grid's own orientation.</summary>
        public string    SplitAxis           { get; set; } = "NS";
        public double    SplitOverlapFt      { get; set; }
        public bool      SplitDeleteOriginal { get; set; } = true;

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
                DiagnosticsLog.Error($"ScopeBoxManager: action '{Action}' aborted", ex);
                summary = AppStrings.T("scopeBoxes.manager.status.aborted", ex.Message);
                failed++;
            }

            ManagerScanResult? refreshed = null;
            try { refreshed = ScopeBoxManagerScanHandler.Collect(doc); }
            catch (Exception ex) { DiagnosticsLog.Error("ScopeBoxManager: post-action rescan failed", ex); }

            try { OnActionComplete?.Invoke(ok, failed, summary, refreshed); }
            finally
            {
                // Session-long static handler — drop the action's payload.
                Action        = "";
                BoxId         = ElementId.InvalidElementId;
                ViewIds       = new List<ElementId>();
                DatumIds      = new List<ElementId>();
                BoxIds        = new List<ElementId>();
                RenamePairs   = new List<(ElementId, string)>();
                NorthGridId = SouthGridId = EastGridId = WestGridId = ElementId.InvalidElementId;
                SplitGridId   = ElementId.InvalidElementId;
                SplitMode     = "Gridline";
                SplitAxis     = "NS";
                SplitOverlapFt = 0;
                SplitDeleteOriginal = true;
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
                case "Duplicate": return Duplicate(doc, ref ok, ref failed);
                case "BindSides": return BindSides(doc, ref ok, ref failed);
                case "Split":     return Split(doc, ref ok, ref failed);
                default:
                    failed++;
                    return AppStrings.T("scopeBoxes.manager.status.unknownAction", Action);
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
                return AppStrings.T("scopeBoxes.manager.status.boxGone");
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
                    catch (Exception ex) { DiagnosticsLog.Swallowed($"ScopeBoxManager assign: read param on {e.Id.Value}", ex); continue; }
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
                        DiagnosticsLog.Swallowed($"ScopeBoxManager assign: set param on {e.Id.Value}", ex);
                    }
                }

                tx.Commit();
            }

            return AppStrings.T(
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
                        DiagnosticsLog.Swallowed($"ScopeBoxManager rename: box {boxId.Value} → '{newName}'", ex);
                    }
                }

                tx.Commit();
            }

            return AppStrings.T("scopeBoxes.manager.status.renamed", ok, skipped, failed);
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
                        DiagnosticsLog.Swallowed($"ScopeBoxManager delete: box {id.Value} '{name}'", ex);
                    }
                }

                tx.Commit();
            }

            return names.Count > 0
                ? AppStrings.T("scopeBoxes.manager.status.deleted", ok, string.Join(", ", names), failed)
                : AppStrings.T("scopeBoxes.manager.status.deletedNone");
        }

        // ── Duplicate ────────────────────────────────────────────────────────────
        private string Duplicate(Document doc, ref int ok, ref int failed)
        {
            var box = doc.GetElement(BoxId);
            if (box == null) { failed++; return AppStrings.T("scopeBoxes.manager.status.boxGone"); }
            string baseName = box.Name;

            var taken = new HashSet<string>(
                ScopeBoxCreatorScanHandler.CollectScopeBoxes(doc).Select(b => b.Name),
                StringComparer.OrdinalIgnoreCase);
            string newName = UniqueCopyName(baseName, taken);

            using (var tx = new Transaction(doc, "Scope Box Manager — duplicate"))
            {
                ConfigureFailures(tx);
                tx.Start();
                try
                {
                    // Small XY offset so the copy isn't perfectly hidden under the original.
                    var copyIds = ElementTransformUtils.CopyElement(doc, BoxId, new XYZ(10, 0, 0));
                    var copyId = copyIds.FirstOrDefault() ?? ElementId.InvalidElementId;
                    var copy = copyId != ElementId.InvalidElementId ? doc.GetElement(copyId) : null;
                    if (copy == null)
                    {
                        failed++;
                        tx.RollBack();
                        return AppStrings.T("scopeBoxes.manager.status.duplicateFailed", baseName);
                    }
                    copy.Name = newName;
                    ok++;
                }
                catch (Exception ex)
                {
                    failed++;
                    DiagnosticsLog.Swallowed($"ScopeBoxManager duplicate: box {BoxId.Value}", ex);
                    tx.RollBack();
                    return AppStrings.T("scopeBoxes.manager.status.duplicateFailed", baseName);
                }
                tx.Commit();
            }

            return AppStrings.T("scopeBoxes.manager.status.duplicated", baseName, newName);
        }

        private static string UniqueCopyName(string baseName, HashSet<string> taken)
        {
            string candidate = $"{baseName} - Copy";
            int n = 2;
            while (taken.Contains(candidate)) candidate = $"{baseName} - Copy {n++}";
            return candidate;
        }

        // ── Bind sides to grids ────────────────────────────────────────────────────
        // The Revit API cannot resize a scope box's footprint (width/depth have no writable
        // parameter — confirmed by the Scope Box Probe), so this repositions the box so its
        // CENTER lands on the target rectangle's center, leaving its existing footprint
        // unchanged, and reports the exact W×D the user must then drag the handles to. Levels
        // within the box's (unchanged) Z-range are auto-assigned to it unless already claimed
        // by a different box.
        private string BindSides(Document doc, ref int ok, ref int failed)
        {
            var box = doc.GetElement(BoxId);
            var bb  = box?.get_BoundingBox(null);
            if (box == null || bb == null) { failed++; return AppStrings.T("scopeBoxes.manager.status.boxGone"); }

            double curMinX = Math.Min(bb.Min.X, bb.Max.X), curMaxX = Math.Max(bb.Min.X, bb.Max.X);
            double curMinY = Math.Min(bb.Min.Y, bb.Max.Y), curMaxY = Math.Max(bb.Min.Y, bb.Max.Y);
            double curMinZ = Math.Min(bb.Min.Z, bb.Max.Z), curMaxZ = Math.Max(bb.Min.Z, bb.Max.Z);
            double curW = curMaxX - curMinX, curD = curMaxY - curMinY;

            double? GridMidY(ElementId gid)
            {
                if (gid == ElementId.InvalidElementId) return null;
                var gbb = doc.GetElement(gid)?.get_BoundingBox(null);
                if (gbb == null) return null;
                return (Math.Min(gbb.Min.Y, gbb.Max.Y) + Math.Max(gbb.Min.Y, gbb.Max.Y)) / 2.0;
            }
            double? GridMidX(ElementId gid)
            {
                if (gid == ElementId.InvalidElementId) return null;
                var gbb = doc.GetElement(gid)?.get_BoundingBox(null);
                if (gbb == null) return null;
                return (Math.Min(gbb.Min.X, gbb.Max.X) + Math.Max(gbb.Min.X, gbb.Max.X)) / 2.0;
            }

            double north = GridMidY(NorthGridId) ?? curMaxY;
            double south = GridMidY(SouthGridId) ?? curMinY;
            double east  = GridMidX(EastGridId)  ?? curMaxX;
            double west  = GridMidX(WestGridId)  ?? curMinX;

            double targetCenterX = (west + east) / 2.0;
            double targetCenterY = (south + north) / 2.0;
            double curCenterX = (curMinX + curMaxX) / 2.0;
            double curCenterY = (curMinY + curMaxY) / 2.0;
            var delta = new XYZ(targetCenterX - curCenterX, targetCenterY - curCenterY, 0);

            int levelsAssigned = 0;
            using (var tx = new Transaction(doc, "Scope Box Manager — bind sides"))
            {
                ConfigureFailures(tx);
                tx.Start();

                try
                {
                    bool pinned = box.Pinned;
                    if (pinned) box.Pinned = false;
                    ElementTransformUtils.MoveElement(doc, BoxId, delta);
                    if (pinned) box.Pinned = true;
                    ok++;
                }
                catch (Exception ex)
                {
                    failed++;
                    DiagnosticsLog.Swallowed($"ScopeBoxManager bind sides: box {BoxId.Value}", ex);
                    tx.RollBack();
                    return AppStrings.T("scopeBoxes.manager.status.bindFailed", box.Name);
                }

                // Levels are automatic: assign every level in the box's Z-range, skipping one
                // already claimed by a DIFFERENT box (single-carrier — see ScopeBoxCreatorRunHandler).
                foreach (var lvl in new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>())
                {
                    if (lvl.Elevation < curMinZ - 0.01 || lvl.Elevation > curMaxZ + 0.01) continue;
                    Parameter? p;
                    try { p = lvl.get_Parameter(BuiltInParameter.DATUM_VOLUME_OF_INTEREST); }
                    catch (Exception ex) { DiagnosticsLog.Swallowed($"ScopeBoxManager bind sides: read level {lvl.Id.Value} param", ex); continue; }
                    if (p == null || p.IsReadOnly) continue;

                    var current = p.AsElementId();
                    bool claimedByOther = current != null && current.Value != ElementId.InvalidElementId.Value && current.Value != BoxId.Value;
                    if (claimedByOther) continue;

                    try { p.Set(BoxId); levelsAssigned++; }
                    catch (Exception ex) { DiagnosticsLog.Swallowed($"ScopeBoxManager bind sides: assign level {lvl.Id.Value}", ex); }
                }

                tx.Commit();
            }

            double reqW = east - west, reqD = north - south;
            if (Math.Abs(reqW - curW) > 0.5 || Math.Abs(reqD - curD) > 0.5)
                return AppStrings.T("scopeBoxes.manager.status.bindResizeNeeded",
                    box.Name, reqW.ToString("0.#"), reqD.ToString("0.#"), levelsAssigned);
            return AppStrings.T("scopeBoxes.manager.status.bound", box.Name, levelsAssigned);
        }

        // ── Split ────────────────────────────────────────────────────────────────
        // Same footprint constraint as BindSides — each half is a duplicate of the original
        // (so it inherits the un-resizable footprint) repositioned to its own half-center;
        // the exact required W×D per half is reported for a manual handle-drag.
        private string Split(Document doc, ref int ok, ref int failed)
        {
            var box = doc.GetElement(BoxId);
            var bb  = box?.get_BoundingBox(null);
            if (box == null || bb == null) { failed++; return AppStrings.T("scopeBoxes.manager.status.boxGone"); }

            double minX = Math.Min(bb.Min.X, bb.Max.X), maxX = Math.Max(bb.Min.X, bb.Max.X);
            double minY = Math.Min(bb.Min.Y, bb.Max.Y), maxY = Math.Max(bb.Min.Y, bb.Max.Y);
            double curW = maxX - minX, curD = maxY - minY;
            double centerX = (minX + maxX) / 2.0, centerY = (minY + maxY) / 2.0;

            string axis;      // "NS" (splits West/East) | "EW" (splits South/North)
            double splitCoord;

            if (SplitMode == "Gridline")
            {
                var gbb = doc.GetElement(SplitGridId)?.get_BoundingBox(null);
                if (gbb == null) { failed++; return AppStrings.T("scopeBoxes.manager.status.splitNoGrid"); }
                double gMinX = Math.Min(gbb.Min.X, gbb.Max.X), gMaxX = Math.Max(gbb.Min.X, gbb.Max.X);
                double gMinY = Math.Min(gbb.Min.Y, gbb.Max.Y), gMaxY = Math.Max(gbb.Min.Y, gbb.Max.Y);
                bool vertical = (gMaxY - gMinY) > (gMaxX - gMinX); // tall & thin → runs N-S
                axis = vertical ? "NS" : "EW";
                splitCoord = vertical ? (gMinX + gMaxX) / 2.0 : (gMinY + gMaxY) / 2.0;
            }
            else // Middle
            {
                axis = SplitAxis == "EW" ? "EW" : "NS";
                splitCoord = axis == "NS" ? centerX : centerY;
            }

            double half = Math.Max(0, SplitOverlapFt) / 2.0;

            // Half A (west/south) and Half B (east/north) — centers + required footprints.
            double aCenterX, aCenterY, aW, aD, bCenterX, bCenterY, bW, bD;
            if (axis == "NS")
            {
                double aMax = splitCoord + half, bMin = splitCoord - half;
                aCenterX = (minX + aMax) / 2.0; aW = aMax - minX; aCenterY = centerY; aD = curD;
                bCenterX = (bMin + maxX) / 2.0; bW = maxX - bMin; bCenterY = centerY; bD = curD;
            }
            else
            {
                double aMax = splitCoord + half, bMin = splitCoord - half;
                aCenterY = (minY + aMax) / 2.0; aD = aMax - minY; aCenterX = centerX; aW = curW;
                bCenterY = (bMin + maxY) / 2.0; bD = maxY - bMin; bCenterX = centerX; bW = curW;
            }

            var taken = new HashSet<string>(
                ScopeBoxCreatorScanHandler.CollectScopeBoxes(doc).Select(b => b.Name),
                StringComparer.OrdinalIgnoreCase);
            string nameA = UniqueSplitName(box.Name, 1, taken);
            string nameB = UniqueSplitName(box.Name, 2, taken);

            ElementId aId = ElementId.InvalidElementId, bId = ElementId.InvalidElementId;
            using (var tx = new Transaction(doc, "Scope Box Manager — split"))
            {
                ConfigureFailures(tx);
                tx.Start();

                try
                {
                    aId = CreateHalf(doc, BoxId, centerX, centerY, aCenterX, aCenterY, nameA);
                    bId = CreateHalf(doc, BoxId, centerX, centerY, bCenterX, bCenterY, nameB);
                    if (aId == ElementId.InvalidElementId || bId == ElementId.InvalidElementId)
                    {
                        failed++;
                        tx.RollBack();
                        return AppStrings.T("scopeBoxes.manager.status.splitFailed", box.Name);
                    }
                    ok += 2;

                    if (SplitDeleteOriginal)
                    {
                        ReassignCarriers(doc, BoxId, aId);
                        doc.Delete(BoxId);
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    DiagnosticsLog.Swallowed($"ScopeBoxManager split: box {BoxId.Value}", ex);
                    tx.RollBack();
                    return AppStrings.T("scopeBoxes.manager.status.splitFailed", box.Name);
                }

                tx.Commit();
            }

            var resizeNotes = new List<string>();
            if (Math.Abs(aW - curW) > 0.5 || Math.Abs(aD - curD) > 0.5)
                resizeNotes.Add(AppStrings.T("scopeBoxes.manager.status.splitResizeNeeded", nameA, aW.ToString("0.#"), aD.ToString("0.#")));
            if (Math.Abs(bW - curW) > 0.5 || Math.Abs(bD - curD) > 0.5)
                resizeNotes.Add(AppStrings.T("scopeBoxes.manager.status.splitResizeNeeded", nameB, bW.ToString("0.#"), bD.ToString("0.#")));

            string baseMsg = AppStrings.T("scopeBoxes.manager.status.split", nameA, nameB);
            return resizeNotes.Count == 0 ? baseMsg : baseMsg + " " + string.Join(" ", resizeNotes);
        }

        private static ElementId CreateHalf(
            Document doc, ElementId sourceId, double srcCenterX, double srcCenterY,
            double newCenterX, double newCenterY, string name)
        {
            var translation = new XYZ(newCenterX - srcCenterX, newCenterY - srcCenterY, 0);
            var copyIds = ElementTransformUtils.CopyElement(doc, sourceId, translation);
            var copyId = copyIds.FirstOrDefault() ?? ElementId.InvalidElementId;
            var copy = copyId != ElementId.InvalidElementId ? doc.GetElement(copyId) : null;
            if (copy == null) return ElementId.InvalidElementId;
            try { copy.Name = name; }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed($"ScopeBoxManager split: rename half '{name}'", ex);
                doc.Delete(copyId);
                return ElementId.InvalidElementId;
            }
            return copyId;
        }

        private static string UniqueSplitName(string baseName, int half, HashSet<string> taken)
        {
            string candidate = $"{baseName} - {half}";
            int n = 2;
            while (taken.Contains(candidate)) candidate = $"{baseName} - {half} ({n++})";
            taken.Add(candidate);
            return candidate;
        }

        // Re-points every view/datum currently carrying `fromBoxId` to `toBoxId` — used before
        // deleting a split's original so its view/datum assignments survive onto Half A.
        private static void ReassignCarriers(Document doc, ElementId fromBoxId, ElementId toBoxId)
        {
            foreach (var v in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate))
            {
                Parameter? p;
                try { p = v.get_Parameter(BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP); }
                catch (Exception ex) { DiagnosticsLog.Swallowed($"ScopeBoxManager split: read view {v.Id.Value} param", ex); continue; }
                if (p == null || p.IsReadOnly) continue;
                if (p.AsElementId()?.Value != fromBoxId.Value) continue;
                try { p.Set(toBoxId); }
                catch (Exception ex) { DiagnosticsLog.Swallowed($"ScopeBoxManager split: reassign view {v.Id.Value}", ex); }
            }

            IEnumerable<Element> datums = new FilteredElementCollector(doc)
                .WherePasses(new LogicalOrFilter(new List<ElementFilter>
                {
                    new ElementClassFilter(typeof(Autodesk.Revit.DB.Grid)),
                    new ElementClassFilter(typeof(Level)),
                    new ElementClassFilter(typeof(ReferencePlane)),
                }))
                .WhereElementIsNotElementType();
            foreach (var e in datums)
            {
                Parameter? p;
                try { p = e.get_Parameter(BuiltInParameter.DATUM_VOLUME_OF_INTEREST); }
                catch (Exception ex) { DiagnosticsLog.Swallowed($"ScopeBoxManager split: read datum {e.Id.Value} param", ex); continue; }
                if (p == null || p.IsReadOnly) continue;
                if (p.AsElementId()?.Value != fromBoxId.Value) continue;
                try { p.Set(toBoxId); }
                catch (Exception ex) { DiagnosticsLog.Swallowed($"ScopeBoxManager split: reassign datum {e.Id.Value}", ex); }
            }
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
