using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.Testing.AlignSheetViews
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

        public string GetName() => "LemoineTools.Tools.Testing.AlignSheetViews";

        private void Log(string t, string s) => PushLog?.Invoke(t, s);

        // Bases are treated as parallel / matching above these cosine thresholds.
        private const double ParallelDot    = 0.999;
        private const double OrientationDot  = 0.9999;
        private const double MinCropOffsetFt = 1e-4;   // Revit rejects 0 / negative annotation-crop offsets.

        public void Execute(UIApplication app)
        {
            var onProgress = OnProgress ?? ((p, a, b, c) => { });
            var onComplete = OnComplete ?? ((a, b, c) => { });

            int pass = 0, fail = 0, skip = 0;
            long issues0 = LemoineLog.IssueCount;

            try
            {
                var doc = app.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    Log("No active project — open a document and try again.", "fail");
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
                    Log("No valid source sheets — open a document and pick at least one reference sheet.", "fail");
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
                        Log($"Source '{label}' has no placeable views with a crop box — ignored as a reference.", "warn");
                        continue;
                    }
                    sources.Add(new SourceSheet(s.Id, label, entries));
                    Log($"Source '{label}': {entries.Count} reference view(s).", "info");
                }
                if (sources.Count == 0)
                {
                    Log("None of the selected source sheets carry placeable views — nothing to align to.", "fail");
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
                    Log("No valid target sheets (a sheet listed as both source and target is treated as a source).", "fail");
                    onComplete(0, 1, 0);
                    return;
                }

                if (PreviewOnly)
                {
                    Log("Preview only — reporting matches; no viewports will be moved.", "info");
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

                        // Titles last: moving a viewport drags its title, so titles can only be placed
                        // once every box is in its final spot. Regen so moved titles report real outlines.
                        if (AlignTitles && pairs.Count > 0 && !LemoineRun.CancelRequested)
                            AlignAllTitles(doc, pairs);

                        tx.Commit();
                    }
                }

                long issues = LemoineLog.IssuesSince(issues0);
                if (issues > 0)
                    Log($"{issues} non-fatal issue(s) recorded — see diagnostics log.", "warn");

                Log($"Done — {pass} aligned, {fail} error(s), {skip} skipped across {targets.Count} target sheet(s).",
                    fail > 0 ? "warn" : "pass");
                onProgress(100, pass, fail, skip);
                onComplete(pass, fail, skip);
            }
            catch (Exception ex)
            {
                Log($"Align Sheet Views error: {ex.Message}", "fail");
                LemoineLog.Error("AlignSheetViews.Execute", ex);
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
                if (LemoineRun.CancelRequested)
                {
                    Log($"Stopped by user — {i} of {total} target sheet(s) processed; work so far preserved.", "warn");
                    break;   // falls through to caller's commit
                }

                var sheet = targets[i];
                var label = $"{sheet.SheetNumber} - {sheet.Name}";
                var targetEntries = CaptureSheet(doc, sheet);
                if (targetEntries.Count == 0)
                {
                    Log($"[{label}] No placeable views found — cannot align this sheet.", "fail");
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
                    Log($"[{label}] No counterpart views found on any of the {sources.Count} source sheet(s) — left unchanged.", "fail");
                    f++;
                    onProgress(Pct(i + 1, total), p, f, s);
                    continue;
                }

                Log($"[{label}] Best reference: '{bestSource.Label}' — {bestMatch.Pairs.Count} of {bestSource.Entries.Count} view(s) matched.", "info");
                if (sources.Count > 1)
                {
                    var others = scored.Where(x => x.src != bestSource)
                                       .Select(x => $"'{x.src.Label}' ({x.m.Pairs.Count})");
                    Log($"[{label}] Other candidates: {string.Join(", ", others)}.", "info");
                }

                // Report the gaps for the chosen source.
                foreach (var miss in bestMatch.Missing)
                {
                    Log($"[{label}] MISSING counterpart for source view '{miss.ViewName}'.", "fail");
                    LemoineLog.Warn("AlignSheetViews", $"No counterpart for '{miss.ViewName}' on sheet {sheet.Id.Value}.");
                    f++;
                }
                foreach (var amb in bestMatch.Ambiguous)
                {
                    Log($"[{label}] AMBIGUOUS — source view '{amb.src.ViewName}' overlaps both '{amb.a.ViewName}' and '{amb.b.ViewName}'. Left unchanged.", "fail");
                    LemoineLog.Warn("AlignSheetViews", $"Ambiguous match for '{amb.src.ViewName}' on sheet {sheet.Id.Value}.");
                    f++;
                }
                foreach (var extra in bestMatch.Extra)
                {
                    Log($"[{label}] EXTRA view '{extra.ViewName}' has no source counterpart — left unchanged.", "warn");
                    s++;
                }

                // Quality warnings per matched pair (anchor still aligns).
                foreach (var pr in bestMatch.Pairs)
                {
                    pr.Label = label;
                    if (pr.Target.Scale != pr.Source.Scale)
                        Log($"[{label}] '{pr.Target.ViewName}' scale 1:{pr.Target.Scale} differs from source 1:{pr.Source.Scale} — only the anchor point will register.", "warn");
                    if (!OrientationMatches(pr.Source, pr.Target))
                        Log($"[{label}] '{pr.Target.ViewName}' orientation differs from source — field overlay not guaranteed.", "warn");
                    if (pr.Target.Rotation != ViewportRotation.None)
                        Log($"[{label}] '{pr.Target.ViewName}' viewport is rotated ({pr.Target.Rotation}) — overlay aligns the anchor only.", "warn");
                }

                if (!applyMoves)
                {
                    foreach (var pr in bestMatch.Pairs)
                        Log($"[{label}] Would align '{pr.Target.ViewName}' → source '{pr.Source.ViewName}'" +
                            (AlignTitles ? " (and its view title)." : "."), "info");
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
                    if (assigned > 0) Log($"[{label}] {assigned} scope box assignment(s) applied.", "info");
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
                    if (TryAlign(doc, pr.Source, pr.Target))
                    {
                        Log($"[{label}] Aligned '{pr.Target.ViewName}' → source '{pr.Source.ViewName}'.", "info");
                        allPairs.Add(pr);
                        p++;
                    }
                    else
                    {
                        Log($"[{label}] Failed to move '{pr.Target.ViewName}' — see diagnostics log.", "fail");
                        f++;
                    }
                }

                // Phase C — view-only inheritance that does not move the viewport.
                if (InheritGridExtents)    foreach (var pr in bestMatch.Pairs) TrimGrids(doc, pr, label);
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
                Log($"[{label}] Could not assign scope box to '{pr.Target.ViewName}'.", "warn");
                LemoineLog.Swallowed($"AlignSheetViews: scope box on view {pr.Target.ViewId.Value}", ex);
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
                // target's own crop centre (alignment then centres it on the shared anchor). Skipped
                // when the view inherited a scope box this run — a scope box governs the crop region.
                if (gotScopeBox)
                {
                    Log($"[{label}] '{pr.Target.ViewName}' crop is scope-box governed — crop-size inheritance skipped (annotation crop still applied).", "info");
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
                Log($"[{label}] Could not match crop size for '{pr.Target.ViewName}'.", "warn");
                LemoineLog.Swallowed($"AlignSheetViews: crop size on view {pr.Target.ViewId.Value}", ex);
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
                Log($"[{label}] Could not match annotation crop for '{pr.Target.ViewName}' (this view type may not support one).", "warn");
                LemoineLog.Swallowed($"AlignSheetViews: annotation crop on view {pr.Target.ViewId.Value}", ex);
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
                Log($"[{label}] Could not match crop visibility for '{pr.Target.ViewName}'.", "warn");
                LemoineLog.Swallowed($"AlignSheetViews: crop visibility on view {pr.Target.ViewId.Value}", ex);
            }
        }

        // ── Inheritance: grid 2D (view-specific) extents ──────────────────────
        /// <summary>
        /// Copies each source grid's displayed endpoints onto the matching target grid (same model
        /// element, looked up by ElementId) as a per-view 2D override. Endpoints are collinear with
        /// the target grid (it is the same grid), satisfying the SetCurveInView coincidence rule, and
        /// ViewSpecific works even when the grid's 3D extents are locked to a scope box.
        /// </summary>
        private void TrimGrids(Document doc, MatchedPair pr, string label)
        {
            try
            {
                if (!(doc.GetElement(pr.Source.ViewId) is View sv)) return;
                if (!(doc.GetElement(pr.Target.ViewId) is View tv)) return;

                var srcGrids = new FilteredElementCollector(doc, sv.Id)
                    .OfClass(typeof(Grid))
                    .Cast<Grid>()
                    .ToList();

                int trimmed = 0, skipped = 0;
                foreach (var g in srcGrids)
                {
                    try
                    {
                        Curve c = g.GetCurvesInView(DatumExtentType.ViewSpecific, sv)?.FirstOrDefault();
                        if (c == null) { skipped++; continue; }
                        if (!(doc.GetElement(g.Id) is Grid tg)) { skipped++; continue; }
                        tg.SetCurveInView(DatumExtentType.ViewSpecific, tv, c);
                        trimmed++;
                    }
                    catch (Exception ex)
                    {
                        skipped++;
                        LemoineLog.Swallowed($"AlignSheetViews: trim grid {g.Id.Value} in view {tv.Id.Value}", ex);
                    }
                }

                if (srcGrids.Count == 0)
                    Log($"[{label}] '{pr.Source.ViewName}' has no grids to trim.", "info");
                else
                    Log($"[{label}] Grids on '{pr.Target.ViewName}': {trimmed} trimmed" +
                        (skipped > 0 ? $", {skipped} skipped (not visible / not collinear)." : "."),
                        skipped > 0 ? "warn" : "info");
            }
            catch (Exception ex)
            {
                Log($"[{label}] Could not trim grids for '{pr.Target.ViewName}'.", "warn");
                LemoineLog.Swallowed($"AlignSheetViews: trim grids on view {pr.Target.ViewId.Value}", ex);
            }
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
                    Log($"[{pr.Label}] Could not match title line length for '{pr.Target.ViewName}'.", "warn");
                    LemoineLog.Swallowed($"AlignSheetViews: LabelLineLength on viewport {pr.Target.ViewportId.Value}", ex);
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
                    Log($"[{pr.Label}] Could not align title for '{pr.Target.ViewName}'.", "warn");
                    LemoineLog.Swallowed($"AlignSheetViews: align title on viewport {pr.Target.ViewportId.Value}", ex);
                }
            }

            string tone = titleFail > 0 ? "warn" : "info";
            Log($"View titles: {titled} aligned" + (titleFail > 0 ? $", {titleFail} could not be adjusted." : "."), tone);
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
                LemoineLog.Swallowed($"AlignSheetViews: GetAllViewports on sheet {sheet.Id.Value}", ex);
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
                    });

                    if (!view.CropBoxActive)
                        Log($"[note] View '{view.Name}' has no active crop — region matched from its default crop box.", "info");
                }
                catch (Exception ex)
                {
                    LemoineLog.Swallowed($"AlignSheetViews: capture viewport {vpId.Value}", ex);
                }
            }
            return entries;
        }

        // ── Matching ──────────────────────────────────────────────────────────
        /// <summary>Matches every source view on a sheet to a target view, scoring the whole sheet.</summary>
        private SheetMatch MatchSheet(List<VpEntry> srcEntries, List<VpEntry> tgtEntries)
        {
            var res  = new SheetMatch();
            var used = new HashSet<ElementId>();

            foreach (var src in srcEntries)
            {
                var (best, ambiguous) = BestMatch(src, tgtEntries, used);
                if (best == null) { res.Missing.Add(src); continue; }
                if (ambiguous != null) { res.Ambiguous.Add((src, best, ambiguous)); continue; }
                used.Add(best.ViewportId);
                res.Pairs.Add(new MatchedPair(src, best));
            }
            foreach (var t in tgtEntries.Where(te => !used.Contains(te.ViewportId)))
                res.Extra.Add(t);

            // Score: matched count dominates; ties broken toward true overlays then exact scope matches.
            int quality = res.Pairs.Count(pr => pr.Source.Scale == pr.Target.Scale && OrientationMatches(pr.Source, pr.Target));
            int exact   = res.Pairs.Count(pr => pr.Source.ScopeBoxId != ElementId.InvalidElementId
                                             && pr.Source.ScopeBoxId == pr.Target.ScopeBoxId);
            res.Score = res.Pairs.Count * 10000.0 + quality * 100.0 + exact;
            return res;
        }

        /// <summary>
        /// Returns the best target view for a source view (or null if none qualifies), plus a second
        /// candidate when the match is ambiguous. A shared scope box is the exact, preferred key; only
        /// views without a scope box (or with no scope-box counterpart) fall back to crop overlap.
        /// </summary>
        private (VpEntry? best, VpEntry? ambiguous) BestMatch(
            VpEntry src, List<VpEntry> candidates, HashSet<ElementId> used)
        {
            // Scope-box-first: exact match on the shared scope box ElementId.
            if (src.ScopeBoxId != ElementId.InvalidElementId)
            {
                var byScope = candidates
                    .Where(t => !used.Contains(t.ViewportId)
                             && t.Type == src.Type
                             && Math.Abs(src.ViewDir.DotProduct(t.ViewDir)) >= ParallelDot
                             && t.ScopeBoxId == src.ScopeBoxId)
                    .ToList();
                if (byScope.Count == 1) return (byScope[0], null);
                if (byScope.Count > 1)  return BestByOverlap(src, byScope, used);
                // none share the scope box — fall through to overlap matching.
            }
            return BestByOverlap(src, candidates, used);
        }

        /// <summary>Crop-region-overlap matcher (the fallback for views without a shared scope box).</summary>
        private (VpEntry? best, VpEntry? ambiguous) BestByOverlap(
            VpEntry src, List<VpEntry> candidates, HashSet<ElementId> used)
        {
            VpEntry? best = null, second = null;
            double bestScore = 0, secondScore = 0;

            foreach (var t in candidates)
            {
                if (used.Contains(t.ViewportId)) continue;
                if (t.Type != src.Type) continue;
                if (Math.Abs(src.ViewDir.DotProduct(t.ViewDir)) < ParallelDot) continue;

                double score = OverlapInSourcePlane(src, t);
                if (score < OverlapThreshold) continue;

                if (score > bestScore)
                {
                    second = best; secondScore = bestScore;
                    best = t;      bestScore = score;
                }
                else if (score > secondScore)
                {
                    second = t; secondScore = score;
                }
            }

            bool ambiguous = best != null && second != null && secondScore >= 0.8 * bestScore;
            return (best, ambiguous ? second : null);
        }

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
        /// sheet coordinate it occupies on the source sheet. Reads the target view's crop geometry
        /// <b>live</b> so any scope-box / crop-size inheritance applied earlier this run is honoured.
        /// SetBoxCenter needs no regen.
        /// </summary>
        private bool TryAlign(Document doc, VpEntry src, VpEntry target)
        {
            try
            {
                if (!(doc.GetElement(target.ViewportId) is Viewport vp)) return false;
                if (!(doc.GetElement(target.ViewId) is View view))       return false;

                BoundingBoxXYZ cb = view.CropBox;
                if (cb?.Transform == null) return false;
                int scale = view.Scale;
                if (scale <= 0) return false;

                XYZ anchor = AnchorWorld(src);
                XYZ local  = cb.Transform.Inverse.OfPoint(anchor);
                double cx = (cb.Min.X + cb.Max.X) / 2.0;
                double cy = (cb.Min.Y + cb.Max.Y) / 2.0;
                XYZ off = new XYZ((local.X - cx) / scale, (local.Y - cy) / scale, 0);

                vp.SetBoxCenter(src.BoxCenter - off);
                return true;
            }
            catch (Exception ex)
            {
                LemoineLog.Error($"AlignSheetViews: SetBoxCenter on viewport {target.ViewportId.Value}", ex);
                return false;
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
            catch (Exception ex) { LemoineLog.Swallowed("AlignSheetViews: configure failure handling", ex); }
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
        }
    }
}
