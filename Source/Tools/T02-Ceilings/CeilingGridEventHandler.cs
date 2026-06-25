using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.Ceilings
{
    public class CeilingGridEventHandler : IExternalEventHandler
    {
        public enum ToolMode { Project, Reproject }

        // ── Set by viewmodel before Raise() ───────────────────────────────────
        public ToolMode Mode           { get; set; }
        public string   DwgPath        { get; set; } = null!;
        public string   ReprojectMode  { get; set; } = "all";   // "preselected" | "all"
        public string   BatchDwgFolder { get; set; } = "";      // non-empty → batch project mode

        // Pre-selected element IDs — legacy single-reproject support
        public IList<ElementId> PreSelectedIds { get; set; } = new List<ElementId>();

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
                LemoineLog.Error("CeilingGrid: run aborted", ex); Log($"Error: {ex.Message}", "fail");
                fail++;
            }
            finally
            {
                // Session-long static handler — drop the run's payload.
                PreSelectedIds  = new List<ElementId>();
                SelectedViewIds = new List<ElementId>();
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
                Log($"Folder not found: {BatchDwgFolder}", "fail");
                fail++;
                return;
            }

            var dwgFiles = Directory.GetFiles(BatchDwgFolder, "*.dwg", SearchOption.TopDirectoryOnly);
            if (dwgFiles.Length == 0)
            {
                Log($"No DWG files found in: {BatchDwgFolder}", "fail");
                fail++;
                return;
            }

            Log($"Found {dwgFiles.Length} DWG file(s) in folder.", "info");

            var ceilingPlanViews = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewPlan))
                .Cast<ViewPlan>()
                .Where(v => v.ViewType == ViewType.CeilingPlan && !v.IsTemplate)
                .ToDictionary(v => v.Name, v => (View)v, StringComparer.OrdinalIgnoreCase);

            int total = dwgFiles.Length;
            int done  = 0;

            foreach (var dwgFile in dwgFiles)
            {
                if (LemoineRun.CancelRequested)
                {
                    Log($"Stopped by user — {done} of {total} processed; work so far preserved.", "warn");
                    break;
                }

                string viewName = Path.GetFileNameWithoutExtension(dwgFile);
                if (!ceilingPlanViews.TryGetValue(viewName, out var targetView))
                {
                    Log($"No ceiling plan view '{viewName}' — skipping {Path.GetFileName(dwgFile)}.", "info");
                    skip++;
                }
                else
                {
                    Log($"Projecting {Path.GetFileName(dwgFile)} → '{targetView.Name}'", "info");
                    DwgPath = dwgFile;
                    RunProject(doc, targetView, ref pass, ref fail, ref skip);
                }
                done++;
                Progress((int)(done * 90.0 / total), pass, fail, skip);
            }

            Log($"Batch complete — {pass} curve(s) created, {skip} DWG(s) skipped.", "pass");
        }

        // ═════════════════════════════════════════════════════════════════════
        // Batch reproject — iterates each selected view
        // ═════════════════════════════════════════════════════════════════════
        private void RunBatchReproject(Document doc, ref int pass, ref int fail, ref int skip)
        {
            Log($"Batch reprojecting {SelectedViewIds.Count} view(s).", "info");

            int total = SelectedViewIds.Count;
            int done  = 0;

            foreach (var viewId in SelectedViewIds)
            {
                if (LemoineRun.CancelRequested)
                {
                    Log($"Stopped by user — {done} of {total} processed; work so far preserved.", "warn");
                    break;
                }

                var view = doc.GetElement(viewId) as View;
                if (view == null || view.IsTemplate)
                {
                    Log($"View {viewId.Value} not found or is a template — skipped.", "info");
                    skip++;
                }
                else
                {
                    Log($"Reprojecting: {view.Name}", "info");
                    RunReproject(doc, view, ref pass, ref fail, ref skip);
                }
                done++;
                Progress((int)(done * 90.0 / Math.Max(total, 1)), pass, fail, skip);
            }

            Log($"Batch complete — {pass} curve(s) created across {total} view(s).", "pass");
        }

        // ═════════════════════════════════════════════════════════════════════
        // Project
        // ═════════════════════════════════════════════════════════════════════
        private void RunProject(Document doc, View view, ref int pass, ref int fail, ref int skip)
        {
            if (string.IsNullOrWhiteSpace(DwgPath))
            {
                Log("No DWG path set.", "fail"); fail++; return;
            }

            using (var tx = new Transaction(doc, "Project Ceiling Grids"))
            {
                ConfigureFailures(tx);
                tx.Start();

                var ceilings = CeilingGridHelpers.GetCeilingsInView(doc, view);
                if (ceilings.Count == 0)
                {
                    Log($"No ceiling elements in view '{view.Name}'.", "fail");
                    fail++; tx.RollBack(); return;
                }
                Log($"Found {ceilings.Count} ceiling(s).", "info");

                var faces = CeilingGridHelpers.GetCeilingBottomFaces(ceilings);
                if (faces.Count == 0)
                {
                    Log("Could not extract soffit faces.", "fail");
                    fail++; tx.RollBack(); return;
                }

                var importId = ImportDwg(doc, view);
                if (importId == ElementId.InvalidElementId) { fail++; tx.RollBack(); return; }

                var cadCurves = ExtractCadCurves(doc, importId);
                if (cadCurves.Count == 0)
                {
                    Log("No curves found in DWG.", "fail");
                    doc.Delete(importId); fail++; tx.RollBack(); return;
                }
                Log($"Extracted {cadCurves.Count} curve(s) from DWG.", "info");

                doc.Delete(importId);

                var cache   = new Dictionary<double, SketchPlane>();
                int noMatch = 0;
                int total   = cadCurves.Count;
                int done    = 0;
                var prog    = new RunProgressReporter(Log, total, "curves");

                foreach (var cad in cadCurves)
                {
                    if (LemoineRun.CancelRequested)
                    {
                        Log($"Stopped by user — {done} of {total} processed; work so far preserved.", "warn");
                        break;
                    }

                    var projected = CeilingGridHelpers.ProjectCurveOntoCeilings(cad, faces);
                    if (projected.Count == 0) { noMatch++; }
                    else
                    {
                        foreach (var proj in projected)
                        {
                            try
                            {
                                CeilingGridHelpers.TryCreateModelCurve(doc, view, proj, cache);
                                pass++;
                            }
                            catch (Exception ex)
                            {
                                Log($"Curve creation failed: {ex.Message}", "fail");
                                fail++;
                            }
                        }
                    }
                    done++;
                    Progress((int)(done * 90.0 / total), pass, fail, skip);
                    prog.Tick();
                }

                skip += noMatch;
                tx.Commit();

                Log($"Complete — {pass} model curve(s) created across {cache.Count} elevation(s).", "pass");
                if (noMatch > 0) Log($"{noMatch} CAD curve(s) had no ceiling overlap and were skipped.", "info");
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // Reproject
        // ═════════════════════════════════════════════════════════════════════
        private void RunReproject(Document doc, View view, ref int pass, ref int fail, ref int skip)
        {
            IList<ModelCurve> sourceCurves;
            if (ReprojectMode == "preselected" && PreSelectedIds?.Count > 0)
            {
                sourceCurves = PreSelectedIds
                    .Select(id => doc.GetElement(id))
                    .OfType<ModelCurve>()
                    .ToList();
                Log($"Using {sourceCurves.Count} pre-selected curve(s).", "info");
            }
            else
            {
                sourceCurves = CeilingGridHelpers.GetModelCurvesInView(doc, view);
                Log($"Found {sourceCurves.Count} ModelCurve(s) in view '{view.Name}'.", "info");
            }

            if (sourceCurves.Count == 0)
            {
                Log("No source curves found.", "fail"); fail++; return;
            }

            var ceilings = CeilingGridHelpers.GetCeilingsInView(doc, view);
            if (ceilings.Count == 0)
            {
                Log($"No ceiling elements in view '{view.Name}'.", "fail"); fail++; return;
            }

            var faces = CeilingGridHelpers.GetCeilingBottomFaces(ceilings);
            if (faces.Count == 0)
            {
                Log("Could not extract soffit faces.", "fail"); fail++; return;
            }
            Log($"Found {ceilings.Count} ceiling(s), {faces.Count} face(s).", "info");

            using (var tx = new Transaction(doc, "Reproject Ceiling Grids"))
            {
                ConfigureFailures(tx);
                tx.Start();

                var sourceGeom = sourceCurves
                    .Select(mc => (Id: mc.Id, Curve: mc.GeometryCurve))
                    .Where(g => g.Curve != null)
                    .ToList();

                var projected = new List<Curve>();
                var toDelete  = new List<ElementId>();
                int noMatch   = 0;
                int total     = sourceGeom.Count;
                int done      = 0;
                var projProg  = new RunProgressReporter(Log, total, "source curves");

                foreach (var (id, src) in sourceGeom)
                {
                    if (LemoineRun.CancelRequested)
                    {
                        Log($"Stopped by user — {done} of {total} processed; work so far preserved.", "warn");
                        break;
                    }

                    var projs = CeilingGridHelpers.ProjectCurveOntoCeilings(src, faces);
                    if (projs.Count == 0)
                    {
                        // No ceiling under this curve — keep the original. Deleting it
                        // would be destructive: a reproject that finds no overlap (wrong
                        // active view, ceilings not in view) must not wipe the grid lines.
                        noMatch++;
                    }
                    else
                    {
                        projected.AddRange(projs);
                        toDelete.Add(id);  // replaced by its projection below
                    }
                    done++;
                    Progress((int)(done * 60.0 / total), pass, fail, noMatch);
                    projProg.Tick();
                }

                if (toDelete.Count > 0)
                    doc.Delete(toDelete);

                var cache = new Dictionary<double, SketchPlane>();
                total = projected.Count;
                done  = 0;
                var createProg = new RunProgressReporter(Log, total, "new curves");

                foreach (var proj in projected)
                {
                    if (LemoineRun.CancelRequested)
                    {
                        Log($"Stopped by user — {done} of {total} processed; work so far preserved.", "warn");
                        break;
                    }

                    try
                    {
                        CeilingGridHelpers.TryCreateModelCurve(doc, view, proj, cache);
                        pass++;
                    }
                    catch (Exception ex)
                    {
                        Log($"Curve creation failed: {ex.Message}", "fail");
                        fail++;
                    }
                    done++;
                    Progress(60 + (int)(done * 30.0 / Math.Max(total, 1)), pass, fail, noMatch);
                    createProg.Tick();
                }

                skip += noMatch;
                tx.Commit();

                Log($"Complete — {pass} curve(s) created across {cache.Count} elevation(s).", "pass");
                if (noMatch > 0)
                    Log($"{noMatch} curve(s) had no ceiling overlap and were kept unchanged.", "info");
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
                if (ok) { Log($"DWG imported: {Path.GetFileName(DwgPath)}", "info"); return id; }
                Log("DWG import returned false — check the file path.", "fail");
                return ElementId.InvalidElementId;
            }
            catch (Exception ex)
            {
                Log($"DWG import error: {ex.Message}", "fail");
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
