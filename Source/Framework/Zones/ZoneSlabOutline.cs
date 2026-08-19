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
    // curve-joining problem with a long tail of failures.
    //
    // WHY A UNION, AND NOT JUST THE SLAB LOOPS:
    //
    //   Emitting every slab's loops verbatim draws the floor plan, not the
    //   building outline. Three separate artefacts come out of that:
    //
    //     • two abutting slabs each draw the edge they share, so a line runs
    //       straight through the middle of the building;
    //     • every opening that survives an area filter is drawn as a hole;
    //     • a shape-edited or sloped slab has SEVERAL top faces, and each one
    //       contributes its own ring — those are the "slope" lines.
    //
    //   So each slab's OUTER loop is flattened to z=0, extruded into a prism,
    //   and all the prisms are unioned. Dropping inner loops at the source is
    //   what fills the holes (no threshold to tune); the union is what removes
    //   the shared edges and merges a sloped slab's faces back into one shape.
    //   Reading only the union's upward faces' outermost loops is what leaves a
    //   single continuous line around the outside.
    //
    //   Disjoint masses — a campus, a detached wing — union into separate lumps
    //   and yield one ring each, which is correct.
    //
    // API surface, confirmed against libs/RevitAPI.dll (2024) by reading the
    // metadata tables scoped to each declaring type, never a string search:
    //
    //   IList<Reference>  HostObjectUtils.GetTopFaces(HostObject)
    //   Face              element.GetGeometryObjectFromReference(r) as Face
    //   IList<CurveLoop>  Face.GetEdgesAsCurveLoops()
    //   IList<XYZ>        Curve.Tessellate()
    //   Solid             GeometryCreationUtilities.CreateExtrusionGeometry(
    //                         profileLoops, extrusionDir, extrusionDist)
    //   Solid             BooleanOperationsUtils.ExecuteBooleanOperation(
    //                         solid0, solid1, booleanType)
    //   BooleanOperationsType.Union
    //   FaceArray         Solid.Faces
    //   XYZ               PlanarFace.FaceNormal
    //
    // Tessellating is the other half. Arcs, ellipses and splines all become
    // points, so the consumer only ever draws straight segments — one code path
    // regardless of how the slab edge was modelled, and the precision loss is
    // invisible at key-plan scale. It is also what lets a non-planar (sloped)
    // face's loop be rebuilt as a flat, extrudable profile.
    //
    // Read-only. No transaction. One pass per level.
    //
    // UNVERIFIED, needs a Windows/Revit run: the boolean union itself. The API
    // members and signatures are confirmed, but how Revit's booleans behave on
    // real slab profiles — near-tangent edges, slivers where two slabs meet at a
    // hair of overlap — has not been observed. Every fold is guarded and a total
    // failure falls back to the per-slab rings WITH A LOG LINE, never silently.
    // =========================================================================
    public static class ZoneSlabOutline
    {
        /// <summary>Where an outline came from. Always reported — never a silent downgrade.</summary>
        public enum Source { SlabEdges, Roofs, ZoneExtents, None }

        /// <summary>
        /// One closed ring of the outline, as world XY points.
        ///
        /// Always an outer boundary now: openings are dropped before the union rather than
        /// carried through it, so a key plan never draws a hole. IsOuter is kept so an existing
        /// consumer still compiles and reads correctly.
        /// </summary>
        public sealed class Ring
        {
            public List<XYZ> Points { get; } = new List<XYZ>();
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

        /// <summary>
        /// Collects one category's slabs, flattens each one's OUTER loop, unions them, and
        /// emits the union's outline. Returns false when the category yielded nothing.
        /// </summary>
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

            // ── Read every slab's OUTER profile, flattened to z=0 ─────────────
            var profiles = new List<List<XYZ>>();
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

                    // The largest loop is this face's outline. Every other loop is an opening
                    // and is DROPPED — that is what fills the holes, with no threshold to tune.
                    CurveLoop? outer = null;
                    double best = -1;
                    foreach (var l in loops)
                    {
                        double a = ZonePolygonOps.AreaFt2(ZonePolygonOps.FlattenLoop(l, tf));
                        if (a > best) { best = a; outer = l; }
                    }
                    if (outer == null) continue;

                    var pts = ZonePolygonOps.FlattenLoop(outer, tf);
                    if (pts.Count >= 3) profiles.Add(pts);
                }
            }

            if (profiles.Count == 0) return false;

            // ── Union them ───────────────────────────────────────────────────
            var rings = ZonePolygonOps.Union(profiles.Cast<IReadOnlyList<XYZ>>(), say);
            if (rings.Count == 0)
            {
                // The union failed outright. Falling back to the raw profiles still draws a
                // readable key plan, but it is a DOWNGRADE and is stated rather than hidden.
                say($"The slab outline could not be unioned ({profiles.Count} profile(s)); the key plan " +
                    "will show each slab's own edge, so shared edges between slabs may appear.", "warn");
                rings = profiles;
            }

            int before = result.Rings.Count;
            foreach (var ring in rings) AddRing(result, ring);
            return result.Rings.Count > before;
        }

        /// <summary>Records one finished ring and grows the result's world bounds.</summary>
        private static void AddRing(Result result, List<XYZ> pts)
        {
            if (pts == null || pts.Count < 3) return;

            var ring = new Ring { IsOuter = true };
            foreach (var p in pts)
            {
                ring.Points.Add(p);
                if (p.X < result.MinX) result.MinX = p.X;
                if (p.Y < result.MinY) result.MinY = p.Y;
                if (p.X > result.MaxX) result.MaxX = p.X;
                if (p.Y > result.MaxY) result.MaxY = p.Y;
            }
            result.Rings.Add(ring);
        }
    }
}
