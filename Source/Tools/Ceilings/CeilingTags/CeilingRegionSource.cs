using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using LemoineTools.Framework;
using LemoineTools.Tools.Ceilings.CeilingTags.TagCore;

namespace LemoineTools.Tools.Ceilings.CeilingTags
{
    /// <summary>Everything the commit phase needs to create one region's tags, kept parallel to
    /// the Revit-free plan so the plan itself never holds a Revit type.</summary>
    public sealed class CeilingSourceRef
    {
        public Reference? TagRef   { get; set; }
        /// <summary>World Z of the ceiling's bottom face — the tag point's elevation.</summary>
        public double     ZWorld   { get; set; }
        public ElementId  ElementId { get; set; } = ElementId.InvalidElementId;
        public bool       Linked   { get; set; }
        public string     Name     { get; set; } = "";
    }

    /// <summary>
    /// The ceiling implementation of the read side: pulls host and linked ceilings out of a
    /// view exactly once and converts each to a <see cref="TaggableRegion"/>.
    ///
    /// A future room source would sit beside this one, producing the same
    /// <see cref="TaggableRegion"/>s from <c>SpatialElement.GetBoundarySegments</c>; nothing in
    /// the planner would change. The two differ again only at commit time, because a room tag
    /// is a <c>RoomTag</c> created through <c>Creation.Document.NewRoomTag</c>, not an
    /// <c>IndependentTag</c>.
    /// </summary>
    public static class CeilingRegionSource
    {
        /// <summary>Ceilings all share one occlusion layer: in an RCP you look up, so a lower
        /// ceiling hides a higher one wherever they overlap in plan.</summary>
        public const int CeilingLayer = 0;

        /// <summary>
        /// Reads every ceiling visible in <paramref name="view"/> (host + loaded links).
        /// <paramref name="geometryCache"/> is keyed by (link id, element id) so a ceiling
        /// appearing in several selected views is only tessellated once for the whole run.
        /// </summary>
        public static void Collect(
            Document hostDoc,
            ViewPlan view,
            List<TaggableRegion> regions,
            Dictionary<string, CeilingSourceRef> refs,
            Dictionary<(long link, long el), CachedGeometry> geometryCache,
            Action<string, string> log,
            ref int failed,
            ref int noGeometry)
        {
            // ── Host ceilings ────────────────────────────────────────────────
            foreach (Element el in new FilteredElementCollector(hostDoc, view.Id)
                         .OfClass(typeof(Ceiling))
                         .WhereElementIsNotElementType())
            {
                AddRegion(el, null, Transform.Identity, 0L, regions, refs, geometryCache, log,
                          ref failed, ref noGeometry);
            }

            // ── Linked ceilings ──────────────────────────────────────────────
            foreach (RevitLinkInstance link in new FilteredElementCollector(hostDoc, view.Id)
                         .OfClass(typeof(RevitLinkInstance))
                         .Cast<RevitLinkInstance>()
                         .Where(li => li.GetLinkDocument() != null))
            {
                Document? linkDoc = link.GetLinkDocument();
                if (linkDoc == null) continue;

                Transform xform = link.GetTotalTransform();
                var bbFilter = GetViewBoundsFilter(view, xform.Inverse);

                foreach (Element el in new FilteredElementCollector(linkDoc)
                             .OfClass(typeof(Ceiling))
                             .WherePasses(bbFilter)
                             .WhereElementIsNotElementType())
                {
                    AddRegion(el, link, xform, link.Id.Value, regions, refs, geometryCache, log,
                              ref failed, ref noGeometry);
                }
            }
        }

        /// <summary>Tessellated bottom-face geometry for one ceiling, in WORLD feet.</summary>
        public sealed class CachedGeometry
        {
            public List<Loop2> Outers { get; } = new List<Loop2>();
            public List<Loop2> Holes  { get; } = new List<Loop2>();
            public double      ZWorld { get; set; }
            public bool        Valid  { get; set; }
        }

        private static void AddRegion(
            Element el, RevitLinkInstance? link, Transform xform, long linkKey,
            List<TaggableRegion> regions,
            Dictionary<string, CeilingSourceRef> refs,
            Dictionary<(long link, long el), CachedGeometry> cache,
            Action<string, string> log,
            ref int failed,
            ref int noGeometry)
        {
            var key = (linkKey, el.Id.Value);

            if (!cache.TryGetValue(key, out CachedGeometry? geom) || geom == null)
            {
                geom = ExtractBottomFace(el as Ceiling, xform);
                cache[key] = geom;
            }
            // No usable bottom face (no solid, or none facing down). Counted so the view's
            // log can say how many ceilings were dropped — a silently vanishing ceiling is
            // indistinguishable from a broken collector.
            if (!geom.Valid) { noGeometry++; return; }

            // Region id must be unique per (link, element) so two links carrying the same
            // element id can never collide into one region.
            string regionId = $"{linkKey}:{el.Id.Value}";
            if (refs.ContainsKey(regionId)) return;   // same element already collected for this view

            Reference? tagRef;
            try
            {
                tagRef = link != null
                    ? new Reference(el).CreateLinkReference(link)
                    : new Reference(el);
            }
            catch (Exception ex)
            {
                // A linked element that can't produce a valid reference fails only its own tag.
                DiagnosticsLog.Swallowed($"CeilingTags: build reference for element {el.Id}", ex);
                log(AppStrings.T("ceilings.tags.log.refFailed", el.Id, ex.Message), "warn");
                failed++;
                return;
            }

            var region = new TaggableRegion
            {
                Id             = regionId,
                DisplayName    = SafeName(el),
                OcclusionLayer = CeilingLayer,
                SortDepth      = geom.ZWorld,
            };
            region.Outers.AddRange(geom.Outers);
            region.Holes.AddRange(geom.Holes);
            regions.Add(region);

            refs[regionId] = new CeilingSourceRef
            {
                TagRef    = tagRef,
                ZWorld    = geom.ZWorld,
                ElementId = el.Id,
                Linked    = link != null,
                Name      = region.DisplayName,
            };
        }

        private static string SafeName(Element el)
        {
            try { return el.Name ?? ""; }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed($"CeilingTags: read name of element {el.Id}", ex);
                return "";
            }
        }

        /// <summary>
        /// Finds the ceiling's bottom face and returns its boundary loops tessellated into
        /// world XY, plus the face's world Z.
        ///
        /// The largest-area loop becomes the outer and the rest become holes. That split only
        /// drives the region's bounding box — the rasterizer fills with an even-odd rule, so a
        /// mis-ordered loop still subtracts correctly.
        /// </summary>
        private static CachedGeometry ExtractBottomFace(Ceiling? ceiling, Transform xform)
        {
            var result = new CachedGeometry();
            if (ceiling == null) return result;

            try
            {
                var opts = new Options { ComputeReferences = false, IncludeNonVisibleObjects = false };
                GeometryElement? geom = ceiling.get_Geometry(opts);
                if (geom == null) return result;

                Face? bottom = null;
                foreach (GeometryObject obj in geom)
                {
                    if (!(obj is Solid solid) || solid.Volume <= 1e-9) continue;
                    foreach (Face face in solid.Faces)
                    {
                        try
                        {
                            BoundingBoxUV fbb = face.GetBoundingBox();
                            var mid = new UV((fbb.Min.U + fbb.Max.U) * 0.5, (fbb.Min.V + fbb.Max.V) * 0.5);
                            XYZ n = face.ComputeNormal(mid);
                            if (n.Z < -0.9) { bottom = face; break; }
                        }
                        catch (Exception ex)
                        {
                            DiagnosticsLog.Swallowed("CeilingTags: skip malformed face", ex);
                        }
                    }
                    if (bottom != null) break;
                }
                if (bottom == null) return result;

                IList<CurveLoop> loops = bottom.GetEdgesAsCurveLoops();
                if (loops == null || loops.Count == 0) return result;

                var built = new List<(Loop2 Loop, double Area)>();
                double zSum = 0; int zCount = 0;

                foreach (CurveLoop cl in loops)
                {
                    var pts = new List<Pt2>();
                    foreach (Curve c in cl)
                    {
                        IList<XYZ> tess = c.Tessellate();
                        // Drop each curve's last point — it is the next curve's first.
                        for (int i = 0; i < tess.Count - 1; i++)
                        {
                            XYZ w = xform.OfPoint(tess[i]);
                            pts.Add(new Pt2(w.X, w.Y));
                            zSum += w.Z; zCount++;
                        }
                    }
                    if (pts.Count >= 3)
                    {
                        var loop = new Loop2(pts);
                        built.Add((loop, loop.AbsArea));
                    }
                }

                if (built.Count == 0) return result;

                built.Sort((a, b) => b.Area.CompareTo(a.Area));
                result.Outers.Add(built[0].Loop);
                for (int i = 1; i < built.Count; i++) result.Holes.Add(built[i].Loop);

                result.ZWorld = zCount > 0 ? zSum / zCount : 0.0;
                result.Valid  = true;
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed($"CeilingTags: extract bottom face of ceiling {ceiling.Id}", ex);
            }

            return result;
        }

        /// <summary>
        /// Bounding-box filter for a link's ceilings, in the LINK's coordinates. Mirrors the
        /// heatmap's filter: no upper Z cap keyed to a level, because a double-height space's
        /// ceiling can sit well above the next level up and must not be silently excluded.
        /// </summary>
        private static BoundingBoxIntersectsFilter GetViewBoundsFilter(ViewPlan view, Transform invLinkXform)
        {
            double levelElev = view.GenLevel?.Elevation ?? 0.0;
            double zMaxWorld = levelElev + 1e6;

            double zMin = invLinkXform.OfPoint(new XYZ(0, 0, levelElev - 1.0)).Z;
            double zMax = invLinkXform.OfPoint(new XYZ(0, 0, zMaxWorld)).Z;
            if (zMin > zMax) { double t = zMin; zMin = zMax; zMax = t; }

            if (!view.CropBoxActive)
            {
                return new BoundingBoxIntersectsFilter(new Outline(
                    new XYZ(-1e6, -1e6, zMin), new XYZ(1e6, 1e6, zMax)));
            }

            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;
            bool got = false;
            try
            {
                foreach (CurveLoop loop in view.GetCropRegionShapeManager().GetCropShape())
                foreach (Curve curve in loop)
                foreach (XYZ pt in curve.Tessellate())
                {
                    if (pt.X < minX) minX = pt.X;
                    if (pt.Y < minY) minY = pt.Y;
                    if (pt.X > maxX) maxX = pt.X;
                    if (pt.Y > maxY) maxY = pt.Y;
                    got = true;
                }
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed("CeilingTags: read crop shape", ex);
            }

            if (!got)
            {
                BoundingBoxXYZ cb = view.CropBox;
                Transform t = cb.Transform;
                foreach (XYZ local in new[]
                {
                    new XYZ(cb.Min.X, cb.Min.Y, 0), new XYZ(cb.Max.X, cb.Min.Y, 0),
                    new XYZ(cb.Max.X, cb.Max.Y, 0), new XYZ(cb.Min.X, cb.Max.Y, 0),
                })
                {
                    XYZ w = t.OfPoint(local);
                    if (w.X < minX) minX = w.X;
                    if (w.Y < minY) minY = w.Y;
                    if (w.X > maxX) maxX = w.X;
                    if (w.Y > maxY) maxY = w.Y;
                }
            }

            XYZ p1 = invLinkXform.OfPoint(new XYZ(minX, minY, 0));
            XYZ p2 = invLinkXform.OfPoint(new XYZ(maxX, maxY, 0));

            return new BoundingBoxIntersectsFilter(new Outline(
                new XYZ(Math.Min(p1.X, p2.X), Math.Min(p1.Y, p2.Y), zMin),
                new XYZ(Math.Max(p1.X, p2.X), Math.Max(p1.Y, p2.Y), zMax)));
        }
    }
}
