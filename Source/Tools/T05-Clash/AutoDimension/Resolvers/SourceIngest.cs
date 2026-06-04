using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.Clash.AutoDimension.Resolvers
{
    /// <summary>
    /// Step 1 of the pipeline: find the Clash Finder's tagged cross-line markers in a view and
    /// validate that each yields a usable endpoint reference. Lines carrying no reference are
    /// reported as unresolved sources (never silently skipped). Read-only — no transaction.
    /// </summary>
    public static class SourceIngest
    {
        /// <summary>
        /// Collects valid source lines from <paramref name="view"/>, appending any line that
        /// cannot produce an endpoint reference to <paramref name="unresolved"/>. Results are
        /// returned in a deterministic order (by ElementId).
        /// </summary>
        public static List<SourceLine> Collect(
            Document doc, View view, ViewProjection projection,
            List<Core.UnresolvedTarget> unresolved)
        {
            var result = new List<SourceLine>();
            if (doc == null || view == null) return result;

            List<DetailCurve> curves;
            try
            {
                curves = new FilteredElementCollector(doc, view.Id)
                    .OfCategory(BuiltInCategory.OST_Lines)
                    .WhereElementIsNotElementType()
                    .Where(ClashTagSchema.IsOurs)
                    .OfType<DetailCurve>()
                    .OrderBy(dc => dc.Id.IntegerValue)
                    .ToList();
            }
            catch (Exception ex)
            {
                LemoineLog.Swallowed("SourceIngest: collect tagged cross lines", ex);
                return result;
            }

            // Group the cross lines back into clashes. Every line of one clash carries the same
            // group id (stamped at marking); lines without one (legacy markers) fall back to one
            // source per line. Grouped → exactly one source per clash, anchored at the centre.
            var grouped   = new Dictionary<string, List<DetailCurve>>();
            var ungrouped = new List<DetailCurve>();
            foreach (var dc in curves)
            {
                string g = ClashTagSchema.ReadGroup(dc);
                if (string.IsNullOrEmpty(g)) { ungrouped.Add(dc); continue; }
                if (!grouped.TryGetValue(g, out var list)) grouped[g] = list = new List<DetailCurve>();
                list.Add(dc);
            }

            foreach (var g in grouped.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                var src = BuildClashSource(g, grouped[g], projection, unresolved);
                if (src != null) result.Add(src);
            }

            // Legacy / un-grouped markers: one source per line at its endpoint (old behaviour).
            foreach (var dc in ungrouped)
            {
                var src = BuildSingleLineSource(dc.Id.IntegerValue.ToString(), dc, projection, unresolved);
                if (src != null) result.Add(src);
            }

            return result;
        }

        /// <summary>One source for a whole clash: the centre is the centroid of all its lines'
        /// endpoints; the reference is the endpoint nearest that centre (in centre-mode markers an
        /// arm meets exactly at the centre, so the dimension attaches at the clash point).</summary>
        private static SourceLine? BuildClashSource(
            string key, List<DetailCurve> lines, ViewProjection projection, List<Core.UnresolvedTarget> unresolved)
        {
            // Read each line's geometry exactly once (Id-ordered for deterministic tie-breaks).
            var geos = new List<(Curve gc, XYZ p0, XYZ p1)>();
            foreach (var dc in lines.OrderBy(d => d.Id.IntegerValue))
            {
                Curve? gc = SafeGeometry(dc);
                if (gc == null) continue;
                geos.Add((gc, gc.GetEndPoint(0), gc.GetEndPoint(1)));
            }
            if (geos.Count == 0)
            {
                unresolved.Add(new Core.UnresolvedTarget { SourceKey = key, Reason = "clash marker has no readable line geometry" });
                return null;
            }

            XYZ sum = XYZ.Zero;
            foreach (var g in geos) { sum += g.p0; sum += g.p1; }
            XYZ centre = sum.Divide(geos.Count * 2);

            // Nearest endpoint to the centroid → its reference + point.
            Reference? bestRef = null;
            XYZ? bestPt = null;
            double bestDist = double.MaxValue;
            foreach (var g in geos)
            {
                for (int idx = 0; idx <= 1; idx++)
                {
                    XYZ p = idx == 0 ? g.p0 : g.p1;
                    double d = p.DistanceTo(centre);
                    if (d >= bestDist) continue;
                    Reference? r = null;
                    try { r = g.gc.GetEndPointReference(idx); }
                    catch (Exception ex) { LemoineLog.Swallowed("SourceIngest: endpoint reference", ex); }
                    if (r == null) continue;
                    bestDist = d; bestRef = r; bestPt = p;
                }
            }

            if (bestRef == null || bestPt == null)
            {
                unresolved.Add(new Core.UnresolvedTarget { SourceKey = key, Reason = "clash marker carries no usable endpoint reference" });
                return null;
            }

            return new SourceLine
            {
                SourceKey = key,
                Id        = lines[0].Id,
                SourceRef = bestRef,
                Anchor3d  = bestPt,
                Anchor2d  = projection.To2D(bestPt),
            };
        }

        /// <summary>Legacy path: one source from a single line's endpoint(1).</summary>
        private static SourceLine? BuildSingleLineSource(
            string key, DetailCurve dc, ViewProjection projection, List<Core.UnresolvedTarget> unresolved)
        {
            Curve? gc = SafeGeometry(dc);
            Reference? sref = null;
            XYZ? anchor = null;
            if (gc != null)
            {
                try { sref = gc.GetEndPointReference(1); anchor = gc.GetEndPoint(1); }
                catch (Exception ex) { LemoineLog.Swallowed("SourceIngest: endpoint reference", ex); }
            }
            if (sref == null || anchor == null)
            {
                unresolved.Add(new Core.UnresolvedTarget { SourceKey = key, Reason = "source cross-line carries no usable endpoint reference" });
                return null;
            }
            return new SourceLine
            {
                SourceKey = key,
                Id        = dc.Id,
                SourceRef = sref,
                Anchor3d  = anchor,
                Anchor2d  = projection.To2D(anchor),
            };
        }

        private static Curve? SafeGeometry(DetailCurve dc)
        {
            try { return dc.GeometryCurve; }
            catch (Exception ex) { LemoineLog.Swallowed("SourceIngest: read geometry curve", ex); return null; }
        }
    }
}
