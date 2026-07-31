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
    /// Optionally inherits, from the source view onto the matched target view: scope box assignment
    /// (applied FIRST, before any alignment, because it rewrites the crop box the alignment math
    /// reads), crop size + annotation crop, grid 2D (view-specific) extents, and crop-region
    /// visibility. A target sheet missing a counterpart — or carrying an unmatched extra — is
    /// reported, never silently skipped. Runs on the Revit API thread; set all inputs before Raise().
    /// </summary>
    public sealed class AlignSheetViewsEventHandler : IExternalEventHandler
    {
        // ── Inputs set by the view model before Raise() ───────────────────────
        public List<ElementId> SourceSheetIds { get; set; } = new List<ElementId>();
        public List<ElementId> TargetSheetIds { get; set; } = new List<ElementId>();

        /// <summary>Minimum in-plane overlap fraction (0..1) for a source/target view pair to match (overlap fallback only).</summary>
        public double OverlapThreshold { get; set; } = 0.5;

        /// <summary>When true: match and report only; no viewport is moved and nothing is committed.</summary>
        public bool PreviewOnly { get; set; } = false;

        /// <summary>When true: after the viewports are aligned, overlay each view title on its source title.</summary>
        public bool AlignTitles { get; set; } = true;

        // ── Inheritance toggles ───────────────────────────────────────────────
        /// <summary>Trim each target grid's 2D (view-specific) extents to the source grid's endpoints.</summary>
        public bool InheritGridExtents { get; set; } = false;

        /// <summary>Assign the source view's scope box to the target view (applied before alignment).</summary>
        public bool InheritScopeBox { get; set; } = false;

        /// <summary>Match the target view's crop-region visibility (CropBoxVisible) to the source.</summary>
        public bool InheritCropVisibility { get; set; } = false;

        /// <summary>Match the target view's crop size + annotation-crop offsets to the source.</summary>
        public bool InheritCropSize { get; set; } = false;

        // ── Callbacks ─────────────────────────────────────────────────────────
        public Action<string, string>?     PushLog    { get; set; }
        public Action<int, int, int, int>? OnProgress { get; set; }
        public Action<int, int, int>?      OnComplete { get; set; }

        public string GetName() => "LemoineTools.Tools.Sheets.AlignSheetViews";

        private void Log(string t, string s) => PushLog?.Invoke(t, s);

        // Bases are treated as parallel / matching above these cosine thresholds.
        private const double ParallelDot    = 0.999;
        private const double OrientationDot  = 0.9999;
        private const double MinCropOffsetFt = 1e-4;   // Revit rejects 0 / negative annotation-crop offsets.
        private const double PlacementTolFt  = 1e-6;   // sheet feet — below this a box has not moved.

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
                    sources.Add(new SourceSheet(s.Id, label, entries));
                    Log(AppStrings.T("testing.alignSheetViews.log.sourceRefViews", label, entries.Count), "info");
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

                if (PreviewOnly)
                {
                    Log(AppStrings.T("testing.alignSheetViews.log.previewOnly"), "info");
                    (pass, fail, skip, _) = DriveTargets(doc, sources, targets, applyMoves: false, onProgress);
                }
                else
                {
                    using (var tx = new Transaction(doc, "Lemoine — Align Sheet Views"))
                    {
                        ConfigureFailures(tx);
                        tx.Start();

                        List<MatchedPair> pairs;
                        (pass, fail, skip, pairs) = DriveTargets(doc, sources, targets, applyMoves: true, onProgress);

                        if (pairs.Count > 0 && !RunState.CancelRequested)
                        {
                            // Everything after the align phase — grid extents, crop visibility, the
                            // annotation-crop writes — can change a view's footprint, and the footprint
                            // is what SetBoxCenter positioned. Regenerate so the boxes report where
                            // they actually ended up, then put any that shifted back.
                            doc.Regenerate();
                            VerifyPlacement(doc, pairs, correct: true);

                            // Titles last: moving a viewport drags its title, so titles can only be
                            // placed once every box is in its final spot.
                            if (AlignTitles)
                            {
                                AlignAllTitles(doc, pairs);
                                // Report-only: correcting a box here would drag the title straight
                                // back off the source it was just aligned to.
                                VerifyPlacement(doc, pairs, correct: false);
                            }
                        }

                        tx.Commit();
                    }
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
                // Session-long static handler — drop the run's payload.
                TargetSheetIds = new List<ElementId>();
                SourceSheetIds = new List<ElementId>();
            }
        }

        // ── Per-target-sheet driver ───────────────────────────────────────────
        private (int pass, int fail, int skip, List<MatchedPair> pairs) DriveTargets(
            Document doc, List<SourceSheet> sources, List<ViewSheet> targets,
            bool applyMoves, Action<int, int, int, int> onProgress)
        {
            int p = 0, f = 0, s = 0;
            int total = targets.Count;
            var allPairs = new List<MatchedPair>();

            for (int i = 0; i < targets.Count; i++)
            {
                if (RunState.CancelRequested)
                {
                    Log(AppStrings.T("testing.alignSheetViews.log.stoppedByUser", i, total), "warn");
                    break;   // falls through to caller's commit
                }

                var sheet = targets[i];
                var label = $"{sheet.SheetNumber} - {sheet.Name}";
                var targetEntries = CaptureSheet(doc, sheet);
                if (targetEntries.Count == 0)
                {
                    Log(AppStrings.T("testing.alignSheetViews.log.noPlaceableViews", label), "fail");
                    f++;
                    onProgress(Pct(i + 1, total), p, f, s);
                    continue;
                }

                // Pick the best source sheet for this target.
                SourceSheet? bestSource = null;
                SheetMatch?  bestMatch  = null;
                var scored = new List<(SourceSheet src, SheetMatch m)>();
                foreach (var src in sources)
                {
                    var m = MatchSheet(src.Entries, targetEntries);
                    scored.Add((src, m));
                    if (bestMatch == null || m.Score > bestMatch.Score)
                    {
                        bestMatch  = m;
                        bestSource = src;
                    }
                }

                if (bestSource == null || bestMatch == null || bestMatch.Pairs.Count == 0)
                {
                    Log(AppStrings.T("testing.alignSheetViews.log.noCounterpart", label, sources.Count), "fail");
                    f++;
                    onProgress(Pct(i + 1, total), p, f, s);
                    continue;
                }

                Log(AppStrings.T("testing.alignSheetViews.log.bestReference", label, bestSource.Label, bestMatch.Pairs.Count, bestSource.Entries.Count), "info");
                if (sources.Count > 1)
                {
                    var others = scored.Where(x => x.src != bestSource)
                                       .Select(x => AppStrings.T("testing.alignSheetViews.log.candidateItem", x.src.Label, x.m.Pairs.Count));
                    Log(AppStrings.T("testing.alignSheetViews.log.otherCandidates", label, string.Join(", ", others)), "info");
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

                if (!applyMoves)
                {
                    foreach (var pr in bestMatch.Pairs)
                    {
                        Log(AppStrings.T("testing.alignSheetViews.log.wouldAlign", label, pr.Target.ViewName, pr.Source.ViewName, (AlignTitles ? AppStrings.T("testing.alignSheetViews.log.wouldAlignTitle") : AppStrings.T("testing.alignSheetViews.log.wouldAlignEnd"))), "info");
                        DiagnosePair(doc, pr, label);
                        // Read-only grid report: preview's job is to say whether the drawing or the
                        // tool is at fault, and it used to be silent on grids entirely.
                        if (InheritGridExtents) TrimGrids(doc, pr, label, apply: false);
                    }
                    foreach (var miss in bestMatch.Missing)
                        DiagnoseMissing(miss, targetEntries, label);
                    s += bestMatch.Pairs.Count;
                    onProgress(Pct(i + 1, total), p, f, s);
                    continue;
                }

                // ── Apply ─────────────────────────────────────────────────────
                // Phase A — geometry-affecting inheritance FIRST (scope box rewrites the crop box).
                bool geomChanged = false;
                var inheritedScope = new HashSet<long>();
                if (InheritScopeBox)
                {
                    int assigned = 0;
                    foreach (var pr in bestMatch.Pairs)
                    {
                        if (pr.Source.ScopeBoxId == ElementId.InvalidElementId) continue;
                        if (AssignScopeBox(doc, pr, label))
                        {
                            inheritedScope.Add(pr.Target.ViewId.Value);
                            geomChanged = true;
                            assigned++;
                        }
                    }
                    if (assigned > 0) Log(AppStrings.T("testing.alignSheetViews.log.scopeBoxApplied", label, assigned), "info");
                }
                if (InheritCropSize)
                {
                    foreach (var pr in bestMatch.Pairs)
                        if (InheritCropGeometry(doc, pr, inheritedScope.Contains(pr.Target.ViewId.Value), label))
                            geomChanged = true;
                }

                // One regen per target sheet so the moved crop boxes report live geometry before align.
                if (geomChanged) doc.Regenerate();

                // Phase B — align off the (now live) crop geometry.
                foreach (var pr in bestMatch.Pairs)
                {
                    if (TryAlign(doc, pr))
                    {
                        Log(AppStrings.T("testing.alignSheetViews.log.aligned", label, pr.Target.ViewName, pr.Source.ViewName), "info");
                        allPairs.Add(pr);
                        p++;
                    }
                    else
                    {
                        Log(AppStrings.T("testing.alignSheetViews.log.failedMove", label, pr.Target.ViewName), "fail");
                        f++;
                    }
                }

                // Phase C — view-only inheritance that does not move the viewport.
                if (InheritGridExtents)    foreach (var pr in bestMatch.Pairs) TrimGrids(doc, pr, label, apply: true);
                if (InheritCropVisibility) foreach (var pr in bestMatch.Pairs) SetCropVisibility(doc, pr, label);

                onProgress(Pct(i + 1, total), p, f, s);
            }

            return (p, f, s, allPairs);
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
                return prm.Set(pr.Source.ScopeBoxId);
            }
            catch (Exception ex)
            {
                Log(AppStrings.T("testing.alignSheetViews.log.couldNotAssignScope", label, pr.Target.ViewName), "warn");
                DiagnosticsLog.Swallowed($"AlignSheetViews: scope box on view {pr.Target.ViewId.Value}", ex);
                return false;
            }
        }

        // ── Inheritance: crop size + annotation crop ──────────────────────────
        private bool InheritCropGeometry(Document doc, MatchedPair pr, bool gotScopeBox, string label)
        {
            bool changed = false;
            try
            {
                if (!(doc.GetElement(pr.Target.ViewId) is View tv)) return false;

                // Crop size — resize the target crop to the source crop's dimensions, keeping the
                // target's own crop centre (alignment then centres it on the shared anchor).
                // Skipped whenever a scope box governs the crop region: one the view inherited this
                // run, OR one it ALREADY carried. The second case used to slip through, and writing
                // CropBox underneath a scope box fights it and yields an unpredictable crop.
                bool scopeGoverned = gotScopeBox;
                if (!scopeGoverned)
                {
                    var tsb = tv.get_Parameter(BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP);
                    scopeGoverned = tsb != null
                                 && tsb.StorageType == StorageType.ElementId
                                 && tsb.AsElementId() != ElementId.InvalidElementId;
                }

                if (pr.Source.Scale != pr.Target.Scale)
                    Log(AppStrings.T("testing.alignSheetViews.log.annoScaleDiffers", label, pr.Target.ViewName), "warn");

                if (scopeGoverned)
                {
                    Log(AppStrings.T("testing.alignSheetViews.log.cropScopeGoverned", label, pr.Target.ViewName), "info");
                }
                else
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
                            changed = true;
                        }
                    }
                }

                // Annotation crop offsets — replicate the source's annotation crop margins.
                changed |= InheritAnnotationCrop(doc, pr, tv, label);
            }
            catch (Exception ex)
            {
                Log(AppStrings.T("testing.alignSheetViews.log.couldNotMatchCropSize", label, pr.Target.ViewName), "warn");
                DiagnosticsLog.Swallowed($"AlignSheetViews: crop size on view {pr.Target.ViewId.Value}", ex);
            }
            return changed;
        }

        private bool InheritAnnotationCrop(Document doc, MatchedPair pr, View tv, string label)
        {
            try
            {
                if (!(doc.GetElement(pr.Source.ViewId) is View sv)) return false;
                var srcActive = sv.get_Parameter(BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE);
                if (srcActive == null || srcActive.AsInteger() != 1) return false; // source has no annotation crop

                ViewCropRegionShapeManager sm = sv.GetCropRegionShapeManager();
                double top = sm.TopAnnotationCropOffset, bot = sm.BottomAnnotationCropOffset;
                double left = sm.LeftAnnotationCropOffset, right = sm.RightAnnotationCropOffset;

                var tgtActive = tv.get_Parameter(BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE);
                if (tgtActive == null || tgtActive.IsReadOnly) return false;
                tv.CropBoxActive = true;
                tgtActive.Set(1);

                ViewCropRegionShapeManager tm = tv.GetCropRegionShapeManager();
                tm.TopAnnotationCropOffset    = Math.Max(MinCropOffsetFt, top);
                tm.BottomAnnotationCropOffset = Math.Max(MinCropOffsetFt, bot);
                tm.LeftAnnotationCropOffset   = Math.Max(MinCropOffsetFt, left);
                tm.RightAnnotationCropOffset  = Math.Max(MinCropOffsetFt, right);
                return true;
            }
            catch (Exception ex)
            {
                Log(AppStrings.T("testing.alignSheetViews.log.couldNotMatchAnnoCrop", label, pr.Target.ViewName), "warn");
                DiagnosticsLog.Swallowed($"AlignSheetViews: annotation crop on view {pr.Target.ViewId.Value}", ex);
                return false;
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

        // ── Inheritance: grid 2D (view-specific) extents ──────────────────────
        /// <summary>
        /// Makes each grid's displayed endpoints — and its head/tail bubbles — in the target view
        /// match the same grid in the source view. Source and target grid are one element; only the
        /// per-view override differs, so the source curve is collinear with the target grid by
        /// construction.
        ///
        /// A datum's extent mode is per view AND per end (the 3D/2D toggle in the UI), so this is
        /// not a plain get/set. Four rules the earlier versions each got wrong:
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
        ///
        /// With <paramref name="apply"/> false nothing is written — every read, comparison and
        /// verdict still runs and is reported, which is how preview mode answers "is this the
        /// drawing or the tool?" without touching the model.
        /// </summary>
        private void TrimGrids(Document doc, MatchedPair pr, string label, bool apply)
        {
            try
            {
                if (!(doc.GetElement(pr.Source.ViewId) is View sv)) return;
                if (!(doc.GetElement(pr.Target.ViewId) is View tv)) return;

                var srcGrids = new FilteredElementCollector(doc, sv.Id)
                    .OfClass(typeof(Grid))
                    .Cast<Grid>()
                    .ToList();

                if (srcGrids.Count == 0)
                {
                    Log(AppStrings.T("testing.alignSheetViews.log.noGrids", label, pr.Source.ViewName), "info");
                    return;
                }

                // A grid the target shows and the source does not can never be matched. Name it
                // rather than leaving a difference the user has to spot on the plot.
                var srcIds = new HashSet<long>(srcGrids.Select(g => g.Id.Value));
                int targetOnly = new FilteredElementCollector(doc, tv.Id)
                    .OfClass(typeof(Grid))
                    .Cast<Grid>()
                    .Count(g => !srcIds.Contains(g.Id.Value));

                // Each outcome is counted separately — a bare "skipped" number cannot be acted on.
                var t       = new GridTally();
                var writes  = new List<GridWrite>();
                var visible = new List<Grid>();   // fed to the elbow pass once extents are final

                // ── Pass 1 — read the source, decide, switch the target's extent mode ──
                foreach (var g in srcGrids)
                {
                    try
                    {
                        // Visibility is a HINT, never a veto. CanBeVisibleInView rejects grids the
                        // user can plainly see, and gating on it skipped the trim outright — the same
                        // mistake as trusting IsCurveValidInView. Reconcile whatever DIFFERS between
                        // the two views, then do the work regardless and let Revit's own exception be
                        // the verdict if it really cannot be done.
                        ReconcileVisibility(doc, g, sv, tv, apply, t, label, pr);
                        visible.Add(g);

                        // Bubbles are a property of their own, independent of the extents, so they
                        // are matched for EVERY visible grid — before the curve work and regardless
                        // of how it turns out. Doing this only on the success paths is what made
                        // heads move on some grids and not others in the same view.
                        MirrorBubbles(g, sv, tv, apply, t);

                        // Source on model extents: match it by putting the target back on model too.
                        if (!UsesViewSpecific(g, sv))
                        {
                            t.ModelSource++;
                            if (ResetEndsToModel(g, tv, apply)) t.Changed++;
                            continue;
                        }

                        // No Model fallback here: falling back would copy the grid's shared 3D
                        // extent and report it as a successful trim, which is a silent wrong answer.
                        IList<Curve>? curves = TryGetCurves(g, DatumExtentType.ViewSpecific, sv);
                        if (curves == null || curves.Count == 0)
                        {
                            t.NoSourceCurve++;
                            DiagnosticsLog.Warn("AlignSheetViews",
                                $"Grid {g.Id.Value} ('{g.Name}') claims view-specific extents in source view {sv.Id.Value} but returned no curve — not trimmed.");
                            continue;
                        }
                        // SetCurveInView takes a single curve, so a multi-segment grid can only carry
                        // its first segment across. Say so rather than dropping the rest silently.
                        if (curves.Count > 1) t.MultiSegment++;

                        // Revit rejects an unbound curve outright ("the curve is unbound or not
                        // coincident"), so catch it here where it can be named.
                        if (curves[0] is Line sl && !sl.IsBound)
                        {
                            t.Unbound++;
                            DiagnosticsLog.Warn("AlignSheetViews",
                                $"Grid {g.Id.Value} ('{g.Name}') returned an unbound line from source view {sv.Id.Value} — not trimmed.");
                            continue;
                        }

                        // The target's OWN current curve is the reference plane: whatever Revit
                        // reports for this datum in this view is by definition coincident with the
                        // datum's line there, which is the exact test SetCurveInView applies.
                        Curve? existing = DisplayedCurve(g, tv);
                        Curve  c        = CoincideWith(curves[0], existing, tv);

                        if (existing != null && CurvesMatch(existing, c))
                        {
                            t.AlreadyMatching++;
                            continue;
                        }

                        if (!apply)
                        {
                            t.Changed++;
                            if (existing != null)
                                Log(AppStrings.T("testing.alignSheetViews.log.gridWouldMove", label, pr.Target.ViewName, g.Name,
                                        F(existing.GetEndPoint(0).DistanceTo(c.GetEndPoint(0))),
                                        F(existing.GetEndPoint(1).DistanceTo(c.GetEndPoint(1)))), "info");
                            continue;
                        }

                        // Record the original modes so a rejected write can put them back.
                        var m0 = TryGetExtentType(g, DatumEnds.End0, tv);
                        var m1 = TryGetExtentType(g, DatumEnds.End1, tv);

                        g.SetDatumExtentType(DatumEnds.End0, tv, DatumExtentType.ViewSpecific);
                        g.SetDatumExtentType(DatumEnds.End1, tv, DatumExtentType.ViewSpecific);
                        writes.Add(new GridWrite(g, c, m0, m1));
                    }
                    catch (Exception ex)
                    {
                        t.Errored++;
                        DiagnosticsLog.Swallowed($"AlignSheetViews: prepare grid {g.Id.Value} for view {tv.Id.Value}", ex);
                    }
                }

                // One regen for the whole view so the mode switches are live before any curve is
                // written — never per grid (a regen recomputes the entire model).
                if (writes.Count > 0) doc.Regenerate();

                // ── Pass 2 — write the curves ──
                foreach (var w in writes)
                {
                    try
                    {
                        if (!w.Grid.IsCurveValidInView(DatumExtentType.ViewSpecific, tv, w.Curve))
                        {
                            // Advisory, not authoritative: attempt the write anyway and let the throw
                            // (if any) be the verdict, so a false negative can never silently skip.
                            DiagnosticsLog.Warn("AlignSheetViews",
                                $"Grid {w.Grid.Id.Value} ('{w.Grid.Name}') reported its source curve invalid in view {tv.Id.Value} — writing anyway.");
                        }
                        w.Grid.SetCurveInView(DatumExtentType.ViewSpecific, tv, w.Curve);
                        t.Changed++;
                    }
                    catch (Exception ex)
                    {
                        t.Rejected++;
                        DiagnosticsLog.Swallowed($"AlignSheetViews: set curve for grid {w.Grid.Id.Value} in view {tv.Id.Value}", ex);
                        // A failed restore leaves the end in 2D mode with no curve written — a
                        // half-change the user has to know about, not just a diagnostics entry.
                        if (!RestoreEnds(w, tv))
                            Log(AppStrings.T("testing.alignSheetViews.log.gridsRestoreFailed", label, pr.Target.ViewName, w.Grid.Name), "warn");
                    }
                }

                // ── Pass 3 — leader elbows, once the extents are final ──
                // An elbow hangs off the grid end, so it can only be placed after the curve write.
                foreach (var g in visible) MirrorElbows(g, sv, tv, apply, t, label, pr);

                ReportGridTally(t, targetOnly, pr, label, apply);
            }
            catch (Exception ex)
            {
                Log(AppStrings.T("testing.alignSheetViews.log.couldNotTrimGrids", label, pr.Target.ViewName), "warn");
                DiagnosticsLog.Swallowed($"AlignSheetViews: trim grids on view {pr.Target.ViewId.Value}", ex);
            }
        }

        /// <summary>Every outcome gets a line — a zero result always names its reason.</summary>
        private void ReportGridTally(GridTally t, int targetOnly, MatchedPair pr, string label, bool apply)
        {
            Log(AppStrings.T(apply ? "testing.alignSheetViews.log.gridsResult"
                                   : "testing.alignSheetViews.log.gridsWouldResult",
                             label, pr.Target.ViewName, t.Changed, t.AlreadyMatching),
                t.Changed > 0 || t.AlreadyMatching > 0 ? "info" : "warn");

            if (t.Bubbles > 0)
                Log(AppStrings.T(apply ? "testing.alignSheetViews.log.gridsBubbles"
                                       : "testing.alignSheetViews.log.gridsWouldBubbles",
                                 label, pr.Target.ViewName, t.Bubbles), "info");
            if (t.Elbows > 0)
                Log(AppStrings.T(apply ? "testing.alignSheetViews.log.gridsElbows"
                                       : "testing.alignSheetViews.log.gridsWouldElbows",
                                 label, pr.Target.ViewName, t.Elbows), "info");
            if (t.ModelSource > 0)
                Log(AppStrings.T("testing.alignSheetViews.log.gridsModelSource", label, pr.Source.ViewName, t.ModelSource), "info");
            if (t.Restored > 0)
                Log(AppStrings.T("testing.alignSheetViews.log.gridsRestored", label, pr.Target.ViewName, t.Restored), "info");
            if (t.Blocked > 0)
                Log(AppStrings.T("testing.alignSheetViews.log.gridsBlocked", label, pr.Target.ViewName, t.Blocked), "warn");
            if (t.VisibilityUnexplained > 0)
                Log(AppStrings.T("testing.alignSheetViews.log.gridsVisibilityUnexplained", label, pr.Target.ViewName, t.VisibilityUnexplained), "warn");
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
            if (targetOnly > 0)
                Log(AppStrings.T("testing.alignSheetViews.log.gridsTargetOnly", label, pr.Target.ViewName, targetOnly), "warn");
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

        /// <summary>
        /// Brings the target view's grid-visibility settings into line with the source view's, and
        /// names what it could not change.
        ///
        /// Only settings that DIFFER between the two views are touched or reported. A filter, hidden
        /// category or hidden workset that is identical on both views cannot explain why a grid shows
        /// in one and not the other, so naming it is a false lead — which is exactly what the first
        /// version of this did, blaming a filter that was present on the source view too.
        ///
        /// A filter is matched against the grid through the filter's OWN rules
        /// (<see cref="ElementFilter.PassesFilter(Element)"/>), so a filter that merely covers the
        /// Grids category but whose rules this grid does not satisfy is never blamed. One that
        /// genuinely differs is reported but not flipped: its visibility governs every category it
        /// names, so clearing it would silently reveal everything else it hides.
        /// </summary>
        private void ReconcileVisibility(Document doc, Grid g, View sv, View tv, bool apply,
                                         GridTally t, string label, MatchedPair pr)
        {
            var cleared  = new List<string>();
            var blockers = new List<string>();
            try
            {
                // 1 — element hidden in the target but not the source.
                if (g.IsHidden(tv) && !g.IsHidden(sv))
                {
                    cleared.Add(AppStrings.T("testing.alignSheetViews.log.gridCauseHidden"));
                    if (apply) tv.UnhideElements(new List<ElementId> { g.Id });
                }

                // 2 — Grids category switched off in the target but not the source.
                var cat = Category.GetCategory(doc, BuiltInCategory.OST_Grids);
                if (cat != null && tv.GetCategoryHidden(cat.Id) && !sv.GetCategoryHidden(cat.Id))
                {
                    cleared.Add(AppStrings.T("testing.alignSheetViews.log.gridCauseCategory"));
                    if (apply && tv.CanCategoryBeHidden(cat.Id)) tv.SetCategoryHidden(cat.Id, false);
                }

                // 3 — the grid's workset hidden in the target but not the source.
                if (doc.IsWorkshared)
                {
                    WorksetId ws = g.WorksetId;
                    if (ws != null
                        && tv.GetWorksetVisibility(ws) == WorksetVisibility.Hidden
                        && sv.GetWorksetVisibility(ws) != WorksetVisibility.Hidden)
                    {
                        cleared.Add(AppStrings.T("testing.alignSheetViews.log.gridCauseWorkset"));
                        if (apply) tv.SetWorksetVisibility(ws, WorksetVisibility.Visible);
                    }
                }

                // 4 — a filter that hides THIS grid in the target and does not do so in the source.
                foreach (var fid in tv.GetFilters())
                {
                    if (tv.GetFilterVisibility(fid)) continue;
                    if (!(doc.GetElement(fid) is ParameterFilterElement pf)) continue;
                    if (!FilterCatches(pf, g)) continue;
                    if (SourceHidesToo(sv, fid)) continue;   // identical on both — cannot be the cause
                    blockers.Add(AppStrings.T("testing.alignSheetViews.log.gridCauseFilter", pf.Name));
                }
            }
            catch (Exception ex)
            {
                blockers.Add(AppStrings.T("testing.alignSheetViews.log.gridCauseTemplate", TemplateName(doc, tv)));
                DiagnosticsLog.Swallowed($"AlignSheetViews: reconcile visibility of grid {g.Id.Value} in view {tv.Id.Value}", ex);
            }

            if (cleared.Count > 0)
            {
                t.Restored++;
                Log(AppStrings.T(apply ? "testing.alignSheetViews.log.gridRestored"
                                       : "testing.alignSheetViews.log.gridWouldRestore",
                                 label, pr.Target.ViewName, g.Name, string.Join(", ", cleared)), "info");
            }
            if (blockers.Count > 0)
            {
                t.Blocked++;
                Log(AppStrings.T("testing.alignSheetViews.log.gridBlocked",
                                 label, pr.Target.ViewName, g.Name, string.Join(", ", blockers)), "warn");
            }
            // Revit says it cannot show here and nothing in the two views' settings differs. Worth a
            // count — the trim is still attempted, so this is context, not a skip.
            if (cleared.Count == 0 && blockers.Count == 0 && !CanBeVisible(g, tv) && CanBeVisible(g, sv))
                t.VisibilityUnexplained++;
        }

        /// <summary>True when the filter's own rules actually select this grid. A filter that names
        /// the Grids category but whose rules the grid fails is not what is hiding it.</summary>
        private static bool FilterCatches(ParameterFilterElement pf, Grid g)
        {
            try
            {
                if (!pf.GetCategories().Any(c => c.Value == (long)BuiltInCategory.OST_Grids)) return false;
                ElementFilter ef = pf.GetElementFilter();
                return ef == null || ef.PassesFilter(g);   // a rule-less filter catches the whole category
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed($"AlignSheetViews: evaluate filter {pf.Id.Value} against grid {g.Id.Value}", ex);
                return false;   // cannot prove it applies — do not blame it
            }
        }

        /// <summary>True when the source view carries the same filter and also hides it.</summary>
        private static bool SourceHidesToo(View sv, ElementId filterId)
        {
            try { return sv.GetFilters().Contains(filterId) && !sv.GetFilterVisibility(filterId); }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed($"AlignSheetViews: filter {filterId.Value} on source view {sv.Id.Value}", ex);
                return false;
            }
        }

        private static string TemplateName(Document doc, View v)
        {
            try
            {
                if (v.ViewTemplateId == ElementId.InvalidElementId) return "—";
                return (doc.GetElement(v.ViewTemplateId) as View)?.Name ?? "—";
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed($"AlignSheetViews: template name of view {v.Id.Value}", ex);
                return "—";
            }
        }

        /// <summary>Puts both of a grid's ends in the target view back on model extents.</summary>
        private static bool ResetEndsToModel(Grid g, View tv, bool apply)
        {
            bool needed = TryGetExtentType(g, DatumEnds.End0, tv) != DatumExtentType.Model
                       || TryGetExtentType(g, DatumEnds.End1, tv) != DatumExtentType.Model;
            if (!needed) return false;
            if (!apply)  return true;   // preview: report the change, write nothing
            g.SetDatumExtentType(DatumEnds.End0, tv, DatumExtentType.Model);
            g.SetDatumExtentType(DatumEnds.End1, tv, DatumExtentType.Model);
            return true;
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
        private static void MirrorBubbles(Grid g, View sv, View tv, bool apply, GridTally t)
        {
            foreach (var end in new[] { DatumEnds.End0, DatumEnds.End1 })
            {
                try
                {
                    if (!g.HasBubbleInView(end, tv)) continue;
                    bool want = g.IsBubbleVisibleInView(end, sv);
                    if (want == g.IsBubbleVisibleInView(end, tv)) continue;
                    if (!apply) { t.Bubbles++; continue; }
                    if (want) g.ShowBubbleInView(end, tv);
                    else      g.HideBubbleInView(end, tv);
                    t.Bubbles++;   // counted only once the write actually landed
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
        /// its neighbours — from the source view to the target.
        ///
        /// Matched in BOTH directions: a source end with no elbow has any elbow on the target
        /// removed, so the two views genuinely agree instead of the target keeping jogs the source
        /// does not have. Elbow and End are absolute model points, so they are shifted onto the
        /// target view's plane exactly as the extent curve is — a leader copied across levels
        /// otherwise lands at the source's elevation.
        /// </summary>
        private void MirrorElbows(Grid g, View sv, View tv, bool apply, GridTally t, string label, MatchedPair pr)
        {
            XYZ delta = LeaderPlaneDelta(g, sv, tv);

            foreach (var end in new[] { DatumEnds.End0, DatumEnds.End1 })
            {
                try
                {
                    Leader? srcLeader = TryGetLeader(g, end, sv);
                    Leader? tgtLeader = TryGetLeader(g, end, tv);

                    if (srcLeader == null)
                    {
                        if (tgtLeader == null) continue;
                        if (!apply) { t.Elbows++; continue; }
                        try
                        {
                            // DatumPlane has no RemoveLeader in the 2024 API — clearing an elbow is a
                            // null SetLeader. If that is rejected the target keeps a jog the source
                            // does not have, which is a visible difference and must be said out loud.
                            g.SetLeader(end, tv, null);
                            t.Elbows++;
                        }
                        catch (Exception rex)
                        {
                            Log(AppStrings.T("testing.alignSheetViews.log.gridElbowStuck",
                                             label, pr.Target.ViewName, g.Name), "warn");
                            DiagnosticsLog.Swallowed($"AlignSheetViews: clear leader {end} of grid {g.Id.Value} in view {tv.Id.Value}", rex);
                        }
                        continue;
                    }

                    // Only an end that carries a bubble here can carry a leader.
                    if (!g.HasBubbleInView(end, tv)) continue;

                    XYZ elbow = srcLeader.Elbow + delta;
                    XYZ tip   = srcLeader.End   + delta;

                    if (tgtLeader != null
                        && tgtLeader.Elbow.DistanceTo(elbow) < CurveMatchTolFt
                        && tgtLeader.End.DistanceTo(tip)     < CurveMatchTolFt)
                        continue;   // already matching — do not count a no-op as a change

                    t.Elbows++;
                    if (!apply) continue;

                    // AddLeader returns the new leader — no need to read it back.
                    if (tgtLeader == null) tgtLeader = g.AddLeader(end, tv);
                    if (tgtLeader == null) continue;
                    tgtLeader.Elbow = elbow;
                    tgtLeader.End   = tip;
                }
                catch (Exception ex)
                {
                    DiagnosticsLog.Swallowed($"AlignSheetViews: elbow {end} of grid {g.Id.Value} in view {tv.Id.Value}", ex);
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

        /// <summary>Plane offset between the grid's own displayed curve in each view.</summary>
        private static XYZ LeaderPlaneDelta(Grid g, View sv, View tv)
        {
            Curve? sc = DisplayedCurve(g, sv);
            Curve? tc = DisplayedCurve(g, tv);
            if (sc == null || tc == null) return XYZ.Zero;
            return PlaneDelta(sc.GetEndPoint(0), tc.GetEndPoint(0), tv);
        }

        private const double CurveMatchTolFt = 1e-6;

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

        /// <summary>True when either end has been dragged in this view (2D override in play).</summary>
        private static bool UsesViewSpecific(Grid g, View view)
            => TryGetExtentType(g, DatumEnds.End0, view) == DatumExtentType.ViewSpecific
            || TryGetExtentType(g, DatumEnds.End1, view) == DatumExtentType.ViewSpecific;

        /// <summary>The curve the grid actually displays in <paramref name="view"/>, read from
        /// whichever extent mode that view is using.</summary>
        private static Curve? DisplayedCurve(Grid g, View view)
        {
            var mode   = UsesViewSpecific(g, view) ? DatumExtentType.ViewSpecific : DatumExtentType.Model;
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

        // ── Post-placement drift check ────────────────────────────────────────
        /// <summary>
        /// Confirms every aligned viewport is still on the centre the alignment computed, and — when
        /// <paramref name="correct"/> is set — puts back any that moved.
        ///
        /// SetBoxCenter positions the viewport's on-sheet FOOTPRINT, and the footprint is derived
        /// from the view's crop plus annotation crop. So anything that touches those after the align
        /// phase shifts the drawing on the sheet even though nothing asked the viewport to move. This
        /// pass makes that class of bug impossible to ship silently: a drifted viewport is named,
        /// with its distance, rather than showing up as "the views are slightly off".
        ///
        /// A clean report is just as useful — it rules the align phase's own arithmetic in as the
        /// remaining suspect instead of leaving both possibilities open.
        /// </summary>
        private void VerifyPlacement(Document doc, List<MatchedPair> pairs, bool correct)
        {
            int drifted = 0, failed = 0;
            double worst = 0; string worstName = "";

            foreach (var pr in pairs)
            {
                try
                {
                    if (pr.IntendedCentre == null) continue;
                    if (!(doc.GetElement(pr.Target.ViewportId) is Viewport vp)) continue;

                    XYZ now = vp.GetBoxCenter();
                    if (now == null) { failed++; continue; }

                    double d = now.DistanceTo(pr.IntendedCentre);
                    if (d <= PlacementTolFt) continue;

                    drifted++;
                    if (d > worst) { worst = d; worstName = pr.Target.ViewName; }
                    DiagnosticsLog.Warn("AlignSheetViews",
                        $"Viewport {pr.Target.ViewportId.Value} ('{pr.Target.ViewName}') sits {d:0.######}' from its aligned centre.");

                    if (correct) vp.SetBoxCenter(pr.IntendedCentre);
                }
                catch (Exception ex)
                {
                    failed++;
                    DiagnosticsLog.Swallowed($"AlignSheetViews: placement check on viewport {pr.Target.ViewportId.Value}", ex);
                }
            }

            if (drifted == 0)
                Log(AppStrings.T(correct ? "testing.alignSheetViews.log.placementOk"
                                         : "testing.alignSheetViews.log.placementOkTitles", pairs.Count), "info");
            else
                Log(AppStrings.T(correct ? "testing.alignSheetViews.log.placementCorrected"
                                         : "testing.alignSheetViews.log.placementDriftTitles",
                                 drifted, worstName, F(worst)), "warn");

            if (failed > 0)
                Log(AppStrings.T("testing.alignSheetViews.log.placementUnchecked", failed), "warn");
        }

        // ── View-title (label) alignment ──────────────────────────────────────
        /// <summary>
        /// Runs after every viewport box is in its final position. Sets each target title's line
        /// length to its source's, regenerates once so the moved/resized titles report their real
        /// on-sheet outlines, then shifts each target's LabelOffset so its title outline overlays
        /// the source title's. A title that can't be read or set is reported and skipped.
        /// </summary>
        private void AlignAllTitles(Document doc, List<MatchedPair> pairs)
        {
            // Pass 1 — match line length (independent of position) before the regen.
            foreach (var pr in pairs)
            {
                try
                {
                    if (!(doc.GetElement(pr.Source.ViewportId) is Viewport sVp)) continue;
                    if (!(doc.GetElement(pr.Target.ViewportId) is Viewport tVp)) continue;
                    tVp.LabelLineLength = sVp.LabelLineLength;
                }
                catch (Exception ex)
                {
                    Log(AppStrings.T("testing.alignSheetViews.log.couldNotMatchTitleLine", pr.Label, pr.Target.ViewName), "warn");
                    DiagnosticsLog.Swallowed($"AlignSheetViews: LabelLineLength on viewport {pr.Target.ViewportId.Value}", ex);
                }
            }

            // Regen so GetLabelOutline reflects both the moved box and the new line length.
            doc.Regenerate();

            // Pass 2 — shift each target label so its outline overlays the source label's.
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

            string tone = titleFail > 0 ? "warn" : "info";
            Log(titleFail > 0 ? AppStrings.T("testing.alignSheetViews.log.viewTitlesSome", titled, titleFail) : AppStrings.T("testing.alignSheetViews.log.viewTitlesOk", titled), tone);
        }

        // ── Preview diagnostics ───────────────────────────────────────────────
        /// <summary>
        /// Preview-only, read-only report for one matched pair. Its job is to SETTLE, on a real plot,
        /// whether a viewport's on-sheet footprint includes the annotation crop - the assumption the
        /// alignment fix rests on. It prints the actual <c>GetBoxOutline</c> size next to the size
        /// predicted with and without the annotation offsets; whichever matches is the answer.
        /// It also prints the box centre the old and new formulas produce, so the views that were
        /// being misplaced (and by how much) are named explicitly.
        /// </summary>
        private void DiagnosePair(Document doc, MatchedPair pr, string label)
        {
            try
            {
                var src = pr.Source; var tgt = pr.Target;
                if (!(doc.GetElement(tgt.ViewId) is View tv)) return;
                BoundingBoxXYZ cb = tv.CropBox;
                if (cb?.Transform == null) return;
                int scale = tv.Scale;
                if (scale <= 0 || src.Scale <= 0) return;

                Log(AppStrings.T("testing.alignSheetViews.log.diagHeader", label, tgt.ViewName, src.ViewName), "info");
                Log(AppStrings.T("testing.alignSheetViews.log.diagScale",
                        src.Scale, tgt.Scale, tgt.Rotation,
                        F(src.Transform.BasisX.DotProduct(tgt.Transform.BasisX)),
                        F(src.Transform.BasisY.DotProduct(tgt.Transform.BasisY))), "info");

                double scw = src.CropMax.X - src.CropMin.X, sch = src.CropMax.Y - src.CropMin.Y;
                double tcw = cb.Max.X - cb.Min.X,           tch = cb.Max.Y - cb.Min.Y;
                Log(AppStrings.T("testing.alignSheetViews.log.diagCrop", F(scw), F(sch), F(tcw), F(tch)), "info");

                ReadAnnotationCrop(tv, out bool tAct, out double tTop, out double tBot, out double tLeft, out double tRight);
                Log(AppStrings.T("testing.alignSheetViews.log.diagAnno", src.AnnoCropActive, tAct), "info");
                Log(AppStrings.T("testing.alignSheetViews.log.diagAnnoOff",
                        F(src.AnnoLeft), F(src.AnnoRight), F(src.AnnoBottom), F(src.AnnoTop),
                        F(tLeft), F(tRight), F(tBot), F(tTop)), "info");

                // The decisive test: actual footprint vs the two predictions.
                if (TryOutlineSize(doc, src.ViewportId, out double sow, out double soh) &&
                    TryOutlineSize(doc, tgt.ViewportId, out double tow, out double toh))
                {
                    double sNoW = scw / src.Scale,  sNoH = sch / src.Scale;
                    double tNoW = tcw / scale,      tNoH = tch / scale;
                    double sYesW = (scw + (src.AnnoCropActive ? src.AnnoLeft + src.AnnoRight : 0)) / src.Scale;
                    double sYesH = (sch + (src.AnnoCropActive ? src.AnnoTop + src.AnnoBottom : 0)) / src.Scale;
                    double tYesW = (tcw + (tAct ? tLeft + tRight : 0)) / scale;
                    double tYesH = (tch + (tAct ? tTop  + tBot   : 0)) / scale;

                    Log(AppStrings.T("testing.alignSheetViews.log.diagOutline", F(sow), F(soh), F(tow), F(toh)), "info");
                    Log(AppStrings.T("testing.alignSheetViews.log.diagPredNoAnno", F(sNoW), F(sNoH), F(tNoW), F(tNoH)), "info");
                    Log(AppStrings.T("testing.alignSheetViews.log.diagPredAnno",  F(sYesW), F(sYesH), F(tYesW), F(tYesH)), "info");

                    if (src.AnnoCropActive || tAct)
                    {
                        double errNo  = Math.Abs(sow - sNoW)  + Math.Abs(soh - sNoH)  + Math.Abs(tow - tNoW)  + Math.Abs(toh - tNoH);
                        double errYes = Math.Abs(sow - sYesW) + Math.Abs(soh - sYesH) + Math.Abs(tow - tYesW) + Math.Abs(toh - tYesH);
                        Log(AppStrings.T("testing.alignSheetViews.log.diagVerdict",
                                AppStrings.T(errYes <= errNo
                                    ? "testing.alignSheetViews.log.diagVerdictYes"
                                    : "testing.alignSheetViews.log.diagVerdictNo")), errYes <= errNo ? "info" : "warn");
                    }
                }

                // What the two formulas would do to this viewport.
                XYZ oldC = LegacyTargetBoxCentre(src, cb, scale);
                XYZ newC = TargetBoxCentre(src, tgt, tv, cb, scale);
                double moved = newC.DistanceTo(oldC);
                Log(AppStrings.T("testing.alignSheetViews.log.diagCentres", P(src.BoxCenter), P(tgt.BoxCenter)), "info");
                Log(AppStrings.T("testing.alignSheetViews.log.diagNewCentre", P(oldC), P(newC), F(moved)),
                    moved > 1e-6 ? "warn" : "info");

                if (tgt.Rotation != ViewportRotation.None)
                {
                    XYZ anchor = AnchorWorld(src);
                    XYZ local  = cb.Transform.Inverse.OfPoint(anchor);
                    FootprintCentre(cb.Min, cb.Max, tAct, tTop, tBot, tLeft, tRight, out double fx, out double fy);
                    var raw = new XYZ((local.X - fx) / scale, (local.Y - fy) / scale, 0);
                    XYZ a = SourceAnchorOnSheet(src) - new XYZ( raw.Y, -raw.X, 0);
                    XYZ b = SourceAnchorOnSheet(src) - new XYZ(-raw.Y,  raw.X, 0);
                    Log(AppStrings.T("testing.alignSheetViews.log.diagRotCandidates", P(a), P(b)), "warn");
                }
            }
            catch (Exception ex)
            {
                Log(AppStrings.T("testing.alignSheetViews.log.diagFailed", label, pr.Target.ViewName), "warn");
                DiagnosticsLog.Swallowed($"AlignSheetViews: diagnostics for viewport {pr.Target.ViewportId.Value}", ex);
            }
        }

        /// <summary>
        /// Preview-only report for a source view that found no counterpart, listing the candidates it
        /// was compared against and whether each failed on in-plane overlap or purely on DEPTH. The
        /// depth veto is worth surfacing: cross-level alignment is the tool's main use case, and two
        /// levels' plan-view depth ranges do not necessarily overlap.
        /// </summary>
        private void DiagnoseMissing(VpEntry miss, List<VpEntry> targetEntries, string label)
        {
            try
            {
                Log(AppStrings.T("testing.alignSheetViews.log.diagMissingHdr", label, miss.ViewName), "info");

                var cands = targetEntries.Where(t => Eligible(miss, t)).ToList();
                if (cands.Count == 0)
                {
                    Log(AppStrings.T("testing.alignSheetViews.log.diagNoCands"), "info");
                    return;
                }
                foreach (var t in cands)
                {
                    OverlapComponents(miss, t, out double inPlane, out double depth);
                    Log(AppStrings.T("testing.alignSheetViews.log.diagMissingCand",
                            t.ViewName,
                            F(inPlane * 100.0),
                            F(OverlapThreshold * 100.0),
                            AppStrings.T(depth > 0
                                ? "testing.alignSheetViews.log.diagDepthOk"
                                : "testing.alignSheetViews.log.diagDepthNo")), "info");
                }
            }
            catch (Exception ex)
            {
                Log(AppStrings.T("testing.alignSheetViews.log.diagFailed", label, miss.ViewName), "warn");
                DiagnosticsLog.Swallowed($"AlignSheetViews: missing-match diagnostics for view {miss.ViewId.Value}", ex);
            }
        }

        private static bool TryOutlineSize(Document doc, ElementId viewportId, out double w, out double h)
        {
            w = 0; h = 0;
            try
            {
                if (!(doc.GetElement(viewportId) is Viewport vp)) return false;
                Outline o = vp.GetBoxOutline();
                if (o == null) return false;
                w = o.MaximumPoint.X - o.MinimumPoint.X;
                h = o.MaximumPoint.Y - o.MinimumPoint.Y;
                return true;
            }
            catch (Exception ex)
            {
                // Diagnostics-only: the caller simply omits the measured-outline line. Recorded so a
                // missing ACTUAL row in the preview report is explainable rather than a mystery.
                DiagnosticsLog.Swallowed($"AlignSheetViews: GetBoxOutline on viewport {viewportId.Value}", ex);
                return false;
            }
        }

        private static string F(double v) => v.ToString("0.####");
        private static string P(XYZ p)    => $"({p.X.ToString("0.####")}, {p.Y.ToString("0.####")})";

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
                        CropActive    = view.CropBoxActive,
                        CropBoxVisible = view.CropBoxVisible,
                        ScopeBoxId    = scopeBox,
                        AnnoCropActive = annoActive,
                        AnnoTop       = annoTop,
                        AnnoBottom    = annoBot,
                        AnnoLeft      = annoLeft,
                        AnnoRight     = annoRight,
                    });

                    if (!view.CropBoxActive)
                        Log(AppStrings.T("testing.alignSheetViews.log.noteNoCrop", view.Name), "info");
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
            OverlapComponents(src, cand, out double inPlane, out double depth);
            return depth <= 0 ? 0 : inPlane;
        }

        /// <summary>
        /// Splits the overlap test into its in-plane fraction and its depth (cut-range) overlap, so
        /// the preview diagnostics can report WHICH of the two vetoed a candidate. A candidate
        /// rejected purely on depth is worth seeing: cross-level alignment is the tool's main use
        /// case, and plan-view depth ranges do not always overlap between levels.
        /// </summary>
        private static void OverlapComponents(VpEntry src, VpEntry cand, out double inPlaneFraction, out double depthOverlap)
        {
            inPlaneFraction = 0; depthOverlap = 0;
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

            depthOverlap = Overlap1D(sNmin, sNmax, nMin, nMax);

            double ov = Overlap1D(sUmin, sUmax, uMin, uMax) * Overlap1D(sVmin, sVmax, vMin, vMax);
            if (ov <= 0) return;

            double areaS = Math.Abs((sUmax - sUmin) * (sVmax - sVmin));
            double areaC = Math.Abs((uMax - uMin) * (vMax - vMin));
            double denom = Math.Min(areaS, areaC);
            inPlaneFraction = denom <= 1e-9 ? 0 : ov / denom;
        }

        private static double Overlap1D(double aMin, double aMax, double bMin, double bMax)
            => Math.Max(0, Math.Min(aMax, bMax) - Math.Max(aMin, bMin));

        private static bool OrientationMatches(VpEntry a, VpEntry b)
            => a.Transform.BasisX.DotProduct(b.Transform.BasisX) > OrientationDot
            && a.Transform.BasisY.DotProduct(b.Transform.BasisY) > OrientationDot;

        // ── Alignment ─────────────────────────────────────────────────────────
        /// <summary>
        /// Moves the target viewport so the source view's crop-centre world point lands at the same
        /// sheet coordinate it occupies on the source sheet. Reads the target view's crop geometry
        /// <b>live</b> so any scope-box / crop-size inheritance applied earlier this run is honoured.
        /// SetBoxCenter needs no regen.
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
                if (!(doc.GetElement(target.ViewId) is View view))       return false;

                BoundingBoxXYZ cb = view.CropBox;
                if (cb?.Transform == null) return false;
                int scale = view.Scale;
                if (scale <= 0 || src.Scale <= 0) return false;

                XYZ centre = TargetBoxCentre(src, target, view, cb, scale);
                vp.SetBoxCenter(centre);
                // Kept so a later phase that shifts this viewport can be detected and undone.
                pr.IntendedCentre = centre;
                return true;
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error($"AlignSheetViews: SetBoxCenter on viewport {target.ViewportId.Value}", ex);
                return false;
            }
        }

        /// <summary>
        /// The sheet point to move the target viewport's box centre to, so the shared world anchor
        /// registers on both sheets. Split out of <see cref="TryAlign"/> so the preview diagnostics
        /// can report exactly what a real run would do without moving anything.
        /// </summary>
        private XYZ TargetBoxCentre(VpEntry src, VpEntry target, View targetView, BoundingBoxXYZ cb, int scale)
        {
            // Read the target's annotation crop LIVE - InheritCropSize may have just rewritten it.
            ReadAnnotationCrop(targetView, out bool tAct, out double tTop, out double tBot, out double tLeft, out double tRight);

            XYZ anchor = AnchorWorld(src);
            XYZ local  = cb.Transform.Inverse.OfPoint(anchor);

            FootprintCentre(cb.Min, cb.Max, tAct, tTop, tBot, tLeft, tRight, out double fx, out double fy);
            XYZ off = ApplyRotation(new XYZ((local.X - fx) / scale, (local.Y - fy) / scale, 0), target.Rotation);

            return SourceAnchorOnSheet(src) - off;
        }

        /// <summary>Box centre the OLD (pre-fix) formula would have produced - diagnostics only.</summary>
        private static XYZ LegacyTargetBoxCentre(VpEntry src, BoundingBoxXYZ cb, int scale)
        {
            XYZ anchor = AnchorWorld(src);
            XYZ local  = cb.Transform.Inverse.OfPoint(anchor);
            double cx = (cb.Min.X + cb.Max.X) / 2.0;
            double cy = (cb.Min.Y + cb.Max.Y) / 2.0;
            return src.BoxCenter - new XYZ((local.X - cx) / scale, (local.Y - cy) / scale, 0);
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
        /// PROVISIONAL: the sign convention is not verified against Revit. Preview-mode diagnostics
        /// print BOTH candidate mappings for every rotated viewport so a single plot confirms it or
        /// flips it.
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
            public SourceSheet(ElementId id, string label, List<VpEntry> entries)
            {
                Id = id; Label = label; Entries = entries;
            }
            public ElementId     Id      { get; }
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

        // ── A matched source→target viewport pair, recorded for the apply + title passes ─
        private sealed class MatchedPair
        {
            public MatchedPair(VpEntry source, VpEntry target)
            {
                Source = source; Target = target;
            }
            public VpEntry Source { get; }
            public VpEntry Target { get; }
            public string  Label  { get; set; } = "";

            /// <summary>Sheet centre the alignment computed for this viewport, or null if it never
            /// placed. The yardstick the post-placement drift check measures against.</summary>
            public XYZ?    IntendedCentre { get; set; }
        }

        // ── Per-view grid outcome counts ──────────────────────────────────────
        // One field per outcome so a zero result always names its cause. "Changed" counts real
        // differences only — a curve written identical to the one already there is AlreadyMatching,
        // because "N trimmed" that included no-op writes is what hid this bug in the first place.
        private sealed class GridTally
        {
            public int Changed        { get; set; }
            public int AlreadyMatching { get; set; }
            public int Bubbles        { get; set; }
            public int Elbows         { get; set; }
            public int ModelSource    { get; set; }
            public int NoSourceCurve  { get; set; }
            public int Rejected       { get; set; }
            public int Errored        { get; set; }
            public int MultiSegment   { get; set; }
            public int Unbound        { get; set; }
            public int Restored             { get; set; }
            public int Blocked              { get; set; }
            public int VisibilityUnexplained { get; set; }
        }

        // ── A queued grid curve write, with the extent modes to restore if it is rejected ─
        private sealed class GridWrite
        {
            public GridWrite(Grid grid, Curve curve, DatumExtentType mode0, DatumExtentType mode1)
            {
                Grid = grid; Curve = curve; Mode0 = mode0; Mode1 = mode1;
            }
            public Grid            Grid  { get; }
            public Curve           Curve { get; }
            public DatumExtentType Mode0 { get; }
            public DatumExtentType Mode1 { get; }
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
            public bool             CropActive     { get; set; }
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
