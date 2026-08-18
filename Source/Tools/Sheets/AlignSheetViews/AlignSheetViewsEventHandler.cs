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

        /// <summary>Match the target view's crop-region visibility (CropBoxVisible) to the source.</summary>
        public bool InheritCropVisibility { get; set; } = false;

        /// <summary>Match the target view's crop size + annotation-crop offsets to the source.
        /// Independent of everything else — the alignment itself never needs it, because it
        /// registers a shared world anchor through whatever crop each view actually has.</summary>
        public bool InheritCropSize { get; set; } = false;

        // ── Sheet content ─────────────────────────────────────────────────────
        /// <summary>Copy the reference sheet's legend placements onto each target sheet: place the
        /// legend where the target does not carry it, move the target's own instance where it does.
        /// Independent of the view alignment — a target that matched no views still gets its
        /// legends.</summary>
        public bool PlaceLegends { get; set; } = false;

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

        // Two viewports of the SAME legend draw the same footprint, so a size difference between
        // their outlines is a real difference in title state — not rounding. Loose enough not to
        // fire on floating-point noise, tight enough that a title band can never hide under it.
        private const double OutlineSizeTolFt = 1e-4;

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
                    // A reference sheet carrying only legends has nothing for the view matcher, but
                    // it is still a usable reference when legends are being placed — so it is only
                    // dropped when it offers neither. It still scores 0 on views, so it can only be
                    // chosen for a target that matched nothing anywhere.
                    var legends = new List<LegendEntry>();
                    string label = $"{s.SheetNumber} - {s.Name}";
                    if (PlaceLegends)
                    {
                        legends = CaptureLegends(doc, s, out bool legendsComplete);
                        // A partial read here is not destructive — it can only mean a legend the
                        // reference carries is never copied — but the user would have no way to tell
                        // that from a legend they simply forgot to place.
                        if (!legendsComplete)
                            Log(AppStrings.T("testing.alignSheetViews.log.legendSourceReadPartial", label), "warn");
                    }
                    if (entries.Count == 0 && legends.Count == 0)
                    {
                        Log(AppStrings.T("testing.alignSheetViews.log.sourceNoViews", label), "warn");
                        continue;
                    }
                    sources.Add(new SourceSheet(s.Id, label, entries, legends));
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

            // Legend work, all of which needs the run's regenerate before it can be checked.
            var legendPlacements = new List<LegendPlacement>();
            var legendTally      = new LegendTally();
            // "This reference has no legends" is a property of the REFERENCE, not of the target
            // being processed — logging it per target would repeat one fact once per sheet.
            var legendSourcesReported = new HashSet<string>();

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

                // Pick the best source sheet for this target. One source is the common case and
                // needs no scoring at all. This runs even for a sheet with no placeable views:
                // the alignment has nothing to do there, but the legend pass still needs to know
                // which reference this target belongs to.
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

                // A sheet the alignment cannot act on is still reported as a failure exactly as
                // before — it just no longer skips the legend pass on its way out, because legend
                // placement never depended on a view matching in the first place.
                bool alignable = bestSource != null && bestMatch != null && bestMatch.Pairs.Count > 0;
                if (targetEntries.Count == 0)
                {
                    Log(AppStrings.T("testing.alignSheetViews.log.noPlaceableViews", label), "fail");
                    f++;
                }
                else if (!alignable)
                {
                    Log(AppStrings.T("testing.alignSheetViews.log.noCounterpart", label, sources.Count), "fail");
                    f++;
                }

                // Spelled out rather than reusing `alignable` so the compiler can see the two
                // references are non-null inside the block — the condition is the same one.
                if (bestMatch != null && bestSource != null && bestMatch.Pairs.Count > 0)
                {
                    // Report the gaps for the chosen source, each with the reason it is a gap.
                    foreach (var miss in bestMatch.Missing)
                    {
                        Log(DescribeMiss(label, miss), "fail");
                        DiagnosticsLog.Warn("AlignSheetViews", $"No counterpart for '{miss.Source.ViewName}' on sheet {sheet.Id.Value}.");
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

                        // The pair aligned, but on measurements worth seeing: a scope box only one
                        // side carries, crop rectangles that cover noticeably different model area,
                        // or a counterpart found only because depth stopped being a veto. None of
                        // these stop the alignment — all three used to be invisible.
                        if (pr.ScopeBoxMismatch)
                            Log(AppStrings.T("testing.alignSheetViews.log.scopeBoxMismatch", label, pr.Target.ViewName,
                                             ScopeBoxLabel(doc, pr.Source.ScopeBoxId), ScopeBoxLabel(doc, pr.Target.ScopeBoxId),
                                             Pct01(pr.AreaMatch)), "warn");
                        if (pr.AreaMatch > 0 && pr.AreaMatch < OverlapThreshold)
                            Log(AppStrings.T("testing.alignSheetViews.log.areaMismatch", label, pr.Target.ViewName,
                                             Pct01(pr.AreaMatch), CropSizeLabel(pr.Source), CropSizeLabel(pr.Target)), "warn");
                        if (!pr.DepthOverlaps)
                            Log(AppStrings.T("testing.alignSheetViews.log.matchedDifferentDepth", label, pr.Target.ViewName), "warn");
                    }

                    // ── Phase A — every write whose result the alignment can predict ──
                    // No scope box is ever written to a target view. A scope box difference is a
                    // fact to REPORT (above), not a difference to erase: the alignment registers a
                    // shared world anchor through each view's OWN crop geometry, so a grid line
                    // lands at the identical paper coordinate whether or not the two views crop the
                    // same way. Forcing the source's scope box onto the target would change the
                    // model area that view shows — a permanent edit, made to satisfy a matching
                    // heuristic that does not need it — and when the box's vertical extent does not
                    // reach the target's level it fails outright and takes the alignment with it.
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
                }

                // ── Legends — deliberately outside the `alignable` gate ──
                // A legend carries no crop box and no world geometry, so its placement is a copy of
                // a sheet coordinate and owes nothing to whether the views matched. Gating it on the
                // alignment would mean a sheet whose views could not be paired silently lost its
                // legends too, for a reason that has nothing to do with them.
                if (PlaceLegends && bestSource != null)
                    PlaceLegendsOnSheet(doc, sheet, bestSource, label,
                                        legendTally, legendPlacements, legendSourcesReported,
                                        ref p, ref f, ref s);

                // One line per sheet. A clean sheet says so and nothing else; a sheet with problems
                // points at the detail already printed above it rather than repeating it. With more
                // than one reference in play the line also names the one this sheet was aligned to —
                // otherwise a target matched to the wrong reference is invisible.
                // Only sheets the alignment actually ran on get this line: one that failed out above
                // has already said so, and a second line claiming "0 views aligned" would read as a
                // separate result rather than the same one.
                if (alignable && bestSource != null)
                {
                    bool nameRef = sources.Count > 1;
                    Log(_sheetIssues == 0
                            ? (nameRef
                                ? AppStrings.T("testing.alignSheetViews.log.sheetOkRef", label, alignedHere, bestSource.Label)
                                : AppStrings.T("testing.alignSheetViews.log.sheetOk", label, alignedHere))
                            : (nameRef
                                ? AppStrings.T("testing.alignSheetViews.log.sheetWithIssuesRef", label, alignedHere, _sheetIssues, bestSource.Label)
                                : AppStrings.T("testing.alignSheetViews.log.sheetWithIssues", label, alignedHere, _sheetIssues)),
                        _sheetIssues == 0 ? "pass" : "warn");
                }

                onProgress(Pct(i + 1, total), p, f, s);
            }

            // ── Run end — the one place live geometry is genuinely required ──
            if (allPairs.Count > 0 || legendPlacements.Count > 0)
            {
                // Everything above wrote without reading back: boxes are placed, titles have their
                // line lengths, leaders exist, and any legend this run created exists but has never
                // been measured. This single regenerate makes all of it live — legends add none of
                // their own, which is the whole reason their writes were queued rather than checked
                // as they were made.
                doc.Regenerate();

                // Legends go first: a viewport outline is only trustworthy on freshly regenerated
                // geometry, and both passes below write boxes and label offsets that dirty it again.
                int legendCorrected = legendPlacements.Count > 0
                    ? VerifyLegendPlacements(doc, legendPlacements, legendTally)
                    : 0;

                if (allPairs.Count > 0)
                {
                    PositionElbows(pendingElbows);

                    // Only now is the real crop geometry available to check the predictions against.
                    int corrected = VerifyPlacements(doc, allPairs);

                    // A correction moves the box, and a title travels with its box — so a corrected
                    // viewport's label outline is stale and has to be recomputed before it is read.
                    // A corrected legend dirtied the document in exactly the same way, so it counts
                    // toward this regenerate too.
                    if (corrected > 0 || legendCorrected > 0) doc.Regenerate();

                    AlignTitleOffsets(doc, allPairs);
                }
            }

            if (PlaceLegends) ReportLegendSummary(legendTally);

            return (p, f, s);
        }

        // ── Match diagnostics ─────────────────────────────────────────────────
        /// <summary>
        /// The run-log line for one reference view that found no counterpart, naming the cause.
        ///
        /// "[FAIL] no counterpart for 'X'" was true but useless: the two commonest causes need
        /// opposite fixes, and both looked the same. A target sheet that holds nothing of that view
        /// type is a selection mistake; a candidate whose crop covers a different amount of model
        /// is a threshold decision the user can act on — but only if the measured overlap and the
        /// two crop sizes are actually printed.
        /// </summary>
        private string DescribeMiss(string label, MissInfo miss)
        {
            string view = miss.Source.ViewName;

            if (!miss.AnyEligible)
                return AppStrings.T("testing.alignSheetViews.log.missNoEligible",
                                    label, view, miss.Source.Type.ToString());

            if (miss.Best == null || miss.BestScore <= 0)
                return AppStrings.T("testing.alignSheetViews.log.missNoOverlap",
                                    label, view, CropSizeLabel(miss.Source));

            if (miss.BestTaken)
                return AppStrings.T("testing.alignSheetViews.log.missTargetTaken",
                                    label, view, miss.Best.ViewName, Pct01(miss.BestScore));

            // The size-mismatch case the user hit: a real candidate, real overlap, but the crops
            // cover different model areas so the fraction falls under the threshold. Both sizes and
            // both numbers are printed so the choice — re-crop, or lower the threshold — is informed.
            return AppStrings.T("testing.alignSheetViews.log.missLowOverlap",
                                label, view, miss.Best.ViewName,
                                Pct01(miss.BestScore), Pct01(OverlapThreshold),
                                CropSizeLabel(miss.Source), CropSizeLabel(miss.Best));
        }

        /// <summary>A view's crop rectangle as a readable model size plus its scale, e.g. <c>212' × 148' @ 1:96</c>.
        /// This is the number that explains a low overlap, so it is printed rather than described.</summary>
        private static string CropSizeLabel(VpEntry e)
        {
            double w = Math.Abs(e.CropMax.X - e.CropMin.X);
            double h = Math.Abs(e.CropMax.Y - e.CropMin.Y);
            return AppStrings.T("testing.alignSheetViews.log.cropSize", w.ToString("0.#"), h.ToString("0.#"), e.Scale);
        }

        /// <summary>A scope box's name for the log, or the "(none)" placeholder for an unset one.</summary>
        private static string ScopeBoxLabel(Document doc, ElementId id)
        {
            if (id == null || id == ElementId.InvalidElementId)
                return AppStrings.T("testing.alignSheetViews.log.scopeBoxNone");
            try
            {
                var el = doc.GetElement(id);
                if (el != null && !string.IsNullOrEmpty(el.Name)) return el.Name;
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed($"AlignSheetViews: read scope box name {id.Value}", ex);
            }
            return id.Value.ToString();
        }

        private static int Pct01(double fraction) => (int)Math.Round(Math.Max(0, Math.Min(1, fraction)) * 100);

        // ── Inheritance: crop size + annotation crop ──────────────────────────
        private void InheritCropGeometry(Document doc, MatchedPair pr, string label)
        {
            try
            {
                if (!(doc.GetElement(pr.Target.ViewId) is View tv)) return;

                // Crop size — resize the target crop to the source crop's dimensions, keeping the
                // target's own crop centre (alignment then centres it on the shared anchor).
                // Skipped whenever a scope box governs the target's crop region: writing CropBox
                // underneath a scope box fights it and yields an unpredictable crop.
                var tsb = tv.get_Parameter(BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP);
                bool scopeGoverned = tsb != null
                                  && tsb.StorageType == StorageType.ElementId
                                  && tsb.AsElementId() != ElementId.InvalidElementId;

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

        // ══════════════════════════════════════════════════════════════════════
        // Legends
        //
        // A legend is the one view type Revit lets you place on many sheets — and more than once
        // on the same sheet — so it is matched by the legend view's own ElementId rather than by
        // geometry. It also carries no crop box and no world anchor, which is what makes this pass
        // simple: a legend view's content and scale are properties of the VIEW, so the same legend
        // draws the same footprint on every sheet, and "the same place on the target" is a straight
        // copy of the source viewport's box centre. No scale conversion, no anchor projection.
        //
        // The one trap is the view title. GetBoxCenter and GetBoxOutline both measure the drawing
        // AND its title, so two viewports' centres only describe the same drawing position once
        // their title state matches — which is why the type and label settings are copied before
        // the centre is written, not after.
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Reads every legend viewport on a sheet.
        ///
        /// <paramref name="complete"/> reports whether the sheet was read in full, and it matters:
        /// an incomplete read of a TARGET sheet is indistinguishable from a sheet that does not have
        /// the legend, and this pass responds to a missing legend by creating one. Swallowing the
        /// difference would silently duplicate legends the sheet already carried.
        /// </summary>
        private List<LegendEntry> CaptureLegends(Document doc, ViewSheet sheet, out bool complete)
        {
            complete = true;
            var entries = new List<LegendEntry>();
            ICollection<ElementId> vpIds;
            try { vpIds = sheet.GetAllViewports(); }
            catch (Exception ex)
            {
                complete = false;
                DiagnosticsLog.Swallowed($"AlignSheetViews: GetAllViewports (legends) on sheet {sheet.Id.Value}", ex);
                return entries;
            }

            foreach (var vpId in vpIds)
            {
                try
                {
                    if (!(doc.GetElement(vpId) is Viewport vp)) continue;
                    if (!(doc.GetElement(vp.ViewId) is View view) || view.IsTemplate) continue;
                    if (view.ViewType != ViewType.Legend) continue;

                    entries.Add(new LegendEntry
                    {
                        ViewportId      = vp.Id,
                        ViewId          = view.Id,
                        ViewName        = view.Name,
                        BoxCenter       = vp.GetBoxCenter(),
                        Rotation        = vp.Rotation,
                        TypeId          = vp.GetTypeId(),
                        LabelLineLength = vp.LabelLineLength,
                        LabelOffset     = vp.LabelOffset,
                    });
                }
                catch (Exception ex)
                {
                    complete = false;
                    DiagnosticsLog.Swallowed($"AlignSheetViews: capture legend viewport {vpId.Value}", ex);
                }
            }
            return entries;
        }

        /// <summary>
        /// Pairs the reference sheet's legend instances with the target's, one legend view at a time.
        ///
        /// Within a legend view, instances are paired <b>globally nearest-first</b> rather than in
        /// list order, so a legend already roughly where it belongs maps to the source instance it
        /// actually corresponds to. Leftovers on the source side are placements to create; leftovers
        /// on the target side are the user's own extra copies and are never touched.
        /// </summary>
        private static LegendMatch MatchLegends(List<LegendEntry> srcLegends, List<LegendEntry> tgtLegends)
        {
            var res         = new LegendMatch();
            var usedTargets = new HashSet<long>();

            foreach (var grp in srcLegends.GroupBy(e => e.ViewId.Value))
            {
                var sources = grp.ToList();
                var targets = tgtLegends.Where(t => t.ViewId.Value == grp.Key).ToList();

                var cands = new List<(LegendEntry S, LegendEntry T, double D)>();
                foreach (var sl in sources)
                    foreach (var tl in targets)
                        cands.Add((sl, tl, sl.BoxCenter.DistanceTo(tl.BoxCenter)));

                var pairedSources = new HashSet<long>();
                foreach (var c in cands.OrderBy(x => x.D))
                {
                    if (pairedSources.Contains(c.S.ViewportId.Value)) continue;
                    if (usedTargets.Contains(c.T.ViewportId.Value))   continue;
                    pairedSources.Add(c.S.ViewportId.Value);
                    usedTargets.Add(c.T.ViewportId.Value);
                    res.Move.Add((c.S, c.T));
                }

                foreach (var sl in sources.Where(x => !pairedSources.Contains(x.ViewportId.Value)))
                    res.Create.Add(sl);
            }

            foreach (var tl in tgtLegends.Where(t => !usedTargets.Contains(t.ViewportId.Value)))
                res.Extra.Add(tl);

            return res;
        }

        /// <summary>Places / moves one target sheet's legends to match the reference sheet's.</summary>
        private void PlaceLegendsOnSheet(
            Document doc, ViewSheet sheet, SourceSheet source, string label,
            LegendTally tally, List<LegendPlacement> queued, HashSet<string> sourcesReported,
            ref int pass, ref int fail, ref int skip)
        {
            // A reference with no legends is a property of the reference, so it is said once rather
            // than repeated for every target aligned to it — but it IS said: the user ticked this
            // option, and a silently empty pass is indistinguishable from a broken one.
            if (source.Legends.Count == 0)
            {
                if (sourcesReported.Add(source.Label))
                {
                    Log(AppStrings.T("testing.alignSheetViews.log.legendsNoneOnSource", source.Label), "warn");
                    ReportSheetSchedules(doc, source);
                }
                return;
            }
            if (sourcesReported.Add(source.Label)) ReportSheetSchedules(doc, source);

            // A partial read of the target's existing legends would be read as "this sheet does not
            // have them" and answered by creating duplicates on top of what is already there. The
            // sheet is left alone instead, and said so.
            List<LegendEntry> targetLegends = CaptureLegends(doc, sheet, out bool readComplete);
            if (!readComplete)
            {
                Log(AppStrings.T("testing.alignSheetViews.log.legendReadFailed", label), "fail");
                fail++;
                return;
            }

            LegendMatch match = MatchLegends(source.Legends, targetLegends);

            int placedHere = 0, movedHere = 0, sameHere = 0;

            foreach (var (src, tgt) in match.Move)
            {
                LegendOutcome outcome = MoveLegend(doc, src, tgt, label);
                switch (outcome)
                {
                    case LegendOutcome.Moved:
                        movedHere++; tally.Moved++; pass++;
                        queued.Add(new LegendPlacement(sheet.Id, src.ViewportId, tgt.ViewportId, label, src.ViewName));
                        break;
                    case LegendOutcome.AlreadyInPlace:
                        sameHere++; tally.AlreadyInPlace++;
                        break;
                    default:
                        tally.Failed++; fail++;
                        break;
                }
            }

            foreach (var src in match.Create)
            {
                ElementId created = CreateLegend(doc, sheet, src, label);
                if (created == ElementId.InvalidElementId) { tally.Failed++; fail++; continue; }
                placedHere++; tally.Placed++; pass++;
                queued.Add(new LegendPlacement(sheet.Id, src.ViewportId, created, label, src.ViewName));
            }

            foreach (var extra in match.Extra)
            {
                tally.LeftAlone++; skip++;
                Log(AppStrings.T("testing.alignSheetViews.log.legendExtra", label, extra.ViewName), "warn");
            }

            if (placedHere > 0 || movedHere > 0)
                Log(AppStrings.T("testing.alignSheetViews.log.legendsResult", label, placedHere, movedHere, sameHere), "pass");
        }

        /// <summary>
        /// Moves an existing target legend onto the reference's placement. Returns what happened, so
        /// a legend that was already correct is counted as such rather than as a move that did
        /// nothing — the two look identical in a plain success count and only one of them is work.
        /// </summary>
        private LegendOutcome MoveLegend(Document doc, LegendEntry src, LegendEntry tgt, string label)
        {
            try
            {
                if (!(doc.GetElement(tgt.ViewportId) is Viewport vp))
                {
                    // The caller counts this as a failure, so it has to say so — a counted failure
                    // with no line is the one kind the user cannot act on.
                    Log(AppStrings.T("testing.alignSheetViews.log.legendMoveFailed", label, src.ViewName), "fail");
                    DiagnosticsLog.Warn("AlignSheetViews",
                        $"Legend viewport {tgt.ViewportId.Value} vanished between capture and move.");
                    return LegendOutcome.Failed;
                }

                if (tgt.TypeId          == src.TypeId
                 && tgt.Rotation        == src.Rotation
                 && Math.Abs(tgt.LabelLineLength - src.LabelLineLength) <= PlacementTolFt
                 && tgt.LabelOffset.DistanceTo(src.LabelOffset)         <= PlacementTolFt
                 && tgt.BoxCenter.DistanceTo(src.BoxCenter)             <= PlacementTolFt)
                    return LegendOutcome.AlreadyInPlace;

                // A pinned viewport silently refuses to move, so the pin is lifted for the write and
                // put back exactly as it was — including when the write throws.
                bool wasPinned = vp.Pinned;
                if (wasPinned) vp.Pinned = false;
                try
                {
                    ApplyLegendAppearance(doc, vp, src, label, isNew: false);
                    vp.SetBoxCenter(src.BoxCenter);
                }
                finally
                {
                    if (wasPinned)
                    {
                        try { vp.Pinned = true; }
                        catch (Exception ex)
                        {
                            Log(AppStrings.T("testing.alignSheetViews.log.legendRepinFailed", label, src.ViewName), "warn");
                            DiagnosticsLog.Swallowed($"AlignSheetViews: restore pin on legend viewport {tgt.ViewportId.Value}", ex);
                        }
                    }
                }
                return LegendOutcome.Moved;
            }
            catch (Exception ex)
            {
                Log(AppStrings.T("testing.alignSheetViews.log.legendMoveFailed", label, src.ViewName), "fail");
                DiagnosticsLog.Swallowed($"AlignSheetViews: move legend viewport {tgt.ViewportId.Value}", ex);
                return LegendOutcome.Failed;
            }
        }

        /// <summary>Places a legend the target sheet does not carry. Returns the new viewport's id,
        /// or <see cref="ElementId.InvalidElementId"/> if it could not be placed.</summary>
        private ElementId CreateLegend(Document doc, ViewSheet sheet, LegendEntry src, string label)
        {
            try
            {
                if (!Viewport.CanAddViewToSheet(doc, sheet.Id, src.ViewId))
                {
                    Log(AppStrings.T("testing.alignSheetViews.log.legendCannotPlace", label, src.ViewName), "warn");
                    DiagnosticsLog.Warn("AlignSheetViews",
                        $"Revit will not add legend view {src.ViewId.Value} ('{src.ViewName}') to sheet {sheet.Id.Value}.");
                    return ElementId.InvalidElementId;
                }

                Viewport vp = Viewport.Create(doc, sheet.Id, src.ViewId, src.BoxCenter);
                if (vp == null)
                {
                    Log(AppStrings.T("testing.alignSheetViews.log.legendCreateFailed", label, src.ViewName), "fail");
                    DiagnosticsLog.Warn("AlignSheetViews",
                        $"Viewport.Create returned null for legend view {src.ViewId.Value} on sheet {sheet.Id.Value}.");
                    return ElementId.InvalidElementId;
                }

                // Create placed the box at the reference's centre, but the writes below change what
                // the box IS — a viewport type with a title draws a bigger box than one without, and
                // the centre tracks it — so the centre is restated once the appearance is settled.
                ApplyLegendAppearance(doc, vp, src, label, isNew: true);
                vp.SetBoxCenter(src.BoxCenter);
                return vp.Id;
            }
            catch (Exception ex)
            {
                Log(AppStrings.T("testing.alignSheetViews.log.legendCreateFailed", label, src.ViewName), "fail");
                DiagnosticsLog.Swallowed($"AlignSheetViews: create legend {src.ViewId.Value} on sheet {sheet.Id.Value}", ex);
                return ElementId.InvalidElementId;
            }
        }

        /// <summary>
        /// Mirrors the reference viewport's type, rotation and title settings onto the target.
        ///
        /// This is not cosmetic. The viewport TYPE is what decides whether a view title is drawn at
        /// all, and <c>GetBoxCenter</c> measures the drawing together with its title — so a target
        /// whose type differs from the reference's has a box of a different size, and giving the two
        /// boxes the same centre would leave their drawings offset by half the difference. Each
        /// write is guarded on its own: a viewport type that refuses one setting should not cost the
        /// legend the others.
        /// </summary>
        /// <param name="isNew">True when the viewport was created by this run. A fresh viewport is
        /// born with the document's default type, so adopting the reference's is part of placing it
        /// — not a restyle of something the user had already set up, and not worth a line each.</param>
        private void ApplyLegendAppearance(Document doc, Viewport vp, LegendEntry src, string label, bool isNew)
        {
            try
            {
                if (src.TypeId != ElementId.InvalidElementId
                 && vp.GetTypeId() != src.TypeId
                 && doc.GetElement(src.TypeId) is ElementType)
                {
                    vp.ChangeTypeId(src.TypeId);
                    // Only an EXISTING legend's type change is worth a line: that one alters how a
                    // legend the user already placed looks, not just where it sits.
                    if (!isNew)
                        Log(AppStrings.T("testing.alignSheetViews.log.legendTypeChanged", label, src.ViewName), "warn");
                }
            }
            catch (Exception ex)
            {
                Log(AppStrings.T("testing.alignSheetViews.log.legendTypeFailed", label, src.ViewName), "warn");
                DiagnosticsLog.Swallowed($"AlignSheetViews: legend viewport type on {vp.Id.Value}", ex);
            }

            try { if (vp.Rotation != src.Rotation) vp.Rotation = src.Rotation; }
            catch (Exception ex)
            {
                Log(AppStrings.T("testing.alignSheetViews.log.legendRotationFailed", label, src.ViewName), "warn");
                DiagnosticsLog.Swallowed($"AlignSheetViews: legend rotation on viewport {vp.Id.Value}", ex);
            }

            // A refused title write leaves the legend drawing a different footprint than the
            // reference, so the placement below no longer registers exactly — that is a user-visible
            // outcome, not a diagnostics detail. The view path warns for the same failure.
            try { vp.LabelLineLength = src.LabelLineLength; }
            catch (Exception ex)
            {
                Log(AppStrings.T("testing.alignSheetViews.log.legendTitleFailed", label, src.ViewName), "warn");
                DiagnosticsLog.Swallowed($"AlignSheetViews: legend LabelLineLength on viewport {vp.Id.Value}", ex);
            }

            try { vp.LabelOffset = src.LabelOffset; }
            catch (Exception ex)
            {
                Log(AppStrings.T("testing.alignSheetViews.log.legendTitleFailed", label, src.ViewName), "warn");
                DiagnosticsLog.Swallowed($"AlignSheetViews: legend LabelOffset on viewport {vp.Id.Value}", ex);
            }
        }

        /// <summary>
        /// Checks each placed legend against the geometry the document actually ended up with, and
        /// returns how many had to be corrected.
        ///
        /// The size test is the whole point, and it is not optional. A viewport's box outline tracks
        /// its view title, so a centre that has moved means one of two opposite things: at EQUAL
        /// outline size the two viewports draw the same footprint and the gap is a genuine
        /// misplacement to correct; at DIFFERENT size the box has grown around a stationary drawing,
        /// and "correcting" the centre would take a legend that is sitting correctly and displace it
        /// by half a title. So a size mismatch is reported and left alone — never corrected.
        ///
        /// Both remaining checks — off the drawing area, and overlapping something already on the
        /// sheet — need the same outlines, so they ride along here rather than costing another pass.
        /// </summary>
        private int VerifyLegendPlacements(Document doc, List<LegendPlacement> placements, LegendTally tally)
        {
            int corrected = 0;

            // Both per-sheet lookups are identical for every legend on that sheet, and one of them
            // walks all its viewports — so they are resolved once per sheet, not once per legend.
            var titleBlocks = new Dictionary<long, Outline?>();
            var neighbours  = new Dictionary<long, List<(string Name, Outline Box)>>();

            foreach (var pl in placements)
            {
                try
                {
                    if (!(doc.GetElement(pl.SourceViewportId) is Viewport sVp)) continue;
                    if (!(doc.GetElement(pl.TargetViewportId) is Viewport tVp)) continue;

                    Outline srcBox = sVp.GetBoxOutline();
                    Outline tgtBox = tVp.GetBoxOutline();
                    if (srcBox == null || tgtBox == null)
                    {
                        // Placed, but never measured — so none of the checks below ran for it. A
                        // silent skip here is indistinguishable from a clean verification.
                        Log(AppStrings.T("testing.alignSheetViews.log.legendVerifyFailed", pl.Label, pl.ViewName), "warn");
                        DiagnosticsLog.Warn("AlignSheetViews",
                            $"Legend viewport {pl.TargetViewportId.Value} ('{pl.ViewName}') returned no box outline — placement not verified.");
                        continue;
                    }

                    double sw = srcBox.MaximumPoint.X - srcBox.MinimumPoint.X;
                    double sh = srcBox.MaximumPoint.Y - srcBox.MinimumPoint.Y;
                    double tw = tgtBox.MaximumPoint.X - tgtBox.MinimumPoint.X;
                    double th = tgtBox.MaximumPoint.Y - tgtBox.MinimumPoint.Y;

                    if (Math.Abs(sw - tw) > OutlineSizeTolFt || Math.Abs(sh - th) > OutlineSizeTolFt)
                    {
                        // Same legend view, different on-sheet footprint — the title state still
                        // differs, so the two centres do not describe the same drawing position and
                        // there is no correction that can be trusted.
                        tally.SizeMismatch++;
                        Log(AppStrings.T("testing.alignSheetViews.log.legendSizeDiffers", pl.Label, pl.ViewName), "warn");
                        DiagnosticsLog.Warn("AlignSheetViews",
                            $"Legend viewport {pl.TargetViewportId.Value} ('{pl.ViewName}') outline {tw:0.####}×{th:0.####} differs from the reference's {sw:0.####}×{sh:0.####} — left where it is.");
                    }
                    else
                    {
                        // Equal size, so equal centres mean equal drawings. SetBoxCenter is absolute,
                        // which makes this exact rather than a nudge that could compound.
                        XYZ truth = sVp.GetBoxCenter();
                        XYZ cur   = tVp.GetBoxCenter();
                        double dx = truth.X - cur.X, dy = truth.Y - cur.Y;
                        double off = Math.Sqrt(dx * dx + dy * dy);
                        if (off > PlacementTolFt)
                        {
                            tVp.SetBoxCenter(new XYZ(truth.X, truth.Y, cur.Z));
                            corrected++;
                            // The outline just moved with the box; shifting the one already in hand
                            // keeps the two checks below exact without re-reading dirtied geometry.
                            tgtBox = Translate(tgtBox, dx, dy);
                            Log(AppStrings.T("testing.alignSheetViews.log.legendCorrected", pl.Label, pl.ViewName, F(off)), "warn");
                        }
                    }

                    CheckLegendOnSheet(doc, pl, tgtBox, titleBlocks, neighbours);
                }
                catch (Exception ex)
                {
                    Log(AppStrings.T("testing.alignSheetViews.log.legendVerifyFailed", pl.Label, pl.ViewName), "warn");
                    DiagnosticsLog.Swallowed($"AlignSheetViews: verify legend viewport {pl.TargetViewportId.Value}", ex);
                }
            }

            return corrected;
        }

        /// <summary>
        /// Warns when a legend has landed somewhere it should not be. Sheet coordinates are copied
        /// verbatim from the reference — the same basis the view alignment uses — so a target whose
        /// title block is a different size, or placed at a different origin, can receive a legend
        /// that falls off the drawing area or on top of something already there. Both are named and
        /// left in place: the position came from the reference, and quietly relocating it would be a
        /// guess the user never asked for. Undo puts it back.
        /// </summary>
        private void CheckLegendOnSheet(
            Document doc, LegendPlacement pl, Outline box,
            Dictionary<long, Outline?> titleBlocks,
            Dictionary<long, List<(string Name, Outline Box)>> neighbours)
        {
            long sheetKey = pl.SheetId.Value;

            if (!titleBlocks.TryGetValue(sheetKey, out Outline? tb))
            {
                tb = TryTitleBlockOutline(doc, pl.SheetId);
                titleBlocks[sheetKey] = tb;
            }
            if (tb != null
             && (box.MinimumPoint.X < tb.MinimumPoint.X - OutlineSizeTolFt
              || box.MinimumPoint.Y < tb.MinimumPoint.Y - OutlineSizeTolFt
              || box.MaximumPoint.X > tb.MaximumPoint.X + OutlineSizeTolFt
              || box.MaximumPoint.Y > tb.MaximumPoint.Y + OutlineSizeTolFt))
            {
                Log(AppStrings.T("testing.alignSheetViews.log.legendOffSheet", pl.Label, pl.ViewName), "warn");
                DiagnosticsLog.Warn("AlignSheetViews",
                    $"Legend viewport {pl.TargetViewportId.Value} ('{pl.ViewName}') falls outside the title block of sheet {sheetKey}.");
            }

            if (!neighbours.TryGetValue(sheetKey, out var others))
            {
                others = CaptureViewportBoxes(doc, pl.SheetId);
                neighbours[sheetKey] = others;
            }
            foreach (var other in others)
            {
                if (!BoxesOverlap(box, other.Box)) continue;
                Log(AppStrings.T("testing.alignSheetViews.log.legendOverlaps", pl.Label, pl.ViewName, other.Name), "warn");
                break;   // one line names the collision; listing every viewport it touches adds nothing
            }
        }

        /// <summary>The placed title block's on-sheet bounding box, or null when the sheet has none
        /// (which is not an error — it just means there is no drawing area to test against).</summary>
        private Outline? TryTitleBlockOutline(Document doc, ElementId sheetId)
        {
            try
            {
                if (!(doc.GetElement(sheetId) is ViewSheet sheet)) return null;
                var tb = new FilteredElementCollector(doc, sheet.Id)
                    .OfCategory(BuiltInCategory.OST_TitleBlocks)
                    .WhereElementIsNotElementType()
                    .FirstElement();
                if (tb == null) return null;
                BoundingBoxXYZ bb = tb.get_BoundingBox(sheet);
                if (bb == null) return null;
                return new Outline(bb.Min, bb.Max);
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed($"AlignSheetViews: title block bounds on sheet {sheetId.Value}", ex);
                return null;
            }
        }

        /// <summary>Every non-legend viewport's on-sheet footprint, for the overlap check.</summary>
        private List<(string Name, Outline Box)> CaptureViewportBoxes(Document doc, ElementId sheetId)
        {
            var result = new List<(string, Outline)>();
            try
            {
                if (!(doc.GetElement(sheetId) is ViewSheet sheet)) return result;
                foreach (var vpId in sheet.GetAllViewports())
                {
                    try
                    {
                        if (!(doc.GetElement(vpId) is Viewport vp)) continue;
                        if (!(doc.GetElement(vp.ViewId) is View view)) continue;
                        if (view.ViewType == ViewType.Legend) continue;   // legend-on-legend is the user's own layout
                        Outline box = vp.GetBoxOutline();
                        if (box != null) result.Add((view.Name, box));
                    }
                    catch (Exception ex)
                    {
                        DiagnosticsLog.Swallowed($"AlignSheetViews: outline of viewport {vpId.Value}", ex);
                    }
                }
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed($"AlignSheetViews: viewport outlines on sheet {sheetId.Value}", ex);
            }
            return result;
        }

        private void ReportLegendSummary(LegendTally t)
        {
            if (t.Placed == 0 && t.Moved == 0 && t.AlreadyInPlace == 0 && t.LeftAlone == 0 && t.Failed == 0) return;
            Log(AppStrings.T("testing.alignSheetViews.log.legendsSummary",
                             t.Placed, t.Moved, t.AlreadyInPlace, t.LeftAlone),
                t.Failed > 0 || t.SizeMismatch > 0 ? "warn" : "pass");
        }

        /// <summary>
        /// Names any schedule instances on the reference sheet. A keynote legend is a SCHEDULE, not a
        /// legend — it is a <c>ScheduleSheetInstance</c> rather than a viewport, so this tool cannot
        /// place it. Saying so once beats letting the user find the gap on a plot and conclude the
        /// legend pass is broken.
        /// </summary>
        private void ReportSheetSchedules(Document doc, SourceSheet source)
        {
            try
            {
                if (source.SheetId == ElementId.InvalidElementId) return;

                int n = new FilteredElementCollector(doc, source.SheetId)
                    .OfClass(typeof(ScheduleSheetInstance))
                    .GetElementCount();
                if (n > 0)
                    Log(AppStrings.T("testing.alignSheetViews.log.legendSchedulesIgnored", source.Label, n), "warn");
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed($"AlignSheetViews: schedule scan on reference '{source.Label}'", ex);
            }
        }

        private static Outline Translate(Outline o, double dx, double dy)
            => new Outline(new XYZ(o.MinimumPoint.X + dx, o.MinimumPoint.Y + dy, o.MinimumPoint.Z),
                           new XYZ(o.MaximumPoint.X + dx, o.MaximumPoint.Y + dy, o.MaximumPoint.Z));

        /// <summary>In-plane overlap test. Touching edges are not an overlap.</summary>
        private static bool BoxesOverlap(Outline a, Outline b)
            => Overlap1D(a.MinimumPoint.X, a.MaximumPoint.X, b.MinimumPoint.X, b.MaximumPoint.X) > OutlineSizeTolFt
            && Overlap1D(a.MinimumPoint.Y, a.MaximumPoint.Y, b.MinimumPoint.Y, b.MaximumPoint.Y) > OutlineSizeTolFt;

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

                    // Legends are viewports too, and a legend view DOES report a scale — so the
                    // guard below never excluded them and only the crop-box test stood between a
                    // legend and the view matcher. That is not something to leave to chance: two
                    // legends are trivially "eligible" for each other (same ViewType, parallel
                    // ViewDirection), and pairing them would move one by crop-anchor maths computed
                    // from geometry a legend does not have. Legends belong to the legend pass.
                    if (view.ViewType == ViewType.Legend) continue;

                    if (view.Scale <= 0) continue;                 // perspective / schedule — no model scale

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
        ///
        /// Candidates are ranked by (depth overlap, in-plane overlap) so a counterpart at the same
        /// cut depth still wins whenever one exists, while a source whose only candidates sit at a
        /// different depth is matched rather than failed.
        ///
        /// Every source that ends up unmatched carries a <see cref="MissInfo"/> recording the best
        /// candidate it saw and why that candidate was rejected, so the run log can say WHY two
        /// sheets did not pair instead of only that they did not.
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
                    res.Pairs.Add(NewPair(src, byScope[0]));
                }
                else if (byScope.Count > 1)
                {
                    restrictTo[src.ViewportId] = new HashSet<ElementId>(byScope.Select(t => t.ViewportId));
                }
            }

            // Pass 2 - overlap fallback, every qualifying pair scored up front. Sub-threshold and
            // ineligible candidates are not discarded outright: the best of them is remembered per
            // source so an unmatched source can explain itself.
            var cands = new List<(VpEntry S, VpEntry T, double Score, bool DepthOk)>();
            var misses = new Dictionary<ElementId, MissInfo>();
            foreach (var src in srcEntries)
            {
                if (settled.Contains(src.ViewportId)) continue;
                var miss = new MissInfo(src);
                misses[src.ViewportId] = miss;
                restrictTo.TryGetValue(src.ViewportId, out var allowed);
                foreach (var t in tgtEntries)
                {
                    if (allowed != null && !allowed.Contains(t.ViewportId)) continue;
                    if (!Eligible(src, t)) continue;
                    miss.AnyEligible = true;
                    double sc = OverlapInSourcePlane(src, t, out bool depthOk);
                    if (sc > miss.BestScore)
                    {
                        miss.Best        = t;
                        miss.BestScore   = sc;
                        miss.BestDepthOk = depthOk;
                        miss.BestTaken   = used.Contains(t.ViewportId);
                    }
                    if (used.Contains(t.ViewportId)) continue;
                    if (sc < OverlapThreshold) continue;
                    cands.Add((src, t, sc, depthOk));
                }
            }

            // Depth first, then in-plane overlap — a same-depth counterpart is always preferred, and
            // a depth mismatch only decides anything when nothing at the same depth is available.
            foreach (var c in cands.OrderByDescending(x => x.DepthOk).ThenByDescending(x => x.Score).ToList())
            {
                if (settled.Contains(c.S.ViewportId)) continue;
                if (used.Contains(c.T.ViewportId))    continue;

                // Ambiguous when another still-free target in the SAME depth tier scores nearly as
                // well for this source. A rival across the tier boundary is not a real rival — the
                // tier already decided between them.
                var rival = cands.FirstOrDefault(o => o.S.ViewportId == c.S.ViewportId
                                                   && o.T.ViewportId != c.T.ViewportId
                                                   && o.DepthOk == c.DepthOk
                                                   && !used.Contains(o.T.ViewportId)
                                                   && o.Score >= 0.8 * c.Score);
                settled.Add(c.S.ViewportId);
                if (rival.T != null) { res.Ambiguous.Add((c.S, c.T, rival.T)); continue; }

                used.Add(c.T.ViewportId);
                var pair = NewPair(c.S, c.T);
                pair.AreaMatch     = c.Score;
                pair.DepthOverlaps = c.DepthOk;
                res.Pairs.Add(pair);
            }

            foreach (var src in srcEntries.Where(se => !settled.Contains(se.ViewportId)))
                res.Missing.Add(misses.TryGetValue(src.ViewportId, out var mi) ? mi : new MissInfo(src));
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
        /// Builds a pair and records the two facts every later phase and log line needs: how much
        /// model area the two views actually share, and whether they disagree about scope boxes.
        /// The area is measured here for scope-box pairs (which never went through the overlap
        /// scoring); the overlap pass overwrites it with the score it already computed.
        /// </summary>
        private MatchedPair NewPair(VpEntry src, VpEntry tgt)
        {
            var pair = new MatchedPair(src, tgt)
            {
                ScopeBoxMismatch = src.ScopeBoxId != ElementId.InvalidElementId
                                && tgt.ScopeBoxId != src.ScopeBoxId,
            };
            pair.AreaMatch     = OverlapInSourcePlane(src, tgt, out bool depthOk);
            pair.DepthOverlaps = depthOk;
            return pair;
        }

        /// <summary>A candidate must be the same view type and look the same way to be pairable.</summary>
        private static bool Eligible(VpEntry src, VpEntry cand)
            => cand.Type == src.Type
            && Math.Abs(src.ViewDir.DotProduct(cand.ViewDir)) >= ParallelDot;

        /// <summary>
        /// In-plane overlap fraction (intersection / smaller crop area) of a candidate view's
        /// crop rectangle against the source's, both projected into the source crop frame.
        ///
        /// <paramref name="depthOverlaps"/> reports whether the two views' cut-depth ranges also
        /// intersect. This used to be a hard veto — a depth miss returned 0 and the candidate was
        /// disqualified before its footprint was ever measured. That is wrong for exactly the case
        /// this tool exists for: a view governed by a SCOPE BOX takes its crop from the box, and a
        /// scope box has a finite vertical extent, so a scoped reference view and an unscoped
        /// target on another level can have disjoint depth ranges while covering the same plan
        /// area. The sheet then reported "no counterpart" and nothing aligned. Depth is now a
        /// ranking tier in <see cref="MatchSheet"/>: a same-depth candidate still wins whenever one
        /// exists, but its absence never fails the sheet.
        /// </summary>
        private static double OverlapInSourcePlane(VpEntry src, VpEntry cand, out bool depthOverlaps)
        {
            depthOverlaps = false;
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

            // Depth is reported, never vetoed — see the summary above.
            depthOverlaps = Overlap1D(sNmin, sNmax, nMin, nMax) > 0;

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

        // ══════════════════════════════════════════════════════════════════════
        // Legend types
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>What happened to one legend the tool tried to move.</summary>
        private enum LegendOutcome { Failed, Moved, AlreadyInPlace }

        /// <summary>A legend viewport captured from a sheet. Everything here is read before the run
        /// writes anything, and everything is a property of the VIEWPORT — the legend view itself is
        /// never modified.</summary>
        private sealed class LegendEntry
        {
            public ElementId        ViewportId      { get; set; } = ElementId.InvalidElementId;
            public ElementId        ViewId          { get; set; } = ElementId.InvalidElementId;
            public string           ViewName        { get; set; } = "";
            public XYZ              BoxCenter       { get; set; } = XYZ.Zero;
            public ViewportRotation Rotation        { get; set; } = ViewportRotation.None;
            public ElementId        TypeId          { get; set; } = ElementId.InvalidElementId;
            public double           LabelLineLength { get; set; }
            public XYZ              LabelOffset     { get; set; } = XYZ.Zero;
        }

        /// <summary>One reference sheet's legends reconciled against one target sheet's.</summary>
        private sealed class LegendMatch
        {
            /// <summary>Target instances to move onto their reference counterpart.</summary>
            public List<(LegendEntry Src, LegendEntry Tgt)> Move { get; } = new List<(LegendEntry, LegendEntry)>();
            /// <summary>Reference instances the target does not have — to be placed.</summary>
            public List<LegendEntry> Create { get; } = new List<LegendEntry>();
            /// <summary>Target instances the reference does not have. Reported, never deleted.</summary>
            public List<LegendEntry> Extra  { get; } = new List<LegendEntry>();
        }

        /// <summary>A legend placed or moved this run, waiting on the run's regenerate before its
        /// real on-sheet geometry can be measured.</summary>
        private sealed class LegendPlacement
        {
            public LegendPlacement(ElementId sheetId, ElementId sourceViewportId,
                                   ElementId targetViewportId, string label, string viewName)
            {
                SheetId          = sheetId;
                SourceViewportId = sourceViewportId;
                TargetViewportId = targetViewportId;
                Label            = label;
                ViewName         = viewName;
            }
            public ElementId SheetId          { get; }
            public ElementId SourceViewportId { get; }
            public ElementId TargetViewportId { get; }
            public string    Label            { get; }
            public string    ViewName         { get; }
        }

        /// <summary>Run-wide legend counts. Placed and Moved are split from AlreadyInPlace on
        /// purpose: a single "N done" that counted no-op writes is what makes a run that changed
        /// nothing look like a run that worked.</summary>
        private sealed class LegendTally
        {
            public int Placed         { get; set; }
            public int Moved          { get; set; }
            public int AlreadyInPlace { get; set; }
            public int LeftAlone      { get; set; }
            public int Failed         { get; set; }
            public int SizeMismatch   { get; set; }
        }

        // ── A captured source sheet ───────────────────────────────────────────
        private sealed class SourceSheet
        {
            public SourceSheet(ElementId sheetId, string label, List<VpEntry> entries, List<LegendEntry> legends)
            {
                SheetId = sheetId; Label = label; Entries = entries; Legends = legends;
            }
            public ElementId         SheetId { get; }
            public string            Label   { get; }
            public List<VpEntry>     Entries { get; }
            /// <summary>Empty unless <see cref="PlaceLegends"/> is on — the capture is skipped
            /// entirely when the option is off.</summary>
            public List<LegendEntry> Legends { get; }
        }

        // ── One source-sheet → target-sheet matching result ───────────────────
        private sealed class SheetMatch
        {
            public List<MatchedPair> Pairs     { get; } = new List<MatchedPair>();
            public List<MissInfo>    Missing   { get; } = new List<MissInfo>();
            public List<VpEntry>     Extra     { get; } = new List<VpEntry>();
            public List<(VpEntry src, VpEntry a, VpEntry b)> Ambiguous { get; } = new List<(VpEntry, VpEntry, VpEntry)>();
            public double            Score     { get; set; }
        }

        /// <summary>
        /// Why one reference view found no counterpart. "No counterpart" on its own is not a
        /// diagnosis — the two commonest causes (the target sheet holds nothing of that view type,
        /// and the closest candidate's crop is a different size or position so the overlap falls
        /// under the threshold) look identical in the log and need opposite fixes. This carries
        /// enough to name the cause and quote the numbers behind it.
        /// </summary>
        private sealed class MissInfo
        {
            public MissInfo(VpEntry source) { Source = source; }

            public VpEntry  Source { get; }

            /// <summary>Was there any same-type, same-orientation view on the target sheet at all?</summary>
            public bool     AnyEligible { get; set; }

            /// <summary>The closest eligible candidate seen, whatever its score.</summary>
            public VpEntry? Best        { get; set; }

            /// <summary>That candidate's in-plane overlap fraction (0..1).</summary>
            public double   BestScore   { get; set; }

            /// <summary>Whether that candidate's cut-depth range met the source's.</summary>
            public bool     BestDepthOk { get; set; }

            /// <summary>Whether that candidate had already been claimed by another reference view.</summary>
            public bool     BestTaken   { get; set; }
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

            /// <summary>
            /// The source carries a scope box the target does not share (none, or a different one).
            /// The alignment does not care — it registers a shared world point using each view's own
            /// crop — but it is the single most useful thing to say in the log when a run looks wrong,
            /// and it is what keeps the tool from silently writing a scope box the user never asked for.
            /// </summary>
            public bool      ScopeBoxMismatch { get; set; }

            /// <summary>Fraction of model area the two views share (0..1), as measured at match time.</summary>
            public double    AreaMatch { get; set; }

            /// <summary>Whether the two views' cut-depth ranges met. False is normal for a scoped
            /// reference aligned to an unscoped target on another level.</summary>
            public bool      DepthOverlaps { get; set; }

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
