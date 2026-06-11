using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using LemoineTools.Lemoine;
using LemoineTools.PdfGeometry.Plans;

namespace LemoineTools.Tools.Testing.PdfRegionWand
{
    public enum PdfWandOp
    {
        SelectImage,
        PickSeedPoint,
        StartSplitLines,
        FinishSplitLines,
        CreateRegion,
        Detach,
    }

    /// <summary>Result of picking the PDF underlay.</summary>
    public sealed class ImageSelection
    {
        public long ImageElementIdValue { get; set; }
        public string DisplayName { get; set; } = "";
        public string? PdfPath { get; set; }
        public int PageNumber { get; set; } = 1;
        public ImagePlacement Placement { get; set; } = new ImagePlacement();
    }

    /// <summary>
    /// All Revit-thread work for the PDF Region Wand palette, multiplexed by
    /// <see cref="Op"/>. The palette sets the relevant properties, raises the
    /// event, and receives results through the callbacks (invoked on Revit's
    /// thread — the palette marshals onto its own dispatcher with BeginInvoke).
    ///
    /// The handler also owns the session's <c>DocumentChanged</c> subscription:
    /// a NAMED instance method, attached on the Revit thread at image selection
    /// and detached by the <see cref="PdfWandOp.Detach"/> op when the palette
    /// closes — a leaked anonymous subscription would outlive the palette and
    /// crash Revit later (see CLAUDE.md crash rules). It watches for deletions
    /// of session-created elements (undo awareness) and captures CurveElements
    /// added while the native split-line tool is active.
    /// </summary>
    public class PdfRegionWandEventHandler : IExternalEventHandler
    {
        // ── Inputs (set by the palette before Raise) ─────────────────────────
        public PdfWandOp Op { get; set; }
        public RegionPlan? Plan { get; set; }
        public RegionOutputOptions? Options { get; set; }
        public IRegionOutputAdapter? Adapter { get; set; }
        public int FaceId { get; set; }
        public PdfToModelTransform? Transform { get; set; }
        public bool UseModelLines { get; set; }
        public bool KeepSplitLines { get; set; }

        // ── Callbacks (invoked on Revit's thread) ────────────────────────────
        public Action<ImageSelection?>? OnImageSelected { get; set; }
        public Action<XYZ?>? OnSeedPicked { get; set; }
        public Action<bool>? OnSplitLinesStarted { get; set; }
        public Action<List<List<XYZ>>>? OnSplitLinesCaptured { get; set; }
        public Action<int, long>? OnRegionCreated { get; set; }   // faceId, elementId.Value (-1 = failed)
        public Action<List<long>>? OnTrackedElementsDeleted { get; set; }
        public Action<string, string>? PushLog { get; set; }

        // ── DocumentChanged state (mutated on Revit's thread only) ──────────
        private Autodesk.Revit.ApplicationServices.Application? _dbApp;
        private bool _subscribed;
        private bool _capturingSplitLines;
        private readonly List<ElementId> _capturedCurveIds = new List<ElementId>();
        private readonly HashSet<long> _trackedElementIds = new HashSet<long>();

        public string GetName() => "LemoineTools.Tools.Testing.PdfRegionWand.PdfRegionWandEventHandler";

        public void Execute(UIApplication app)
        {
            try
            {
                switch (Op)
                {
                    case PdfWandOp.SelectImage: ExecuteSelectImage(app); break;
                    case PdfWandOp.PickSeedPoint: ExecutePickPoint(app); break;
                    case PdfWandOp.StartSplitLines: ExecuteStartSplitLines(app); break;
                    case PdfWandOp.FinishSplitLines: ExecuteFinishSplitLines(app); break;
                    case PdfWandOp.CreateRegion: ExecuteCreateRegion(app); break;
                    case PdfWandOp.Detach: DetachDocumentChanged(); break;
                }
            }
            catch (Exception ex)
            {
                LemoineLog.Error($"PdfRegionWand: op {Op}", ex);
                PushLog?.Invoke($"{Op} failed: {ex.Message}", "fail");
                // Unblock the palette — every op's caller waits on its callback.
                switch (Op)
                {
                    case PdfWandOp.SelectImage: OnImageSelected?.Invoke(null); break;
                    case PdfWandOp.PickSeedPoint: OnSeedPicked?.Invoke(null); break;
                    case PdfWandOp.StartSplitLines: OnSplitLinesStarted?.Invoke(false); break;
                    case PdfWandOp.FinishSplitLines: OnSplitLinesCaptured?.Invoke(new List<List<XYZ>>()); break;
                    case PdfWandOp.CreateRegion: OnRegionCreated?.Invoke(FaceId, -1); break;
                }
            }
        }

        // ──────────────────────────────────────────────────────── SelectImage

        private sealed class ImageInstanceFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem) => elem is ImageInstance;
            public bool AllowReference(Reference reference, XYZ position) => false;
        }

        private void ExecuteSelectImage(UIApplication app)
        {
            var uidoc = app.ActiveUIDocument;
            if (uidoc == null) { OnImageSelected?.Invoke(null); return; }
            var doc = uidoc.Document;
            var view = uidoc.ActiveView;

            Reference picked;
            try
            {
                picked = uidoc.Selection.PickObject(ObjectType.Element,
                    new ImageInstanceFilter(), "Pick the placed PDF underlay.");
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                OnImageSelected?.Invoke(null);
                return;
            }

            if (!(doc.GetElement(picked) is ImageInstance image))
            {
                PushLog?.Invoke("Selected element is not an image/PDF underlay.", "fail");
                OnImageSelected?.Invoke(null);
                return;
            }

            var selection = new ImageSelection
            {
                ImageElementIdValue = image.Id.Value,
                DisplayName = image.Name,
            };

            // Placement (rotation-independent size from the sheet parameters).
            double wFt = image.get_Parameter(BuiltInParameter.RASTER_SHEETWIDTH)?.AsDouble() ?? 0;
            double hFt = image.get_Parameter(BuiltInParameter.RASTER_SHEETHEIGHT)?.AsDouble() ?? 0;
            double rotation = 0;
            if (image.Location is LocationPoint lp)
            {
                try { rotation = lp.Rotation; }
                catch (Exception ex) { LemoineLog.Swallowed("PdfRegionWand: read LocationPoint.Rotation", ex); }
            }
            var bb = image.get_BoundingBox(view);
            if (bb == null)
            {
                PushLog?.Invoke("Selected image has no bounding box in the active view.", "fail");
                OnImageSelected?.Invoke(null);
                return;
            }
            var center = (bb.Min + bb.Max) * 0.5;
            bool bboxFallback = false;
            if (wFt <= 0 || hFt <= 0)
            {
                wFt = bb.Max.X - bb.Min.X;
                hFt = bb.Max.Y - bb.Min.Y;
                bboxFallback = Math.Abs(rotation) > 1e-6;
            }
            selection.Placement = new ImagePlacement
            {
                WidthFt = wFt, HeightFt = hFt,
                CenterX = center.X, CenterY = center.Y, CenterZ = center.Z,
                RotationRad = rotation,
                SizeFromRotatedBbox = bboxFallback,
            };

            // Source file path + page number from the type.
            if (doc.GetElement(image.GetTypeId()) is ImageType imageType)
            {
                selection.PdfPath = ResolveSourcePath(doc, imageType);
                try { selection.PageNumber = Math.Max(1, imageType.GetImageTypeOptions().PageNumber); }
                catch (Exception ex) { LemoineLog.Swallowed("PdfRegionWand: read ImageTypeOptions.PageNumber", ex); }
            }

            if (selection.PdfPath == null)
                PushLog?.Invoke("Could not resolve the source PDF on disk — browse to the file manually.", "warn");
            else if (!System.IO.File.Exists(selection.PdfPath))
            {
                PushLog?.Invoke($"Source PDF missing on disk: {selection.PdfPath} — browse to the file manually.", "warn");
                selection.PdfPath = null;
            }

            AttachDocumentChanged(app);
            OnImageSelected?.Invoke(selection);
        }

        private string? ResolveSourcePath(Document doc, ImageType imageType)
        {
            // Linked PDFs carry an external file reference; imported ones only a
            // file-name parameter (which may or may not include the folder).
            try
            {
                if (ExternalFileUtils.IsExternalFileReference(doc, imageType.Id))
                {
                    var efr = ExternalFileUtils.GetExternalFileReference(doc, imageType.Id);
                    var path = ModelPathUtils.ConvertModelPathToUserVisiblePath(efr.GetAbsolutePath());
                    if (!string.IsNullOrWhiteSpace(path)) return path;
                }
            }
            catch (Exception ex)
            {
                LemoineLog.Swallowed("PdfRegionWand: GetExternalFileReference", ex);
            }

            try
            {
                var p = imageType.get_Parameter(BuiltInParameter.RASTER_SYMBOL_FILENAME)?.AsString();
                if (!string.IsNullOrWhiteSpace(p)) return p;
            }
            catch (Exception ex)
            {
                LemoineLog.Swallowed("PdfRegionWand: RASTER_SYMBOL_FILENAME", ex);
            }
            return null;
        }

        // ─────────────────────────────────────────────────────── PickSeedPoint

        private void ExecutePickPoint(UIApplication app)
        {
            var uidoc = app.ActiveUIDocument;
            if (uidoc == null) { OnSeedPicked?.Invoke(null); return; }
            try
            {
                var p = uidoc.Selection.PickPoint("Pick a point inside the region to trace.");
                OnSeedPicked?.Invoke(p);
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                OnSeedPicked?.Invoke(null);
            }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException ex)
            {
                LemoineLog.Swallowed("PdfRegionWand: PickPoint without a work plane", ex);
                PushLog?.Invoke("The active view has no work plane to pick on — open a plan view.", "fail");
                OnSeedPicked?.Invoke(null);
            }
        }

        // ─────────────────────────────────────────────────────── Split lines

        private void ExecuteStartSplitLines(UIApplication app)
        {
            var uidoc = app.ActiveUIDocument;
            if (uidoc == null) { OnSplitLinesStarted?.Invoke(false); return; }

            AttachDocumentChanged(app);
            _capturedCurveIds.Clear();
            _capturingSplitLines = true;

            var cmd = RevitCommandId.LookupPostableCommandId(
                UseModelLines ? PostableCommand.ModelLine : PostableCommand.DetailLine);
            if (cmd == null || !uidoc.Application.CanPostCommand(cmd))
            {
                _capturingSplitLines = false;
                PushLog?.Invoke("The line tool cannot be started in the current context.", "fail");
                OnSplitLinesStarted?.Invoke(false);
                return;
            }

            // PostCommand executes after control returns to Revit — which works
            // because the palette is modeless.
            uidoc.Application.PostCommand(cmd);
            OnSplitLinesStarted?.Invoke(true);
        }

        private void ExecuteFinishSplitLines(UIApplication app)
        {
            _capturingSplitLines = false;
            var uidoc = app.ActiveUIDocument;
            var doc = uidoc?.Document;
            var polylines = new List<List<XYZ>>();
            var alive = new List<ElementId>();

            if (doc != null)
            {
                foreach (var id in _capturedCurveIds.Distinct())
                {
                    // Tolerate lines the user undid mid-session — just skip them.
                    if (!(doc.GetElement(id) is CurveElement ce)) continue;
                    var curve = ce.GeometryCurve;
                    if (curve == null) continue;

                    List<XYZ> pts;
                    if (curve is Line line)
                        pts = new List<XYZ> { line.GetEndPoint(0), line.GetEndPoint(1) };
                    else
                        pts = curve.Tessellate().ToList(); // arcs/splines → chordal polyline

                    if (pts.Count >= 2)
                    {
                        polylines.Add(pts);
                        alive.Add(id);
                    }
                }

                if (!KeepSplitLines && alive.Count > 0)
                {
                    using (var tx = new Transaction(doc, "PDF Wand — Remove split lines"))
                    {
                        tx.Start();
                        ConfigureFailures(tx);
                        try
                        {
                            doc.Delete(alive);
                            tx.Commit();
                        }
                        catch (Exception ex)
                        {
                            tx.RollBack();
                            LemoineLog.Error("PdfRegionWand: delete captured split lines", ex);
                            PushLog?.Invoke("Could not remove the drawn split lines — they remain in the model.", "warn");
                        }
                    }
                }
            }

            _capturedCurveIds.Clear();
            OnSplitLinesCaptured?.Invoke(polylines);
        }

        // ─────────────────────────────────────────────────────── CreateRegion

        private void ExecuteCreateRegion(UIApplication app)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null || Plan == null || Options == null || Adapter == null || Transform == null)
            {
                OnRegionCreated?.Invoke(FaceId, -1);
                return;
            }

            // One region = one transaction = one undo step. Never batched.
            using (var tx = new Transaction(doc, $"PDF Wand {Adapter.DisplayName} — Region {FaceId}"))
            {
                tx.Start();
                ConfigureFailures(tx);
                try
                {
                    var ids = Adapter.Create(doc, Plan, Transform, Options,
                        (m, s) => PushLog?.Invoke(m, s));
                    if (ids == null || ids.Count == 0)
                        throw new InvalidOperationException("Adapter returned no elements.");
                    tx.Commit();

                    foreach (var id in ids) _trackedElementIds.Add(id.Value);
                    OnRegionCreated?.Invoke(FaceId, ids[0].Value);
                }
                catch (Exception ex)
                {
                    tx.RollBack();
                    LemoineLog.Error($"PdfRegionWand: create region {FaceId}", ex);
                    PushLog?.Invoke($"Region {FaceId}: {Adapter.DisplayName.ToLowerInvariant()} creation failed — {ex.Message}", "fail");
                    OnRegionCreated?.Invoke(FaceId, -1);
                }
            }
        }

        private void ConfigureFailures(Transaction tx)
        {
            var opts = tx.GetFailureHandlingOptions();
            opts.SetClearAfterRollback(true);
            opts.SetDelayedMiniWarnings(true);
            opts.SetFailuresPreprocessor(new SuppressWarningsPreprocessor(PushLog));
            tx.SetFailureHandlingOptions(opts);
        }

        private sealed class SuppressWarningsPreprocessor : IFailuresPreprocessor
        {
            private readonly Action<string, string>? _log;
            private readonly HashSet<string> _seen = new HashSet<string>(StringComparer.Ordinal);
            public SuppressWarningsPreprocessor(Action<string, string>? log) { _log = log; }

            public FailureProcessingResult PreprocessFailures(FailuresAccessor fa)
            {
                foreach (var msg in fa.GetFailureMessages()
                                      .Where(m => m.GetSeverity() == FailureSeverity.Warning))
                {
                    string desc;
                    try { desc = msg.GetDescriptionText(); }
                    catch (Exception ex)
                    {
                        LemoineLog.Swallowed("PdfRegionWand: read warning text", ex);
                        desc = "(unreadable warning)";
                    }
                    if (_seen.Add(desc))
                    {
                        _log?.Invoke($"[warning] {desc}", "warn");
                        LemoineLog.Warn("PdfRegionWand", desc);
                    }
                    fa.DeleteWarning(msg);
                }
                return FailureProcessingResult.Continue;
            }
        }

        // ───────────────────────────────────────── DocumentChanged life cycle

        private void AttachDocumentChanged(UIApplication app)
        {
            if (_subscribed) return;
            _dbApp = app.Application;
            _dbApp.DocumentChanged += OnDocumentChanged;
            _subscribed = true;
        }

        private void DetachDocumentChanged()
        {
            if (!_subscribed || _dbApp == null) return;
            _dbApp.DocumentChanged -= OnDocumentChanged;
            _dbApp = null;
            _subscribed = false;
            _capturingSplitLines = false;
            _capturedCurveIds.Clear();
            _trackedElementIds.Clear();
        }

        /// <summary>
        /// Raised on Revit's main thread for every model change. Kept cheap:
        /// id-set checks only. Detects (a) deletion of session-created elements
        /// — undo or manual — so the palette can release the matching face, and
        /// (b) curve elements drawn while split-line capture is active.
        /// </summary>
        private void OnDocumentChanged(object sender, DocumentChangedEventArgs e)
        {
            try
            {
                var deleted = e.GetDeletedElementIds();
                if (deleted != null && deleted.Count > 0 && _trackedElementIds.Count > 0)
                {
                    var ours = deleted.Select(id => id.Value)
                                      .Where(_trackedElementIds.Contains)
                                      .ToList();
                    if (ours.Count > 0)
                    {
                        foreach (var v in ours) _trackedElementIds.Remove(v);
                        OnTrackedElementsDeleted?.Invoke(ours);
                    }
                }

                if (_capturingSplitLines)
                {
                    var doc = e.GetDocument();
                    foreach (var id in e.GetAddedElementIds())
                        if (doc.GetElement(id) is CurveElement)
                            _capturedCurveIds.Add(id);
                }
            }
            catch (Exception ex)
            {
                LemoineLog.Swallowed("PdfRegionWand: DocumentChanged watch", ex);
            }
        }
    }
}
