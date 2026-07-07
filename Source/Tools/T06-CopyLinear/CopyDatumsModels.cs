using System.Collections.Generic;

namespace LemoineTools.Tools.CopyLinear
{
    /// <summary>One loaded link that contains grids and/or levels, for the Copy Datums picker.</summary>
    public sealed class CopyDatumLinkInfo
    {
        public string Name       { get; set; } = "";
        public long   LinkInstId { get; set; }
        public List<CopyDatumItem> Grids  { get; set; } = new List<CopyDatumItem>();
        public List<CopyDatumItem> Levels { get; set; } = new List<CopyDatumItem>();
    }

    /// <summary>One grid or level in a linked document.</summary>
    public sealed class CopyDatumItem
    {
        public string Name         { get; set; } = "";
        public long   ElemId       { get; set; }   // element id within the link document
        public bool   ExistsInHost { get; set; }   // a host grid/level already carries this name
    }
}
