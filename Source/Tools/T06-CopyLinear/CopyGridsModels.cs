using System.Collections.Generic;

namespace LemoineTools.Tools.CopyLinear
{
    /// <summary>One loaded link that contains grids, for the Copy Grids picker.</summary>
    public sealed class CopyGridLinkInfo
    {
        public string Name       { get; set; } = "";
        public long   LinkInstId { get; set; }
        public List<CopyGridItem> Grids { get; set; } = new List<CopyGridItem>();
    }

    /// <summary>One grid in a linked document.</summary>
    public sealed class CopyGridItem
    {
        public string Name         { get; set; } = "";
        public long   ElemId       { get; set; }   // grid id within the link document
        public bool   ExistsInHost { get; set; }   // a host grid already carries this name
    }
}
