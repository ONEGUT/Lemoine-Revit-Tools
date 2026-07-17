using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using LemoineTools.Framework;
using LemoineTools.Tools.Dimensioning;

namespace LemoineTools.Tools.Dimensioning.ElevationTag
{
    /// <summary>Aggregate result of one elevation-tag run.</summary>
    public struct ElevationTagResult
    {
        public int Placed;    // spot elevations placed
        public int Failures;  // markers that could not be tagged
    }

    /// <summary>
    /// Places a Revit spot elevation at each clash marker in the selected section / elevation views.
    /// The Clash Finder's elevation mode draws one tagged vertical diameter line per clash (spanning
    /// the round fill top→bottom); this pass re-finds those lines and anchors a <see cref="SpotDimension"/>
    /// at the chosen <c>Top</c> / <c>Centre</c> / <c>Bottom</c> point. The caller owns no transaction —
    /// this opens its own, so it must run after the marking transaction has committed.
    /// Each placed tag is stamped via <see cref="ClashTagSchema"/> so the clear-previous pass removes it.
    /// </summary>
    public static class ElevationTagRunner
    {
        // Leader geometry in PAPER feet, multiplied by the view scale at placement — the old
        // fixed 1.5 ft / 3.0 ft model offsets matched a 1:96 view and were wrong everywhere
        // else (overlapping the round at coarse scales, miles away at fine ones). These paper
        // values reproduce the old look exactly at 1:96. ⚠ tunable — verify on a Windows plot.
        private const double LeaderBendPaperFt = 1.5 / 96.0;
        private const double LeaderEndPaperFt  = 3.0 / 96.0;

        // anchorMode: "Top" | "Centre" | "Bottom"
        public static ElevationTagResult Run(
            Document doc, IList<ElementId> viewIds, string anchorMode,
            ElementId spotTypeId, Action<string, string> log, Action<int>? progress = null)
        {
            log = log ?? ((a, b) => { });
            var result = new ElevationTagResult();
            if (doc == null || viewIds == null || viewIds.Count == 0) return result;

            using (var tx = new Transaction(doc, "Lemoine - Clash Elevation Tags"))
            {
                tx.Start();
                ConfigureFailures(tx);

                int processed = 0;
                foreach (var viewId in viewIds)
                {
                    // Cooperative cancel at the per-view boundary; tags already placed below
                    // still commit, so a stopped run preserves the views finished so far.
                    if (RunState.CancelRequested)
                    {
                        log(AppStrings.T("common.log.stoppedByUser", processed, viewIds.Count), "warn");
                        break;
                    }
                    processed++;
                    progress?.Invoke((int)(processed * 100.0 / viewIds.Count));

                    if (!(doc.GetElement(viewId) is View view)) continue;

                    XYZ up    = view.UpDirection.Normalize();
                    XYZ right = view.RightDirection.Normalize();

                    // Tagged straight detail lines are the round-fill diameter markers placed in
                    // elevation mode — one per clash. (Filled regions carry the same tag but are not
                    // CurveElements, so they are excluded here.)
                    List<CurveElement> markers;
                    try
                    {
                        markers = new FilteredElementCollector(doc, viewId)
                            .OfCategory(BuiltInCategory.OST_Lines)
                            .WhereElementIsNotElementType()
                            .Where(ClashTagSchema.IsOurs)
                            .OfType<CurveElement>()
                            .Where(ce => ce.GeometryCurve is Line)
                            .ToList();
                    }
                    catch (Exception ex)
                    {
                        DiagnosticsLog.Swallowed("ElevationTagRunner: collect marker lines", ex);
                        log(AppStrings.T("clash.elevationFinder.log.markersReadFail", view.Name, ex.Message), "fail");
                        result.Failures++;
                        continue;
                    }

                    if (markers.Count == 0) continue;
                    log(AppStrings.T("clash.elevationFinder.log.viewMarkers", view.Name, markers.Count), "info");

                    foreach (var ce in markers)
                    {
                        try
                        {
                            var line = (Line)ce.GeometryCurve;
                            var p0 = line.GetEndPoint(0);
                            var p1 = line.GetEndPoint(1);

                            // Top = the endpoint higher along the view's up axis.
                            bool p1IsTop = p1.DotProduct(up) >= p0.DotProduct(up);
                            XYZ top    = p1IsTop ? p1 : p0;
                            XYZ bottom = p1IsTop ? p0 : p1;
                            XYZ centre = (p0 + p1).Multiply(0.5);

                            XYZ anchor =
                                  string.Equals(anchorMode, "Top",    StringComparison.OrdinalIgnoreCase) ? top
                                : string.Equals(anchorMode, "Bottom", StringComparison.OrdinalIgnoreCase) ? bottom
                                : centre;

                            // Prefer the curve's geometry reference; fall back to a whole-element
                            // reference so a detail line whose curve exposes no reference can still anchor.
                            Reference? reference = ce.GeometryCurve.Reference;
                            if (reference == null)
                            {
                                try { reference = new Reference(ce); }
                                catch (Exception ex) { DiagnosticsLog.Swallowed("ElevationTagRunner: build element reference", ex); }
                            }
                            if (reference == null)
                            {
                                DiagnosticsLog.Swallowed("ElevationTagRunner: marker line has no reference",
                                    new InvalidOperationException("null curve reference"));
                                log(AppStrings.T("clash.elevationFinder.log.noReference", view.Name), "fail");
                                result.Failures++;
                                continue;
                            }

                            // Leader bend + text offset to the right of the round so the tag
                            // clears the fill — paper-space distances × the view scale, so the
                            // tag sits the same distance off the round on paper at every scale.
                            double scale = view.Scale > 0 ? view.Scale : 96;
                            XYZ bend = anchor.Add(right.Multiply(LeaderBendPaperFt * scale));
                            XYZ end  = anchor.Add(right.Multiply(LeaderEndPaperFt * scale));

                            var spot = doc.Create.NewSpotElevation(
                                view, reference, anchor, bend, end, anchor, true);

                            if (spot == null) { result.Failures++; continue; }

                            if (spotTypeId != ElementId.InvalidElementId)
                            {
                                try { if (spot.GetTypeId() != spotTypeId) spot.ChangeTypeId(spotTypeId); }
                                catch (Exception ex) { DiagnosticsLog.Swallowed("ElevationTagRunner: set spot elevation type", ex); }
                            }

                            ClashTagSchema.StampTag(spot, ClashTagSchema.ReadGroup(ce));
                            result.Placed++;
                        }
                        catch (Exception ex)
                        {
                            DiagnosticsLog.Swallowed("ElevationTagRunner: place spot elevation", ex);
                            log(AppStrings.T("clash.elevationFinder.log.spotFail", view.Name, ex.Message), "fail");
                            result.Failures++;
                        }
                    }
                }

                tx.Commit();
            }

            return result;
        }

        private static void ConfigureFailures(Transaction tx)
        {
            var opts = tx.GetFailureHandlingOptions();
            opts.SetClearAfterRollback(true);
            opts.SetDelayedMiniWarnings(true);
            tx.SetFailureHandlingOptions(opts);
        }
    }
}
