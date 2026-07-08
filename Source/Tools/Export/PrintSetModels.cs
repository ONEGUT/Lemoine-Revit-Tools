using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace LemoineTools.Tools.BulkExport
{
    /// <summary>A ViewSheetSet captured from the document (main thread) — name + members.</summary>
    public sealed class PrintSetInfo
    {
        public ElementId       Id        { get; set; } = ElementId.InvalidElementId;
        public string          Name      { get; set; } = "";
        public List<ElementId> MemberIds { get; set; } = new List<ElementId>();
    }

    /// <summary>
    /// One print set chosen as an export group, with optional per-set overrides. Null override
    /// fields mean "use the tool's global setting/pattern" — see BulkExportEventHandler.ExportPrintSetMode.
    /// </summary>
    public sealed class PrintSetExportSpec
    {
        public string          Name            { get; set; } = "";
        public List<ElementId> MemberIds       { get; set; } = new List<ElementId>();
        public string?         PatternOverride { get; set; }
        public bool?           PdfOverride     { get; set; }
        public bool?           DwgOverride     { get; set; }
    }
}
