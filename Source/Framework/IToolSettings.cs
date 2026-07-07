namespace LemoineTools.Framework
{
    // =========================================================================
    // IToolSettings
    //
    // Optional interface a ViewModel can implement alongside IStepFlowTool to
    // expose tool-specific persistent settings.
    //
    // Contract:
    //   GetSettingsSpec() — return a ToolSettingsSpec whose Default values
    //                       are seeded from YourToolSettings.Instance.*.
    //                       Return null → "No settings for this tool." shown.
    //
    //   ApplySettings()  — called once per changed setting when the user clicks
    //                       Apply. Write to YourToolSettings.Instance.* and
    //                       call .Save() at the end.
    //
    // Note (AutoFilters): the Auto Filters tool uses a standalone FiltersSettingsWindow
    //   opened from the ribbon. GetSettingsSpec() returns null for this tool.
    // =========================================================================

    public interface IToolSettings
    {
        /// <summary>
        /// Returns the declarative settings spec for this tool.
        /// Seed every Default from YourToolSettings.Instance.*.
        /// Return null if the tool has no persistent settings.
        /// </summary>
        ToolSettingsSpec? GetSettingsSpec();

        /// <summary>
        /// Called once per changed setting value when the user clicks Apply.
        /// Write the value to YourToolSettings.Instance.* then call .Save().
        /// </summary>
        void ApplySettings(string groupId, string settingId, object value);
    }
}
