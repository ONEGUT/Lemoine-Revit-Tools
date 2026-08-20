using Autodesk.Revit.DB;

namespace LemoineTools.Framework.Zones
{
    // =========================================================================
    // SheetAnchorMath — "put this world point at this sheet coordinate."
    //
    // Extracted verbatim from AlignSheetViewsEventHandler, which had already
    // worked out the parts that are easy to get subtly wrong:
    //
    //   • An ACTIVE, ASYMMETRIC annotation crop moves a viewport's on-sheet
    //     footprint away from its model-crop centre. SetBoxCenter acts on the
    //     FOOTPRINT, while the anchor is a model point, so both sides have to be
    //     converted through FootprintCentre or every annotated view is offset.
    //   • Source and target can be at DIFFERENT scales, and each side must use
    //     its own.
    //   • A rotated viewport turns its crop X axis onto a sheet Y axis, so an
    //     uncompensated offset is applied in the wrong direction.
    //
    // Two callers, one formula, differing only in where the anchor comes from:
    //
    //   Align Sheet Views → the anchor pair is read off a reference viewport
    //   Zones             → the anchor pair is READ FROM THE STORED PLACEMENT
    //
    // That second case is the whole point of zone placements, and it carries one
    // rule that is easy to violate and silent when violated:
    //
    //   A STORED PLACEMENT'S WORLD ANCHOR IS AUTHORITATIVE. Never recompute an
    //   anchor from current extents while reusing a stored sheet coordinate —
    //   the pair is the record. Measured on the Python port of the solver: an
    //   area whose extents grow 10 ft, re-anchored from its new centre but kept
    //   at its stored sheet coordinate, moves the building 5/8" on a 1/8" plot,
    //   with no error anywhere.
    //
    // PROVISIONAL: the rotation sign convention is inherited unverified from
    // Align Sheet Views and still needs confirming against a Revit plot.
    // =========================================================================
    public static class SheetAnchorMath
    {
        /// <summary>
        /// Centre of a viewport's on-sheet footprint, expressed in the view's crop-local
        /// coordinates. With no active annotation crop this is just the model-crop centre, so
        /// views that were already placing correctly do not move. With an active one the
        /// footprint is the crop grown by the four offsets, so its centre shifts by half their
        /// difference.
        /// </summary>
        public static void FootprintCentre(XYZ cropMin, XYZ cropMax, bool annoActive,
                                           double annoTop, double annoBottom,
                                           double annoLeft, double annoRight,
                                           out double cx, out double cy)
        {
            cx = (cropMin.X + cropMax.X) / 2.0;
            cy = (cropMin.Y + cropMax.Y) / 2.0;
            if (!annoActive) return;
            cx += (annoRight - annoLeft)   / 2.0;
            cy += (annoTop   - annoBottom) / 2.0;
        }

        /// <summary>
        /// Maps an offset expressed in crop-plane axes onto sheet axes for a rotated viewport.
        /// PROVISIONAL: the sign convention is not verified against Revit.
        /// </summary>
        public static XYZ ApplyRotation(XYZ v, ViewportRotation r)
        {
            switch (r)
            {
                case ViewportRotation.Clockwise:        return new XYZ( v.Y, -v.X, 0);
                case ViewportRotation.Counterclockwise: return new XYZ(-v.Y,  v.X, 0);
                default:                                return v;
            }
        }

        /// <summary>The world point at the centre of a view's model crop box.</summary>
        public static XYZ AnchorWorld(Transform cropTransform, XYZ cropMin, XYZ cropMax)
        {
            double cx = (cropMin.X + cropMax.X) / 2.0;
            double cy = (cropMin.Y + cropMax.Y) / 2.0;
            double cz = (cropMin.Z + cropMax.Z) / 2.0;
            return cropTransform.OfPoint(new XYZ(cx, cy, cz));
        }

        /// <summary>
        /// Sheet coordinate at which a placed view's model-crop centre actually sits. That is
        /// the viewport's box centre only when the view has no asymmetric annotation crop;
        /// otherwise the anchor sits off-centre by half the left/right (and bottom/top) offset
        /// difference. Uses the view's OWN scale.
        /// </summary>
        public static XYZ SourceAnchorOnSheet(XYZ boxCenter, XYZ cropMin, XYZ cropMax,
                                              bool annoActive, double annoTop, double annoBottom,
                                              double annoLeft, double annoRight,
                                              int scale, ViewportRotation rotation)
        {
            double acx = (cropMin.X + cropMax.X) / 2.0;
            double acy = (cropMin.Y + cropMax.Y) / 2.0;
            FootprintCentre(cropMin, cropMax, annoActive, annoTop, annoBottom, annoLeft, annoRight,
                            out double fx, out double fy);
            XYZ d = ApplyRotation(new XYZ((acx - fx) / scale, (acy - fy) / scale, 0), rotation);
            return boxCenter + d;
        }

        /// <summary>
        /// The value to pass to <c>Viewport.SetBoxCenter</c> so that <paramref name="anchorWorld"/>
        /// lands exactly on <paramref name="anchorSheet"/>.
        ///
        /// This is the one formula both callers share. <c>SetBoxCenter</c> is ABSOLUTE, so a
        /// wrong input cannot compound the way a relative nudge would — a correction re-places
        /// the viewport rather than moving it further.
        /// </summary>
        public static XYZ BoxCentreForAnchor(XYZ anchorWorld, XYZ anchorSheet,
                                             Transform cropTransform, XYZ cropMin, XYZ cropMax,
                                             bool annoActive, double annoTop, double annoBottom,
                                             double annoLeft, double annoRight,
                                             int scale, ViewportRotation rotation)
        {
            XYZ local = cropTransform.Inverse.OfPoint(anchorWorld);

            FootprintCentre(cropMin, cropMax, annoActive, annoTop, annoBottom, annoLeft, annoRight,
                            out double fx, out double fy);
            XYZ off = ApplyRotation(new XYZ((local.X - fx) / scale, (local.Y - fy) / scale, 0), rotation);

            return anchorSheet - off;
        }
    }
}
