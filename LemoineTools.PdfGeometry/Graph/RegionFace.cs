using System.Collections.Generic;

namespace LemoineTools.PdfGeometry.Graph
{
    public enum FaceStatus
    {
        /// <summary>Boundary registered, no element created yet.</summary>
        Traced,

        /// <summary>An element exists in the model for this face.</summary>
        ElementCreated,

        /// <summary>The element was deleted (undo or manual) — available to re-create without re-tracing.</summary>
        Available,

        /// <summary>A split line drawn after tracing cuts through this face; it must be re-wanded.</summary>
        Invalidated,
    }

    /// <summary>
    /// A registered region: an outer loop and zero or more hole loops, each a
    /// sequence of references into the shared edge store. Outer loops are
    /// stored counter-clockwise, holes clockwise (face interior on the left of
    /// travel in both cases).
    /// </summary>
    public sealed class RegionFace
    {
        public int FaceId { get; }
        public List<EdgeRef> OuterLoop { get; }
        public List<List<EdgeRef>> Holes { get; }
        public double AreaSqPts { get; set; }
        public FaceStatus Status { get; set; } = FaceStatus.Traced;

        /// <summary>Revit ElementId value of the created element, when Status is ElementCreated.</summary>
        public long ElementIdValue { get; set; }

        /// <summary>Extraction mode that produced this face ("Vector" | "Raster").</summary>
        public string ExtractionMode { get; set; } = "";

        public RegionFace(int faceId, List<EdgeRef> outerLoop, List<List<EdgeRef>> holes)
        {
            FaceId = faceId;
            OuterLoop = outerLoop;
            Holes = holes;
        }
    }
}
