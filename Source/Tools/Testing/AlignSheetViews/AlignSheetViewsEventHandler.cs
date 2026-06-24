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
    /// top of its counterpart on a chosen source/reference sheet. The source sheet's
    /// viewports are never moved — they are ground truth. Each source view is matched to a
    /// target view by model-region overlap (in the source view's own plane, gated by view
    /// type and parallel view direction), then the target viewport's box centre is set so a
    /// shared world anchor lands at the same sheet coordinate as on the source.
    ///
    /// A target sheet missing a counterpart for a source view — or carrying an unmatched
    /// extra — is reported, never silently skipped. SetBoxCenter needs no regen, so the
    /// whole run commits once. Runs on the Revit API thread; set all inputs before Raise().
    /// </summary>
    public sealed class AlignSheetViewsEventHandler : IExternalEventHandler
    {
        // ── Inputs set by the view model before Raise() ───────────────────────
        public ElementId       SourceSheetId  { get; set; } = ElementId.InvalidElementId;
        public List<ElementId> TargetSheetIds { get; set; } = new List<ElementId>();

        /// <summary>Minimum in-plane overlap fraction (0..1) for a source/target view pair to match.</summary>
        public double OverlapThreshold { get; set; } = 0.5;

        /// <summary>When true: match and report only; no viewport is moved and nothing is committed.</summary>
        public bool PreviewOnly { get; set; } = false;

        /// <summary>When true: after the viewports are aligned, move each view title (label) so it overlays
        /// the source title and match the source's title line length. Titles are done last because moving a
        /// viewport drags its title with it.</summary>
        public bool AlignTitles { get; set; } = true;

        // ── Callbacks ─────────────────────────────────────────────────────────
        public Action<string, string>?     PushLog    { get; set; }
        public Action<int, int, int, int>? OnProgress { get; set; }
        public Action<int, int, int>?      OnComplete { get; set; }

        public string GetName() => "LemoineTools.Tools.Testing.AlignSheetViews";

        private void Log(string t, string s) => PushLog?.Invoke(t, s);

        // Bases are treated as parallel / matching above these cosine thresholds.
        private const double ParallelDot   = 0.999;
        private const double OrientationDot = 0.9999;

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
                if (!(doc.GetElement(SourceSheetId) is ViewSheet source))
                {
                    Log("Source sheet not found — it may have been deleted.", "fail");
                    onComplete(0, 1, 0);
                    return;
                }

                var sourceEntries = CaptureSheet(doc, source);
                if (sourceEntries.Count == 0)
                {
                    Log($"Source sheet '{source.SheetNumber} - {source.Name}' has no placeable views with a crop box — nothing to align to.", "fail");
                    onComplete(0, 1, 0);
                    return;
                }
                Log($"Source '{source.SheetNumber} - {source.Name}': {sourceEntries.Count} reference view(s).", "info");

                // Targets, de-duplicated and never including the source sheet itself.
                var targets = TargetSheetIds
                    .Where(id => id != null && id != ElementId.InvalidElementId && id != SourceSheetId)
                    .Distinct()
                    .Select(id => doc.GetElement(id) as ViewSheet)
                    .Where(s => s != null)
                    .Cast<ViewSheet>()
                    .ToList();

                if (targets.Count == 0)
                {
                    Log("No valid target sheets (a source sheet listed as its own target is ignored).", "fail");
                    onComplete(0, 1, 0);
                    return;
                }

                if (PreviewOnly)
                {
                    Log("Preview only — reporting matches; no viewports will be moved.", "info");
                    (pass, fail, skip, _) = DriveTargets(doc, source, sourceEntries, targets, applyMoves: false, onProgress);
                }
                else
                {
                    using (var tx = new Transaction(doc, "Lemoine — Align Sheet Views"))
                    {
                        ConfigureFailures(tx);
                        tx.Start();

                        List<MatchedPair> pairs;
                        (pass, fail, skip, pairs) = DriveTargets(doc, source, sourceEntries, targets, applyMoves: true, onProgress);

                        // Titles last: moving a viewport drags its title, so the title can only be
                        // placed once every box is in its final spot. Align label line length, regen
                        // so the moved titles report their real outlines, then match each to its source.
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
                SourceSheetId  = ElementId.InvalidElementId;
            }
        }

        // ── Per-target-sheet driver ───────────────────────────────────────────
        private (int pass, int fail, int skip, List<MatchedPair> pairs) DriveTargets(
            Document doc, ViewSheet source, List<VpEntry> sourceEntries, List<ViewSheet> targets,
            bool applyMoves, Action<int, int, int, int> onProgress)
        {
            int p = 0, f = 0, s = 0;
            int total = targets.Count;
            var pairs = new List<MatchedPair>();

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
                    Log($"[{label}] No placeable views found — all {sourceEntries.Count} counterpart(s) missing.", "fail");
                    f += sourceEntries.Count;
                    onProgress(Pct(i + 1, total), p, f, s);
                    continue;
                }

                var used = new HashSet<ElementId>();   // target viewports already matched on this sheet
                foreach (var src in sourceEntries)
                {
                    var (best, second) = BestMatch(src, targetEntries, used);
                    if (best == null)
                    {
                        Log($"[{label}] MISSING counterpart for source view '{src.ViewName}'.", "fail");
                        LemoineLog.Warn("AlignSheetViews", $"No counterpart for '{src.ViewName}' on sheet {sheet.Id.Value}.");
                        f++;
                        continue;
                    }
                    if (second != null)
                    {
                        Log($"[{label}] AMBIGUOUS — source view '{src.ViewName}' overlaps both '{best.ViewName}' and '{second.ViewName}'. Left unchanged.", "fail");
                        LemoineLog.Warn("AlignSheetViews", $"Ambiguous match for '{src.ViewName}' on sheet {sheet.Id.Value}.");
                        f++;
                        continue;
                    }

                    used.Add(best.ViewportId);

                    // Warn on conditions that prevent a true field overlay (anchor still aligns).
                    if (best.Scale != src.Scale)
                        Log($"[{label}] '{best.ViewName}' scale 1:{best.Scale} differs from source 1:{src.Scale} — only the anchor point will register.", "warn");
                    if (!OrientationMatches(src, best))
                        Log($"[{label}] '{best.ViewName}' orientation differs from source — field overlay not guaranteed.", "warn");
                    if (best.Rotation != ViewportRotation.None)
                        Log($"[{label}] '{best.ViewName}' viewport is rotated ({best.Rotation}) — overlay aligns the anchor only.", "warn");

                    if (applyMoves)
                    {
                        if (TryAlign(doc, src, best, out _))
                        {
                            Log($"[{label}] Aligned '{best.ViewName}' → source '{src.ViewName}'.", "info");
                            pairs.Add(new MatchedPair(src.ViewportId, best.ViewportId, label, src.ViewName, best.ViewName));
                            p++;
                        }
                        else
                        {
                            Log($"[{label}] Failed to move '{best.ViewName}' — see diagnostics log.", "fail");
                            f++;
                        }
                    }
                    else
                    {
                        Log($"[{label}] Would align '{best.ViewName}' → source '{src.ViewName}'" +
                            (AlignTitles ? " (and its view title)." : "."), "info");
                        s++;
                    }
                }

                // Any target view that matched no source view.
                foreach (var t in targetEntries.Where(te => !used.Contains(te.ViewportId)))
                {
                    Log($"[{label}] EXTRA view '{t.ViewName}' has no source counterpart — left unchanged.", "warn");
                    s++;
                }

                onProgress(Pct(i + 1, total), p, f, s);
            }

            return (p, f, s, pairs);
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
                    if (!(doc.GetElement(pr.SourceViewportId) is Viewport sVp)) continue;
                    if (!(doc.GetElement(pr.TargetViewportId) is Viewport tVp)) continue;
                    tVp.LabelLineLength = sVp.LabelLineLength;
                }
                catch (Exception ex)
                {
                    Log($"[{pr.Label}] Could not match title line length for '{pr.TargetViewName}'.", "warn");
                    LemoineLog.Swallowed($"AlignSheetViews: LabelLineLength on viewport {pr.TargetViewportId.Value}", ex);
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
                    if (!(doc.GetElement(pr.SourceViewportId) is Viewport sVp)) continue;
                    if (!(doc.GetElement(pr.TargetViewportId) is Viewport tVp)) continue;

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
                    Log($"[{pr.Label}] Could not align title for '{pr.TargetViewName}'.", "warn");
                    LemoineLog.Swallowed($"AlignSheetViews: align title on viewport {pr.TargetViewportId.Value}", ex);
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

                    entries.Add(new VpEntry
                    {
                        ViewportId = vp.Id,
                        ViewName   = view.Name,
                        Type       = view.ViewType,
                        ViewDir    = view.ViewDirection,
                        Transform  = cb.Transform,
                        CropMin    = cb.Min,
                        CropMax    = cb.Max,
                        Scale      = view.Scale,
                        BoxCenter  = vp.GetBoxCenter(),
                        Rotation   = vp.Rotation,
                        CropActive = view.CropBoxActive,
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
        /// <summary>
        /// Returns the best target view for a source view (or null if none clears the
        /// overlap threshold), plus a second candidate when the match is ambiguous.
        /// Overlap is measured in the source view's own plane; the cut-depth ranges must
        /// also intersect so identical-looking views on different levels never match.
        /// </summary>
        private (VpEntry? best, VpEntry? ambiguous) BestMatch(
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

            // Ambiguous only when the runner-up is genuinely competitive with the winner.
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

            // Source rectangle in its own frame is just its local crop extents.
            double sUmin = src.CropMin.X, sUmax = src.CropMax.X;
            double sVmin = src.CropMin.Y, sVmax = src.CropMax.Y;
            double sNmin = src.CropMin.Z, sNmax = src.CropMax.Z;

            // Candidate's 4 plan corners → world → source (u,v,n) coordinates.
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

            // Cut-depth gate: the two view ranges along the source normal must intersect.
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
        /// Moves the target viewport so the source view's crop-centre world point lands at the
        /// same sheet coordinate it occupies on the source sheet. SetBoxCenter needs no regen.
        /// </summary>
        private bool TryAlign(Document doc, VpEntry src, VpEntry target, out XYZ newCenter)
        {
            newCenter = XYZ.Zero;
            try
            {
                if (!(doc.GetElement(target.ViewportId) is Viewport vp)) return false;

                // World anchor = source crop centre. On the source it maps to S.BoxCenter
                // exactly, so the target only needs its own offset for that same point removed.
                XYZ anchor = AnchorWorld(src);
                newCenter = src.BoxCenter - SheetOffset(target, anchor);
                vp.SetBoxCenter(newCenter);
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

        /// <summary>Sheet-feet offset of world point P from the viewport's box centre (model feet / scale).</summary>
        private static XYZ SheetOffset(VpEntry e, XYZ p)
        {
            XYZ local = e.Transform.Inverse.OfPoint(p);
            double cx = (e.CropMin.X + e.CropMax.X) / 2.0;
            double cy = (e.CropMin.Y + e.CropMax.Y) / 2.0;
            return new XYZ((local.X - cx) / e.Scale, (local.Y - cy) / e.Scale, 0);
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

        // ── A matched source→target viewport pair, recorded for the title pass ─
        private sealed class MatchedPair
        {
            public MatchedPair(ElementId sourceViewportId, ElementId targetViewportId,
                               string label, string sourceViewName, string targetViewName)
            {
                SourceViewportId = sourceViewportId;
                TargetViewportId = targetViewportId;
                Label            = label;
                SourceViewName   = sourceViewName;
                TargetViewName   = targetViewName;
            }

            public ElementId SourceViewportId { get; }
            public ElementId TargetViewportId { get; }
            public string    Label            { get; }
            public string    SourceViewName   { get; }
            public string    TargetViewName   { get; }
        }

        // ── Captured per-viewport state ───────────────────────────────────────
        private sealed class VpEntry
        {
            public ElementId        ViewportId { get; set; } = ElementId.InvalidElementId;
            public string           ViewName   { get; set; } = "";
            public ViewType         Type       { get; set; }
            public XYZ              ViewDir    { get; set; } = XYZ.BasisZ;
            public Transform        Transform  { get; set; } = Transform.Identity;
            public XYZ              CropMin    { get; set; } = XYZ.Zero;
            public XYZ              CropMax    { get; set; } = XYZ.Zero;
            public int              Scale      { get; set; } = 1;
            public XYZ              BoxCenter  { get; set; } = XYZ.Zero;
            public ViewportRotation Rotation   { get; set; } = ViewportRotation.None;
            public bool             CropActive { get; set; }
        }
    }
}
