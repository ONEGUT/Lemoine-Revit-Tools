using System.Collections.Generic;

namespace LemoineTools.Tools.Setup
{
    /// <summary>
    /// How a document's (host or a link's) alignment anchor point + direction is resolved.
    /// <see cref="InternalOrigin"/> is the default — it reproduces Revit's native "Auto – Origin
    /// to Origin" link positioning, which matches how these projects are normally set up (every
    /// discipline modeled off its own Internal Origin). <see cref="GridIntersection"/> is the
    /// legacy named-grid-pair mechanism, kept as an override for the exceptional link that
    /// wasn't modeled on-origin.
    /// </summary>
    public enum AnchorSource
    {
        InternalOrigin,
        GridIntersection,
    }

    /// <summary>
    /// How a document's Z (elevation) anchor is resolved. Independent of <see cref="AnchorSource"/>
    /// — a link can use Grid Intersection for its XY anchor while Z still targets the host's
    /// Internal Origin (Z = 0), or vice versa.
    /// </summary>
    public enum ZSource
    {
        InternalOriginZ,
        MatchedLevel,
    }

    /// <summary>
    /// A single grid's endpoint geometry, captured on the main thread in its own document's
    /// internal coordinates (host grids in host coords, a link's grids in that link's own
    /// coords — never transformed). Used only to test whether two grids in the SAME document
    /// cross each other, so the Grid 2 picker can be filtered to grids that actually intersect
    /// the chosen Grid 1 (see AlignCoordinatesViewModel.GridsCross). A grid whose curve isn't a
    /// straight Line (arc/spline) has <see cref="IsLine"/> false and is treated as always
    /// crossing — never filtered out, since curved-grid intersection isn't worth modelling here.
    /// </summary>
    public sealed class GridGeom
    {
        public string Name   { get; set; } = "";
        public bool   IsLine { get; set; }
        public double X0, Y0, X1, Y1;
    }

    /// <summary>
    /// A loaded Revit link, with the grids it contains (offered for that link's own
    /// Grid Intersection override — no longer required to match the host's grid names, and no
    /// longer used to filter which links are selectable).
    /// </summary>
    public sealed class AlignLinkInfo
    {
        public string          Name       { get; set; } = "";
        public long            LinkInstId { get; set; }
        public HashSet<string> GridNames  { get; set; } = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        public List<GridGeom>  Grids      { get; set; } = new List<GridGeom>();
    }

    /// <summary>
    /// Per-link alignment method for a run. Defaults to the run-level <see cref="AnchorSource.InternalOrigin"/>
    /// (that link's own Internal Origin is the point moved onto the resolved host anchor); when
    /// <see cref="Overridden"/> the link uses its own named grid pair instead.
    /// </summary>
    public sealed class LinkAlignSpec
    {
        public long   LinkInstId { get; set; }
        public string LinkName   { get; set; } = "";
        public bool   Selected   { get; set; } = true;

        public bool          Overridden   { get; set; }
        public AnchorSource  AnchorSource { get; set; } = AnchorSource.InternalOrigin;
        public string        Grid1Name    { get; set; } = "";
        public string        Grid2Name    { get; set; } = "";
    }

    /// <summary>
    /// Revit-thread snapshot handed to <see cref="AlignCoordinatesViewModel"/>: the host's grid
    /// names and level names (for the pickers) plus every loaded link's grid-name set.
    /// </summary>
    public sealed class AlignCoordinatesData
    {
        public List<string>        HostGridNames  { get; set; } = new List<string>();
        public List<GridGeom>      HostGrids      { get; set; } = new List<GridGeom>();
        public List<string>        HostLevelNames { get; set; } = new List<string>();
        public List<AlignLinkInfo> Links          { get; set; } = new List<AlignLinkInfo>();
    }

    /// <summary>One file (host or a link) that Compare Grids can include in the audit.</summary>
    public sealed class CompareFileInfo
    {
        public string Name       { get; set; } = "";
        public long   LinkInstId { get; set; }   // 0 = host
        public bool   IsHost     => LinkInstId == 0L;
    }

    /// <summary>Revit-thread snapshot handed to <see cref="CompareGridsViewModel"/>.</summary>
    public sealed class CompareGridsData
    {
        public List<CompareFileInfo> Files { get; set; } = new List<CompareFileInfo>();
    }

    /// <summary>
    /// A loaded Revit link eligible for Push Coordinates to Links. No grid/anchor concept is
    /// needed here — this tool reads whatever position the link instance is already at (however
    /// it got there) and commits that into the link's own file.
    /// </summary>
    public sealed class PushLinkInfo
    {
        public string Name       { get; set; } = "";
        public long   LinkInstId { get; set; }
    }

    /// <summary>
    /// Revit-thread snapshot handed to <see cref="PushCoordinatesToLinksViewModel"/>: every loaded
    /// link. Every source is corrected and saved in place — workshared or not — so no destination
    /// folder is needed here.
    /// </summary>
    public sealed class PushCoordinatesData
    {
        public List<PushLinkInfo> Links { get; set; } = new List<PushLinkInfo>();
    }

    /// <summary>Per-link selection for a Push Coordinates to Links run.</summary>
    public sealed class PushLinkSpec
    {
        public long   LinkInstId { get; set; }
        public string LinkName   { get; set; } = "";
        public bool   Selected   { get; set; } = true;
    }
}
