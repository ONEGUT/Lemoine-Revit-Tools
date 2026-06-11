using System;
using System.Collections.Generic;
using System.Linq;
using LemoineTools.PdfGeometry.Graph;
using LemoineTools.PdfGeometry.Pdf;
using LemoineTools.PdfGeometry.Plans;
using LemoineTools.PdfGeometry.Primitives;
using LemoineTools.PdfGeometry.Raster;
using LemoineTools.PdfGeometry.Simplify;
using LemoineTools.PdfGeometry.Vector;

namespace LemoineTools.PdfGeometry.Engine
{
    public enum ExtractionMode { Vector, Raster }

    public enum WandStatus
    {
        Ok,
        Duplicate,        // region already traced — existing face returned
        SeedOnLinework,   // pick landed on a barrier pixel
        NotEnclosed,      // flood leaked to the page border / size cap
        NoVectorLoop,     // no closed vector loop around the pick — suggest raster override
        TooSmall,
        SelfIntersecting,
        NoSource,         // engine has no raster/segments loaded for the active mode
        Failed,
    }

    public sealed class WandResult
    {
        public WandStatus Status { get; }
        public RegionFace? Face { get; }
        public RegionPlan? Plan { get; }
        public string? Message { get; }

        public WandResult(WandStatus status, RegionFace? face, RegionPlan? plan, string? message)
        {
            Status = status; Face = face; Plan = plan; Message = message;
        }
    }

    public sealed class WandConfig
    {
        public GeomTol Tol { get; set; } = new GeomTol();
        public double BinarizeThreshold { get; set; } = 0.5;
        public int SplitLineStrokePx { get; set; } = 3;

        /// <summary>Leak guard: max region size in PDF pt² before a flood is declared unenclosed.</summary>
        public double MaxRegionAreaPts2 { get; set; } = 800 * 800;

        public PathExtractOptions VectorOptions { get; set; } = new PathExtractOptions();
    }

    /// <summary>
    /// Session orchestrator: owns the <see cref="RegionSession"/> graph, the
    /// active extraction mode and its source data (barrier bitmap / segment
    /// graph), and routes wand picks to the matching engine. All inputs and
    /// outputs are PDF point space. Not thread-safe — the caller serializes
    /// access (the palette runs one wand at a time).
    /// </summary>
    public sealed class RegionWandEngine
    {
        private readonly WandConfig _cfg;

        private PageRaster? _raster;
        private BitGrid? _barriers;

        private List<Seg2> _baseSegments = new List<Seg2>();
        private SegmentGraph? _graph;
        private bool _graphDirty;
        private bool _vectorLoaded;

        public RegionSession Session { get; }
        public ExtractionMode Mode { get; set; } = ExtractionMode.Raster;

        public RegionWandEngine(WandConfig cfg)
        {
            _cfg = cfg;
            Session = new RegionSession(cfg.Tol);
        }

        public bool HasRaster => _raster != null;
        public bool HasVector => _vectorLoaded;

        // ---------------------------------------------------------------- loading

        public void LoadRaster(PageRaster raster)
        {
            _raster = raster;
            _barriers = raster.Binarize(_cfg.BinarizeThreshold);
            foreach (var split in Session.SplitLineEdges)
                StampSplitLine(split.Polyline);
        }

        public void LoadVectorSegments(List<Seg2> segments)
        {
            _baseSegments = segments;
            _vectorLoaded = true;
            _graphDirty = true;
        }

        // ------------------------------------------------------------ split lines

        /// <summary>
        /// Registers a user split line (exact polyline, PDF points) as a barrier
        /// in the graph, the raster mask, and the vector arrangement.
        /// </summary>
        public SplitLineResult AddSplitLine(List<Pt2> polyline)
        {
            var result = Session.AddSplitLine(polyline);
            if (_barriers != null) StampSplitLine(result.Edge.Polyline);
            _graphDirty = true;
            return result;
        }

        private void StampSplitLine(IReadOnlyList<Pt2> polylinePts)
        {
            if (_raster == null || _barriers == null) return;
            double ppp = _raster.PixelsPerPoint;
            var px = polylinePts
                .Select(p => new Pt2(p.X * ppp, (_raster.PageHeightPts - p.Y) * ppp))
                .ToList();
            LineRasterizer.StampPolyline(_barriers, px, _cfg.SplitLineStrokePx);
        }

        // ----------------------------------------------------------------- wand

        public WandResult Wand(Pt2 seedPdf) =>
            Mode == ExtractionMode.Raster ? WandRaster(seedPdf) : WandVector(seedPdf);

        private WandResult WandRaster(Pt2 seedPdf)
        {
            if (_raster == null || _barriers == null)
                return new WandResult(WandStatus.NoSource, null, null, "No rasterized page loaded.");

            double ppp = _raster.PixelsPerPoint;
            int seedX = (int)Math.Round(seedPdf.X * ppp);
            int seedY = (int)Math.Round((_raster.PageHeightPts - seedPdf.Y) * ppp);
            int maxPixels = (int)Math.Min(int.MaxValue, _cfg.MaxRegionAreaPts2 * ppp * ppp);

            var flood = ScanlineFloodFill.Fill(_barriers, seedX, seedY, maxPixels);
            if (!flood.Enclosed)
            {
                switch (flood.FailReason)
                {
                    case FloodFailReason.SeedOnBarrier:
                        return new WandResult(WandStatus.SeedOnLinework, null, null,
                            "Pick landed on linework — pick inside the open area of the region.");
                    case FloodFailReason.SeedOutOfBounds:
                        return new WandResult(WandStatus.Failed, null, null, "Pick is outside the PDF page.");
                    default:
                        return new WandResult(WandStatus.NotEnclosed, null, null,
                            "Region is not enclosed — the fill leaked to the page edge. Close the boundary or draw split lines.");
                }
            }

            var contours = MooreContourTracer.Trace(flood.Region!, flood.MinX, flood.MinY, flood.MaxX, flood.MaxY);

            var outer = PixelRingToPdf(contours.Outer, ppp);
            outer = DouglasPeucker.SimplifyClosed(outer, _cfg.Tol.SimplifyTol);
            outer = OrthoSnap.Apply(outer, _cfg.Tol.OrthoSnapAngleDeg, _cfg.Tol.SimplifyTol);

            var holes = new List<List<Pt2>>();
            foreach (var holePx in contours.Holes)
            {
                var hole = PixelRingToPdf(holePx, ppp);
                hole = DouglasPeucker.SimplifyClosed(hole, _cfg.Tol.SimplifyTol);
                hole = OrthoSnap.Apply(hole, _cfg.Tol.OrthoSnapAngleDeg, _cfg.Tol.SimplifyTol);
                if (hole.Count >= 3) holes.Add(hole);
            }

            return Register(outer, holes, "Raster");
        }

        private List<Pt2> PixelRingToPdf(List<Pt2> ringPx, double ppp)
        {
            var outPts = new List<Pt2>(ringPx.Count);
            foreach (var p in ringPx)
                outPts.Add(new Pt2(p.X / ppp, _raster!.PageHeightPts - p.Y / ppp));
            return outPts;
        }

        private WandResult WandVector(Pt2 seedPdf)
        {
            if (!_vectorLoaded)
                return new WandResult(WandStatus.NoSource, null, null, "No vector segments loaded.");

            if (_graph == null || _graphDirty)
            {
                var all = new List<Seg2>(_baseSegments);
                foreach (var split in Session.SplitLineEdges)
                {
                    var poly = split.Polyline;
                    for (int i = 1; i < poly.Count; i++)
                        all.Add(new Seg2(poly[i - 1], poly[i]));
                }
                _graph = SegmentGraph.Build(all, _cfg.Tol.VectorJoinTol);
                _graphDirty = false;
            }

            var loop = LoopFinder.FindEnclosing(_graph, seedPdf, _cfg.Tol.MinFaceArea);
            if (loop == null)
                return new WandResult(WandStatus.NoVectorLoop, null, null,
                    "No closed vector loop found around the pick — try the raster override for this drawing.");

            // Vector rings are exact node geometry; flattened beziers still carry
            // dense vertices, so run a light simplification but never ortho-snap
            // true vector linework.
            var outer = DouglasPeucker.SimplifyClosed(loop.Outer, Math.Min(_cfg.Tol.SimplifyTol, _cfg.Tol.ChordTol));
            var holes = loop.Holes
                .Select(h => DouglasPeucker.SimplifyClosed(h, Math.Min(_cfg.Tol.SimplifyTol, _cfg.Tol.ChordTol)))
                .Where(h => h.Count >= 3)
                .ToList();

            return Register(outer, holes, "Vector");
        }

        private WandResult Register(List<Pt2> outer, List<List<Pt2>> holes, string mode)
        {
            var reg = Session.RegisterFace(outer, holes, mode);
            switch (reg.Status)
            {
                case RegisterFaceStatus.Ok:
                    return new WandResult(WandStatus.Ok, reg.Face, Session.BuildPlan(reg.Face!.FaceId), reg.Warning);
                case RegisterFaceStatus.Duplicate:
                    return new WandResult(WandStatus.Duplicate, reg.Face, Session.BuildPlan(reg.Face!.FaceId), reg.Warning);
                case RegisterFaceStatus.TooSmall:
                    return new WandResult(WandStatus.TooSmall, null, null, reg.Warning);
                case RegisterFaceStatus.SelfIntersecting:
                    return new WandResult(WandStatus.SelfIntersecting, null, null, reg.Warning);
                default:
                    return new WandResult(WandStatus.Failed, null, null, reg.Warning);
            }
        }
    }
}
