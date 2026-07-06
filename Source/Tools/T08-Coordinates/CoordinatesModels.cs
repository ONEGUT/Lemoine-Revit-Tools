using System.Collections.Generic;

namespace LemoineTools.Tools.Coordinates
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
    /// A loaded Revit link, with the set of grid names it contains (offered for that link's own
    /// Grid Intersection override — no longer required to match the host's grid names, and no
    /// longer used to filter which links are selectable).
    /// </summary>
    public sealed class AlignLinkInfo
    {
        public string          Name       { get; set; } = "";
        public long            LinkInstId { get; set; }
        public HashSet<string> GridNames  { get; set; } = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
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
    /// link plus the host document's folder (used to resolve the workshared-copy subfolder).
    /// </summary>
    public sealed class PushCoordinatesData
    {
        public List<PushLinkInfo> Links      { get; set; } = new List<PushLinkInfo>();
        public string?            HostFolder { get; set; }
    }

    /// <summary>Per-link selection for a Push Coordinates to Links run.</summary>
    public sealed class PushLinkSpec
    {
        public long   LinkInstId { get; set; }
        public string LinkName   { get; set; } = "";
        public bool   Selected   { get; set; } = true;
    }
}
