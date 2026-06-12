using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.CopyLinear
{
    /// <summary>
    /// Main run for the Copy Linear Elements tool. Pulls the chosen source runs out of a link and
    /// either splits each into standard-length segments or replaces each with a picked family placed
    /// at intervals. Every created host element is stamped (<see cref="CopyLinearStampSchema"/>) so a
    /// re-run can reconcile against the model: rebuild only changed/new sources, leave unchanged ones,
    /// and delete outputs whose source is gone. All work happens in one transaction with a single
    /// regen at the end (never per item — CLAUDE.md).
    /// </summary>
    public sealed class CopyLinearRunHandler : IExternalEventHandler
    {
        // ── Inputs (set before Raise) ─────────────────────────────────────────
        public CopyLinearSourceSpec Spec { get; set; } = new CopyLinearSourceSpec();
        public string Mode { get; set; } = "Split";   // "Split" | "Replace"

        public double SegmentLengthFeet { get; set; } = 20.0;
        public double GapFeet           { get; set; } = 0.0;
        public bool   KeepRemainder     { get; set; } = true;

        public long   SymbolId          { get; set; }       // Replace mode
        public double IntervalFeet      { get; set; } = 10.0;
        public double ExtraSpacingFeet  { get; set; } = 0.0;
        public bool   RotateToRun       { get; set; } = true;
        public string LengthParamName   { get; set; } = "";

        public bool   OnlyChanged       { get; set; }
        public bool   DeleteOrphans     { get; set; } = true;

        // ── Callbacks ─────────────────────────────────────────────────────────
        public Action<string, string>?     PushLog    { get; set; }
        public Action<int, int, int, int>? OnProgress { get; set; }
        public Action<int, int, int>?      OnComplete { get; set; }

        public string GetName() => "LemoineTools.Tools.CopyLinear.CopyLinearRunHandler";

        private void Log(string t, string s) => PushLog?.Invoke(t, s);

        private bool _loggedPlacement;   // one placement-path line per run

        public void Execute(UIApplication app)
        {
            _loggedPlacement = false;
            int pass = 0, fail = 0, skip = 0;
            long issues0 = LemoineLog.IssueCount;
            try
            {
                var doc = app.ActiveUIDocument?.Document;
                if (doc == null) { Log("No active document.", "fail"); OnComplete?.Invoke(0, 1, 0); return; }

                var src = CopyLinearSource.Resolve(doc, Spec.LinkInstId);
                if (src?.Doc == null) { Log("Source document is not loaded.", "fail"); OnComplete?.Invoke(0, 1, 0); return; }

                FamilySymbol? symbol = null;
                if (Mode == "Replace")
                {
                    symbol = doc.GetElement(new ElementId(SymbolId)) as FamilySymbol;
                    if (symbol == null) { Log("Pick a family type for Replace mode.", "fail"); OnComplete?.Invoke(0, 1, 0); return; }
                }

                // ── Gather current source runs (straight lines only) keyed by stable identity ──
                var current = new Dictionary<string, SourceRun>(StringComparer.Ordinal);
                int nonLinear = 0;
                foreach (var el in CopyLinearSource.Collect(src.Doc, Spec))
                {
                    try
                    {
                        if (!CopyLinearSource.PassesFilters(el, Spec)) continue;
                        var line = CopyLinearSource.StraightLine(el);
                        if (line == null) { nonLinear++; continue; }

                        XYZ a = src.Transform.OfPoint(line.Value.A);
                        XYZ b = src.Transform.OfPoint(line.Value.B);
                        string typeName; try { typeName = el.Name ?? ""; } catch { typeName = ""; }
                        string key  = CopyLinearEngine.SourceKey(src.LinkInstUid, el.UniqueId);
                        string hash = CopyLinearEngine.GeoHash(a, b,
                            CopyLinearSource.ReadSize(el), CopyLinearSource.ReadSystem(el),
                            typeName, CopyLinearSource.ReadPhase(src.Doc, el));
                        current[key] = new SourceRun { Element = el, A = a, B = b, Key = key, Hash = hash };
                    }
                    catch (Exception ex)
                    {
                        skip++;
                        LemoineLog.Swallowed($"CopyLinear: read source {el?.Id}", ex);
                    }
                }

                skip += nonLinear;
                if (nonLinear > 0) Log($"— {nonLinear} element(s) skipped — no straight location curve.", "info");

                // ── Read existing stamps and decide what to (re)build / delete ──
                var stampsByKey = new Dictionary<string, (List<ElementId> ids, string hash)>(StringComparer.Ordinal);
                foreach (var rec in CopyLinearStampSchema.ReadAll(doc))
                {
                    if (!stampsByKey.TryGetValue(rec.SourceKey, out var entry))
                        entry = (new List<ElementId>(), rec.GeoHash);
                    entry.ids.Add(rec.ElementId);
                    stampsByKey[rec.SourceKey] = entry;
                }

                var toBuild   = new List<SourceRun>();
                int unchanged = 0;
                foreach (var run in current.Values)
                {
                    bool stamped = stampsByKey.TryGetValue(run.Key, out var prior);
                    bool changed = !stamped || prior.hash != run.Hash;
                    if (OnlyChanged && !changed) { unchanged++; continue; }
                    toBuild.Add(run);
                }

                // Orphans: stamped outputs whose source no longer exists in the current selection.
                var orphanIds = new List<ElementId>();
                if (DeleteOrphans)
                    foreach (var kv in stampsByKey)
                        if (!current.ContainsKey(kv.Key))
                            orphanIds.AddRange(kv.Value.ids);

                string runId = Guid.NewGuid().ToString("N");
                int total = toBuild.Count, done = 0;
                Log($"{Mode} mode — {current.Count} source run(s); building {toBuild.Count}, "
                    + $"{unchanged} unchanged, {orphanIds.Count} orphaned output(s) to remove.", "info");

                using (var tx = new Transaction(doc, "Copy Linear Elements"))
                {
                    tx.Start();
                    var failureHandler = ConfigureFailures(tx);

                    // Remove orphaned outputs, then any prior outputs for the keys we are rebuilding,
                    // so a re-run is idempotent and never duplicates.
                    SafeDelete(doc, orphanIds);
                    foreach (var run in toBuild)
                        if (stampsByKey.TryGetValue(run.Key, out var prior))
                            SafeDelete(doc, prior.ids);

                    // Levels (sorted by elevation) resolve an explicit host plane for family placement,
                    // so a work-plane/level-based family no longer depends on the active view's work plane.
                    List<Level>? levels = null;
                    if (Mode == "Replace")
                    {
                        if (!symbol!.IsActive) symbol.Activate();
                        levels = new FilteredElementCollector(doc).OfClass(typeof(Level))
                            .Cast<Level>().OrderBy(l => l.Elevation).ToList();
                    }

                    bool described = false;
                    foreach (var run in toBuild)
                    {
                        if (!described) { described = true; Log(DescribeSource(run.Element), "info"); }
                        try
                        {
                            var (made, innerFail) = Mode == "Replace"
                                ? BuildReplace(doc, run, symbol!, levels, runId)
                                : BuildSplit(doc, src.Doc!, run, src.Transform, runId);
                            pass += made;
                            fail += innerFail;
                            if (made == 0 && innerFail == 0) skip++;
                        }
                        catch (Exception ex)
                        {
                            fail++;
                            LemoineLog.Error($"CopyLinear: build {run.Key}", ex);
                            Log($"✗ {run.Element.Id}: {ex.Message}", "fail");
                        }
                        done++;
                        if (total > 0) OnProgress?.Invoke((int)(done * 95.0 / total), pass, fail, skip);
                    }

                    doc.Regenerate();
                    tx.Commit();

                    // Report each distinct Revit failure by its real description — this is the
                    // ground truth for diagnosing "work plane" style errors.
                    foreach (var f in failureHandler.Captured.Distinct().Take(15))
                    {
                        Log($"Revit failure: {f}", "warn");
                        LemoineLog.Warn("CopyLinear: revit failure", f);
                    }
                }

                long issues = LemoineLog.IssuesSince(issues0);
                if (issues > 0) Log($"{issues} non-fatal issue(s) recorded — see diagnostics log.", "warn");
                Log($"Done. {pass} output(s) created, {skip} skipped, {fail} failed.", fail > 0 ? "warn" : "pass");
                OnProgress?.Invoke(100, pass, fail, skip);
                OnComplete?.Invoke(pass, fail, skip);
            }
            catch (Exception ex)
            {
                LemoineLog.Error("CopyLinearRunHandler.Execute", ex);
                Log($"Run aborted: {ex.Message}", "fail");
                OnComplete?.Invoke(pass, fail + 1, skip);
            }
        }

        // ── Split: copy the run into the host, then break it into standard-length cells ──
        private (int made, int failed) BuildSplit(Document doc, Document srcDoc, SourceRun run, Transform tx, string runId)
        {
            ICollection<ElementId> copied;
            try
            {
                // Work-plane-based / CurveBased elements from a linked model require a geometrically
                // equivalent SketchPlane to already exist in the host before CopyElements runs —
                // otherwise Revit can't resolve the sketch plane reference and shows the work-plane
                // error even when the active view has a visible work plane. Creating it here (inside
                // the transaction) gives CopyElements a target to match by geometric equivalence.
                // Same-document copies skip this: the sketch plane is already in the host.
                if (!ReferenceEquals(srcDoc, doc))
                    EnsureHorizontalSketchPlane(doc, (run.A.Z + run.B.Z) * 0.5);

                // Host source → same-document copy (the cross-document overload rejects identical
                // source/dest); link source → cross-document copy with the link transform applied.
                copied = ReferenceEquals(srcDoc, doc)
                    ? ElementTransformUtils.CopyElement(doc, run.Element.Id, XYZ.Zero)
                    : ElementTransformUtils.CopyElements(
                        srcDoc, new List<ElementId> { run.Element.Id }, doc, tx, CopyOptions());
            }
            catch (Exception ex)
            {
                LemoineLog.Error($"CopyLinear: copy source {run.Element.Id}", ex);
                Log($"✗ {run.Element.Id}: copy failed — {ex.GetType().Name}: {ex.Message}", "fail");
                return (0, 1);
            }

            var hostCopy = copied?.Select(doc.GetElement).FirstOrDefault(e => e?.Location is LocationCurve lc && lc.Curve is Line);
            if (hostCopy == null)
            {
                SafeDelete(doc, copied?.ToList() ?? new List<ElementId>());
                Log($"— {run.Element.Id}: copy has no straight curve, skipped.", "info");
                return (0, 0);
            }

            var hostLine = ((LocationCurve)hostCopy.Location).Curve as Line;
            XYZ a = hostLine!.GetEndPoint(0), b = hostLine.GetEndPoint(1);
            double totalLen = a.DistanceTo(b);

            var cells = CopyLinearEngine.SplitStations(totalLen, SegmentLengthFeet, GapFeet, KeepRemainder);
            if (cells.Count == 0)
            {
                SafeDelete(doc, new List<ElementId> { hostCopy.Id });
                Log($"— {run.Element.Id}: run shorter than one segment, skipped.", "info");
                return (0, 0);
            }

            // Make every segment id FIRST, copying the pristine full-length element, then reassign
            // each one's curve (mirrors SplitElementsShared — never copy an already-mutated element).
            var segIds = new List<ElementId> { hostCopy.Id };
            for (int i = 1; i < cells.Count; i++)
            {
                var c = ElementTransformUtils.CopyElement(doc, hostCopy.Id, XYZ.Zero);
                if (c == null || c.Count == 0)
                {
                    Log($"✗ {run.Element.Id}: copy #{i} failed — split is incomplete.", "warn");
                    break;
                }
                segIds.Add(c.First());
            }

            int made = 0, innerFailed = 0;
            for (int i = 0; i < segIds.Count && i < cells.Count; i++)
            {
                Element seg = doc.GetElement(segIds[i]);
                if (seg == null) continue;

                XYZ segA = CopyLinearEngine.PointAlong(a, b, cells[i].Start);
                XYZ segB = CopyLinearEngine.PointAlong(a, b, cells[i].End);
                if (segA.DistanceTo(segB) < 1e-4)
                {
                    if (i > 0) SafeDelete(doc, new List<ElementId> { seg.Id });
                    continue;
                }

                try
                {
                    Line newCurve = Line.CreateBound(segA, segB);  // validate before touching connectors
                    DisconnectConnectors(seg);
                    ((LocationCurve)seg.Location).Curve = newCurve;
                    if (!CopyLinearStampSchema.Stamp(seg, run.Key, run.Hash, runId, "Split"))
                        LemoineLog.Warn("CopyLinear: stamp", $"segment for {run.Element.Id} not stamped — it won't reconcile on a later run.");
                    made++;
                }
                catch (Exception ex)
                {
                    if (i > 0) SafeDelete(doc, new List<ElementId> { seg.Id });
                    LemoineLog.Error($"CopyLinear: set segment curve {run.Element.Id}#{i}", ex);
                    Log($"✗ {run.Element.Id} seg {i}: {ex.GetType().Name}: {ex.Message}", "fail");
                    innerFailed++;
                }
            }
            return (made, innerFailed);
        }

        // ── Replace: place the picked family at intervals along the run ──
        //
        // Which NewFamilyInstance overload is VALID depends on the family's FamilyPlacementType —
        // a line-based (CurveBased) family throws the "work plane" error from EVERY point-based
        // overload no matter what work plane the active view carries, so the placement must
        // dispatch on placement type rather than use one overload for all families:
        //   CurveBased / CurveDrivenStructural → NewFamilyInstance(Line, symbol, level, type)
        //   WorkPlaneBased                     → NewFamilyInstance(level.GetPlaneReference(), p, dir, symbol)
        //   level/point families               → NewFamilyInstance(p, symbol, level, type)
        //   CurveBasedDetail                   → view-specific; reported as unsupported.
        private (int made, int failed) BuildReplace(Document doc, SourceRun run, FamilySymbol symbol, List<Level>? levels, string runId)
        {
            XYZ a = run.A, b = run.B;
            double totalLen = a.DistanceTo(b);
            var stations = CopyLinearEngine.PlacementStations(totalLen, IntervalFeet, ExtraSpacingFeet);
            if (stations.Count == 0) { Log($"— {run.Element.Id}: no placement points, skipped.", "info"); return (0, 0); }

            FamilyPlacementType placement = FamilyPlacementType.OneLevelBased;
            try { placement = symbol.Family?.FamilyPlacementType ?? FamilyPlacementType.OneLevelBased; }
            catch (Exception ex) { LemoineLog.Swallowed("CopyLinear: read FamilyPlacementType", ex); }

            if (placement == FamilyPlacementType.CurveBasedDetail || placement == FamilyPlacementType.ViewBased)
            {
                Log($"✗ {symbol.Name} is a detail (view-specific) family — pick a model family for Replace mode.", "fail");
                return (0, 1);
            }

            bool curveBased = placement == FamilyPlacementType.CurveBased
                           || placement == FamilyPlacementType.CurveDrivenStructural;

            if (!_loggedPlacement)
            {
                _loggedPlacement = true;
                Log($"Replace family '{symbol.Name}' — placement type {placement}, path: "
                    + (curveBased ? "line + sketch plane" :
                       placement == FamilyPlacementType.WorkPlaneBased ? "level plane reference" : "point + level"), "info");
            }

            // Host the instances on the level nearest the run, not the active view's work plane.
            Level? level = NearestLevel(levels, (a.Z + b.Z) * 0.5);
            if (level == null && (curveBased || placement == FamilyPlacementType.WorkPlaneBased))
            {
                Log($"✗ {run.Element.Id}: the project has no level to host '{symbol.Name}' on.", "fail");
                return (0, 1);
            }

            XYZ dir = b - a;
            double angle = RotateToRun ? CopyLinearEngine.PlanRotation(dir) : 0.0;
            int made = 0, innerFail = 0;
            foreach (double s in stations)
            {
                XYZ p = CopyLinearEngine.PointAlong(a, b, s);
                FamilyInstance? inst;
                try
                {
                    if (curveBased)
                    {
                        // A line-based family IS its curve — give each station a segment of one
                        // interval (clamped to the run end) instead of a point.
                        XYZ q = CopyLinearEngine.PointAlong(a, b, Math.Min(s + IntervalFeet, totalLen));
                        if (p.DistanceTo(q) < 1e-4) continue;   // degenerate tail segment
                        var line = Line.CreateBound(p, q);

                        if (placement == FamilyPlacementType.CurveDrivenStructural)
                        {
                            // Structural curve families use the level-based overload.
                            inst = doc.Create.NewFamilyInstance(line, symbol, level!, StructuralType.NonStructural);
                        }
                        else
                        {
                            // Non-structural CurveBased families (site lines, generic model line-based)
                            // need NewFamilyInstance(Reference, Line, FamilySymbol) where the Reference
                            // is a SketchPlane. The level-based overload is for CurveDrivenStructural
                            // only and throws "no work plane" for CurveBased families.
                            var sp = EnsureHorizontalSketchPlane(doc, (p.Z + q.Z) * 0.5);
                            if (sp == null)
                            {
                                Log($"✗ {run.Element.Id}: could not create sketch plane for CurveBased placement.", "fail");
                                innerFail++;
                                continue;
                            }
                            inst = doc.Create.NewFamilyInstance(sp.GetPlaneReference(), line, symbol);
                        }
                    }
                    else if (placement == FamilyPlacementType.WorkPlaneBased)
                    {
                        // Work-plane-based families reject point+level placement; host them on the
                        // level's own plane reference. The direction vector doubles as rotation.
                        var flat = new XYZ(dir.X, dir.Y, 0.0);
                        XYZ refDir = (RotateToRun && flat.GetLength() > 1e-6) ? flat.Normalize() : XYZ.BasisX;
                        inst = doc.Create.NewFamilyInstance(level!.GetPlaneReference(), p, refDir, symbol);
                    }
                    else
                    {
                        inst = PlaceInstance(doc, p, symbol, level);
                    }
                }
                catch (Exception ex)
                {
                    LemoineLog.Error($"CopyLinear: place instance for {run.Element.Id} ({placement})", ex);
                    Log($"✗ {run.Element.Id}: place failed ({placement}) — {ex.GetType().Name}: {ex.Message}", "fail");
                    innerFail++;
                    continue;
                }
                if (inst == null) { innerFail++; continue; }

                // Curve-based and work-plane-based placements already carry the run direction.
                if (!curveBased && placement != FamilyPlacementType.WorkPlaneBased && Math.Abs(angle) > 1e-6)
                {
                    var axis = Line.CreateBound(p, p + XYZ.BasisZ);
                    try { ElementTransformUtils.RotateElement(doc, inst.Id, axis, angle); }
                    catch (Exception ex) { LemoineLog.Swallowed("CopyLinear: rotate instance", ex); }
                }

                // A line-based instance's length is driven by its curve — skip the parameter write.
                if (!curveBased && !string.IsNullOrWhiteSpace(LengthParamName))
                {
                    var lp = inst.LookupParameter(LengthParamName);
                    if (lp != null && !lp.IsReadOnly && lp.StorageType == StorageType.Double)
                        try { lp.Set(IntervalFeet); } catch (Exception ex) { LemoineLog.Swallowed("CopyLinear: set length param", ex); }
                    else if (lp == null)
                        LemoineLog.Warn("CopyLinear: length parameter", $"'{LengthParamName}' not found on {symbol.Name}");
                }

                if (!CopyLinearStampSchema.Stamp(inst, run.Key, run.Hash, runId, "Replace"))
                    LemoineLog.Warn("CopyLinear: stamp", $"instance for {run.Element.Id} not stamped — it won't reconcile on a later run.");
                made++;
            }
            return (made, innerFail);
        }

        // ── helpers ────────────────────────────────────────────────────────────

        // Places one point-based family instance. Level overload avoids dependence on the active
        // view's work plane (which throws in 3D / schedule views with no work plane).
        private static FamilyInstance PlaceInstance(Document doc, XYZ p, FamilySymbol symbol, Level? level)
        {
            if (level != null)
                return doc.Create.NewFamilyInstance(p, symbol, level, StructuralType.NonStructural);
            return doc.Create.NewFamilyInstance(p, symbol, StructuralType.NonStructural);
        }

        // Returns (or creates) a horizontal SketchPlane at the given Z elevation.
        // Work-plane-based and CurveBased families in a linked document carry a sketch-plane
        // reference. CopyElements resolves it by searching the host for a geometrically equivalent
        // plane; if none exists it falls back to the active view's work plane, and if that is also
        // absent it shows the "no corresponding Work Plane" error. Pre-creating the matching plane
        // here gives CopyElements (and NewFamilyInstance) something to bind to regardless of which
        // view the user happens to have active.
        private static SketchPlane? EnsureHorizontalSketchPlane(Document doc, double z)
        {
            const double tol = 0.01;   // feet — generous enough to match level rounding
            foreach (var sp in new FilteredElementCollector(doc)
                                   .OfClass(typeof(SketchPlane)).Cast<SketchPlane>())
            {
                try
                {
                    var pln = sp.GetPlane();
                    if (Math.Abs(pln.Normal.Z - 1.0) < 1e-4 && Math.Abs(pln.Origin.Z - z) < tol)
                        return sp;
                }
                catch (Exception ex) { LemoineLog.Swallowed("CopyLinear: read sketch plane", ex); }
            }
            try
            {
                return SketchPlane.Create(doc,
                    Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, z)));
            }
            catch (Exception ex)
            {
                LemoineLog.Swallowed("CopyLinear: create sketch plane", ex);
                return null;
            }
        }

        // Highest level at or below z (levels pre-sorted ascending); falls back to the lowest level.
        private static Level? NearestLevel(List<Level>? levels, double z)
        {
            if (levels == null || levels.Count == 0) return null;
            Level? best = levels[0];
            foreach (var l in levels)
            {
                if (l.Elevation <= z + 1e-6) best = l;
                else break;
            }
            return best;
        }

        // One-line identity of a source element — class, category, and for family instances the
        // family name, placement type, and host kind. This is what tells us which placement/copy
        // path the element actually needs.
        private static string DescribeSource(Element el)
        {
            try
            {
                string cls = el.GetType().Name;
                string cat;
                try { cat = el.Category?.Name ?? "?"; }
                catch (Exception ex) { LemoineLog.Swallowed("CopyLinear: describe category", ex); cat = "?"; }
                string extra = "";
                if (el is FamilyInstance fi)
                {
                    var fam = fi.Symbol?.Family;
                    string host;
                    try { host = fi.Host?.GetType().Name ?? "none"; }
                    catch (Exception ex) { LemoineLog.Swallowed("CopyLinear: describe host", ex); host = "?"; }
                    extra = $", family '{fam?.Name}', placement {(fam?.FamilyPlacementType.ToString() ?? "?")}, host {host}";
                }
                return $"Source element {el.Id}: {cls} [{cat}]{extra}";
            }
            catch (Exception ex)
            {
                LemoineLog.Swallowed("CopyLinear: describe source", ex);
                return $"Source element {el.Id}";
            }
        }

        private static void SafeDelete(Document doc, List<ElementId> ids)
        {
            if (ids == null) return;
            foreach (var id in ids)
            {
                if (id == null || id == ElementId.InvalidElementId) continue;
                try { if (doc.GetElement(id) != null) doc.Delete(id); }
                catch (Exception ex) { LemoineLog.Swallowed("CopyLinear: delete output", ex); }
            }
        }

        private static void DisconnectConnectors(Element el)
        {
            try
            {
                ConnectorManager? cm = (el as MEPCurve)?.ConnectorManager
                                       ?? (el as FamilyInstance)?.MEPModel?.ConnectorManager;
                if (cm == null) return;
                foreach (Connector c in cm.Connectors)
                    foreach (Connector other in c.AllRefs.Cast<Connector>().ToList())
                        try { c.DisconnectFrom(other); } catch (Exception ex) { LemoineLog.Swallowed("CopyLinear: disconnect connector", ex); }
            }
            catch (Exception ex) { LemoineLog.Swallowed("CopyLinear: disconnect connectors", ex); }
        }

        // Copy options that silently reuse the destination's types — suppresses the modal
        // "Duplicate Types" prompt that otherwise pops for every cross-document copy.
        private static CopyPasteOptions CopyOptions()
        {
            var opts = new CopyPasteOptions();
            opts.SetDuplicateTypeNamesHandler(new UseDestinationTypes());
            return opts;
        }

        private sealed class UseDestinationTypes : IDuplicateTypeNamesHandler
        {
            public DuplicateTypeAction OnDuplicateTypeNamesFound(DuplicateTypeNamesHandlerArgs args)
                => DuplicateTypeAction.UseDestinationTypes;
        }

        private static SilentFailureHandler ConfigureFailures(Transaction tx)
        {
            var handler = new SilentFailureHandler();
            try
            {
                var opts = tx.GetFailureHandlingOptions();
                opts.SetClearAfterRollback(true);
                opts.SetForcedModalHandling(false);
                opts.SetFailuresPreprocessor(handler);
                tx.SetFailureHandlingOptions(opts);
            }
            catch (Exception ex) { LemoineLog.Swallowed("CopyLinear: configure failure handling", ex); }
            return handler;
        }

        // Captures every Revit failure raised during the transaction — name, severity, elements —
        // so the run log can report exactly which failure fired instead of an anonymous count.
        // Warnings are deleted (suppressed); errors are recorded and left to ForcedModalHandling.
        private sealed class SilentFailureHandler : IFailuresPreprocessor
        {
            public int DismissedCount { get; private set; }
            private readonly List<string> _captured = new List<string>();
            public IReadOnlyList<string> Captured => _captured;

            public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
            {
                foreach (var msg in failuresAccessor.GetFailureMessages().ToList())
                {
                    var sev = msg.GetSeverity();
                    string desc;
                    try { desc = msg.GetDescriptionText(); }
                    catch (Exception ex) { LemoineLog.Swallowed("CopyLinear: read failure description", ex); desc = "(no description)"; }
                    string ids = "";
                    try { ids = string.Join(", ", msg.GetFailingElementIds().Select(i => i.Value)); }
                    catch (Exception ex) { LemoineLog.Swallowed("CopyLinear: read failing ids", ex); }
                    _captured.Add($"[{sev}] {desc}" + (ids.Length > 0 ? $" — elements {ids}" : ""));

                    if (sev == FailureSeverity.Warning)
                    {
                        failuresAccessor.DeleteWarning(msg);
                        DismissedCount++;
                    }
                }
                return FailureProcessingResult.Continue;
            }
        }

        private sealed class SourceRun
        {
            public Element Element = null!;
            public XYZ     A = XYZ.Zero;
            public XYZ     B = XYZ.Zero;
            public string  Key  = "";
            public string  Hash = "";
        }
    }
}
