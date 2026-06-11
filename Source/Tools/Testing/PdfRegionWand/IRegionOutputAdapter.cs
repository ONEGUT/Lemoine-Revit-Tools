using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using LemoineTools.PdfGeometry.Plans;

namespace LemoineTools.Tools.Testing.PdfRegionWand
{
    /// <summary>Per-run options for an output adapter. Floor-specific today; future adapters add their own fields.</summary>
    public sealed class RegionOutputOptions
    {
        public ElementId TypeId { get; set; } = ElementId.InvalidElementId;
        public ElementId LevelId { get; set; } = ElementId.InvalidElementId;
        public bool Structural { get; set; }

        /// <summary>Holes below this area are dropped from the sketch.</summary>
        public double MinHoleAreaFt2 { get; set; } = 0.25;
    }

    /// <summary>
    /// Converts one traced region into Revit element(s). Implementations are
    /// called inside ONE already-open transaction per region (the caller owns
    /// the transaction so each fill stays an individual undo step) and return
    /// the created ElementIds for session tracking.
    ///
    /// FloorAdapter is the only implementation today; the seam exists so
    /// ceilings, room/space separation + rooms/spaces, area boundaries, filled
    /// regions, detail-line traces, and CSV zone export can slot in later.
    /// </summary>
    public interface IRegionOutputAdapter
    {
        string DisplayName { get; }

        IList<ElementId> Create(
            Document doc,
            RegionPlan plan,
            PdfToModelTransform transform,
            RegionOutputOptions options,
            Action<string, string> log);
    }
}
