using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.Testing.ElevationTag
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
        // anchorMode: "Top" | "Centre" | "Bottom"
        public static ElevationTagResult Run(
            Document doc, IList<ElementId> viewIds, string anchorMode,
            ElementId spotTypeId, Action<string, string> log)
        {
            log = log ?? ((a, b) => { });
            var result = new ElevationTagResult();
            if (doc == null || viewIds == null || viewIds.Count == 0) return result;

            using (var tx = new Transaction(doc, "Lemoine - Clash Elevation Tags"))
            {
                tx.Start();
                ConfigureFailures(tx);

                foreach (var viewId in viewIds)
                {
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
                        LemoineLog.Swallowed("ElevationTagRunner: collect marker lines", ex);
                        log($"Could not read markers in '{view.Name}': {ex.Message}", "fail");
                        result.Failures++;
                        continue;
                    }

                    if (markers.Count == 0) continue;
                    log($"— '{view.Name}': {markers.Count} marker(s) —", "info");

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
                                catch (Exception ex) { LemoineLog.Swallowed("ElevationTagRunner: build element reference", ex); }
                            }
                            if (reference == null)
                            {
                                LemoineLog.Swallowed("ElevationTagRunner: marker line has no reference",
                                    new InvalidOperationException("null curve reference"));
                                log($"Marker in '{view.Name}' had no usable reference — skipped.", "fail");
                                result.Failures++;
                                continue;
                            }

                            // Leader bend + text offset to the right of the round so the tag clears the fill.
                            XYZ bend = anchor.Add(right.Multiply(1.5));
                            XYZ end  = anchor.Add(right.Multiply(3.0));

                            var spot = doc.Create.NewSpotElevation(
                                view, reference, anchor, bend, end, anchor, true);

                            if (spot == null) { result.Failures++; continue; }

                            if (spotTypeId != ElementId.InvalidElementId)
                            {
                                try { if (spot.GetTypeId() != spotTypeId) spot.ChangeTypeId(spotTypeId); }
                                catch (Exception ex) { LemoineLog.Swallowed("ElevationTagRunner: set spot elevation type", ex); }
                            }

                            ClashTagSchema.StampTag(spot, ClashTagSchema.ReadGroup(ce));
                            result.Placed++;
                        }
                        catch (Exception ex)
                        {
                            LemoineLog.Swallowed("ElevationTagRunner: place spot elevation", ex);
                            log($"Spot elevation failed in '{view.Name}': {ex.Message}", "fail");
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
