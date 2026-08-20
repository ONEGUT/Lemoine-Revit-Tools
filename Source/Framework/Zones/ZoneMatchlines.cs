using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace LemoineTools.Framework.Zones
{
    // =========================================================================
    // ZoneMatchlines — reads matchlines out of a document (usually a link) so a
    // key plan can draw them and trim its highlight to them.
    //
    // WHAT A MATCHLINE IS, TO THE API:
    //
    //   There is NO Matchline class in Autodesk.Revit.DB (confirmed by reading
    //   libs/RevitAPI.dll's metadata, not by string-searching it). A matchline
    //   is an ordinary element of category OST_Matchline, and it is VIEW
    //   SPECIFIC — it belongs to the plan view it was drawn in, reachable via
    //   Element.OwnerViewId. So a document is full of matchlines belonging to
    //   many views, and taking all of them would stack every level's matchlines
    //   on top of each other.
    //
    //   That is why this filters by OWNER VIEW, and matches the owner view to
    //   the wanted level by the LEVEL'S NAME — the cross-document identity rule
    //   this whole zone model follows, since a linked model's level is a
    //   different element in a different document and its id means nothing here.
    //
    // Geometry is read as straight segments in HOST world XY at z = 0: a
    // CurveElement exposes GeometryCurve directly, and anything else falls back
    // to the element's geometry. Curved matchlines tessellate like everything
    // else in the key plan.
    //
    // Read-only. No transaction.
    //
    // UNVERIFIED, needs a Windows/Revit run: whether every matchline in the wild
    // resolves through CurveElement.GeometryCurve, or whether some come back
    // only through the geometry fallback. Both paths are implemented and a zero
    // result is always reported.
    // =========================================================================
    public static class ZoneMatchlines
    {
        /// <summary>One matchline segment, in host world coordinates at z = 0.</summary>
        public sealed class Segment
        {
            public XYZ A { get; set; } = XYZ.Zero;
            public XYZ B { get; set; } = XYZ.Zero;
            /// <summary>Name of the view the matchline was drawn in. Provenance for the log.</summary>
            public string OwnerViewName { get; set; } = "";
        }

        public sealed class Result
        {
            public List<Segment> Segments { get; } = new List<Segment>();
            /// <summary>Views that contributed, for a log line that names what was read.</summary>
            public List<string> Views { get; } = new List<string>();
            /// <summary>Matchlines found in the document before the level filter.</summary>
            public int TotalFound { get; set; }
            public bool Ok => Segments.Count > 0;
        }

        /// <summary>
        /// Collects the matchlines drawn on plan views of one level.
        ///
        /// Reports a zero result explicitly — a silent empty list is indistinguishable from a
        /// broken collector, and that silence has hidden real bugs in this repo before.
        /// </summary>
        public static Result Collect(Document? sourceDoc, Transform? transform,
                                     string hostLevelName, Action<string, string>? log = null)
        {
            var result = new Result();
            if (sourceDoc == null) return result;

            void Say(string m, string t)
            {
                try { log?.Invoke(m, t); }
                catch (Exception ex) { DiagnosticsLog.Swallowed("ZoneMatchlines: log", ex); }
            }

            Transform tf = transform ?? Transform.Identity;

            List<Element> matchlines;
            try
            {
                matchlines = new FilteredElementCollector(sourceDoc)
                    .OfCategory(BuiltInCategory.OST_Matchline)
                    .WhereElementIsNotElementType()
                    .ToList();
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("ZoneMatchlines: collect matchlines", ex);
                Say("Matchlines could not be read from the selected document.", "warn");
                return result;
            }

            result.TotalFound = matchlines.Count;
            if (matchlines.Count == 0)
            {
                Say("Found 0 matchlines in the selected document.", "warn");
                return result;
            }

            // Owner views whose GenLevel matches the wanted level, BY NAME.
            var wantedViews = new Dictionary<ElementId, string>();
            try
            {
                foreach (var v in new FilteredElementCollector(sourceDoc)
                             .OfClass(typeof(ViewPlan)).Cast<ViewPlan>())
                {
                    if (v.IsTemplate) continue;
                    Level? gen = null;
                    try { gen = v.GenLevel; }
                    catch (Exception ex) { DiagnosticsLog.Swallowed($"ZoneMatchlines: GenLevel of '{v.Name}'", ex); }
                    if (gen == null) continue;
                    if (!string.Equals(gen.Name, hostLevelName, StringComparison.OrdinalIgnoreCase)) continue;
                    wantedViews[v.Id] = v.Name;
                }
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("ZoneMatchlines: collect plan views", ex);
            }

            if (wantedViews.Count == 0)
            {
                Say($"Found {matchlines.Count} matchline(s), but no plan view on a level named " +
                    $"'{hostLevelName}' to read them from — none were used.", "warn");
                return result;
            }

            int used = 0;
            foreach (var el in matchlines)
            {
                ElementId owner;
                try { owner = el.OwnerViewId; }
                catch (Exception ex)
                {
                    DiagnosticsLog.Swallowed($"ZoneMatchlines: owner view of {el.Id}", ex);
                    continue;
                }
                if (!wantedViews.TryGetValue(owner, out string viewName)) continue;

                var curves = ReadCurves(el);
                if (curves.Count == 0) continue;

                used++;
                if (!result.Views.Contains(viewName)) result.Views.Add(viewName);

                foreach (var c in curves)
                {
                    IList<XYZ>? pts = null;
                    try { pts = c.Tessellate(); }
                    catch (Exception ex)
                    {
                        DiagnosticsLog.Swallowed($"ZoneMatchlines: tessellate {el.Id}", ex);
                        continue;
                    }
                    if (pts == null || pts.Count < 2) continue;

                    for (int i = 0; i < pts.Count - 1; i++)
                    {
                        XYZ a = tf.OfPoint(pts[i]);
                        XYZ b = tf.OfPoint(pts[i + 1]);
                        var fa = new XYZ(a.X, a.Y, 0);
                        var fb = new XYZ(b.X, b.Y, 0);
                        if (fa.DistanceTo(fb) < ZonePolygonOps.WeldToleranceFt) continue;
                        result.Segments.Add(new Segment { A = fa, B = fb, OwnerViewName = viewName });
                    }
                }
            }

            if (result.Segments.Count == 0)
                Say($"Found {matchlines.Count} matchline(s) but none on '{hostLevelName}' " +
                    "yielded readable geometry.", "warn");
            else
                Say($"Read {result.Segments.Count} matchline segment(s) from {used} matchline(s) " +
                    $"on '{hostLevelName}' (views: {string.Join(", ", result.Views)}).", "info");

            return result;
        }

        /// <summary>
        /// A matchline's curves. CurveElement exposes GeometryCurve directly; anything else
        /// falls back to walking the element's geometry, because there is no Matchline class
        /// guaranteeing the first path.
        /// </summary>
        private static List<Curve> ReadCurves(Element el)
        {
            var curves = new List<Curve>();

            if (el is CurveElement ce)
            {
                try
                {
                    var gc = ce.GeometryCurve;
                    if (gc != null) { curves.Add(gc); return curves; }
                }
                catch (Exception ex)
                {
                    DiagnosticsLog.Swallowed($"ZoneMatchlines: GeometryCurve of {el.Id}", ex);
                }
            }

            try
            {
                var opts = new Options { ComputeReferences = false, IncludeNonVisibleObjects = false };
                var geo = el.get_Geometry(opts);
                if (geo == null) return curves;
                foreach (var o in geo)
                {
                    if (o is Curve c) curves.Add(c);
                    else if (o is GeometryInstance gi)
                    {
                        var inst = gi.GetInstanceGeometry();
                        if (inst == null) continue;
                        foreach (var io in inst)
                            if (io is Curve ic) curves.Add(ic);
                    }
                }
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed($"ZoneMatchlines: geometry of {el.Id}", ex);
            }

            return curves;
        }
    }
}
