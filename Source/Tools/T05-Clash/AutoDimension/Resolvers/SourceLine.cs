using Autodesk.Revit.DB;

namespace LemoineTools.Tools.Clash.AutoDimension.Resolvers
{
    /// <summary>
    /// One ingested source cross-line (the tagged DetailCurve output by the Clash Finder) with
    /// its validated dimensionable endpoint reference and its anchor projected into view-2D.
    /// </summary>
    public sealed class SourceLine
    {
        /// <summary>Stable per-run identity of the source line (its ElementId integer value).</summary>
        public string SourceKey { get; set; } = "";

        public ElementId Id { get; set; } = ElementId.InvalidElementId;

        /// <summary>Dimensionable endpoint reference of the curve (source side of the dimension).</summary>
        public Reference SourceRef { get; set; } = null!;

        /// <summary>World anchor point (the curve endpoint the reference belongs to).</summary>
        public XYZ Anchor3d { get; set; } = XYZ.Zero;

        /// <summary>Anchor projected into the view 2D plane (fed to the layout core).</summary>
        public Core.Vec2 Anchor2d { get; set; }
    }
}
