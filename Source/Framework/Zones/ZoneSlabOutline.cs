using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace LemoineTools.Framework.Zones
{
    // =========================================================================
    // ZoneSlabOutline — the building outline for a key plan, from slab edges.
    //
    // Slabs rather than exterior walls, deliberately. A slab face is ALREADY a
    // closed loop; a wall run is not. Walls break at doors and curtain walls,
    // courtyards add inner loops, and joining them into a reliable outline is a
    // curve-joining problem with a long tail of failures. Nothing about a slab
    // needs joining.
    //
    // API surface, confirmed against libs/RevitAPI.dll (2024):
    //
    //   IList<Reference>  HostObjectUtils.GetTopFaces(HostObject)
    //   Face              element.GetGeometryObjectFromReference(r) as Face
    //   IList<CurveLoop>  Face.GetEdgesAsCurveLoops()
    //   IList<XYZ>        Curve.Tessellate()
    //
    // GetTopFaces is what keeps this simple: no geometry options, no solid
    // walking, no hunting for a face whose normal points up.
    //
    // Tessellating is the other half. Arcs, ellipses and splines all become
    // points, so the consumer only ever draws straight segments — one code path
    // regardless of how the slab edge was modelled, and the precision loss is
    // invisible at key-plan scale.
    //
    // Read-only. No transaction. One pass per level.
    // =========================================================================
    public static class ZoneSlabOutline
    {
        /// <summary>
        /// Openings smaller than this are dropped, in square feet. A slab is punched by shafts,
        /// stairs and risers exactly as a ceiling is punched by light fittings, and taking those
        /// loops literally is what made a 40×30 room measure 7.3 ft wide in the ceiling work.
        /// An atrium or a lightwell clears this and is drawn.
        /// </summary>
        public const double MinOpeningAreaFt2 = 100.0;

        /// <summary>Where an outline came from. Always reported — never a silent downgrade.</summary>
        public enum Source { SlabEdges, Roofs, ZoneExtents, None }

        /// <summary>One closed ring of the outline, as world XY points.</summary>
        public sealed class Ring
        {
            public List<XYZ> Points { get; } = new List<XYZ>();
            /// <summary>False for an opening (a hole) that survived the area filter.</summary>
            public bool IsOuter { get; set; } = true;
        }

        public sealed class Result
        {
            public List<Ring> Rings  { get; } = new List<Ring>();
            public Source     From   { get; set; } = Source.None;
            public bool       Ok     => Rings.Count > 0;
            /// <summary>World bounds of everything collected, for centring the key plan.</summary>
            public double MinX = double.MaxValue, MinY = double.MaxValue;
            public double MaxX = double.MinValue, MaxY = double.MinValue;

            public double CentreX => Ok ? (MinX + MaxX) / 2.0 : 0;
            public double CentreY => Ok ? (MinY + MaxY) / 2.0 : 0;
            public double WidthFt => Ok ? MaxX - MinX : 0;
            public double DepthFt => Ok ? MaxY - MinY : 0;
        }

        /// <summary>
        /// Collects the outline for one level from a source document.
        ///
        /// Falls back slabs → roofs → nothing, and always reports which was used. A silent
        /// downgrade would leave the user looking at a different drawing than they think.
        /// </summary>
        public static Result Collect(Document? sourceDoc, Transform? transform,
                                     string hostLevelName, Action<string, string>? log = null)
        {
            var result = new Result();
            if (sourceDoc == null) return result;

            void Say(string m, string t)
            {
                try { log?.Invoke(m, t); }
                catch (Exception ex) { DiagnosticsLog.Swallowed("ZoneSlabOutline: log", ex); }
            }

            Transform tf = transform ?? Transform.Identity;

            // Match the level by NAME — cross-document identity, the same rule everything else
            // in the zone model follows.
            Level? level = null;
            try
            {
                level = new FilteredElementCollector(sourceDoc)
                    .OfClass(typeof(Level)).Cast<Level>()
                    .FirstOrDefault(l => string.Equals(l.Name, hostLevelName, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex) { DiagnosticsLog.Swallowed("ZoneSlabOutline: find level", ex); }

            if (!TryHosts(sourceDoc, level, BuiltInCategory.OST_Floors, tf, result, Say))
            {
                Say($"No floors found on '{hostLevelName}' — falling back to roofs.", "warn");
                if (TryHosts(sourceDoc, level, BuiltInCategory.OST_Roofs, tf, result, Say))
                    result.From = Source.Roofs;
            }
            else result.From = Source.SlabEdges;

            if (!result.Ok)
            {
                result.From = Source.None;
                Say($"No slab or roof outline could be read for '{hostLevelName}'.", "warn");
            }

            return result;
        }

        private static bool TryHosts(Document doc, Level? level, BuiltInCategory category,
                                     Transform tf, Result result, Action<string, string> say)
        {
            List<HostObject> hosts;
            try
            {
                var collector = new FilteredElementCollector(doc)
                    .OfCategory(category)
                    .WhereElementIsNotElementType();

                hosts = collector.OfType<HostObject>().ToList();

                // Restrict to the level when one resolved. A model with no matching level still
                // yields an outline from everything, which is better than nothing for a key plan.
                if (level != null)
                {
                    var onLevel = hosts.Where(h =>
                    {
                        try { return h.LevelId == level.Id; }
                        catch (Exception ex)
                        {
                            DiagnosticsLog.Swallowed("ZoneSlabOutline: read host level", ex);
                            return false;
                        }
                    }).ToList();
                    if (onLevel.Count > 0) hosts = onLevel;
                }
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error($"ZoneSlabOutline: collect {category}", ex);
                return false;
            }

            if (hosts.Count == 0) return false;

            int before = result.Rings.Count;
            foreach (var h in hosts)
            {
                IList<Reference>? faces = null;
                try { faces = HostObjectUtils.GetTopFaces(h); }
                catch (Exception ex)
                {
                    DiagnosticsLog.Swallowed($"ZoneSlabOutline: top faces of {h.Id}", ex);
                    continue;
                }
                if (faces == null) continue;

                foreach (var fref in faces)
                {
                    Face? face = null;
                    try { face = h.GetGeometryObjectFromReference(fref) as Face; }
                    catch (Exception ex)
                    {
                        DiagnosticsLog.Swallowed($"ZoneSlabOutline: resolve face on {h.Id}", ex);
                        continue;
                    }
                    if (face == null) continue;

                    IList<CurveLoop>? loops = null;
                    try { loops = face.GetEdgesAsCurveLoops(); }
                    catch (Exception ex)
                    {
                        DiagnosticsLog.Swallowed($"ZoneSlabOutline: loops on {h.Id}", ex);
                        continue;
                    }
                    if (loops == null || loops.Count == 0) continue;

                    // The largest loop is the outline; the rest are openings, and small ones
                    // are dropped so risers and stair voids do not clutter a key plan.
                    var measured = loops
                        .Select(l => (Loop: l, Area: LoopAreaFt2(l)))
                        .OrderByDescending(x => x.Area)
                        .ToList();

                    for (int i = 0; i < measured.Count; i++)
                    {
                        bool outer = i == 0;
                        if (!outer && measured[i].Area < MinOpeningAreaFt2) continue;
                        AddRing(result, measured[i].Loop, tf, outer);
                    }
                }
            }

            return result.Rings.Count > before;
        }

        private static void AddRing(Result result, CurveLoop loop, Transform tf, bool outer)
        {
            var ring = new Ring { IsOuter = outer };
            try
            {
                foreach (var c in loop)
                {
                    // Tessellate: every curve type collapses to points, so the drawing side
                    // never has to special-case an arc or a spline.
                    var pts = c.Tessellate();
                    if (pts == null) continue;
                    // Skip the last point of each curve — it repeats the next curve's first.
                    for (int i = 0; i < pts.Count - 1; i++)
                    {
                        XYZ w = tf.OfPoint(pts[i]);
                        ring.Points.Add(new XYZ(w.X, w.Y, 0));
                        if (w.X < result.MinX) result.MinX = w.X;
                        if (w.Y < result.MinY) result.MinY = w.Y;
                        if (w.X > result.MaxX) result.MaxX = w.X;
                        if (w.Y > result.MaxY) result.MaxY = w.Y;
                    }
                }
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed("ZoneSlabOutline: tessellate loop", ex);
                return;
            }

            if (ring.Points.Count >= 3) result.Rings.Add(ring);
        }

        /// <summary>
        /// Planar area of a loop by the shoelace formula on its tessellated XY points. Used only
        /// to rank loops and to drop small openings, so an approximation is exactly right here.
        /// </summary>
        private static double LoopAreaFt2(CurveLoop loop)
        {
            try
            {
                var pts = new List<XYZ>();
                foreach (var c in loop)
                {
                    var t = c.Tessellate();
                    if (t == null) continue;
                    for (int i = 0; i < t.Count - 1; i++) pts.Add(t[i]);
                }
                if (pts.Count < 3) return 0;

                double sum = 0;
                for (int i = 0; i < pts.Count; i++)
                {
                    var a = pts[i];
                    var b = pts[(i + 1) % pts.Count];
                    sum += (a.X * b.Y) - (b.X * a.Y);
                }
                return Math.Abs(sum) / 2.0;
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed("ZoneSlabOutline: loop area", ex);
                return 0;
            }
        }
    }
}
