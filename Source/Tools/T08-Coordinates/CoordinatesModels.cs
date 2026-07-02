using System.Collections.Generic;

namespace LemoineTools.Tools.Coordinates
{
    /// <summary>
    /// A loaded Revit link, with the set of grid names it contains. Used by Align Coordinates
    /// to decide which links carry both of the host grids the user picked (only those can be
    /// aligned by matched grid intersection).
    /// </summary>
    public sealed class AlignLinkInfo
    {
        public string          Name       { get; set; } = "";
        public long            LinkInstId { get; set; }
        public HashSet<string> GridNames  { get; set; } = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
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
}
