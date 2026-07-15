using System.Collections.Generic;

namespace LemoineTools.Tools.Setup
{
    /// <summary>
    /// Which feature of a document (host or a link) defines its alignment reference point — and,
    /// for the directional ones, its orientation. Each carries its own elevation: Internal Origin
    /// is Z = 0, Project Base Point / Survey Point take their marker's own elevation, and Grid
    /// Intersection pairs a grid∩ (XY) with a picked level (Z). Grid Intersection and Survey Point
    /// carry a direction (grid-line bearing / survey true-north angle) so a link using either can
    /// be rotated to match the host; Internal Origin and Project Base Point carry no direction, so
    /// a link (or host) using them is only translated, never rotated.
    /// </summary>
    public enum AnchorSource
    {
        InternalOrigin,
        ProjectBasePoint,
        SurveyPoint,
        GridIntersection,
    }

    /// <summary>
    /// A single grid's endpoint geometry, captured on the main thread in its own document's
    /// internal coordinates (host grids in host coords, a link's grids in that link's own
    /// coords — never transformed). Used only to test whether two grids in the SAME document
    /// cross each other, so the Grid 2 picker can be filtered to grids that actually intersect
    /// the chosen Grid 1 (see AlignCoordinatesViewModel.GridsCross). A grid whose curve isn't a
    /// straight Line (arc/spline) has <see cref="IsLine"/> false and is treated as always
    /// crossing — never filtered out, since curved-grid intersection isn't worth modeling here.
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
    /// longer used to filter which links are selectable) and its level names (offered for the
    /// per-link "level to move to the target elevation" picker when the host Z method is
    /// Matched Level).
    /// </summary>
    public sealed class AlignLinkInfo
    {
        public string          Name       { get; set; } = "";
        public long            LinkInstId { get; set; }
        public HashSet<string> GridNames  { get; set; } = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        public List<GridGeom>  Grids      { get; set; } = new List<GridGeom>();
        public List<string>    LevelNames { get; set; } = new List<string>();
    }

    /// <summary>
    /// Per-link alignment for a run: which of the link's own features (<see cref="AnchorSource"/>)
    /// is moved onto the resolved host reference. Defaults to <see cref="AnchorSource.InternalOrigin"/>.
    /// <see cref="LevelName"/> / <see cref="Grid1Name"/> / <see cref="Grid2Name"/> apply only when
    /// <see cref="AnchorSource"/> is <see cref="AnchorSource.GridIntersection"/>.
    /// </summary>
    public sealed class LinkAlignSpec
    {
        public long   LinkInstId { get; set; }
        public string LinkName   { get; set; } = "";
        public bool   Selected   { get; set; } = true;

        public AnchorSource  AnchorSource { get; set; } = AnchorSource.InternalOrigin;
        public string        Grid1Name    { get; set; } = "";
        public string        Grid2Name    { get; set; } = "";

        /// <summary>For a Grid Intersection anchor: the level in THIS link that supplies the Z
        /// (elevation) of the reference point. Empty = the link has no levels; Z falls back to 0.</summary>
        public string        LevelName    { get; set; } = "";
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
