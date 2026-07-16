using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;

namespace LemoineTools.Tools.Ceilings
{
    public class CeilingGridEventHandler : IExternalEventHandler
    {
        public enum ToolMode { Project, Reproject }

        // ── Set by viewmodel before Raise() ───────────────────────────────────
        public ToolMode Mode           { get; set; }
        public string   DwgPath        { get; set; } = null!;
        public string   BatchDwgFolder { get; set; } = "";      // non-empty → batch project mode

        // Batch reproject — set by ReprojectCeilingGridsViewModel
        public List<ElementId> SelectedViewIds { get; set; } = new List<ElementId>();

        // ── Callbacks — set by viewmodel, invoked on WPF thread ──────────────
        public Action<string, string>?     PushLog    { get; set; }
        public Action<int, int, int, int>? OnProgress { get; set; }
        public Action<int, int, int>?      OnComplete { get; set; }

        // ═════════════════════════════════════════════════════════════════════
        // Execute — called on Revit main thread
        // ═════════════════════════════════════════════════════════════════════
        public void Execute(UIApplication app)
        {
            var doc  = app.ActiveUIDocument.Document;
            int pass = 0, fail = 0, skip = 0;

            try
            {
                if (SelectedViewIds.Count > 0)
                    RunBatchReproject(doc, ref pass, ref fail, ref skip);
                else if (!string.IsNullOrWhiteSpace(BatchDwgFolder))
                    RunBatchProject(doc, ref pass, ref fail, ref skip);
                else if (Mode == ToolMode.Project)
                    RunProject(doc, doc.ActiveView, ref pass, ref fail, ref skip);
                else
                    RunReproject(doc, doc.ActiveView, ref pass, ref fail, ref skip);
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("CeilingGrid: run aborted", ex); Log(AppStrings.T("ceilings.grids.log.error", ex.Message), "fail");
                fail++;
            }
            finally
            {
                // Session-long static handler — drop the run's payload so nothing (view ids or
                // file/folder paths) carries into the next run.
                SelectedViewIds = new List<ElementId>();
                DwgPath         = "";
                BatchDwgFolder  = "";
            }

            Progress(100, pass, fail, skip);
            Complete(pass, fail, skip);
        }

        public string GetName() => $"LemoineTools.CeilingGridEventHandler.{Mode}";

        // ═════════════════════════════════════════════════════════════════════
        // Batch project — scans folder for DWGs, matches names to ceiling plan views
        // ═════════════════════════════════════════════════════════════════════
        private void RunBatchProject(Document doc, ref int pass, ref int fail, ref int skip)
        {
            if (!Directory.Exists(BatchDwgFolder))
            {
                Log(AppStrings.T("ceilings.grids.log.folderNotFound", BatchDwgFolder), "fail");
                fail++;
                return;
            }

            var dwgFiles = Directory.GetFiles(BatchDwgFolder, "*.dwg", SearchOption.TopDirectoryOnly);
            if (dwgFiles.Length == 0)
            {
                Log(AppStrings.T("ceilings.grids.log.noDwgInFolder", BatchDwgFolder), "fail");
                fail++;
                return;
            }

            Log(AppStrings.T("ceilings.grids.log.foundDwgFiles", dwgFiles.Length), "info");

            var ceilingPlanViews = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewPlan))
                .Cast<ViewPlan>()
                .Where(v => v.ViewType == ViewType.CeilingPlan && !v.IsTemplate)
                .ToDictionary(v => v.Name, v => (View)v, StringComparer.OrdinalIgnoreCase);

            int total = dwgFiles.Length;
            int done  = 0;
            int slice = Math.Max(1, (int)(90.0 / total));   // per-DWG share of the 0–90% band

            foreach (var dwgFile in dwgFiles)
            {
                if (RunState.CancelRequested)
                {
                    Log(AppStrings.T("common.log.stoppedByUser", done, total), "warn");
                    break;
                }

                string viewName = Path.GetFileNameWithoutExtension(dwgFile);
                if (!ceilingPlanViews.TryGetValue(viewName, out var targetView))
                {
                    Log(AppStrings.T("ceilings.grids.log.noViewSkipDwg", viewName, Path.GetFileName(dwgFile)), "info");
                    skip++;
                }
                else
                {
                    Log(AppStrings.T("ceilings.grids.log.projectingDwg", Path.GetFileName(dwgFile), targetView.Name), "info");
                    DwgPath = dwgFile;
                    // Map the inner run's progress into this DWG's slice so the bar advances
                    // monotonically instead of snapping back to ~0 for every file.
                    RunProject(doc, targetView, ref pass, ref fail, ref skip, (int)(done * 90.0 / total), slice);
                }
                done++;
                Progress((int)(done * 90.0 / total), pass, fail, skip);
            }

            Log(AppStrings.T("ceilings.grids.log.batchCompleteProject", pass, skip), "pass");
        }

        // ═════════════════════════════════════════════════════════════════════
        // Batch reproject — iterates each selected view
        // ═════════════════════════════════════════════════════════════════════
        private void RunBatchReproject(Document doc, ref int pass, ref int fail, ref int skip)
        {
            Log(AppStrings.T("ceilings.grids.log.batchReprojecting", SelectedViewIds.Count), "info");

            int total = SelectedViewIds.Count;
            int done  = 0;
            int slice = Math.Max(1, (int)(90.0 / Math.Max(total, 1)));   // per-view share of the 0–90% band

            foreach (var viewId in SelectedViewIds)
            {
                if (RunState.CancelRequested)
                {
                    Log(AppStrings.T("common.log.stoppedByUser", done, total), "warn");
                    break;
                }

                var view = doc.GetElement(viewId) as View;
                if (view == null || view.IsTemplate)
                {
                    Log(AppStrings.T("ceilings.grids.log.viewNotFound", viewId.Value), "info");
                    skip++;
                }
                else
                {
                    Log(AppStrings.T("ceilings.grids.log.reprojecting", view.Name), "info");
                    // Map the inner run's progress into this view's slice (monotonic bar).
                    RunReproject(doc, view, ref pass, ref fail, ref skip, (int)(done * 90.0 / Math.Max(total, 1)), slice);
                }
                done++;
                Progress((int)(done * 90.0 / Math.Max(total, 1)), pass, fail, skip);
            }

            Log(AppStrings.T("ceilings.grids.log.batchCompleteReproject", pass, total), "pass");
        }

        // ═════════════════════════════════════════════════════════════════════
        // Project
        // ═════════════════════════════════════════════════════════════════════
        private void RunProject(Document doc, View view, ref int pass, ref int fail, ref int skip,
                                int progBase = 0, int progSpan = 90)
        {
            if (string.IsNullOrWhiteSpace(DwgPath))
            {
                Log(AppStrings.T("ceilings.grids.log.noDwgPath"), "fail"); fail++; return;
            }
            if (!(view is ViewPlan))
            {
                // Single-file mode projects into the active view; guard against a 3D view,
                // sheet, or schedule where the import/collector behaviour is undefined.
                Log(AppStrings.T("ceilings.grids.log.notPlanView", view.Name), "fail"); fail++; return;
            }

            int passBefore = pass;

            using (var tx = new Transaction(doc, "Project Ceiling Grids"))
            {
                ConfigureFailures(tx);
                tx.Start();

                var ceilings = CeilingGridHelpers.GetCeilingsInView(doc, view);
                if (ceilings.Count == 0)
                {
                    Log(AppStrings.T("ceilings.grids.log.noCeilings", view.Name), "fail");
                    fail++; tx.RollBack(); return;
                }
                Log(AppStrings.T("ceilings.grids.log.foundCeilings", ceilings.Count), "info");

                var faces = CeilingGridHelpers.GetCeilingBottomFaces(ceilings);
                if (faces.Count == 0)
                {
                    Log(AppStrings.T("ceilings.grids.log.noSoffit"), "fail");
                    fail++; tx.RollBack(); return;
                }

                var importId = ImportDwg(doc, view);
                if (importId == ElementId.InvalidElementId) { fail++; tx.RollBack(); return; }

                var cadCurves = ExtractCadCurves(doc, importId);
                if (cadCurves.Count == 0)
                {
                    Log(AppStrings.T("ceilings.grids.log.noCurvesInDwg"), "fail");
                    doc.Delete(importId); fail++; tx.RollBack(); return;
                }
                Log(AppStrings.T("ceilings.grids.log.extractedCurves", cadCurves.Count), "info");

                doc.Delete(importId);

                var cache   = new Dictionary<double, SketchPlane>();
                int noMatch = 0, detailCount = 0, failedCurves = 0;
                int total   = cadCurves.Count;
                int done    = 0;
                var prog    = new RunProgressReporter(Log, total, "curves");

                foreach (var cad in cadCurves)
                {
                    if (RunState.CancelRequested)
                    {
                        Log(AppStrings.T("common.log.stoppedByUser", done, total), "warn");
                        break;
                    }

                    var projected = CeilingGridHelpers.ProjectCurveOntoCeilings(cad, faces);
                    if (projected.Count == 0) { noMatch++; }
                    else
                    {
                        foreach (var proj in projected)
                        {
                            var res = CeilingGridHelpers.TryCreateModelCurve(doc, view, proj, cache);
                            if (res == CurveCreateResult.Failed) { failedCurves++; fail++; }
                            else { pass++; if (res == CurveCreateResult.Detail) detailCount++; }
                        }
                    }
                    done++;
                    Progress(progBase + (int)(done * (double)progSpan / total), pass, fail, skip);
                    prog.Tick();
                }

                skip += noMatch;
                tx.Commit();

                Log(AppStrings.T("ceilings.grids.log.completeProject", pass - passBefore, cache.Count), "pass");
                if (detailCount  > 0) Log(AppStrings.T("ceilings.grids.log.detailFallback", detailCount), "warn");
                if (failedCurves > 0) Log(AppStrings.T("ceilings.grids.log.curveCreateFailed", failedCurves), "fail");
                if (noMatch      > 0) Log(AppStrings.T("ceilings.grids.log.noOverlapSkipped", noMatch), "info");
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // Reproject
        // ═════════════════════════════════════════════════════════════════════
        private void RunReproject(Document doc, View view, ref int pass, ref int fail, ref int skip,
                                  int progBase = 0, int progSpan = 90)
        {
            IList<ModelCurve> sourceCurves = CeilingGridHelpers.GetModelCurvesInView(doc, view);
            Log(AppStrings.T("ceilings.grids.log.foundModelCurves", sourceCurves.Count, view.Name), "info");

            if (sourceCurves.Count == 0)
            {
                Log(AppStrings.T("ceilings.grids.log.noSourceCurves"), "fail"); fail++; return;
            }

            var ceilings = CeilingGridHelpers.GetCeilingsInView(doc, view);
            if (ceilings.Count == 0)
            {
                Log(AppStrings.T("ceilings.grids.log.noCeilings", view.Name), "fail"); fail++; return;
            }

            var faces = CeilingGridHelpers.GetCeilingBottomFaces(ceilings);
            if (faces.Count == 0)
            {
                Log(AppStrings.T("ceilings.grids.log.noSoffit"), "fail"); fail++; return;
            }
            Log(AppStrings.T("ceilings.grids.log.foundCeilingsFaces", ceilings.Count, faces.Count), "info");

            int passBefore = pass;

            using (var tx = new Transaction(doc, "Reproject Ceiling Grids"))
            {
                ConfigureFailures(tx);
                tx.Start();

                var sourceGeom = sourceCurves
                    .Select(mc => (Id: mc.Id, Curve: mc.GeometryCurve))
                    .Where(g => g.Curve != null)
                    .ToList();

                var cache = new Dictionary<double, SketchPlane>();
                int noMatch = 0, detailCount = 0, failedCurves = 0;
                int total   = sourceGeom.Count;
                int done    = 0;
                var prog    = new RunProgressReporter(Log, total, "source curves");

                // Process one source curve at a time: create its replacement(s) FIRST, then
                // delete the original. The old code bulk-deleted every matched curve up front
                // and recreated them in a second cancellable loop — cancelling (or a creation
                // failure) mid-recreate left curves deleted-but-not-replaced yet still committed,
                // silently destroying grid lines. Pairing the delete with the create means a
                // cancel commits only fully-swapped curves; every unprocessed original is kept.
                foreach (var (id, src) in sourceGeom)
                {
                    if (RunState.CancelRequested)
                    {
                        Log(AppStrings.T("common.log.stoppedByUser", done, total), "warn");
                        break;
                    }

                    var projs = CeilingGridHelpers.ProjectCurveOntoCeilings(src, faces);
                    if (projs.Count == 0)
                    {
                        // No ceiling under this curve — keep the original. A reproject that
                        // finds no overlap (wrong active view, ceilings not in view) must not
                        // wipe the grid lines.
                        noMatch++;
                    }
                    else
                    {
                        int madeForThis = 0;
                        foreach (var proj in projs)
                        {
                            var res = CeilingGridHelpers.TryCreateModelCurve(doc, view, proj, cache);
                            if (res == CurveCreateResult.Failed) { failedCurves++; fail++; }
                            else { pass++; madeForThis++; if (res == CurveCreateResult.Detail) detailCount++; }
                        }
                        // Remove the original only once at least one replacement exists, so a
                        // failed recreate never destroys the source curve.
                        if (madeForThis > 0)
                            doc.Delete(id);
                    }

                    done++;
                    Progress(progBase + (int)(done * (double)progSpan / Math.Max(total, 1)), pass, fail, noMatch);
                    prog.Tick();
                }

                skip += noMatch;
                tx.Commit();

                Log(AppStrings.T("ceilings.grids.log.completeReproject", pass - passBefore, cache.Count), "pass");
                if (detailCount  > 0) Log(AppStrings.T("ceilings.grids.log.detailFallback", detailCount), "warn");
                if (failedCurves > 0) Log(AppStrings.T("ceilings.grids.log.curveCreateFailed", failedCurves), "fail");
                if (noMatch      > 0) Log(AppStrings.T("ceilings.grids.log.noOverlapKept", noMatch), "info");
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // DWG helpers
        // ═════════════════════════════════════════════════════════════════════
        private ElementId ImportDwg(Document doc, View view)
        {
            try
            {
                var opts = new DWGImportOptions
                {
                    Placement    = ImportPlacement.Origin,
                    Unit         = ImportUnit.Default,
                    ColorMode    = ImportColorMode.Preserved,
                    ThisViewOnly = true,
                };
                bool ok = doc.Import(DwgPath, opts, view, out ElementId id);
                if (ok) { Log(AppStrings.T("ceilings.grids.log.dwgImported", Path.GetFileName(DwgPath)), "info"); return id; }
                Log(AppStrings.T("ceilings.grids.log.dwgImportFalse"), "fail");
                return ElementId.InvalidElementId;
            }
            catch (Exception ex)
            {
                Log(AppStrings.T("ceilings.grids.log.dwgImportError", ex.Message), "fail");
                return ElementId.InvalidElementId;
            }
        }

        private static IList<Curve> ExtractCadCurves(Document doc, ElementId importId)
        {
            var curves   = new List<Curve>();
            var instance = doc.GetElement(importId) as ImportInstance;
            if (instance == null) return curves;
            var geom = instance.get_Geometry(new Options { ComputeReferences = false });
            if (geom != null) CollectCurves(geom, Transform.Identity, curves);
            return curves;
        }

        private static void CollectCurves(GeometryElement geom, Transform t, List<Curve> curves)
        {
            foreach (GeometryObject obj in geom)
            {
                switch (obj)
                {
                    case GeometryInstance gi:
                        CollectCurves(gi.GetInstanceGeometry(t), t, curves);
                        break;
                    case Line line:
                        curves.Add(line.CreateTransformed(t) as Curve ?? line); break;
                    case Arc arc:
                        curves.Add(arc.CreateTransformed(t) as Curve ?? arc); break;
                    case NurbSpline sp:
                        curves.Add(sp.CreateTransformed(t) as Curve ?? sp); break;
                    case PolyLine poly:
                        var pts = poly.GetCoordinates();
                        for (int i = 0; i < pts.Count - 1; i++)
                        {
                            XYZ p0 = t.OfPoint(pts[i]), p1 = t.OfPoint(pts[i + 1]);
                            if (!p0.IsAlmostEqualTo(p1)) curves.Add(Line.CreateBound(p0, p1));
                        }
                        break;
                }
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // Helpers
        // ═════════════════════════════════════════════════════════════════════
        private static void ConfigureFailures(Transaction tx)
        {
            var opts = tx.GetFailureHandlingOptions();
            opts.SetClearAfterRollback(true);
            opts.SetDelayedMiniWarnings(true);
            tx.SetFailureHandlingOptions(opts);
        }

        private void Log(string text, string status) => PushLog?.Invoke(text, status);
        private void Progress(int pct, int p, int f, int s) => OnProgress?.Invoke(pct, p, f, s);
        private void Complete(int p, int f, int s) => OnComplete?.Invoke(p, f, s);
    }
}
