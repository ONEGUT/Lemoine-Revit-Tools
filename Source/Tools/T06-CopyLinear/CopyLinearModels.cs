using System.Collections.Generic;

namespace LemoineTools.Tools.CopyLinear
{
    /// <summary>One source document the picker can scan (host or a loaded link).</summary>
    public sealed class CopyLinearDocInfo
    {
        public string Name       { get; set; } = "";
        public long   LinkInstId { get; set; }            // 0 = host document
        public string LinkInstUid { get; set; } = "";     // UniqueId of the link instance (stable across sessions)
        public List<CopyLinearWorksetInfo> Worksets { get; set; } = new List<CopyLinearWorksetInfo>();
    }

    /// <summary>One user workset of a source document (UI-side; not persisted).</summary>
    public sealed class CopyLinearWorksetInfo
    {
        public int    Id   { get; set; }
        public string Name { get; set; } = "";
    }

    /// <summary>One placeable host family type, for Replace mode.</summary>
    public sealed class CopyLinearFamilyInfo
    {
        /// <summary>Display + persist key: "Category — Family: Type".</summary>
        public string Key      { get; set; } = "";
        public string Category { get; set; } = "";
        public long   SymbolId { get; set; }
    }

    /// <summary>
    /// What the user picked in step 1: one source document, the categories to pull, the worksets
    /// to exclude, and the optional system / size / phase keyword filters (empty list = "all").
    /// Mirrors the clash group-1 picker, with Phase added.
    /// </summary>
    public sealed class CopyLinearSourceSpec
    {
        public long         LinkInstId { get; set; }      // 0 = host
        public List<string> Categories { get; set; } = new List<string>();  // OST_* strings
        public List<int>    ExcludedWorksetIds { get; set; } = new List<int>();

        public List<string> SystemTypes { get; set; } = new List<string>();
        public List<string> Sizes       { get; set; } = new List<string>();
        public List<string> Phases      { get; set; } = new List<string>();
    }

    /// <summary>Distinct filter values found by a phase-1 scan of the chosen source + categories.</summary>
    public sealed class CopyLinearScanResult
    {
        public List<string> SystemTypes { get; set; } = new List<string>();
        public List<string> Sizes       { get; set; } = new List<string>();
        public List<string> Phases      { get; set; } = new List<string>();
        public int          ElementCount { get; set; }
    }
}
