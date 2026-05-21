using System.Collections.Generic;

namespace LemoineTools.Lemoine
{
    // =========================================================================
    // LemoineToolSettingsSpec — declarative settings data model
    //
    // Replaces the old GetSettingRows() / LemoineSettingRow pattern.
    // Tools return one of these from ILemoineToolSettings.GetSettingsSpec().
    // Both StepFlowWindow (in-tool gear overlay) and GlobalSettingsWindow
    // consume this spec and render controls automatically.
    //
    // Kind values and their matching Options type:
    //   "single"  → SingleSelectOpts   Default: string
    //   "multi"   → MultiSelectOpts    Default: List<string>
    //   "toggles" → TogglesOpts        Default: Dictionary<string,bool>
    //   "matrix"  → MatrixOpts         Default: Dictionary<string,string> (key: "Row__Col")
    //   "file"    → FileOpts           Default: string
    //   "date"    → DateOpts           Default: string (ISO) or (string,string)
    //   "range"   → RangeOpts          Default: (double Min, double Max)
    //   "search"  → SearchOpts         Default: string
    //   "toggle"  → ToggleOpts         Default: bool
    //   "text"    → TextOpts           Default: string
    //   "number"  → NumberOpts         Default: double
    //   "color"   → (none)             Default: string hex e.g. "#569cd6"
    //   "info"    → InfoOpts           Default: (none — use Options.Text)
    // =========================================================================

    /// <summary>
    /// Top-level declarative specification returned by <see cref="ILemoineToolSettings.GetSettingsSpec"/>.
    /// Describes all setting groups for one tool; consumed by StepFlowWindow and GlobalSettingsWindow
    /// to render controls automatically.
    /// </summary>
    public sealed class LemoineToolSettingsSpec
    {
        /// <summary>Tool identifier, e.g. "T01". Must match the id in Lemoine_Tools_Settings.html TOOLS[].</summary>
        public string Id          { get; set; } = null!;
        /// <summary>Display name shown in the settings rail.</summary>
        public string Label       { get; set; } = null!;
        /// <summary>Zero-padded tool number string, e.g. "01".</summary>
        public string Icon        { get; set; } = null!;
        /// <summary>One-sentence description shown below the tool name.</summary>
        public string Description { get; set; } = null!;
        /// <summary>Ordered list of setting groups rendered in the settings panel.</summary>
        public List<LemoineSettingsGroup> Groups { get; set; } = new List<LemoineSettingsGroup>();
    }

    /// <summary>
    /// A collapsible section within a tool's settings panel, containing one or more
    /// <see cref="LemoineSettingDef"/> entries rendered as labelled controls.
    /// </summary>
    public sealed class LemoineSettingsGroup
    {
        /// <summary>Unique identifier for this group within the tool spec.</summary>
        public string Id            { get; set; } = null!;
        /// <summary>Heading text displayed in the group's section card.</summary>
        public string Title         { get; set; } = null!;
        /// <summary>Optional explanatory text shown beneath the group title.</summary>
        public string? Hint         { get; set; }
        /// <summary>When <see langword="true"/>, the group's section card is expanded on first render.</summary>
        public bool   OpenByDefault { get; set; }
        /// <summary>Ordered list of individual settings rendered inside this group.</summary>
        public List<LemoineSettingDef> Settings { get; set; } = new List<LemoineSettingDef>();
    }

    /// <summary>
    /// Definition for a single setting control. The <see cref="Kind"/> string determines which
    /// Lemoine control is rendered and which <see cref="Options"/> type is expected.
    /// </summary>
    public sealed class LemoineSettingDef
    {
        /// <summary>Unique identifier for this setting within its group, used as a stable key.</summary>
        public string Id      { get; set; } = null!;
        /// <summary>Human-readable label rendered next to the control.</summary>
        public string Label   { get; set; } = null!;
        /// <summary>Optional tooltip or helper text shown beneath the control label.</summary>
        public string? Hint   { get; set; }
        /// <summary>One of the 13 kind strings (see class summary).</summary>
        public string Kind    { get; set; } = null!;
        /// <summary>Kind-specific options object — use the matching *Opts helper below.</summary>
        public object? Options { get; set; }
        /// <summary>
        /// Current value seeded from YourToolSettings.Instance.*.
        /// Type must match the kind (see class summary).
        /// </summary>
        public object? Default { get; set; }
        /// <summary>Optional badge label shown next to the setting name.</summary>
        public string? Tag     { get; set; }
    }

    // ── Options helpers ───────────────────────────────────────────────────────

    /// <summary>Options for a <c>kind = "single"</c> setting. Provides the ordered list of selectable strings.</summary>
    public sealed class SingleSelectOpts  { public System.Collections.Generic.List<string> Items { get; set; } = null!; }

    /// <summary>
    /// Options for a <c>kind = "multi"</c> setting. Maps named group headings to their
    /// respective selectable item lists, allowing items to be organised into tab sections.
    /// </summary>
    public sealed class MultiSelectOpts   { public System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>> Groups { get; set; } = null!; }

    /// <summary>Options for a <c>kind = "toggles"</c> setting. Supplies the list of individually togglable items.</summary>
    public sealed class TogglesOpts       { public System.Collections.Generic.List<LemoineTools.Lemoine.Controls.ToggleItem> Items { get; set; } = null!; }

    /// <summary>
    /// Options for a <c>kind = "matrix"</c> setting. Defines row labels, column labels, and
    /// per-cell defaults stored under the key format <c>"Row__Col"</c>.
    /// </summary>
    public sealed class MatrixOpts        { public System.Collections.Generic.List<string> Rows { get; set; } = null!; public System.Collections.Generic.List<string> Cols { get; set; } = null!; public System.Collections.Generic.Dictionary<string, string> Defaults { get; set; } = null!; }

    /// <summary>Options for a <c>kind = "file"</c> setting. Configures placeholder text and an optional recent-files list.</summary>
    public sealed class FileOpts          { public string? Placeholder { get; set; } public System.Collections.Generic.List<string>? Recents { get; set; } }

    /// <summary>Options for a <c>kind = "date"</c> setting. Sets the picker mode to <c>"single"</c> or <c>"range"</c>.</summary>
    public sealed class DateOpts          { public string Mode { get; set; } = null!; } // "single" | "range"

    /// <summary>
    /// Options for a <c>kind = "range"</c> setting. Configures unit label, endpoint labels,
    /// step size, and absolute min/max bounds for the dual-handle slider.
    /// </summary>
    public sealed class RangeOpts         { public string? Unit { get; set; } public string? MinLabel { get; set; } public string? MaxLabel { get; set; } public double Step { get; set; } = 1; public double AbsMin { get; set; } public double AbsMax { get; set; } }

    /// <summary>Options for a <c>kind = "search"</c> setting. Provides the autocomplete candidate list.</summary>
    public sealed class SearchOpts        { public System.Collections.Generic.List<string> Items { get; set; } = null!; }

    /// <summary>Options for a <c>kind = "toggle"</c> setting. Optionally overrides the on/off state labels shown beside the switch.</summary>
    public sealed class ToggleOpts        { public string? OnLabel { get; set; } public string? OffLabel { get; set; } }

    /// <summary>Options for a <c>kind = "text"</c> setting. Sets placeholder text and whether to render in a monospace font.</summary>
    public sealed class TextOpts          { public string? Placeholder { get; set; } public bool Mono { get; set; } }

    /// <summary>Options for a <c>kind = "number"</c> setting. Configures unit suffix, allowed range, and increment step.</summary>
    public sealed class NumberOpts        { public string? Unit { get; set; } public double Min { get; set; } public double Max { get; set; } = 100; public double Step { get; set; } = 1; }

    /// <summary>Options for a <c>kind = "info"</c> setting. Supplies the static informational text rendered in the panel.</summary>
    public sealed class InfoOpts          { public string Text { get; set; } = null!; }
}
