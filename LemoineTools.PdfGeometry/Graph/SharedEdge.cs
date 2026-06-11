using System.Collections.Generic;
using LemoineTools.PdfGeometry.Primitives;

namespace LemoineTools.PdfGeometry.Graph
{
    /// <summary>
    /// One boundary polyline in the session's planar region graph, shared by at
    /// most one face on each side. Faces reference edges (never copy their
    /// geometry), so adjacent regions are geometrically identical by
    /// construction — the shared-edge invariant.
    /// Side convention: traversing <see cref="Polyline"/> from first to last
    /// vertex, <see cref="LeftFaceId"/> is the face on the left.
    /// </summary>
    public sealed class SharedEdge
    {
        public const int Unclaimed = -1;

        public int EdgeId { get; }

        /// <summary>Exact geometry in PDF point space. Never mutated after construction.</summary>
        public IReadOnlyList<Pt2> Polyline { get; }

        public int LeftFaceId { get; set; } = Unclaimed;
        public int RightFaceId { get; set; } = Unclaimed;

        /// <summary>True when this edge came from a user-drawn split line rather than a trace.</summary>
        public bool IsSplitLine { get; }

        public double Length { get; }
        public Bounds2 Bounds { get; }

        public SharedEdge(int edgeId, List<Pt2> polyline, bool isSplitLine)
        {
            EdgeId = edgeId;
            Polyline = polyline;
            IsSplitLine = isSplitLine;
            Length = PolylineOps.Length(polyline);
            Bounds = Bounds2.FromPoints(polyline);
        }

        public Pt2 Start => Polyline[0];
        public Pt2 End => Polyline[Polyline.Count - 1];

        public bool HasFreeSide => LeftFaceId == Unclaimed || RightFaceId == Unclaimed;
    }

    /// <summary>A face's reference to a shared edge, with traversal direction.</summary>
    public readonly struct EdgeRef
    {
        public int EdgeId { get; }

        /// <summary>True when the face traverses the edge polyline last→first.</summary>
        public bool Reversed { get; }

        public EdgeRef(int edgeId, bool reversed) { EdgeId = edgeId; Reversed = reversed; }

        public override string ToString() => Reversed ? $"~{EdgeId}" : $"{EdgeId}";
    }
}
