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
    /// to exclude, and the optional parameter value filters discovered by scan (empty list = "all").
    /// </summary>
    public sealed class CopyLinearSourceSpec
    {
        public long         LinkInstId { get; set; }      // 0 = host
        public List<string> Categories { get; set; } = new List<string>();  // OST_* strings
        public List<int>    ExcludedWorksetIds { get; set; } = new List<int>();

        // Dynamic filters keyed by parameter display name; empty value list = include all.
        public Dictionary<string, List<string>> ParamFilters { get; set; } = new Dictionary<string, List<string>>();
    }

    /// <summary>Distinct filter values found by a phase-1 scan of the chosen source + categories.</summary>
    public sealed class CopyLinearScanResult
    {
        // Parameter display name → sorted distinct formatted values; only params with ≥2 distinct values included.
        public Dictionary<string, List<string>> ParameterValues { get; set; } = new Dictionary<string, List<string>>();
        public int ElementCount { get; set; }
    }
}
