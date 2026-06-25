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
    /// at intervals. In delete-previous mode every created host element is stamped
    /// (<see cref="CopyLinearStampSchema"/>) so a re-run can reconcile against the model: rebuild only
    /// changed/new sources, leave unchanged ones, and delete outputs whose source is gone. Otherwise
    /// outputs are left unstamped — finished elements detach from the tool and later runs never touch
    /// them. All work happens in one transaction with a single regen at the end (never per item — CLAUDE.md).
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
        public bool   AlignToSource     { get; set; } = true;
        public string LengthParamName   { get; set; } = "";

        // Manual placement override (Replace mode, AlignToSource off) — run-frame offset
        // components relative to each placement's own source run.
        public double ManualOffsetAlongFeet { get; set; }
        public double ManualOffsetSideFeet  { get; set; }
        public double ManualOffsetUpFeet    { get; set; }

        // Extra rotation applied to EVERY placed instance (with or without align-to-source),
        // about the station point in the source run's own frame: X = about the run direction,
        // Y = about the horizontal perpendicular, Z = about the frame's up axis. Applied in
        // X→Y→Z order, before alignment so calibration measures the rotated footprint.
        public double RotationXRad { get; set; }
        public double RotationYRad { get; set; }
        public double RotationZRad { get; set; }

        // Off = previous runs' outputs are never touched and this run's outputs are left
        // unstamped — finished elements detach from the tool. On = stamped reconciliation.
        public bool   DeletePrevious    { get; set; }
        public bool   OnlyChanged       { get; set; }
        public bool   DeleteOrphans     { get; set; } = true;

        // ── Callbacks ─────────────────────────────────────────────────────────
        public Action<string, string>?     PushLog    { get; set; }
        public Action<int, int, int, int>? OnProgress { get; set; }
        public Action<int, int, int>?      OnComplete { get; set; }

        public string GetName() => "LemoineTools.Tools.CopyLinear.CopyLinearRunHandler";

        private void Log(string t, string s) => PushLog?.Invoke(t, s);

        private bool _loggedPlacement;   // one placement-path line per run

        // Align-to-source: the family's footprint is measured once from the first placed
        // instance (the only regen alignment costs); the correction itself is then computed
        // per placement against THAT placement's own source element box, so every fix is
        // relative to the element it replaces.
        private CopyLinearEngine.InstanceProfile? _alignProfile;
        private bool _alignCalibrationFailed;
        private bool _alignApplyWarned;
        private bool _alignNoBoxWarned;
        private bool _manualApplyWarned;
        private bool _rotationApplyWarned;

        public void Execute(UIApplication app)
        {
            _loggedPlacement = false;
            _alignProfile = null;
            _alignCalibrationFailed = false;
            _alignApplyWarned = false;
            _alignNoBoxWarned = false;
            _manualApplyWarned = false;
            _rotationApplyWarned = false;
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

                        // World-space box corners feed the align-to-source calibration; only
                        // Replace mode reads them (Split copies the source element itself).
                        List<XYZ>? corners = null;
                        if (Mode == "Replace" && AlignToSource)
                        {
                            try
                            {
                                var bb = el.get_BoundingBox(null);
                                if (bb != null) corners = BoxCorners(bb, src.Transform);
                            }
                            catch (Exception ex) { LemoineLog.Swallowed($"CopyLinear: source box {el.Id}", ex); }
                        }

                        current[key] = new SourceRun { Element = el, A = a, B = b, Key = key, Hash = hash, BoxCorners = corners };
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
                // Only the delete-previous mode ever removes earlier outputs.
                var orphanIds = new List<ElementId>();
                if (DeletePrevious && DeleteOrphans)
                    foreach (var kv in stampsByKey)
                        if (!current.ContainsKey(kv.Key))
                            orphanIds.AddRange(kv.Value.ids);

                string runId = Guid.NewGuid().ToString("N");
                int total = toBuild.Count, done = 0;
                Log($"{Mode} mode — {current.Count} source run(s); building {toBuild.Count}, {unchanged} unchanged, "
                    + (DeletePrevious
                        ? $"{orphanIds.Count} orphaned output(s) to remove."
                        : "previous outputs left untouched."), "info");

                using (var tx = new Transaction(doc, "Copy Linear Elements"))
                {
                    tx.Start();
                    var failureHandler = ConfigureFailures(tx);

                    // Delete-previous mode: remove orphaned outputs, then any prior outputs for the
                    // keys we are rebuilding, so a re-run is idempotent and never duplicates.
                    // Otherwise previous runs' elements are plain model elements — never touched.
                    if (DeletePrevious)
                    {
                        SafeDelete(doc, orphanIds);
                        foreach (var run in toBuild)
                            if (stampsByKey.TryGetValue(run.Key, out var prior))
                                SafeDelete(doc, prior.ids);
                    }

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
                    var prog = new RunProgressReporter(Log, total, "source runs");
                    foreach (var run in toBuild)
                    {
                        if (LemoineRun.CancelRequested)
                        {
                            Log($"Stopped by user — {done} of {total} processed; work so far preserved.", "warn");
                            break;   // falls through to doc.Regenerate() + tx.Commit() below
                        }
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
                        prog.Tick();
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
            finally
            {
                // Session-long static handler — drop the run's payload.
                Spec = new CopyLinearSourceSpec();
            }
        }

        // ── Split: copy the run into the host, then break it into standard-length cells ──
        private (int made, int failed) BuildSplit(Document doc, Document srcDoc, SourceRun run, Transform tx, string runId)
        {
            Element? hostCopy = null;

            // Face-hosted CurveBased elements (e.g. a site culvert hosted ON a Duct in the link)
            // cannot be copied with CopyElements: the host element doesn't exist in the destination
            // document, so Revit throws [DocumentCorruption] "no corresponding Work Plane". Detect
            // this before attempting CopyElements and reconstruct directly on a horizontal sketch
            // plane using NewFamilyInstance(sketchPlane, line, symbol) instead — the same re-hosting
            // strategy that BimorphNodes' CopyToActiveDocument uses.
            bool faceHosted = false;
            if (!ReferenceEquals(srcDoc, doc) && run.Element is FamilyInstance fhFi && fhFi.Host != null)
            {
                var fp = fhFi.Symbol?.Family?.FamilyPlacementType;
                faceHosted = fp == FamilyPlacementType.CurveBased;
            }

            if (faceHosted)
            {
                var srcFi = (FamilyInstance)run.Element;
                var hostSymbol = FindOrCopySymbol(doc, srcDoc, srcFi.Symbol);
                if (hostSymbol == null)
                {
                    Log($"✗ {run.Element.Id}: family '{srcFi.Symbol?.Family?.Name}' is not loaded in the host document — load it first.", "fail");
                    return (0, 1);
                }
                if (!hostSymbol.IsActive) hostSymbol.Activate();

                var sp = EnsureHorizontalSketchPlane(doc, (run.A.Z + run.B.Z) * 0.5);
                if (sp == null)
                {
                    Log($"✗ {run.Element.Id}: could not create sketch plane for face-hosted split.", "fail");
                    return (0, 1);
                }
                try
                {
                    hostCopy = doc.Create.NewFamilyInstance(sp.GetPlaneReference(), Line.CreateBound(run.A, run.B), hostSymbol);
                }
                catch (Exception ex)
                {
                    LemoineLog.Error($"CopyLinear: reconstruct face-hosted {run.Element.Id}", ex);
                    Log($"✗ {run.Element.Id}: reconstruct failed — {ex.GetType().Name}: {ex.Message}", "fail");
                    return (0, 1);
                }
            }
            else
            {
                // Normal path: CopyElements resolves the sketch plane automatically (or same-doc copy).
                ICollection<ElementId>? copied = null;
                try
                {
                    if (!ReferenceEquals(srcDoc, doc))
                        EnsureHorizontalSketchPlane(doc, (run.A.Z + run.B.Z) * 0.5);

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

                hostCopy = copied?.Select(doc.GetElement).FirstOrDefault(e => e?.Location is LocationCurve lc && lc.Curve is Line);
                if (hostCopy == null)
                {
                    SafeDelete(doc, copied?.ToList() ?? new List<ElementId>());
                    Log($"— {run.Element.Id}: copy has no straight curve, skipped.", "info");
                    return (0, 0);
                }
            }

            // ── Both paths converge: read the placed element's curve ──────────────
            if (!(hostCopy.Location is LocationCurve hostLocCurve) || !(hostLocCurve.Curve is Line hostLine))
            {
                SafeDelete(doc, new List<ElementId> { hostCopy.Id });
                Log($"— {run.Element.Id}: placed instance has no straight location curve, skipped.", "info");
                return (0, 0);
            }

            XYZ a = hostLine.GetEndPoint(0), b = hostLine.GetEndPoint(1);
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
                    // Stamp only in delete-previous mode — otherwise the finished segment is a
                    // plain element, detached from this tool's reconciliation.
                    if (DeletePrevious && !CopyLinearStampSchema.Stamp(seg, run.Key, run.Hash, runId, "Split"))
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

            bool hasLengthParam = !string.IsNullOrWhiteSpace(LengthParamName);
            bool hasManual = !AlignToSource
                && (Math.Abs(ManualOffsetAlongFeet) > 1e-9 || Math.Abs(ManualOffsetSideFeet) > 1e-9
                 || Math.Abs(ManualOffsetUpFeet)    > 1e-9);
            bool hasRotation = Math.Abs(RotationXRad) > 1e-9 || Math.Abs(RotationYRad) > 1e-9
                            || Math.Abs(RotationZRad) > 1e-9;

            if (!_loggedPlacement)
            {
                _loggedPlacement = true;
                Log($"Replace family '{symbol.Name}' — placement type {placement}, path: "
                    + (curveBased ? "line + sketch plane" :
                       placement == FamilyPlacementType.WorkPlaneBased ? "level plane reference" : "point + level"), "info");
                if (AlignToSource && curveBased)
                    Log("Align to source: skipped — a line-based family already follows the run's curve.", "info");
                if (hasRotation && curveBased)
                    Log("Placement rotation: skipped — a line-based family follows the run's curve and cannot be rotated off it.", "info");
                if (hasManual)
                    Log("Manual placement override active — each instance is moved relative to its own source run.", "info");
            }

            // Host the instances on the level nearest the run, not the active view's work plane.
            Level? level = NearestLevel(levels, (a.Z + b.Z) * 0.5);
            if (level == null && (curveBased || placement == FamilyPlacementType.WorkPlaneBased))
            {
                Log($"✗ {run.Element.Id}: the project has no level to host '{symbol.Name}' on.", "fail");
                return (0, 1);
            }

            XYZ dir = b - a;
            double angle = CopyLinearEngine.PlanRotation(dir);   // always rotate to the run
            int made = 0, innerFail = 0;
            foreach (double s in stations)
            {
                // Trim to fit: a sized element placed at the run's very end has no length left —
                // skip it, the same way Split never emits a zero-length final cell.
                double remaining = totalLen - s;
                if (!curveBased && hasLengthParam && remaining < 1e-4) continue;

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
                        XYZ refDir = flat.GetLength() > 1e-6 ? flat.Normalize() : XYZ.BasisX;
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

                // User X/Y/Z rotation, in this run's own frame — before alignment so the
                // calibration below measures the rotated footprint.
                if (!curveBased && hasRotation)
                    ApplyUserRotation(doc, inst, a, b, p);

                // Align to source: the family's footprint is measured once from the first placed
                // instance (one regen), then each placement's correction is computed against ITS
                // OWN source element's box — a relative transform per source, not a global one.
                // Curve-based families are skipped — they already follow the run's curve, and
                // moving/rotating them would pull the curve off its sketch plane.
                if (AlignToSource && !curveBased)
                {
                    if (_alignProfile == null && !_alignCalibrationFailed)
                        CalibrateAlignment(doc, inst, a, b, p);
                    if (_alignProfile != null)
                    {
                        if (run.BoxCorners != null && run.BoxCorners.Count > 0)
                        {
                            var corr = CopyLinearEngine.ComputeAlignment(run.BoxCorners, _alignProfile, a, b, p);
                            if (corr != null) ApplyAlignment(doc, inst, a, b, corr);
                        }
                        else if (!_alignNoBoxWarned)
                        {
                            _alignNoBoxWarned = true;
                            Log($"Align to source: source {run.Element.Id} has no bounding box — its placements keep the standard position.", "warn");
                        }
                    }
                }
                else if (hasManual)
                {
                    // Auto-align is off — the manual override takes control instead.
                    ApplyManualOverride(doc, inst, a, b, p);
                }

                // A line-based instance's length is driven by its curve — skip the parameter write.
                // The value is trimmed to the remaining run so the last piece never overhangs the end.
                if (!curveBased && hasLengthParam)
                {
                    var lp = inst.LookupParameter(LengthParamName);
                    if (lp != null && !lp.IsReadOnly && lp.StorageType == StorageType.Double)
                        try { lp.Set(Math.Min(IntervalFeet, remaining)); } catch (Exception ex) { LemoineLog.Swallowed("CopyLinear: set length param", ex); }
                    else if (lp == null)
                        LemoineLog.Warn("CopyLinear: length parameter", $"'{LengthParamName}' not found on {symbol.Name}");
                }

                // Stamp only in delete-previous mode — otherwise the finished instance is a
                // plain element, detached from this tool's reconciliation.
                if (DeletePrevious && !CopyLinearStampSchema.Stamp(inst, run.Key, run.Hash, runId, "Replace"))
                    LemoineLog.Warn("CopyLinear: stamp", $"instance for {run.Element.Id} not stamped — it won't reconcile on a later run.");
                made++;
            }
            return (made, innerFail);
        }

        // ── Align-to-source calibration ────────────────────────────────────────

        // Measures the family's footprint from the first placed instance — extents relative to
        // its station, in run-frame components. Costs the run's single calibration regen (never
        // per item — CLAUDE.md). The per-source correction is then pure math for every placement.
        // A failure logs a warning once and leaves every placement at the standard position.
        private void CalibrateAlignment(Document doc, FamilyInstance inst, XYZ a, XYZ b, XYZ p)
        {
            try
            {
                doc.Regenerate();   // one calibration regen for the whole run command
                var bb = inst.get_BoundingBox(null);
                var profile = bb == null ? null : CopyLinearEngine.MeasureInstance(
                    BoxCorners(bb, Transform.Identity), a, b, p);
                if (profile == null)
                {
                    _alignCalibrationFailed = true;
                    Log("Align to source: could not measure the first placed instance — placements keep the standard position.", "warn");
                    LemoineLog.Warn("CopyLinear: align", $"no instance box for {inst.Id}; calibration skipped");
                    return;
                }
                _alignProfile = profile;
                Log("Align to source: family footprint measured from the first placement — "
                    + "each instance is now aligned to its own source element.", "info");
            }
            catch (Exception ex)
            {
                _alignCalibrationFailed = true;
                LemoineLog.Error("CopyLinear: align calibration", ex);
                Log($"Align to source failed ({ex.GetType().Name}: {ex.Message}) — placements keep the standard position.", "warn");
            }
        }

        // Applies the stored correction: the run-frame offset re-projected through THIS run's
        // frame (so it transfers across runs pointing different directions). A failing instance
        // keeps its standard position; the first failure warns in the run log, the rest go to
        // diagnostics only.
        private void ApplyAlignment(
            Document doc, FamilyInstance inst, XYZ a, XYZ b, CopyLinearEngine.AlignmentCorrection c)
        {
            try
            {
                var (along, side, up) = CopyLinearEngine.RunFrame(a, b);
                XYZ off = c.OffsetAlong * along + c.OffsetSide * side + c.OffsetUp * up;
                if (off.GetLength() > 1e-6)
                    ElementTransformUtils.MoveElement(doc, inst.Id, off);
            }
            catch (Exception ex)
            {
                LemoineLog.Swallowed("CopyLinear: apply alignment", ex);
                if (!_alignApplyWarned)
                {
                    _alignApplyWarned = true;
                    Log($"Align to source: instance {inst.Id} could not be adjusted ({ex.GetType().Name}) — it keeps the standard position; further failures go to the diagnostics log.", "warn");
                }
            }
        }

        // User rotation: X about the run direction, Y about the side axis, Z about the frame's
        // up axis — all about the station point, in X→Y→Z order, in this run's own frame so the
        // same dial rotates every instance identically relative to its source run.
        private void ApplyUserRotation(Document doc, FamilyInstance inst, XYZ a, XYZ b, XYZ p)
        {
            try
            {
                var (along, side, up) = CopyLinearEngine.RunFrame(a, b);
                if (Math.Abs(RotationXRad) > 1e-9)
                    ElementTransformUtils.RotateElement(doc, inst.Id, Line.CreateBound(p, p + along), RotationXRad);
                if (Math.Abs(RotationYRad) > 1e-9)
                    ElementTransformUtils.RotateElement(doc, inst.Id, Line.CreateBound(p, p + side), RotationYRad);
                if (Math.Abs(RotationZRad) > 1e-9)
                    ElementTransformUtils.RotateElement(doc, inst.Id, Line.CreateBound(p, p + up), RotationZRad);
            }
            catch (Exception ex)
            {
                LemoineLog.Swallowed("CopyLinear: apply rotation", ex);
                if (!_rotationApplyWarned)
                {
                    _rotationApplyWarned = true;
                    Log($"Placement rotation: instance {inst.Id} could not be rotated ({ex.GetType().Name}) — further failures go to the diagnostics log.", "warn");
                }
            }
        }

        // Manual placement override: a fixed offset given in run-frame components — so every
        // instance moves relative to its own source run, never in a global direction. Pure user
        // input, no measuring. (Rotation lives in ApplyUserRotation and works in both modes.)
        private void ApplyManualOverride(Document doc, FamilyInstance inst, XYZ a, XYZ b, XYZ p)
        {
            try
            {
                var (along, side, up) = CopyLinearEngine.RunFrame(a, b);
                XYZ off = ManualOffsetAlongFeet * along + ManualOffsetSideFeet * side + ManualOffsetUpFeet * up;
                if (off.GetLength() > 1e-9)
                    ElementTransformUtils.MoveElement(doc, inst.Id, off);
            }
            catch (Exception ex)
            {
                LemoineLog.Swallowed("CopyLinear: apply manual override", ex);
                if (!_manualApplyWarned)
                {
                    _manualApplyWarned = true;
                    Log($"Manual override: instance {inst.Id} could not be adjusted ({ex.GetType().Name}) — it keeps the standard position; further failures go to the diagnostics log.", "warn");
                }
            }
        }

        // The 8 world-space corners of a bounding box: bb.Min/Max live in bb.Transform's space,
        // so each corner goes through bb.Transform first, then the extra (link) transform.
        private static List<XYZ> BoxCorners(BoundingBoxXYZ bb, Transform extra)
        {
            var t = extra.Multiply(bb.Transform);
            var pts = new List<XYZ>(8);
            for (int i = 0; i < 8; i++)
                pts.Add(t.OfPoint(new XYZ(
                    (i & 1) == 0 ? bb.Min.X : bb.Max.X,
                    (i & 2) == 0 ? bb.Min.Y : bb.Max.Y,
                    (i & 4) == 0 ? bb.Min.Z : bb.Max.Z)));
            return pts;
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

        // Finds the matching FamilySymbol (by family name + type name) in the host document.
        // If not present, attempts to copy just the type from the source document so the family
        // is available for NewFamilyInstance reconstruction of face-hosted elements.
        private static FamilySymbol? FindOrCopySymbol(Document doc, Document srcDoc, FamilySymbol? srcSymbol)
        {
            if (srcSymbol == null) return null;
            string familyName = srcSymbol.Family?.Name ?? "";
            string typeName   = srcSymbol.Name ?? "";

            // Search the host document first — fastest path.
            var existing = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .FirstOrDefault(s =>
                    string.Equals(s.Family?.Name, familyName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(s.Name, typeName, StringComparison.OrdinalIgnoreCase));
            if (existing != null) return existing;

            // Not in host — try to copy the type element from the source document.
            // CopyElements with a FamilySymbol id loads the family into the destination and
            // returns the corresponding local symbol (or the existing one via UseDestinationTypes).
            try
            {
                var opts = new CopyPasteOptions();
                opts.SetDuplicateTypeNamesHandler(new UseDestinationTypes());
                var copied = ElementTransformUtils.CopyElements(
                    srcDoc, new List<ElementId> { srcSymbol.Id }, doc, Transform.Identity, opts);
                var sym = copied?.Select(id => doc.GetElement(id) as FamilySymbol)
                                 .FirstOrDefault(s => s != null);
                if (sym != null) return sym;
            }
            catch (Exception ex)
            {
                LemoineLog.Swallowed("CopyLinear: copy family symbol to host", ex);
            }

            // Final fallback: any type in the host that belongs to the same family.
            return new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .FirstOrDefault(s => string.Equals(s.Family?.Name, familyName, StringComparison.OrdinalIgnoreCase));
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
            public List<XYZ>? BoxCorners;   // world-space, for align-to-source calibration
        }
    }
}
