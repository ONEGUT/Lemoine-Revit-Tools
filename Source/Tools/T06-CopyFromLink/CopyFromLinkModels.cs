using System.Collections.Generic;

namespace LemoineTools.Tools.CopyFromLink
{
    /// <summary>One loaded link the picker can copy elements from, with its user worksets.</summary>
    public sealed class CopyFromLinkLinkInfo
    {
        public string Name        { get; set; } = "";
        public long   LinkInstId  { get; set; }
        public string LinkInstUid { get; set; } = "";   // UniqueId of the link instance (stable across sessions)
        public List<CopyFromLinkWorksetInfo> Worksets { get; set; } = new List<CopyFromLinkWorksetInfo>();
    }

    /// <summary>One user workset of a source link (UI-side; not persisted).</summary>
    public sealed class CopyFromLinkWorksetInfo
    {
        public int    Id   { get; set; }
        public string Name { get; set; } = "";
    }

    /// <summary>
    /// What the user picked: one source link, the model categories to pull, the worksets to
    /// exclude, and the "Family: Type" keys ticked within those categories. An empty type-key set
    /// means "nothing selected" (the run is invalid) — never "all".
    /// </summary>
    public sealed class CopyFromLinkSpec
    {
        public long         LinkInstId  { get; set; }
        public string       LinkInstUid { get; set; } = "";
        public List<string> Categories  { get; set; } = new List<string>();   // OST_* strings
        public List<int>    ExcludedWorksetIds { get; set; } = new List<int>();
        public List<string> SelectedTypeKeys   { get; set; } = new List<string>();  // "Family: Type"
    }

    /// <summary>
    /// Distinct "Family: Type" keys found by a read-only scan of the chosen link + categories,
    /// grouped by category display name so the picker shows one tab per category.
    /// </summary>
    public sealed class CopyFromLinkScanResult
    {
        // Category display name → sorted distinct "Family: Type" keys.
        public Dictionary<string, List<string>> TypesByCategory { get; set; } = new Dictionary<string, List<string>>();
        public int ElementCount { get; set; }
    }
}
