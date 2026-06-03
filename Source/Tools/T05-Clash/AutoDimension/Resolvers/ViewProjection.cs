using Autodesk.Revit.DB;

namespace LemoineTools.Tools.Testing.AutoDimension.Resolvers
{
    /// <summary>
    /// Maps world XYZ points and directions into the view's 2D paper plane using the view's
    /// right/up basis. This is the single seam between Revit's 3D space and the Revit-free
    /// layout core: plan views, sections and elevations all reduce to (right·p, up·p) here, so
    /// a future section variant is a projection change, not a core rewrite.
    /// </summary>
    public sealed class ViewProjection
    {
        private readonly XYZ _origin;
        private readonly XYZ _right;
        private readonly XYZ _up;

        public ViewProjection(View view)
        {
            _origin = view.Origin ?? XYZ.Zero;
            _right  = view.RightDirection.Normalize();
            _up     = view.UpDirection.Normalize();
        }

        /// <summary>The default horizontal measurement axis in view-2D space (always +X).</summary>
        public Core.Vec2 HorizontalAxis => new Core.Vec2(1, 0);

        /// <summary>World direction of the view's horizontal (paper +X) axis.</summary>
        public XYZ RightWorld => _right;

        /// <summary>World direction of the view's vertical (paper +Y) axis.</summary>
        public XYZ UpWorld => _up;

        /// <summary>Inverse of <see cref="To2D"/>: maps a view-2D point back to a world point on the view plane.</summary>
        public XYZ From2D(Core.Vec2 p) => _origin + _right * p.X + _up * p.Y;

        /// <summary>Projects a world point onto the 2D view plane.</summary>
        public Core.Vec2 To2D(XYZ p)
        {
            XYZ d = p - _origin;
            return new Core.Vec2(d.DotProduct(_right), d.DotProduct(_up));
        }

        /// <summary>Projects a world direction onto the 2D view plane (not normalised).</summary>
        public Core.Vec2 Dir2D(XYZ v) => new Core.Vec2(v.DotProduct(_right), v.DotProduct(_up));

        /// <summary>True if a world normal lies in the view plane (i.e. appears as an edge/line).</summary>
        public bool NormalInPlane(XYZ worldNormal, double tolDeg)
        {
            // A face reads as a vertical edge in this view when its normal has no component
            // along the view direction — equivalently, the projected normal keeps its length.
            XYZ n = worldNormal.Normalize();
            Core.Vec2 proj = Dir2D(n);
            double projLen = proj.Length;
            // projLen ≈ 1 → normal lies in plane; projLen ≈ 0 → normal points along view dir.
            double angle = System.Math.Acos(System.Math.Min(1.0, projLen)) * 180.0 / System.Math.PI;
            return angle <= tolDeg;
        }
    }
}
