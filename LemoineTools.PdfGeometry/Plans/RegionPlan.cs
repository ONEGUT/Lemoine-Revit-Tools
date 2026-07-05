using System.Collections.Generic;
using LemoineTools.PdfGeometry.Primitives;

namespace LemoineTools.PdfGeometry.Plans
{
    /// <summary>
    /// The serializable boundary handed to the Revit side per creation request.
    /// Loops are resolved from the shared edge store at build time; the curve
    /// lists carry the arc-reconstructed pieces (deterministic per shared edge,
    /// so adjacent plans agree exactly along common boundaries), while the
    /// point lists carry the raw polylines for preview drawing and area math.
    /// Outer loops are counter-clockwise, holes clockwise, PDF point space.
    /// </summary>
    public sealed class RegionPlan
    {
        public int FaceId { get; set; }

        public List<Pt2> OuterLoop { get; set; } = new List<Pt2>();
        public List<List<Pt2>> Holes { get; set; } = new List<List<Pt2>>();

        public List<PlanCurve> OuterCurves { get; set; } = new List<PlanCurve>();
        public List<List<PlanCurve>> HoleCurves { get; set; } = new List<List<PlanCurve>>();

        /// <summary>"Vector" | "Raster" — which engine produced the trace.</summary>
        public string ExtractionMode { get; set; } = "";

        /// <summary>Net area (outer minus holes) in PDF points².</summary>
        public double AreaSqPts { get; set; }
    }
}
