using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;

namespace LemoineTools.Tools.Sheets.AlignSheetViews
{
    /// <summary>
    /// Aligns the viewports on a set of target sheets so each view registers exactly on
    /// top of its counterpart on the best-matching source/reference sheet. Source sheets are
    /// ground truth — their viewports are never moved.
    ///
    /// Multiple source sheets may be supplied. For each target sheet the handler scores every
    /// source sheet (by how many of its views match, weighted toward matching scale + orientation
    /// and exact scope-box matches) and aligns the whole target to the single best source sheet.
    ///
    /// A source view is matched to a target view by <b>scope box first</b> (exact, shared
    /// <c>VIEWER_VOLUME_OF_INTEREST_CROP</c> ElementId) and by <b>crop-region overlap</b> only as a
    /// fallback for views with no scope box. Each matched target viewport's box centre is then set
    /// so a shared world anchor lands at the same sheet coordinate as on the source.
    ///
    /// <b>Placement is PREDICTED, not read back.</b> Every geometry write this tool makes has a
    /// result derivable from values already in hand — a scope box makes the target's crop cover the
    /// source's footprint, a crop resize keeps the target's own crop centre, and annotation offsets
    /// are copied from the source — so the alignment never has to regenerate the document just to
    /// read a crop box it could compute. <c>SetBoxCenter</c> is absolute ("go to X", never "move by
    /// X"), so a prediction that turns out wrong cannot accumulate error: one pass at the end of the
    /// run re-reads the real geometry, re-places anything that missed, and reports it.
    ///
    /// Optionally inherits, from the source view onto the matched target view: scope box assignment
    /// (applied FIRST, before any alignment), crop size + annotation crop, grid 2D (view-specific)
    /// extents, and crop-region visibility. A target sheet missing a counterpart — or carrying an
    /// unmatched extra — is reported, never silently skipped. Runs on the Revit API thread; set all
    /// inputs before Raise().
    /// </summary>
    public sealed class AlignSheetViewsEventHandler : IExternalEventHandler
    {
        // ── Inputs set by the view model before Raise() ───────────────────────
        public List<ElementId> SourceSheetIds { get; set; } = new List<ElementId>();
        public List<ElementId> TargetSheetIds { get; set; } = new List<ElementId>();

        /// <summary>Minimum in-plane overlap fraction (0..1) for a source/target view pair to match (overlap fallback only).</summary>
        public double OverlapThreshold { get; set; } = 0.5;

        // ── Inheritance toggles ───────────────────────────────────────────────
        /// <summary>Trim each target grid's 2D (view-specific) extents to the source grid's endpoints.</summary>
        public bool InheritGridExtents { get; set; } = false;

        /// <summary>Assign the source view's scope box to the target view (applied before alignment).</summary>
        public bool InheritScopeBox { get; set; } = false;

        /// <summary>Match the target view's crop-region visibility (CropBoxVisible) to the source.</summary>
        public bool InheritCropVisibility { get; set; } = false;

        /// <summary>Match the target view's crop size + annotation-crop offsets to the source.
        /// The view model also sets this whenever <see cref="InheritScopeBox"/> is on: a scope box
        /// governs the crop rectangle, so those views take their crop size from it and only the
        /// annotation-crop margins are copied.</summary>
        public bool InheritCropSize { get; set; } = false;

        // ── Callbacks ─────────────────────────────────────────────────────────
        public Action<string, string>?     PushLog    { get; set; }
        public Action<int, int, int, int>? OnProgress { get; set; }
        public Action<int, int, int>?      OnComplete { get; set; }

        public string GetName() => "LemoineTools.Tools.Sheets.AlignSheetViews";

        // Issues raised for the target sheet currently being processed. Reset per sheet; drives
        // whether that sheet's roll-up line reads clean or points at the detail above it.
        private int _sheetIssues;

        /// <summary>
        /// The run log is one roll-up line per target sheet plus everything that actually went
        /// wrong. Successes are expected and are not narrated: there is no "info" tone here, so a
        /// line that exists is a line worth reading.
        /// </summary>
        private void Log(string t, string s)
        {
            if (s == "warn" || s == "fail") _sheetIssues++;
            PushLog?.Invoke(t, s);
        }

        // Bases are treated as parallel / matching above these cosine thresholds.
        private const double ParallelDot     = 0.999;
        private const double OrientationDot  = 0.9999;
        private const double MinCropOffsetFt = 1e-4;   // Revit rejects 0 / negative annotation-crop offsets.
        private const double PlacementTolFt  = 1e-6;   // sheet feet — below this a box is where it should be.
        private const double CurveMatchTolFt = 1e-6;

        // ── Per-run caches, cleared in Execute's finally ──────────────────────
        // The source side of a grid comparison is identical for every target aligned to the same
        // reference view, and a view-scoped FilteredElementCollector forces Revit to compute that
        // view's visible element set — so aligning 50 targets to one reference used to redo that
        // work 50 times over.
        private readonly Dictionary<long, SourceViewGrids> _sourceGrids = new Dictionary<long, SourceViewGrids>();
        private Category? _gridCategory;
        private bool      _isWorkshared;

        public void Execute(UIApplication app)
        {
            var onProgress = OnProgress ?? ((p, a, b, c) => { });
            var onComplete = OnComplete ?? ((a, b, c) => { });

            int pass = 0, fail = 0, skip = 0;
            long issues0 = DiagnosticsLog.IssueCount;

            try
            {
                var doc = app.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    Log(AppStrings.T("testing.alignSheetViews.log.noDoc"), "fail");
                    onComplete(0, 1, 0);
                    return;
                }

                // Capture every source sheet's reference viewports.
                var sourceSheets = SourceSheetIds
                    .Where(id => id != null && id != ElementId.InvalidElementId)
                    .Distinct()
                    .Select(id => doc.GetElement(id) as ViewSheet)
                    .Where(s => s != null)
                    .Cast<ViewSheet>()
                    .ToList();

                if (sourceSheets.Count == 0)
                {
                    Log(AppStrings.T("testing.alignSheetViews.log.noSourceSheets"), "fail");
                    onComplete(0, 1, 0);
                    return;
                }

                var sourceById = new HashSet<long>(sourceSheets.Select(s => s.Id.Value));
                var sources = new List<SourceSheet>();
                foreach (var s in sourceSheets)
                {
                    var entries = CaptureSheet(doc, s);
                    string label = $"{s.SheetNumber} - {s.Name}";
                    if (entries.Count == 0)
                    {
                        Log(AppStrings.T("testing.alignSheetViews.log.sourceNoViews", label), "warn");
                        continue;
                    }
                    sources.Add(new SourceSheet(label, entries));
                }
                if (sources.Count == 0)
                {
                    Log(AppStrings.T("testing.alignSheetViews.log.noSourcesWithViews"), "fail");
                    onComplete(0, 1, 0);
                    return;
                }

                // Targets, de-duplicated and never including any source sheet.
                var targets = TargetSheetIds
                    .Where(id => id != null && id != ElementId.InvalidElementId && !sourceById.Contains(id.Value))
                    .Distinct()
                    .Select(id => doc.GetElement(id) as ViewSheet)
                    .Where(s => s != null)
                    .Cast<ViewSheet>()
                    .ToList();

                if (targets.Count == 0)
                {
                    Log(AppStrings.T("testing.alignSheetViews.log.noTargets"), "fail");
                    onComplete(0, 1, 0);
                    return;
                }

                if (InheritGridExtents)
                {
                    _gridCategory = TryGridCategory(doc);
                    _isWorkshared = doc.IsWorkshared;
                }

                using (var tx = new Transaction(doc, "Lemoine — Align Sheet Views"))
                {
                    ConfigureFailures(tx);
                    tx.Start();
                    (pass, fail, skip) = DriveTargets(doc, sources, targets, onProgress);
                    tx.Commit();
                }

                long issues = DiagnosticsLog.IssuesSince(issues0);
                if (issues > 0)
                    Log(AppStrings.T("testing.alignSheetViews.log.issuesRecorded", issues), "warn");

                Log(AppStrings.T("testing.alignSheetViews.log.done", pass, fail, skip, targets.Count),
                    fail > 0 ? "warn" : "pass");
                onProgress(100, pass, fail, skip);
                onComplete(pass, fail, skip);
            }
            catch (Exception ex)
            {
                Log(AppStrings.T("testing.alignSheetViews.log.fatalError", ex.Message), "fail");
                DiagnosticsLog.Error("AlignSheetViews.Execute", ex);
                onComplete(pass, fail + 1, skip);
            }
            finally
            {
                // Session-long static handler — drop the run's payload and caches.
                TargetSheetIds = new List<ElementId>();
                SourceSheetIds = new List<ElementId>();
                _sourceGrids.Clear();
                _gridCategory = null;
                _isWorkshared = false;
            }
        }

        // ── Per-target-sheet driver ───────────────────────────────────────────
        /// <summary>
        /// Walks the target sheets, then finishes the run with the passes that genuinely need live
        /// geometry. Regenerates are the dominant cost of this tool, so there are only two kinds
        /// left: one per sheet when grid extent modes had to be switched (Revit's own ordering
        /// requirement before a 2D curve can be written), and one for the whole run at the end.
        /// The per-viewport alignment needs none at all — see <see cref="TryAlign"/>.
        /// </summary>
        private (int pass, int fail, int skip) DriveTargets(
            Document doc, List<SourceSheet> sources, List<ViewSheet> targets,
            Action<int, int, int, int> onProgress)
        {
            int p = 0, f = 0, s = 0;
            int total = targets.Count;
            var allPairs      = new List<MatchedPair>();
            var pendingElbows = new List<PendingElbow>();

            for (int i = 0; i < targets.Count; i++)
            {
                if (RunState.CancelRequested)
                {
                    Log(AppStrings.T("testing.alignSheetViews.log.stoppedByUser", i, total), "warn");
                    break;   // the run-end passes below still finish everything already queued
                }

                var sheet = targets[i];
                var label = $"{sheet.SheetNumber} - {sheet.Name}";
                _sheetIssues = 0;
                int alignedHere = 0;

                var targetEntries = CaptureSheet(doc, sheet);
                if (targetEntries.Count == 0)
                {
                    Log(AppStrings.T("testing.alignSheetViews.log.noPlaceableViews", label), "fail");
                    f++;
                    onProgress(Pct(i + 1, total), p, f, s);
                    continue;
                }

                // Pick the best source sheet for this target. One source is the common case and
                // needs no scoring at all.
                SourceSheet? bestSource;
                SheetMatch?  bestMatch;
                if (sources.Count == 1)
                {
                    bestSource = sources[0];
                    bestMatch  = MatchSheet(bestSource.Entries, targetEntries);
                }
                else
                {
                    bestSource = null; bestMatch = null;
                    foreach (var src in sources)
                    {
                        var m = MatchSheet(src.Entries, targetEntries);
                        if (bestMatch == null || m.Score > bestMatch.Score)
                        {
                            bestMatch  = m;
                            bestSource = src;
                        }
                    }
                }

                if (bestSource == null || bestMatch == null || bestMatch.Pairs.Count == 0)
                {
                    Log(AppStrings.T("testing.alignSheetViews.log.noCounterpart", label, sources.Count), "fail");
                    f++;
                    onProgress(Pct(i + 1, total), p, f, s);
                    continue;
                }

                // Report the gaps for the chosen source.
                foreach (var miss in bestMatch.Missing)
                {
                    Log(AppStrings.T("testing.alignSheetViews.log.missing", label, miss.ViewName), "fail");
                    DiagnosticsLog.Warn("AlignSheetViews", $"No counterpart for '{miss.ViewName}' on sheet {sheet.Id.Value}.");
                    f++;
                }
                foreach (var amb in bestMatch.Ambiguous)
                {
                    Log(AppStrings.T("testing.alignSheetViews.log.ambiguous", label, amb.src.ViewName, amb.a.ViewName, amb.b.ViewName), "fail");
                    DiagnosticsLog.Warn("AlignSheetViews", $"Ambiguous match for '{amb.src.ViewName}' on sheet {sheet.Id.Value}.");
                    f++;
                }
                foreach (var extra in bestMatch.Extra)
                {
                    Log(AppStrings.T("testing.alignSheetViews.log.extra", label, extra.ViewName), "warn");
                    s++;
                }

                // Quality warnings per matched pair (anchor still aligns).
                foreach (var pr in bestMatch.Pairs)
                {
                    pr.Label = label;
                    if (pr.Target.Scale != pr.Source.Scale)
                        Log(AppStrings.T("testing.alignSheetViews.log.scaleDiffers", label, pr.Target.ViewName, pr.Target.Scale, pr.Source.Scale), "warn");
                    if (!OrientationMatches(pr.Source, pr.Target))
                        Log(AppStrings.T("testing.alignSheetViews.log.orientationDiffers", label, pr.Target.ViewName), "warn");
                    if (pr.Target.Rotation != ViewportRotation.None)
                        Log(AppStrings.T("testing.alignSheetViews.log.rotated", label, pr.Target.ViewName, pr.Target.Rotation), "warn");
                }

                // ── Phase A — every write whose result the alignment can predict ──
                if (InheritScopeBox)
                {
                    foreach (var pr in bestMatch.Pairs)
                    {
                        if (pr.Source.ScopeBoxId == ElementId.InvalidElementId) continue;
                        if (AssignScopeBox(doc, pr, label)) pr.GotScopeBox = true;
                    }
                }
                if (InheritCropSize)
                    foreach (var pr in bestMatch.Pairs) InheritCropGeometry(doc, pr, label);
                if (InheritCropVisibility)
                    foreach (var pr in bestMatch.Pairs) SetCropVisibility(doc, pr, label);

                List<GridPlan>? gridPlans = null;
                if (InheritGridExtents)
                {
                    gridPlans = PrepareGrids(doc, bestMatch.Pairs, label);
                    // One regen for the whole sheet so the extent-mode switches are live before any
                    // curve is written — Revit's ordering requirement, and the only regen left
                    // inside the loop. Never per grid and never per view.
                    if (gridPlans.Any(g => g.Writes.Count > 0)) doc.Regenerate();
                }

                // ── Phase B — the writes that had to follow that regen ──
                if (gridPlans != null)
                {
                    foreach (var plan in gridPlans)
                    {
                        WriteGridCurves(plan);
                        QueueElbows(plan, pendingElbows);
                        ReportGridTally(plan, label);
                    }
                }

                foreach (var pr in bestMatch.Pairs)
                {
                    if (TryAlign(doc, pr))
                    {
                        MatchTitleLineLength(doc, pr);
                        allPairs.Add(pr);
                        alignedHere++;
                        p++;
                    }
                    else
                    {
                        Log(AppStrings.T("testing.alignSheetViews.log.failedMove", label, pr.Target.ViewName), "fail");
                        f++;
                    }
                }

                // One line per sheet. A clean sheet says so and nothing else; a sheet with problems
                // points at the detail already printed above it rather than repeating it. With more
                // than one reference in play the line also names the one this sheet was aligned to —
                // otherwise a target matched to the wrong reference is invisible.
                bool nameRef = sources.Count > 1;
                Log(_sheetIssues == 0
                        ? (nameRef
                            ? AppStrings.T("testing.alignSheetViews.log.sheetOkRef", label, alignedHere, bestSource.Label)
                            : AppStrings.T("testing.alignSheetViews.log.sheetOk", label, alignedHere))
                        : (nameRef
                            ? AppStrings.T("testing.alignSheetViews.log.sheetWithIssuesRef", label, alignedHere, _sheetIssues, bestSource.Label)
                            : AppStrings.T("testing.alignSheetViews.log.sheetWithIssues", label, alignedHere, _sheetIssues)),
                    _sheetIssues == 0 ? "pass" : "warn");

                onProgress(Pct(i + 1, total), p, f, s);
            }

            // ── Run end — the one place live geometry is genuinely required ──
            if (allPairs.Count > 0)
            {
                // Everything above wrote without reading back: boxes are placed, titles have their
                // line lengths, leaders exist. This single regenerate makes all of it live.
                doc.Regenerate();

                PositionElbows(pendingElbows);

                // Only now is the real crop geometry available to check the predictions against.
                int corrected = VerifyPlacements(doc, allPairs);

                // A correction moves the box, and a title travels with its box — so a corrected
                // viewport's label outline is stale and has to be recomputed before it is read.
                if (corrected > 0) doc.Regenerate();

                AlignTitleOffsets(doc, allPairs);
            }

            return (p, f, s);
        }

        // ── Inheritance: scope box (applied before alignment) ─────────────────
        private bool AssignScopeBox(Document doc, MatchedPair pr, string label)
        {
            try
            {
                if (!(doc.GetElement(pr.Target.ViewId) is View tv)) return false;
                var prm = tv.get_Parameter(BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP);
                if (prm == null || prm.IsReadOnly) return false;
                if (prm.AsElementId() == pr.Source.ScopeBoxId) return false; // already correct
                tv.CropBoxActive = true;          // a scope box governs the crop
                if (!prm.Set(pr.Source.ScopeBoxId)) return false;
                PredictScopeBoxCrop(pr);
                return true;
            }
            catch (Exception ex)
            {
                Log(AppStrings.T("testing.alignSheetViews.log.couldNotAssignScope", label, pr.Target.ViewName), "warn");
                DiagnosticsLog.Swallowed($"AlignSheetViews: scope box on view {pr.Target.ViewId.Value}", ex);
                return false;
            }
        }

        /// <summary>
        /// Derives the crop the target view now carries, without regenerating to read it.
        ///
        /// The target has just been given the scope box the SOURCE view already carries, so both
        /// crops cover the same world footprint — and a footprint has exactly one centre. The source
        /// view's crop-centre anchor therefore lands precisely on the target's new crop centre,
        /// which is the only property <see cref="FootprintCentre"/> takes from the rectangle. (The
        /// width and height are recorded for completeness; because only the centre is ever used,
        /// the prediction stays correct even when the two views' in-plane axes are rotated relative
        /// to one another and the rectangles are not the same shape in local coordinates.)
        /// </summary>
        private static void PredictScopeBoxCrop(MatchedPair pr)
        {
            VpEntry src = pr.Source;
            CropState cs = pr.Predicted;

            XYZ local = cs.Transform.Inverse.OfPoint(AnchorWorld(src));
            double hw = (src.CropMax.X - src.CropMin.X) / 2.0;
            double hh = (src.CropMax.Y - src.CropMin.Y) / 2.0;

            // Depth stays the target's own — it is a different level, which is the whole point.
            cs.Min = new XYZ(local.X - hw, local.Y - hh, cs.Min.Z);
            cs.Max = new XYZ(local.X + hw, local.Y + hh, cs.Max.Z);
        }

        // ── Inheritance: crop size + annotation crop ──────────────────────────
        private void InheritCropGeometry(Document doc, MatchedPair pr, string label)
        {
            try
            {
                if (!(doc.GetElement(pr.Target.ViewId) is View tv)) return;

                // Crop size — resize the target crop to the source crop's dimensions, keeping the
                // target's own crop centre (alignment then centres it on the shared anchor).
                // Skipped whenever a scope box governs the crop region: one the view inherited this
                // run, OR one it ALREADY carried. The second case used to slip through, and writing
                // CropBox underneath a scope box fights it and yields an unpredictable crop.
                bool scopeGoverned = pr.GotScopeBox;
                if (!scopeGoverned)
                {
                    var tsb = tv.get_Parameter(BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP);
                    scopeGoverned = tsb != null
                                 && tsb.StorageType == StorageType.ElementId
                                 && tsb.AsElementId() != ElementId.InvalidElementId;
                }

                if (pr.Source.Scale != pr.Target.Scale)
                    Log(AppStrings.T("testing.alignSheetViews.log.annoScaleDiffers", label, pr.Target.ViewName), "warn");

                if (!scopeGoverned)
                {
                    BoundingBoxXYZ tcb = tv.CropBox;
                    if (tcb?.Transform != null)
                    {
                        double sw = pr.Source.CropMax.X - pr.Source.CropMin.X;
                        double sh = pr.Source.CropMax.Y - pr.Source.CropMin.Y;
                        if (sw > MinCropOffsetFt && sh > MinCropOffsetFt)
                        {
                            double cx = (tcb.Min.X + tcb.Max.X) / 2.0;
                            double cy = (tcb.Min.Y + tcb.Max.Y) / 2.0;
                            var nb = new BoundingBoxXYZ { Transform = tcb.Transform };
                            nb.Min = new XYZ(cx - sw / 2.0, cy - sh / 2.0, tcb.Min.Z);
                            nb.Max = new XYZ(cx + sw / 2.0, cy + sh / 2.0, tcb.Max.Z);
                            tv.CropBoxActive = true;
                            tv.CropBox = nb;

                            // A resize keeps the crop's transform and its centre, so the prediction
                            // is simply the rectangle we just wrote.
                            pr.Predicted.Min = nb.Min;
                            pr.Predicted.Max = nb.Max;
                        }
                    }
                }

                // Annotation crop offsets — replicate the source's annotation crop margins.
                InheritAnnotationCrop(doc, pr, tv, label);
            }
            catch (Exception ex)
            {
                Log(AppStrings.T("testing.alignSheetViews.log.couldNotMatchCropSize", label, pr.Target.ViewName), "warn");
                DiagnosticsLog.Swallowed($"AlignSheetViews: crop size on view {pr.Target.ViewId.Value}", ex);
            }
        }

        private void InheritAnnotationCrop(Document doc, MatchedPair pr, View tv, string label)
        {
            try
            {
                if (!(doc.GetElement(pr.Source.ViewId) is View sv)) return;
                var srcActive = sv.get_Parameter(BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE);
                if (srcActive == null || srcActive.AsInteger() != 1) return; // source has no annotation crop

                ViewCropRegionShapeManager sm = sv.GetCropRegionShapeManager();
                double top = sm.TopAnnotationCropOffset, bot = sm.BottomAnnotationCropOffset;
                double left = sm.LeftAnnotationCropOffset, right = sm.RightAnnotationCropOffset;

                var tgtActive = tv.get_Parameter(BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE);
                if (tgtActive == null || tgtActive.IsReadOnly) return;
                tv.CropBoxActive = true;
                if (!tgtActive.Set(1))
                {
                    // The alignment predicts this view's footprint from the annotation crop it is
                    // about to carry, so a refused activation cannot be shrugged off: leave the
                    // captured (inactive) state in place rather than computing a placement from a
                    // crop the view does not actually have, and say so.
                    Log(AppStrings.T("testing.alignSheetViews.log.couldNotMatchAnnoCrop", label, pr.Target.ViewName), "warn");
                    return;
                }

                ViewCropRegionShapeManager tm = tv.GetCropRegionShapeManager();
                tm.TopAnnotationCropOffset    = Math.Max(MinCropOffsetFt, top);
                tm.BottomAnnotationCropOffset = Math.Max(MinCropOffsetFt, bot);
                tm.LeftAnnotationCropOffset   = Math.Max(MinCropOffsetFt, left);
                tm.RightAnnotationCropOffset  = Math.Max(MinCropOffsetFt, right);

                // What was written is what the alignment will read.
                CropState cs = pr.Predicted;
                cs.AnnoActive = true;
                cs.AnnoTop    = Math.Max(MinCropOffsetFt, top);
                cs.AnnoBottom = Math.Max(MinCropOffsetFt, bot);
                cs.AnnoLeft   = Math.Max(MinCropOffsetFt, left);
                cs.AnnoRight  = Math.Max(MinCropOffsetFt, right);
            }
            catch (Exception ex)
            {
                Log(AppStrings.T("testing.alignSheetViews.log.couldNotMatchAnnoCrop", label, pr.Target.ViewName), "warn");
                DiagnosticsLog.Swallowed($"AlignSheetViews: annotation crop on view {pr.Target.ViewId.Value}", ex);
            }
        }

        // ── Inheritance: crop-region visibility ───────────────────────────────
        private void SetCropVisibility(Document doc, MatchedPair pr, string label)
        {
            try
            {
                if (!(doc.GetElement(pr.Target.ViewId) is View tv)) return;
                tv.CropBoxVisible = pr.Source.CropBoxVisible;
            }
            catch (Exception ex)
            {
                Log(AppStrings.T("testing.alignSheetViews.log.couldNotMatchCropVis", label, pr.Target.ViewName), "warn");
                DiagnosticsLog.Swallowed($"AlignSheetViews: crop visibility on view {pr.Target.ViewId.Value}", ex);
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // Grid 2D (view-specific) extent inheritance
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Reads the source, reconciles visibility, mirrors bubbles and switches the target's extent
        /// modes for every pair on one sheet — everything that must happen BEFORE the sheet's single
        /// regenerate. The curve writes it queues are executed by <see cref="WriteGridCurves"/>
        /// afterwards.
        ///
        /// A datum's extent mode is per view AND per end, so this is not a plain get/set. Four rules
        /// the earlier versions each got wrong:
        ///
        /// <list type="bullet">
        /// <item>The target's ends must be switched to ViewSpecific <b>before</b> the curve is
        /// validated or written. <c>IsCurveValidInView</c> answers for the mode the view is actually
        /// in, so asking it first rejected every grid that had never been dragged in the target
        /// view — i.e. exactly the grids this feature exists to fix.</item>
        /// <item>A 2D extent curve lies in its <b>view's</b> plane, so a curve read from one level's
        /// plan has to be translated onto the target view's plane before it can be written there.
        /// Cross-level alignment is this tool's headline use case.</item>
        /// <item>A source on MODEL extents has nothing view-specific to copy — its displayed extent
        /// IS the grid's shared 3D extent, identical in every view. Matching it means resetting the
        /// target's ends to Model, NOT writing that shared curve in as a 2D override: that changes
        /// nothing visible while permanently detaching the target from scope-box/model updates.</item>
        /// <item>Matching extents still leave the head missing if the source shows a bubble and the
        /// target does not, so bubble visibility is mirrored per end as well.</item>
        /// </list>
        /// </summary>
        private List<GridPlan> PrepareGrids(Document doc, List<MatchedPair> pairs, string label)
        {
            var plans = new List<GridPlan>();

            foreach (var pr in pairs)
            {
                var plan = new GridPlan(pr);
                plans.Add(plan);
                try
                {
                    if (!(doc.GetElement(pr.Source.ViewId) is View sv)) continue;
                    if (!(doc.GetElement(pr.Target.ViewId) is View tv)) continue;
                    plan.TargetView = tv;

                    SourceViewGrids src = GetSourceGrids(doc, sv);
                    if (src.Grids.Count == 0)
                    {
                        // A zero result names itself: a silent empty pass is indistinguishable from
                        // a broken one, and the user explicitly asked for grid inheritance here.
                        Log(AppStrings.T("testing.alignSheetViews.log.noGrids", label, pr.Source.ViewName), "warn");
                        continue;
                    }

                    // A grid the target shows and the source does not can never be matched. Name it
                    // rather than leaving a difference the user has to spot on the plot.
                    plan.TargetOnly = new FilteredElementCollector(doc, tv.Id)
                        .OfClass(typeof(Grid))
                        .Cast<Grid>()
                        .Count(g => !src.GridIds.Contains(g.Id.Value));

                    // Everything that is a property of the VIEW PAIR rather than of an individual
                    // grid is resolved once here. Doing it per grid meant re-walking the target's
                    // filter list — and re-reading the source's — for every gridline in the view.
                    var vis = BuildVisibilityContext(doc, sv, tv, src);
                    ApplyViewLevelVisibility(tv, vis);

                    GridTally t = plan.Tally;

                    foreach (var sg in src.Grids)
                    {
                        Grid g = sg.Grid;
                        try
                        {
                            // Visibility is a HINT, never a veto. CanBeVisibleInView rejects grids the
                            // user can plainly see, and gating on it skipped the trim outright — the same
                            // mistake as trusting IsCurveValidInView. Reconcile whatever DIFFERS between
                            // the two views, then do the work regardless and let Revit's own exception be
                            // the verdict if it really cannot be done.
                            ReconcileGridVisibility(g, sg, tv, vis, t, label, pr);

                            // Revit refuses EVERY datum call for a grid it will not display here —
                            // extent type, curves, leaders, the lot — so carrying on raised the same
                            // exception from a dozen call sites for one root cause. Now the reconcile
                            // gets its chance to fix whatever differs from the source, and if Revit
                            // still says no after that, one refusal settles the whole grid.
                            // This is not the old CanBeVisibleInView veto: that ran BEFORE any repair
                            // and skipped grids the user could plainly see.
                            if (!CanBeVisible(g, tv))
                            {
                                t.NotShowable++;
                                DiagnosticsLog.Warn("AlignSheetViews",
                                    $"Grid {g.Id.Value} ('{g.Name}') — Revit will not display it in view {tv.Id.Value}; skipped after the visibility reconcile.");
                                continue;
                            }

                            ReadExtentTypes(g, tv, out DatumExtentType m0, out DatumExtentType m1);
                            Curve? existing = CurveForMode(g, tv, m0, m1);

                            var vg = new VisibleGrid(sg) { TargetDisplayed = existing };
                            plan.Visible.Add(vg);

                            // Bubbles are a property of their own, independent of the extents, so they
                            // are matched for EVERY visible grid — before the curve work and regardless
                            // of how it turns out. Doing this only on the success paths is what made
                            // heads move on some grids and not others in the same view.
                            MirrorBubbles(g, sg, tv);

                            // Source on model extents: match it by putting the target back on model too.
                            if (!sg.UsesViewSpecific)
                            {
                                if (m0 != DatumExtentType.Model || m1 != DatumExtentType.Model)
                                {
                                    g.SetDatumExtentType(DatumEnds.End0, tv, DatumExtentType.Model);
                                    g.SetDatumExtentType(DatumEnds.End1, tv, DatumExtentType.Model);
                                    t.Changed++;
                                }
                                continue;
                            }

                            // No Model fallback here: falling back would copy the grid's shared 3D
                            // extent and report it as a successful trim, which is a silent wrong answer.
                            if (sg.ViewSpecificCurve == null)
                            {
                                t.NoSourceCurve++;
                                DiagnosticsLog.Warn("AlignSheetViews",
                                    $"Grid {g.Id.Value} ('{g.Name}') claims view-specific extents in source view {sv.Id.Value} but returned no curve — not trimmed.");
                                continue;
                            }
                            // SetCurveInView takes a single curve, so a multi-segment grid can only carry
                            // its first segment across. Say so rather than dropping the rest silently.
                            if (sg.CurveCount > 1) t.MultiSegment++;

                            // Revit rejects an unbound curve outright ("the curve is unbound or not
                            // coincident"), so catch it here where it can be named.
                            if (sg.ViewSpecificCurve is Line sl && !sl.IsBound)
                            {
                                t.Unbound++;
                                DiagnosticsLog.Warn("AlignSheetViews",
                                    $"Grid {g.Id.Value} ('{g.Name}') returned an unbound line from source view {sv.Id.Value} — not trimmed.");
                                continue;
                            }

                            // The target's OWN current curve is the reference plane: whatever Revit
                            // reports for this datum in this view is by definition coincident with the
                            // datum's line there, which is the exact test SetCurveInView applies.
                            Curve c = CoincideWith(sg.ViewSpecificCurve, existing, tv);

                            if (existing != null && CurvesMatch(existing, c))
                            {
                                t.AlreadyMatching++;
                                continue;
                            }

                            g.SetDatumExtentType(DatumEnds.End0, tv, DatumExtentType.ViewSpecific);
                            g.SetDatumExtentType(DatumEnds.End1, tv, DatumExtentType.ViewSpecific);
                            plan.Writes.Add(new GridWrite(g, c, m0, m1, vg));
                        }
                        catch (Exception ex)
                        {
                            t.Errored++;
                            DiagnosticsLog.Swallowed($"AlignSheetViews: prepare grid {g.Id.Value} for view {tv.Id.Value}", ex);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log(AppStrings.T("testing.alignSheetViews.log.couldNotTrimGrids", label, pr.Target.ViewName), "warn");
                    DiagnosticsLog.Swallowed($"AlignSheetViews: trim grids on view {pr.Target.ViewId.Value}", ex);
                }
            }

            return plans;
        }

        /// <summary>Writes the curves queued by <see cref="PrepareGrids"/>, after the sheet's regenerate
        /// has made the extent-mode switches live.</summary>
        private void WriteGridCurves(GridPlan plan)
        {
            View? tv = plan.TargetView;
            if (tv == null) return;

            foreach (var w in plan.Writes)
            {
                try
                {
                    // No IsCurveValidInView pre-check: it is advisory only — a false negative is
                    // common on a grid that has never been dragged in this view — and its verdict
                    // was logged and then ignored. Revit's own exception is the authority.
                    w.Grid.SetCurveInView(DatumExtentType.ViewSpecific, tv, w.Curve);
                    w.Target.WrittenCurve = w.Curve;
                    plan.Tally.Changed++;
                }
                catch (Exception ex)
                {
                    plan.Tally.Rejected++;
                    DiagnosticsLog.Swallowed($"AlignSheetViews: set curve for grid {w.Grid.Id.Value} in view {tv.Id.Value}", ex);
                    // A failed restore leaves the end in 2D mode with no curve written — a
                    // half-change the user has to know about, not just a diagnostics entry.
                    if (!RestoreEnds(w, tv))
                        Log(AppStrings.T("testing.alignSheetViews.log.gridsRestoreFailed",
                                         plan.Pair.Label, plan.Pair.Target.ViewName, w.Grid.Name), "warn");
                }
            }
        }

        /// <summary>Every outcome gets a line — a zero result always names its reason.</summary>
        private void ReportGridTally(GridPlan plan, string label)
        {
            GridTally t = plan.Tally;
            MatchedPair pr = plan.Pair;

            // Changed vs already-matching are split deliberately: a single "N trimmed" that counted
            // no-op writes is exactly what made a broken run look like a successful one. Only the
            // zero case is worth a line — a run that did the work does not need narrating.
            if (t.Changed == 0 && t.AlreadyMatching == 0 && plan.Visible.Count > 0)
                Log(AppStrings.T("testing.alignSheetViews.log.gridsResult",
                                 label, pr.Target.ViewName, t.Changed, t.AlreadyMatching), "warn");

            if (t.Blocked > 0)
                Log(AppStrings.T("testing.alignSheetViews.log.gridsBlocked", label, pr.Target.ViewName, t.Blocked), "warn");
            if (t.NotShowable > 0)
                Log(AppStrings.T("testing.alignSheetViews.log.gridsNotShowable", label, pr.Target.ViewName, t.NotShowable), "warn");
            if (t.NoSourceCurve > 0)
                Log(AppStrings.T("testing.alignSheetViews.log.gridsNoSourceCurve", label, pr.Source.ViewName, t.NoSourceCurve), "warn");
            if (t.Rejected > 0)
                Log(AppStrings.T("testing.alignSheetViews.log.gridsRejected", label, pr.Target.ViewName, t.Rejected), "warn");
            if (t.Errored > 0)
                Log(AppStrings.T("testing.alignSheetViews.log.gridsErrored", label, pr.Target.ViewName, t.Errored), "warn");
            if (t.MultiSegment > 0)
                Log(AppStrings.T("testing.alignSheetViews.log.gridsMultiSegment", label, pr.Target.ViewName, t.MultiSegment), "warn");
            if (t.Unbound > 0)
                Log(AppStrings.T("testing.alignSheetViews.log.gridsUnbound", label, pr.Source.ViewName, t.Unbound), "warn");
            if (plan.TargetOnly > 0)
                Log(AppStrings.T("testing.alignSheetViews.log.gridsTargetOnly", label, pr.Target.ViewName, plan.TargetOnly), "warn");
        }

        // ── Source-side grid state, cached per source view for the whole run ──
        private SourceViewGrids GetSourceGrids(Document doc, View sv)
        {
            if (_sourceGrids.TryGetValue(sv.Id.Value, out var cached)) return cached;

            var result = new SourceViewGrids();
            var grids = new FilteredElementCollector(doc, sv.Id)
                .OfClass(typeof(Grid))
                .Cast<Grid>()
                .ToList();

            foreach (var g in grids)
            {
                result.GridIds.Add(g.Id.Value);
                var sg = new SourceGrid(g);
                try
                {
                    ReadExtentTypes(g, sv, out DatumExtentType e0, out DatumExtentType e1);
                    sg.UsesViewSpecific = e0 == DatumExtentType.ViewSpecific || e1 == DatumExtentType.ViewSpecific;

                    if (sg.UsesViewSpecific)
                    {
                        IList<Curve>? cs = TryGetCurves(g, DatumExtentType.ViewSpecific, sv);
                        sg.CurveCount       = cs?.Count ?? 0;
                        sg.ViewSpecificCurve = cs != null && cs.Count > 0 ? cs[0] : null;
                    }
                    sg.DisplayedCurve = CurveForMode(g, sv, e0, e1);
                    sg.Hidden         = g.IsHidden(sv);

                    foreach (var end in new[] { DatumEnds.End0, DatumEnds.End1 })
                    {
                        bool first = end == DatumEnds.End0;
                        try
                        {
                            bool bubble = g.IsBubbleVisibleInView(end, sv);
                            if (first) sg.Bubble0 = bubble; else sg.Bubble1 = bubble;
                        }
                        catch (Exception ex)
                        {
                            DiagnosticsLog.Swallowed($"AlignSheetViews: source bubble {end} of grid {g.Id.Value} in view {sv.Id.Value}", ex);
                        }

                        Leader? lead = TryGetLeader(g, end, sv);
                        if (lead == null) continue;
                        try
                        {
                            if (first) { sg.HasLeader0 = true; sg.Elbow0 = lead.Elbow; sg.Tip0 = lead.End; }
                            else       { sg.HasLeader1 = true; sg.Elbow1 = lead.Elbow; sg.Tip1 = lead.End; }
                        }
                        catch (Exception ex)
                        {
                            DiagnosticsLog.Swallowed($"AlignSheetViews: source leader geometry {end} of grid {g.Id.Value} in view {sv.Id.Value}", ex);
                        }
                    }
                }
                catch (Exception ex)
                {
                    DiagnosticsLog.Swallowed($"AlignSheetViews: capture source grid {g.Id.Value} in view {sv.Id.Value}", ex);
                }
                result.Grids.Add(sg);
            }

            // The source view's own filter and category state — identical for every target aligned
            // to this reference, so it is read once rather than once per grid per target.
            try
            {
                foreach (var fid in sv.GetFilters())
                    if (!sv.GetFilterVisibility(fid)) result.HiddenFilterIds.Add(fid.Value);
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed($"AlignSheetViews: read filters on source view {sv.Id.Value}", ex);
            }
            try
            {
                result.CategoryHidden = _gridCategory != null && sv.GetCategoryHidden(_gridCategory.Id);
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed($"AlignSheetViews: Grids category visibility on source view {sv.Id.Value}", ex);
            }

            _sourceGrids[sv.Id.Value] = result;
            return result;
        }

        /// <summary>
        /// Resolves, once per source/target view pair, everything about grid visibility that is a
        /// property of the two VIEWS rather than of an individual grid.
        ///
        /// Only settings that DIFFER between the two views are collected. A filter, hidden category
        /// or hidden workset that is identical on both views cannot explain why a grid shows in one
        /// and not the other, so naming it is a false lead — which is exactly what the first version
        /// of this did, blaming a filter that was present on the source view too.
        ///
        /// A candidate filter is one hidden in the target, not hidden in the source, and naming the
        /// Grids category. Whether it actually catches a given grid is settled per grid through the
        /// filter's own rules; a filter that merely covers the category but whose rules the grid
        /// fails is never blamed.
        /// </summary>
        private VisibilityContext BuildVisibilityContext(Document doc, View sv, View tv, SourceViewGrids src)
        {
            var ctx = new VisibilityContext();
            try
            {
                if (_gridCategory != null)
                    ctx.ClearCategory = tv.GetCategoryHidden(_gridCategory.Id) && !src.CategoryHidden;
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed($"AlignSheetViews: Grids category visibility on view {tv.Id.Value}", ex);
            }

            try
            {
                foreach (var fid in tv.GetFilters())
                {
                    if (tv.GetFilterVisibility(fid)) continue;
                    if (src.HiddenFilterIds.Contains(fid.Value)) continue;   // identical on both — cannot be the cause
                    if (!(doc.GetElement(fid) is ParameterFilterElement pf)) continue;
                    if (!NamesGrids(pf)) continue;
                    ctx.CandidateFilters.Add(pf);
                }
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed($"AlignSheetViews: read filters on view {tv.Id.Value}", ex);
            }

            ctx.SourceView = sv;
            return ctx;
        }

        /// <summary>Applies the view-level half of the reconcile once, not once per gridline.</summary>
        private void ApplyViewLevelVisibility(View tv, VisibilityContext ctx)
        {
            if (!ctx.ClearCategory || _gridCategory == null) return;
            try
            {
                if (tv.CanCategoryBeHidden(_gridCategory.Id)) tv.SetCategoryHidden(_gridCategory.Id, false);
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed($"AlignSheetViews: show Grids category in view {tv.Id.Value}", ex);
            }
        }

        /// <summary>
        /// Brings one grid's visibility in the target view into line with the source view, and names
        /// what it could not change. A filter that genuinely differs is reported but not flipped: its
        /// visibility governs every category it names, so clearing it would silently reveal
        /// everything else it hides.
        /// </summary>
        private void ReconcileGridVisibility(Grid g, SourceGrid sg, View tv, VisibilityContext ctx,
                                             GridTally t, string label, MatchedPair pr)
        {
            var blockers = new List<string>();
            try
            {
                // 1 — element hidden in the target but not the source.
                if (g.IsHidden(tv) && !sg.Hidden)
                    tv.UnhideElements(new List<ElementId> { g.Id });

                // 2 — the grid's workset hidden in the target but not the source.
                if (_isWorkshared)
                {
                    WorksetId ws = g.WorksetId;
                    if (ws != null && ctx.WorksetHiddenInTargetOnly(ws, tv))
                        tv.SetWorksetVisibility(ws, WorksetVisibility.Visible);
                }

                // 3 — a filter that hides THIS grid in the target and does not do so in the source.
                foreach (var pf in ctx.CandidateFilters)
                {
                    if (!FilterCatches(pf, g)) continue;
                    blockers.Add(AppStrings.T("testing.alignSheetViews.log.gridCauseFilter", pf.Name));
                }
            }
            catch (Exception ex)
            {
                blockers.Add(AppStrings.T("testing.alignSheetViews.log.gridCauseTemplate", TemplateName(tv)));
                DiagnosticsLog.Swallowed($"AlignSheetViews: reconcile visibility of grid {g.Id.Value} in view {tv.Id.Value}", ex);
            }

            if (blockers.Count > 0)
            {
                t.Blocked++;
                Log(AppStrings.T("testing.alignSheetViews.log.gridBlocked",
                                 label, pr.Target.ViewName, g.Name, string.Join(", ", blockers)), "warn");
            }
        }

        /// <summary>True when the filter names the Grids category at all — the cheap half of the
        /// test, done once per filter rather than once per grid.</summary>
        private static bool NamesGrids(ParameterFilterElement pf)
        {
            try { return pf.GetCategories().Any(c => c.Value == (long)BuiltInCategory.OST_Grids); }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed($"AlignSheetViews: read categories of filter {pf.Id.Value}", ex);
                return false;
            }
        }

        /// <summary>True when the filter's own rules actually select this grid. A filter that names
        /// the Grids category but whose rules the grid fails is not what is hiding it.</summary>
        private static bool FilterCatches(ParameterFilterElement pf, Grid g)
        {
            try
            {
                ElementFilter ef = pf.GetElementFilter();
                return ef == null || ef.PassesFilter(g);   // a rule-less filter catches the whole category
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed($"AlignSheetViews: evaluate filter {pf.Id.Value} against grid {g.Id.Value}", ex);
                return false;   // cannot prove it applies — do not blame it
            }
        }

        private static string TemplateName(View v)
        {
            try
            {
                if (v.ViewTemplateId == ElementId.InvalidElementId) return "—";
                return (v.Document.GetElement(v.ViewTemplateId) as View)?.Name ?? "—";
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed($"AlignSheetViews: template name of view {v.Id.Value}", ex);
                return "—";
            }
        }

        private static Category? TryGridCategory(Document doc)
        {
            try { return Category.GetCategory(doc, BuiltInCategory.OST_Grids); }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed("AlignSheetViews: resolve Grids category", ex);
                return null;
            }
        }

        private static bool CanBeVisible(Grid g, View v)
        {
            try { return g.CanBeVisibleInView(v); }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed($"AlignSheetViews: visibility test for grid {g.Id.Value} in view {v.Id.Value}", ex);
                return false;
            }
        }

        /// <summary>Restores the extent modes a rejected write had switched — leaves no half-change.
        /// Returns false when the restore itself failed, so the caller can surface it.</summary>
        private static bool RestoreEnds(GridWrite w, View tv)
        {
            try
            {
                w.Grid.SetDatumExtentType(DatumEnds.End0, tv, w.Mode0);
                w.Grid.SetDatumExtentType(DatumEnds.End1, tv, w.Mode1);
                return true;
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed($"AlignSheetViews: restore extent mode of grid {w.Grid.Id.Value} in view {tv.Id.Value}", ex);
                return false;
            }
        }

        /// <summary>
        /// Mirrors each end's bubble (head/tail) visibility from source view to target view. Extents
        /// alone are not enough: a target whose bubble is off shows no head however well its ends line up.
        /// </summary>
        private static void MirrorBubbles(Grid g, SourceGrid sg, View tv)
        {
            foreach (var end in new[] { DatumEnds.End0, DatumEnds.End1 })
            {
                try
                {
                    if (!g.HasBubbleInView(end, tv)) continue;
                    bool want = end == DatumEnds.End0 ? sg.Bubble0 : sg.Bubble1;
                    if (want == g.IsBubbleVisibleInView(end, tv)) continue;
                    if (want) g.ShowBubbleInView(end, tv);
                    else      g.HideBubbleInView(end, tv);
                }
                catch (Exception ex)
                {
                    DiagnosticsLog.Swallowed($"AlignSheetViews: bubble {end} of grid {g.Id.Value} in view {tv.Id.Value}", ex);
                }
            }
        }

        /// <summary>
        /// Slides <paramref name="src"/> along the view's normal until it lies in the same plane as
        /// <paramref name="reference"/> — the curve Revit itself currently reports for this datum in
        /// this view.
        ///
        /// SetCurveInView demands the curve be "coincident with the original one of the datum
        /// plane": a curve lifted from a plan at one level sits at that level's elevation and is off
        /// the datum's line in a plan at another, which is the ArgumentException this exists to
        /// prevent. The reference curve is the authoritative plane because Revit produced it for
        /// this datum in this view; the view's own origin is NOT (using it moved the curve off the
        /// datum line and made every write throw). Only the component along the view normal is
        /// taken, so the in-plane geometry — the part actually being copied — is untouched, and a
        /// pure translation carries lines and arcs alike.
        ///
        /// With no reference curve the source is returned unchanged: attempting the write and
        /// reporting Revit's verdict beats guessing at a plane.
        /// </summary>
        private static Curve CoincideWith(Curve src, Curve? reference, View v)
        {
            try
            {
                if (reference == null) return src;
                XYZ delta = PlaneDelta(src.GetEndPoint(0), reference.GetEndPoint(0), v);
                return delta.IsZeroLength() ? src : src.CreateTransformed(Transform.CreateTranslation(delta));
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed($"AlignSheetViews: coincide grid curve with view {v.Id.Value}", ex);
                return src;
            }
        }

        /// <summary>Translation that carries a point from one view's plane onto another's, along the
        /// shared view normal. The one place the plane offset between two views is computed.</summary>
        private static XYZ PlaneDelta(XYZ from, XYZ toPlanePoint, View v)
        {
            try
            {
                XYZ n = v.ViewDirection;
                if (n == null || n.IsZeroLength()) return XYZ.Zero;
                n = n.Normalize();
                double d = (toPlanePoint - from).DotProduct(n);
                return Math.Abs(d) < 1e-9 ? XYZ.Zero : n.Multiply(d);
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed($"AlignSheetViews: plane delta for view {v.Id.Value}", ex);
                return XYZ.Zero;
            }
        }

        /// <summary>
        /// Copies each end's leader "elbow" — the jog dragged onto a grid head so it steps aside from
        /// its neighbours — from the source view to the target, queuing the positioning for after the
        /// run's single regenerate.
        ///
        /// Matched in BOTH directions: a source end with no elbow has any elbow on the target
        /// cleared, so the two views agree instead of the target keeping jogs the source does not
        /// have. Elbow and End are absolute model points, so they are shifted onto the target view's
        /// plane exactly as the extent curve is.
        ///
        /// Positioning is deferred because a leader that has just been added reports its DEFAULT
        /// geometry until the document is regenerated — writing to it in the same breath lands on
        /// stale state and the elbow snaps back. And mutating a <see cref="Leader"/> is not enough on
        /// its own: <c>SetLeader</c> is what commits it back onto the datum.
        /// </summary>
        private void QueueElbows(GridPlan plan, List<PendingElbow> pending)
        {
            View? tv = plan.TargetView;
            if (tv == null) return;
            MatchedPair pr = plan.Pair;

            foreach (var vg in plan.Visible)
            {
                SourceGrid sg = vg.Src;
                Grid g = sg.Grid;

                // Both views' displayed curves are already in hand — the source's from the cache,
                // the target's from the prepare pass (or the curve just written to it).
                XYZ delta = XYZ.Zero;
                Curve? targetCurve = vg.WrittenCurve ?? vg.TargetDisplayed;
                if (sg.DisplayedCurve != null && targetCurve != null)
                    delta = PlaneDelta(sg.DisplayedCurve.GetEndPoint(0), targetCurve.GetEndPoint(0), tv);

                foreach (var end in new[] { DatumEnds.End0, DatumEnds.End1 })
                {
                    bool first    = end == DatumEnds.End0;
                    bool hasSrc   = first ? sg.HasLeader0 : sg.HasLeader1;
                    try
                    {
                        Leader? tgtLeader = TryGetLeader(g, end, tv);

                        if (!hasSrc)
                        {
                            if (tgtLeader == null) continue;
                            try
                            {
                                // DatumPlane has no RemoveLeader in the 2024 API — clearing an elbow
                                // is a null SetLeader. A rejection leaves a visible difference, so it
                                // is said out loud rather than swallowed.
                                g.SetLeader(end, tv, null);
                            }
                            catch (Exception rex)
                            {
                                Log(AppStrings.T("testing.alignSheetViews.log.gridElbowStuck",
                                                 pr.Label, pr.Target.ViewName, g.Name), "warn");
                                DiagnosticsLog.Swallowed($"AlignSheetViews: clear leader {end} of grid {g.Id.Value} in view {tv.Id.Value}", rex);
                            }
                            continue;
                        }

                        // Only an end that carries a bubble here can carry a leader.
                        if (!g.HasBubbleInView(end, tv)) continue;

                        XYZ elbow = (first ? sg.Elbow0 : sg.Elbow1) + delta;
                        XYZ tip   = (first ? sg.Tip0   : sg.Tip1)   + delta;

                        if (tgtLeader != null
                            && tgtLeader.Elbow.DistanceTo(elbow) < CurveMatchTolFt
                            && tgtLeader.End.DistanceTo(tip)     < CurveMatchTolFt)
                            continue;   // already matching — a no-op is not a change

                        if (tgtLeader == null) g.AddLeader(end, tv);
                        pending.Add(new PendingElbow(g, end, tv, elbow, tip, pr.Label, pr.Target.ViewName));
                    }
                    catch (Exception ex)
                    {
                        DiagnosticsLog.Swallowed($"AlignSheetViews: elbow {end} of grid {g.Id.Value} in view {tv.Id.Value}", ex);
                    }
                }
            }
        }

        /// <summary>Positions every queued elbow. Runs after the run's single regenerate, so each
        /// leader reports its real geometry rather than the default it was created with.</summary>
        private void PositionElbows(List<PendingElbow> pending)
        {
            foreach (var pe in pending)
            {
                try
                {
                    Leader? lead = TryGetLeader(pe.Grid, pe.End, pe.View);
                    if (lead == null) continue;

                    lead.Elbow = pe.Elbow;
                    lead.End   = pe.Tip;

                    // No IsLeaderValid pre-check — like IsCurveValidInView it is advisory, and its
                    // verdict was logged and then ignored. Revit's own exception is the authority.
                    pe.Grid.SetLeader(pe.End, pe.View, lead);
                }
                catch (Exception ex)
                {
                    Log(AppStrings.T("testing.alignSheetViews.log.gridElbowRejected",
                                     pe.Label, pe.ViewName, pe.Grid.Name), "warn");
                    DiagnosticsLog.Swallowed($"AlignSheetViews: position elbow {pe.End} of grid {pe.Grid.Id.Value} in view {pe.View.Id.Value}", ex);
                }
            }
        }

        private static Leader? TryGetLeader(Grid g, DatumEnds end, View v)
        {
            try { return g.GetLeader(end, v); }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed($"AlignSheetViews: read leader {end} of grid {g.Id.Value} in view {v.Id.Value}", ex);
                return null;
            }
        }

        /// <summary>Endpoint match in either direction — Revit does not guarantee the two views
        /// report a datum's curve with the same start/end, and a reversed copy of the same segment
        /// is the same extents, not a change worth writing.</summary>
        private static bool CurvesMatch(Curve a, Curve b)
        {
            try
            {
                XYZ a0 = a.GetEndPoint(0), a1 = a.GetEndPoint(1);
                XYZ b0 = b.GetEndPoint(0), b1 = b.GetEndPoint(1);
                return (a0.DistanceTo(b0) < CurveMatchTolFt && a1.DistanceTo(b1) < CurveMatchTolFt)
                    || (a0.DistanceTo(b1) < CurveMatchTolFt && a1.DistanceTo(b0) < CurveMatchTolFt);
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed("AlignSheetViews: compare grid curves", ex);
                return false;   // treat as different — a write is recoverable, a silent skip is not
            }
        }

        /// <summary>Reads both ends' extent modes in one go, so the callers that need the pair do not
        /// ask Revit the same question three times over.</summary>
        private static void ReadExtentTypes(Grid g, View view, out DatumExtentType end0, out DatumExtentType end1)
        {
            end0 = TryGetExtentType(g, DatumEnds.End0, view);
            end1 = TryGetExtentType(g, DatumEnds.End1, view);
        }

        /// <summary>The curve the grid actually displays in <paramref name="view"/>, read from
        /// whichever extent mode that view is using — modes supplied by the caller.</summary>
        private static Curve? CurveForMode(Grid g, View view, DatumExtentType end0, DatumExtentType end1)
        {
            var mode = end0 == DatumExtentType.ViewSpecific || end1 == DatumExtentType.ViewSpecific
                ? DatumExtentType.ViewSpecific
                : DatumExtentType.Model;
            var curves = TryGetCurves(g, mode, view);
            return curves != null && curves.Count > 0 ? curves[0] : null;
        }

        private static DatumExtentType TryGetExtentType(Grid g, DatumEnds end, View view)
        {
            try
            {
                return g.GetDatumExtentTypeInView(end, view);
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed($"AlignSheetViews: extent type of grid {g.Id.Value} in view {view.Id.Value}", ex);
                return DatumExtentType.Model;
            }
        }

        private static IList<Curve>? TryGetCurves(Grid g, DatumExtentType type, View view)
        {
            try
            {
                var curves = g.GetCurvesInView(type, view);
                return curves != null && curves.Count > 0 ? curves : null;
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed($"AlignSheetViews: {type} curves of grid {g.Id.Value} in view {view.Id.Value}", ex);
                return null;
            }
        }

        // ── View-title (label) alignment ──────────────────────────────────────
        /// <summary>Copies the source title's underline length. A pure write — it reads nothing that
        /// the alignment has changed, so it runs alongside the placement.</summary>
        private void MatchTitleLineLength(Document doc, MatchedPair pr)
        {
            try
            {
                if (!(doc.GetElement(pr.Source.ViewportId) is Viewport sVp)) return;
                if (!(doc.GetElement(pr.Target.ViewportId) is Viewport tVp)) return;
                tVp.LabelLineLength = sVp.LabelLineLength;
            }
            catch (Exception ex)
            {
                Log(AppStrings.T("testing.alignSheetViews.log.couldNotMatchTitleLine", pr.Label, pr.Target.ViewName), "warn");
                DiagnosticsLog.Swallowed($"AlignSheetViews: LabelLineLength on viewport {pr.Target.ViewportId.Value}", ex);
            }
        }

        /// <summary>
        /// Shifts each target label so its outline overlays its source label's. Runs last, after the
        /// boxes are final and the document has been regenerated, so GetLabelOutline reports the real
        /// on-sheet position — a label outline read before that regenerate describes where the
        /// viewport used to be.
        /// </summary>
        private void AlignTitleOffsets(Document doc, List<MatchedPair> pairs)
        {
            int titled = 0, titleFail = 0;
            foreach (var pr in pairs)
            {
                try
                {
                    if (!(doc.GetElement(pr.Source.ViewportId) is Viewport sVp)) continue;
                    if (!(doc.GetElement(pr.Target.ViewportId) is Viewport tVp)) continue;

                    Outline srcOut = sVp.GetLabelOutline();
                    Outline tgtOut = tVp.GetLabelOutline();
                    if (srcOut == null || tgtOut == null) { titleFail++; continue; }

                    // Translate the target label by the gap between the two outline anchors (min corner).
                    XYZ delta = srcOut.MinimumPoint - tgtOut.MinimumPoint;
                    tVp.LabelOffset = tVp.LabelOffset + delta;
                    titled++;
                }
                catch (Exception ex)
                {
                    titleFail++;
                    Log(AppStrings.T("testing.alignSheetViews.log.couldNotAlignTitle", pr.Label, pr.Target.ViewName), "warn");
                    DiagnosticsLog.Swallowed($"AlignSheetViews: align title on viewport {pr.Target.ViewportId.Value}", ex);
                }
            }

            if (titleFail > 0)
                Log(AppStrings.T("testing.alignSheetViews.log.viewTitlesSome", titled, titleFail), "warn");
        }

        // ── Capture ───────────────────────────────────────────────────────────
        /// <summary>Reads every viewport on a sheet that hosts a graphical, crop-bearing view.</summary>
        private List<VpEntry> CaptureSheet(Document doc, ViewSheet sheet)
        {
            var entries = new List<VpEntry>();
            ICollection<ElementId> vpIds;
            try { vpIds = sheet.GetAllViewports(); }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed($"AlignSheetViews: GetAllViewports on sheet {sheet.Id.Value}", ex);
                return entries;
            }

            foreach (var vpId in vpIds)
            {
                try
                {
                    if (!(doc.GetElement(vpId) is Viewport vp)) continue;
                    if (!(doc.GetElement(vp.ViewId) is View view) || view.IsTemplate) continue;
                    if (view.Scale <= 0) continue;                 // perspective / schedule / legend — no model scale

                    BoundingBoxXYZ cb = view.CropBox;
                    if (cb == null || cb.Transform == null) continue;

                    ElementId scopeBox = ElementId.InvalidElementId;
                    var sbParam = view.get_Parameter(BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP);
                    if (sbParam != null && sbParam.StorageType == StorageType.ElementId)
                        scopeBox = sbParam.AsElementId();

                    ReadAnnotationCrop(view, out bool annoActive,
                                       out double annoTop, out double annoBot,
                                       out double annoLeft, out double annoRight);

                    entries.Add(new VpEntry
                    {
                        ViewportId    = vp.Id,
                        ViewId        = view.Id,
                        ViewName      = view.Name,
                        Type          = view.ViewType,
                        ViewDir       = view.ViewDirection,
                        Transform     = cb.Transform,
                        CropMin       = cb.Min,
                        CropMax       = cb.Max,
                        Scale         = view.Scale,
                        BoxCenter     = vp.GetBoxCenter(),
                        Rotation      = vp.Rotation,
                        CropBoxVisible = view.CropBoxVisible,
                        ScopeBoxId    = scopeBox,
                        AnnoCropActive = annoActive,
                        AnnoTop       = annoTop,
                        AnnoBottom    = annoBot,
                        AnnoLeft      = annoLeft,
                        AnnoRight     = annoRight,
                    });
                }
                catch (Exception ex)
                {
                    DiagnosticsLog.Swallowed($"AlignSheetViews: capture viewport {vpId.Value}", ex);
                }
            }
            return entries;
        }

        // ── Matching ──────────────────────────────────────────────────────────
        /// <summary>
        /// Matches every source view on a sheet to a target view, scoring the whole sheet.
        /// Exact scope-box pairs are claimed first, then the overlap fallback is assigned
        /// <b>globally best-first</b> rather than in source order - previously the first source
        /// processed took its best free target, so it could steal a target that was a decisively
        /// better match for a later source, and a single stolen target mis-pairs two views.
        /// </summary>
        private SheetMatch MatchSheet(List<VpEntry> srcEntries, List<VpEntry> tgtEntries)
        {
            var res     = new SheetMatch();
            var used    = new HashSet<ElementId>();   // claimed targets
            var settled = new HashSet<ElementId>();   // sources that are paired or reported ambiguous

            // Pass 1 - exact, unambiguous scope-box pairs. A shared scope box is an exact key, so
            // these are claimed before any overlap candidate can take the target. A scope box that
            // matches several targets is left to the overlap pass, restricted to those targets.
            var restrictTo = new Dictionary<ElementId, HashSet<ElementId>>();
            foreach (var src in srcEntries)
            {
                if (src.ScopeBoxId == ElementId.InvalidElementId) continue;
                var byScope = tgtEntries.Where(t => !used.Contains(t.ViewportId) && Eligible(src, t)
                                                 && t.ScopeBoxId == src.ScopeBoxId).ToList();
                if (byScope.Count == 1)
                {
                    used.Add(byScope[0].ViewportId);
                    settled.Add(src.ViewportId);
                    res.Pairs.Add(new MatchedPair(src, byScope[0]));
                }
                else if (byScope.Count > 1)
                {
                    restrictTo[src.ViewportId] = new HashSet<ElementId>(byScope.Select(t => t.ViewportId));
                }
            }

            // Pass 2 - overlap fallback, every qualifying pair scored up front.
            var cands = new List<(VpEntry S, VpEntry T, double Score)>();
            foreach (var src in srcEntries)
            {
                if (settled.Contains(src.ViewportId)) continue;
                restrictTo.TryGetValue(src.ViewportId, out var allowed);
                foreach (var t in tgtEntries)
                {
                    if (used.Contains(t.ViewportId)) continue;
                    if (allowed != null && !allowed.Contains(t.ViewportId)) continue;
                    if (!Eligible(src, t)) continue;
                    double sc = OverlapInSourcePlane(src, t);
                    if (sc < OverlapThreshold) continue;
                    cands.Add((src, t, sc));
                }
            }

            foreach (var c in cands.OrderByDescending(x => x.Score).ToList())
            {
                if (settled.Contains(c.S.ViewportId)) continue;
                if (used.Contains(c.T.ViewportId))    continue;

                // Ambiguous when another still-free target scores nearly as well for this source.
                var rival = cands.FirstOrDefault(o => o.S.ViewportId == c.S.ViewportId
                                                   && o.T.ViewportId != c.T.ViewportId
                                                   && !used.Contains(o.T.ViewportId)
                                                   && o.Score >= 0.8 * c.Score);
                settled.Add(c.S.ViewportId);
                if (rival.T != null) { res.Ambiguous.Add((c.S, c.T, rival.T)); continue; }

                used.Add(c.T.ViewportId);
                res.Pairs.Add(new MatchedPair(c.S, c.T));
            }

            foreach (var src in srcEntries.Where(se => !settled.Contains(se.ViewportId)))
                res.Missing.Add(src);
            foreach (var t in tgtEntries.Where(te => !used.Contains(te.ViewportId)))
                res.Extra.Add(t);

            // Score: matched count dominates; ties broken toward true overlays then exact scope matches.
            int quality = res.Pairs.Count(pr => pr.Source.Scale == pr.Target.Scale && OrientationMatches(pr.Source, pr.Target));
            int exact   = res.Pairs.Count(pr => pr.Source.ScopeBoxId != ElementId.InvalidElementId
                                             && pr.Source.ScopeBoxId == pr.Target.ScopeBoxId);
            res.Score = res.Pairs.Count * 10000.0 + quality * 100.0 + exact;
            return res;
        }

        /// <summary>A candidate must be the same view type and look the same way to be pairable.</summary>
        private static bool Eligible(VpEntry src, VpEntry cand)
            => cand.Type == src.Type
            && Math.Abs(src.ViewDir.DotProduct(cand.ViewDir)) >= ParallelDot;

        /// <summary>
        /// In-plane overlap fraction (intersection / smaller crop area) of a candidate view's
        /// crop rectangle against the source's, both projected into the source crop frame.
        /// Returns 0 when the views' cut-depth ranges don't intersect (different level / depth).
        /// </summary>
        private static double OverlapInSourcePlane(VpEntry src, VpEntry cand)
        {
            Transform f = src.Transform;
            XYZ o  = f.Origin;
            XYZ bx = f.BasisX, by = f.BasisY, bn = f.BasisZ;

            double sUmin = src.CropMin.X, sUmax = src.CropMax.X;
            double sVmin = src.CropMin.Y, sVmax = src.CropMax.Y;
            double sNmin = src.CropMin.Z, sNmax = src.CropMax.Z;

            double uMin = double.MaxValue, uMax = double.MinValue;
            double vMin = double.MaxValue, vMax = double.MinValue;
            double nMin = double.MaxValue, nMax = double.MinValue;
            double[] xs = { cand.CropMin.X, cand.CropMax.X };
            double[] ys = { cand.CropMin.Y, cand.CropMax.Y };
            double[] zs = { cand.CropMin.Z, cand.CropMax.Z };
            foreach (var x in xs)
                foreach (var y in ys)
                    foreach (var z in zs)
                    {
                        XYZ w = cand.Transform.OfPoint(new XYZ(x, y, z));
                        XYZ d = w - o;
                        double u = d.DotProduct(bx), v = d.DotProduct(by), n = d.DotProduct(bn);
                        if (u < uMin) uMin = u; if (u > uMax) uMax = u;
                        if (v < vMin) vMin = v; if (v > vMax) vMax = v;
                        if (n < nMin) nMin = n; if (n > nMax) nMax = n;
                    }

            // Depth veto: two levels' plan-view cut ranges do not necessarily overlap.
            if (Overlap1D(sNmin, sNmax, nMin, nMax) <= 0) return 0;

            double ov = Overlap1D(sUmin, sUmax, uMin, uMax) * Overlap1D(sVmin, sVmax, vMin, vMax);
            if (ov <= 0) return 0;

            double areaS = Math.Abs((sUmax - sUmin) * (sVmax - sVmin));
            double areaC = Math.Abs((uMax - uMin) * (vMax - vMin));
            double denom = Math.Min(areaS, areaC);
            return denom <= 1e-9 ? 0 : ov / denom;
        }

        private static double Overlap1D(double aMin, double aMax, double bMin, double bMax)
            => Math.Max(0, Math.Min(aMax, bMax) - Math.Max(aMin, bMin));

        private static bool OrientationMatches(VpEntry a, VpEntry b)
            => a.Transform.BasisX.DotProduct(b.Transform.BasisX) > OrientationDot
            && a.Transform.BasisY.DotProduct(b.Transform.BasisY) > OrientationDot;

        // ── Alignment ─────────────────────────────────────────────────────────
        /// <summary>
        /// Moves the target viewport so the source view's crop-centre world point lands at the same
        /// sheet coordinate it occupies on the source sheet.
        ///
        /// The target's crop geometry is taken from <see cref="MatchedPair.Predicted"/> — captured
        /// before the run and updated in place by each inheritance write — rather than read back from
        /// the document. That is what removes the regenerate this phase used to need: reading
        /// <c>view.CropBox</c> live meant the document had to be recomputed first, whereas every
        /// write made this run has a result derivable from values already in hand.
        ///
        /// The risk that carries is bounded. <c>SetBoxCenter</c> positions the box absolutely, so a
        /// wrong prediction cannot compound the way a relative nudge would, and
        /// <see cref="VerifyPlacements"/> re-checks every placement against the real geometry once
        /// the run regenerates.
        ///
        /// The anchor comes from the MODEL crop box but the viewport is positioned through
        /// <c>SetBoxCenter</c>, which acts on the viewport's on-sheet FOOTPRINT. Those two coincide
        /// only when a view has no annotation crop, or a symmetric one - so both sides are converted
        /// through <see cref="FootprintCentre"/>, and each side uses its OWN scale.
        /// </summary>
        private bool TryAlign(Document doc, MatchedPair pr)
        {
            VpEntry src = pr.Source, target = pr.Target;
            try
            {
                if (!(doc.GetElement(target.ViewportId) is Viewport vp)) return false;
                if (target.Scale <= 0 || src.Scale <= 0) return false;

                XYZ centre = TargetBoxCentre(src, target.Rotation, target.Scale, pr.Predicted);
                vp.SetBoxCenter(centre);
                pr.PlacedCentre = centre;
                return true;
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error($"AlignSheetViews: SetBoxCenter on viewport {target.ViewportId.Value}", ex);
                return false;
            }
        }

        /// <summary>
        /// Checks every placed viewport against the crop the document actually ended up with, and
        /// re-places any that the prediction missed.
        ///
        /// This is the safety net that lets the align phase skip its regenerate. Because
        /// <c>SetBoxCenter</c> is absolute, a correction is exact rather than cumulative: the
        /// viewport is told where to be, not how far to move, so a mispredicted view lands in the
        /// right place on the first correction and no error survives into the commit.
        /// </summary>
        private int VerifyPlacements(Document doc, List<MatchedPair> pairs)
        {
            int corrected = 0;
            foreach (var pr in pairs)
            {
                if (pr.PlacedCentre == null) continue;
                try
                {
                    if (!(doc.GetElement(pr.Target.ViewId) is View tv)) continue;
                    BoundingBoxXYZ cb = tv.CropBox;
                    if (cb?.Transform == null) continue;
                    int scale = tv.Scale;
                    if (scale <= 0 || pr.Source.Scale <= 0) continue;

                    ReadAnnotationCrop(tv, out bool act, out double top, out double bot, out double left, out double right);
                    var actual = new CropState
                    {
                        Transform  = cb.Transform,
                        Min        = cb.Min,
                        Max        = cb.Max,
                        AnnoActive = act,
                        AnnoTop    = top,
                        AnnoBottom = bot,
                        AnnoLeft   = left,
                        AnnoRight  = right,
                    };

                    XYZ truth = TargetBoxCentre(pr.Source, pr.Target.Rotation, scale, actual);
                    double off = truth.DistanceTo(pr.PlacedCentre);
                    if (off <= PlacementTolFt) continue;

                    if (!(doc.GetElement(pr.Target.ViewportId) is Viewport vp)) continue;
                    vp.SetBoxCenter(truth);
                    pr.PlacedCentre = truth;
                    corrected++;

                    Log(AppStrings.T("testing.alignSheetViews.log.placementCorrected",
                                     pr.Label, pr.Target.ViewName, F(off)), "warn");
                    DiagnosticsLog.Warn("AlignSheetViews",
                        $"Viewport {pr.Target.ViewportId.Value} ('{pr.Target.ViewName}') crop differed from the predicted geometry; re-placed by {off:0.######}'.");
                }
                catch (Exception ex)
                {
                    Log(AppStrings.T("testing.alignSheetViews.log.couldNotVerifyPlacement", pr.Label, pr.Target.ViewName), "warn");
                    DiagnosticsLog.Swallowed($"AlignSheetViews: verify placement of viewport {pr.Target.ViewportId.Value}", ex);
                }
            }
            return corrected;
        }

        /// <summary>
        /// The sheet point to move the target viewport's box centre to, so the shared world anchor
        /// registers on both sheets. Takes the target's crop geometry as a value so the same maths
        /// serves both the predicted placement and the verification against the real crop.
        /// </summary>
        private static XYZ TargetBoxCentre(VpEntry src, ViewportRotation rotation, int scale, CropState crop)
        {
            XYZ anchor = AnchorWorld(src);
            XYZ local  = crop.Transform.Inverse.OfPoint(anchor);

            FootprintCentre(crop.Min, crop.Max, crop.AnnoActive,
                            crop.AnnoTop, crop.AnnoBottom, crop.AnnoLeft, crop.AnnoRight,
                            out double fx, out double fy);
            XYZ off = ApplyRotation(new XYZ((local.X - fx) / scale, (local.Y - fy) / scale, 0), rotation);

            return SourceAnchorOnSheet(src) - off;
        }

        /// <summary>
        /// Sheet coordinate at which the source view's anchor (its model-crop centre) actually sits
        /// on the source sheet. That is the source viewport's box centre only when the source has no
        /// asymmetric annotation crop; otherwise the anchor sits off-centre by half the left/right
        /// (and bottom/top) offset difference. Uses the SOURCE view's own scale - which the previous
        /// formula never applied, so source/target scale mismatches were mis-scaled too.
        /// </summary>
        private static XYZ SourceAnchorOnSheet(VpEntry src)
        {
            double acx = (src.CropMin.X + src.CropMax.X) / 2.0;
            double acy = (src.CropMin.Y + src.CropMax.Y) / 2.0;
            FootprintCentre(src.CropMin, src.CropMax, src.AnnoCropActive,
                            src.AnnoTop, src.AnnoBottom, src.AnnoLeft, src.AnnoRight,
                            out double fx, out double fy);
            XYZ d = ApplyRotation(new XYZ((acx - fx) / src.Scale, (acy - fy) / src.Scale, 0), src.Rotation);
            return src.BoxCenter + d;
        }

        /// <summary>
        /// Centre of the viewport's on-sheet footprint, expressed in the view's crop-local coords.
        /// With no active annotation crop this is just the model-crop centre, so views that were
        /// already aligning correctly do not move. With an active annotation crop the footprint is
        /// the crop grown by the four offsets, so its centre shifts by half their difference.
        /// </summary>
        private static void FootprintCentre(XYZ cropMin, XYZ cropMax, bool annoActive,
                                            double annoTop, double annoBottom, double annoLeft, double annoRight,
                                            out double cx, out double cy)
        {
            cx = (cropMin.X + cropMax.X) / 2.0;
            cy = (cropMin.Y + cropMax.Y) / 2.0;
            if (!annoActive) return;
            cx += (annoRight - annoLeft)   / 2.0;
            cy += (annoTop   - annoBottom) / 2.0;
        }

        /// <summary>
        /// Reads a view's annotation-crop state (offsets are model feet). Not every view type
        /// carries an annotation crop, so a failure reports "inactive" rather than throwing.
        /// </summary>
        private void ReadAnnotationCrop(View v, out bool active,
                                        out double top, out double bottom, out double left, out double right)
        {
            active = false; top = bottom = left = right = 0;
            try
            {
                var p = v.get_Parameter(BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE);
                if (p == null || p.AsInteger() != 1) return;
                ViewCropRegionShapeManager sm = v.GetCropRegionShapeManager();
                top    = sm.TopAnnotationCropOffset;
                bottom = sm.BottomAnnotationCropOffset;
                left   = sm.LeftAnnotationCropOffset;
                right  = sm.RightAnnotationCropOffset;
                active = true;
            }
            catch (Exception ex)
            {
                // Falling back to "no annotation crop" silently would reintroduce exactly the
                // mis-placement this fix exists to remove, so the user is told, not just the log.
                active = false; top = bottom = left = right = 0;
                Log(AppStrings.T("testing.alignSheetViews.log.couldNotReadAnnoCrop", v.Name), "warn");
                DiagnosticsLog.Swallowed($"AlignSheetViews: read annotation crop on view {v.Id.Value}", ex);
            }
        }

        /// <summary>
        /// Maps an offset expressed in crop-plane axes onto sheet axes for a rotated viewport: a
        /// rotated viewport turns its crop X axis onto a sheet Y axis, so an uncompensated offset is
        /// applied in the wrong direction. The tool used to warn about rotated viewports and then
        /// move them with the raw offset anyway.
        ///
        /// PROVISIONAL: the sign convention is not verified against Revit.
        /// </summary>
        private static XYZ ApplyRotation(XYZ v, ViewportRotation r)
        {
            switch (r)
            {
                case ViewportRotation.Clockwise:        return new XYZ( v.Y, -v.X, 0);
                case ViewportRotation.Counterclockwise: return new XYZ(-v.Y,  v.X, 0);
                default:                                return v;
            }
        }

        private static XYZ AnchorWorld(VpEntry e)
        {
            double cx = (e.CropMin.X + e.CropMax.X) / 2.0;
            double cy = (e.CropMin.Y + e.CropMax.Y) / 2.0;
            double cz = (e.CropMin.Z + e.CropMax.Z) / 2.0;
            return e.Transform.OfPoint(new XYZ(cx, cy, cz));
        }

        private static string F(double v) => v.ToString("0.####");

        private static int Pct(int done, int total) => total <= 0 ? 100 : (int)Math.Round(100.0 * done / total);

        // ── Failure routing ───────────────────────────────────────────────────
        private void ConfigureFailures(Transaction tx)
        {
            try
            {
                var opts = tx.GetFailureHandlingOptions();
                opts.SetClearAfterRollback(true);
                opts.SetDelayedMiniWarnings(true);
                tx.SetFailureHandlingOptions(opts);
            }
            catch (Exception ex) { DiagnosticsLog.Swallowed("AlignSheetViews: configure failure handling", ex); }
        }

        // ── A captured source sheet ───────────────────────────────────────────
        private sealed class SourceSheet
        {
            public SourceSheet(string label, List<VpEntry> entries)
            {
                Label = label; Entries = entries;
            }
            public string        Label   { get; }
            public List<VpEntry> Entries { get; }
        }

        // ── One source-sheet → target-sheet matching result ───────────────────
        private sealed class SheetMatch
        {
            public List<MatchedPair> Pairs     { get; } = new List<MatchedPair>();
            public List<VpEntry>     Missing   { get; } = new List<VpEntry>();
            public List<VpEntry>     Extra     { get; } = new List<VpEntry>();
            public List<(VpEntry src, VpEntry a, VpEntry b)> Ambiguous { get; } = new List<(VpEntry, VpEntry, VpEntry)>();
            public double            Score     { get; set; }
        }

        /// <summary>
        /// A view's crop geometry as the alignment maths needs it. Seeded from the capture pass and
        /// updated in place by each inheritance write, so the placement never has to read it back.
        /// </summary>
        private sealed class CropState
        {
            public Transform Transform  { get; set; } = Transform.Identity;
            public XYZ       Min        { get; set; } = XYZ.Zero;
            public XYZ       Max        { get; set; } = XYZ.Zero;
            public bool      AnnoActive { get; set; }
            public double    AnnoTop    { get; set; }
            public double    AnnoBottom { get; set; }
            public double    AnnoLeft   { get; set; }
            public double    AnnoRight  { get; set; }
        }

        // ── A matched source→target viewport pair, recorded for the apply + title passes ─
        private sealed class MatchedPair
        {
            public MatchedPair(VpEntry source, VpEntry target)
            {
                Source = source; Target = target;
                Predicted = new CropState
                {
                    Transform  = target.Transform,
                    Min        = target.CropMin,
                    Max        = target.CropMax,
                    AnnoActive = target.AnnoCropActive,
                    AnnoTop    = target.AnnoTop,
                    AnnoBottom = target.AnnoBottom,
                    AnnoLeft   = target.AnnoLeft,
                    AnnoRight  = target.AnnoRight,
                };
            }
            public VpEntry   Source { get; }
            public VpEntry   Target { get; }
            public string    Label  { get; set; } = "";

            /// <summary>True once this run has assigned the source's scope box to the target view.</summary>
            public bool      GotScopeBox { get; set; }

            /// <summary>The target's crop geometry as this run's writes will have left it.</summary>
            public CropState Predicted { get; }

            /// <summary>Sheet centre the alignment computed and wrote — the value the verification
            /// pass measures the real geometry against. Null if it never placed.</summary>
            public XYZ?      PlacedCentre { get; set; }
        }

        // ── Per-view grid outcome counts ──────────────────────────────────────
        // One field per outcome so a zero result always names its cause. "Changed" counts real
        // differences only — a curve written identical to the one already there is AlreadyMatching,
        // because "N trimmed" that included no-op writes is what hid this bug in the first place.
        private sealed class GridTally
        {
            public int Changed         { get; set; }
            public int AlreadyMatching { get; set; }
            public int NoSourceCurve   { get; set; }
            public int Rejected        { get; set; }
            public int Errored         { get; set; }
            public int MultiSegment    { get; set; }
            public int Unbound         { get; set; }
            public int Blocked         { get; set; }
            public int NotShowable     { get; set; }
        }

        // ── One pair's grid work, split around the sheet's single regenerate ──
        private sealed class GridPlan
        {
            public GridPlan(MatchedPair pair) { Pair = pair; }
            public MatchedPair      Pair       { get; }
            public View?            TargetView { get; set; }
            public GridTally        Tally      { get; } = new GridTally();
            public int              TargetOnly { get; set; }
            public List<GridWrite>  Writes     { get; } = new List<GridWrite>();
            public List<VisibleGrid> Visible   { get; } = new List<VisibleGrid>();
        }

        // ── A grid this tool will act on in one target view ───────────────────
        private sealed class VisibleGrid
        {
            public VisibleGrid(SourceGrid src) { Src = src; }
            public SourceGrid Src             { get; }
            /// <summary>The curve the target displayed before any write this run.</summary>
            public Curve?     TargetDisplayed { get; set; }
            /// <summary>Set once a curve write for this grid has landed.</summary>
            public Curve?     WrittenCurve    { get; set; }
        }

        // ── A queued grid curve write, with the extent modes to restore if it is rejected ─
        private sealed class GridWrite
        {
            public GridWrite(Grid grid, Curve curve, DatumExtentType mode0, DatumExtentType mode1, VisibleGrid target)
            {
                Grid = grid; Curve = curve; Mode0 = mode0; Mode1 = mode1; Target = target;
            }
            public Grid            Grid   { get; }
            public Curve           Curve  { get; }
            public DatumExtentType Mode0  { get; }
            public DatumExtentType Mode1  { get; }
            public VisibleGrid     Target { get; }
        }

        // ── An elbow waiting on the run's regenerate before it can be positioned ─
        private sealed class PendingElbow
        {
            public PendingElbow(Grid grid, DatumEnds end, View view, XYZ elbow, XYZ tip, string label, string viewName)
            {
                Grid = grid; End = end; View = view; Elbow = elbow; Tip = tip; Label = label; ViewName = viewName;
            }
            public Grid      Grid     { get; }
            public DatumEnds End      { get; }
            public View      View     { get; }
            public XYZ       Elbow    { get; }
            public XYZ       Tip      { get; }
            public string    Label    { get; }
            public string    ViewName { get; }
        }

        // ── The source side of a grid comparison, read once per source view ───
        private sealed class SourceGrid
        {
            public SourceGrid(Grid grid) { Grid = grid; }
            public Grid   Grid             { get; }
            public bool   UsesViewSpecific { get; set; }
            public Curve? ViewSpecificCurve { get; set; }
            public int    CurveCount       { get; set; }
            public Curve? DisplayedCurve   { get; set; }
            public bool   Hidden           { get; set; }
            public bool   Bubble0          { get; set; }
            public bool   Bubble1          { get; set; }
            public bool   HasLeader0       { get; set; }
            public bool   HasLeader1       { get; set; }
            public XYZ    Elbow0           { get; set; } = XYZ.Zero;
            public XYZ    Tip0             { get; set; } = XYZ.Zero;
            public XYZ    Elbow1           { get; set; } = XYZ.Zero;
            public XYZ    Tip1             { get; set; } = XYZ.Zero;
        }

        private sealed class SourceViewGrids
        {
            public List<SourceGrid> Grids           { get; } = new List<SourceGrid>();
            public HashSet<long>    GridIds         { get; } = new HashSet<long>();
            public HashSet<long>    HiddenFilterIds { get; } = new HashSet<long>();
            public bool             CategoryHidden  { get; set; }
        }

        // ── Grid-visibility facts that belong to a view PAIR, not to one gridline ─
        private sealed class VisibilityContext
        {
            public View?                          SourceView       { get; set; }
            public bool                           ClearCategory    { get; set; }
            public List<ParameterFilterElement>   CandidateFilters { get; } = new List<ParameterFilterElement>();

            // Worksets repeat heavily across the gridlines of one view, so each answer is kept.
            private readonly Dictionary<int, bool> _workset = new Dictionary<int, bool>();

            /// <summary>True when this workset is hidden in the target view and not in the source —
            /// the only case that can explain a grid showing in one view and not the other.</summary>
            public bool WorksetHiddenInTargetOnly(WorksetId ws, View tv)
            {
                int key = ws.IntegerValue;
                if (_workset.TryGetValue(key, out bool cached)) return cached;
                bool result = false;
                try
                {
                    result = tv.GetWorksetVisibility(ws) == WorksetVisibility.Hidden
                          && SourceView != null
                          && SourceView.GetWorksetVisibility(ws) != WorksetVisibility.Hidden;
                }
                catch (Exception ex)
                {
                    DiagnosticsLog.Swallowed($"AlignSheetViews: workset {key} visibility on view {tv.Id.Value}", ex);
                }
                _workset[key] = result;
                return result;
            }
        }

        // ── Captured per-viewport state ───────────────────────────────────────
        private sealed class VpEntry
        {
            public ElementId        ViewportId     { get; set; } = ElementId.InvalidElementId;
            public ElementId        ViewId         { get; set; } = ElementId.InvalidElementId;
            public string           ViewName       { get; set; } = "";
            public ViewType         Type           { get; set; }
            public XYZ              ViewDir        { get; set; } = XYZ.BasisZ;
            public Transform        Transform      { get; set; } = Transform.Identity;
            public XYZ              CropMin        { get; set; } = XYZ.Zero;
            public XYZ              CropMax        { get; set; } = XYZ.Zero;
            public int              Scale          { get; set; } = 1;
            public XYZ              BoxCenter      { get; set; } = XYZ.Zero;
            public ViewportRotation Rotation       { get; set; } = ViewportRotation.None;
            public bool             CropBoxVisible { get; set; }
            public ElementId        ScopeBoxId     { get; set; } = ElementId.InvalidElementId;

            // Annotation crop (model feet). An ACTIVE, ASYMMETRIC annotation crop shifts the
            // viewport's on-sheet footprint away from the model-crop centre, which the
            // alignment maths has to compensate for. See FootprintCentre.
            public bool             AnnoCropActive { get; set; }
            public double           AnnoTop        { get; set; }
            public double           AnnoBottom     { get; set; }
            public double           AnnoLeft       { get; set; }
            public double           AnnoRight      { get; set; }
        }
    }
}
