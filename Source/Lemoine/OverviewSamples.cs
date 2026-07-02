using System.Collections.Generic;

namespace LemoineTools.Lemoine
{
    // ─────────────────────────────────────────────────────────────────────────
    // Revit-free snapshot of the active document's names, captured once on the
    // Revit main thread when the Tools Overview opens (ToolsOverviewSampleCapture)
    // and consumed by the dummy-run demo specs (ToolsOverviewDemos) so each demo
    // presents the user's real views/sheets/levels/links instead of canned data.
    //
    // Plain strings only — the demo StepFlowWindows run on their own STA threads,
    // so nothing here may reference the Revit API or live elements.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>String-only sample data pulled from the active document.</summary>
    public sealed class OverviewSampleSnapshot
    {
        public string DocumentTitle { get; set; } = "";

        public List<string> PlanViews    { get; set; } = new List<string>();
        public List<string> CeilingPlans { get; set; } = new List<string>();
        public List<string> Views3D      { get; set; } = new List<string>();
        public List<string> Sections     { get; set; } = new List<string>();
        public List<string> Sheets       { get; set; } = new List<string>();
        public List<string> Levels       { get; set; } = new List<string>();
        public List<string> Templates    { get; set; } = new List<string>();
        public List<string> Grids        { get; set; } = new List<string>();
        public List<string> RefPlanes    { get; set; } = new List<string>();
        public List<string> TitleBlocks  { get; set; } = new List<string>();
        public List<string> Filters      { get; set; } = new List<string>();

        /// <summary>Loaded link documents only (no host).</summary>
        public List<string> Links { get; set; } = new List<string>();
        /// <summary>Host title followed by every loaded link.</summary>
        public List<string> Documents { get; set; } = new List<string>();

        /// <summary>Configured Auto Filters trade labels (settings, not document).</summary>
        public List<string> Trades { get; set; } = new List<string>();
        /// <summary>Saved clash definition names (settings, not document).</summary>
        public List<string> Definitions { get; set; } = new List<string>();

        /// <summary>Model categories that actually have elements, grouped by discipline.</summary>
        public Dictionary<string, List<string>> CategoryGroups { get; set; } =
            new Dictionary<string, List<string>>();
    }

    /// <summary>
    /// Holder for the snapshot the Tools Overview is currently working from.
    /// Set by OpenOverviewCommand when the window opens, cleared when it closes
    /// so the strings don't outlive the window (memory-lifetime discipline).
    /// </summary>
    public static class OverviewSamples
    {
        public static OverviewSampleSnapshot? Current { get; private set; }

        public static void Set(OverviewSampleSnapshot? snapshot) => Current = snapshot;
        public static void Clear() => Current = null;
    }
}
