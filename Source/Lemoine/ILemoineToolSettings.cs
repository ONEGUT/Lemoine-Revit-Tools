namespace LemoineTools.Lemoine
{
    // =========================================================================
    // ILemoineToolSettings
    //
    // Optional interface a ViewModel can implement alongside ILemoineTool to
    // expose tool-specific persistent settings.
    //
    // Contract:
    //   GetSettingsSpec() — return a LemoineToolSettingsSpec whose Default values
    //                       are seeded from YourToolSettings.Instance.*.
    //                       Return null → "No settings for this tool." shown.
    //
    //   ApplySettings()  — called once per changed setting when the user clicks
    //                       Apply. Write to YourToolSettings.Instance.* and
    //                       call .Save() at the end.
    //
    // ⚠ Library modification note (AutoFilters):
    //   AutoFiltersViewModel uses a bespoke 3-tab modal (AutoFiltersSettingsWindow)
    //   that cannot be represented as a flat LemoineToolSettingsSpec.
    //   GetSettingsSpec() returns null; GlobalSettingsWindow needs a new overload:
    //     RegisterToolWithCustomWindow(string name, Func<UIElement> factory)
    //   This is a pending library change — see OpenSettingsCommand.cs for the
    //   placeholder comment.
    // =========================================================================

    public interface ILemoineToolSettings
    {
        /// <summary>
        /// Returns the declarative settings spec for this tool.
        /// Seed every Default from YourToolSettings.Instance.*.
        /// Return null if the tool has no persistent settings.
        /// </summary>
        LemoineToolSettingsSpec? GetSettingsSpec();

        /// <summary>
        /// Called once per changed setting value when the user clicks Apply.
        /// Write the value to YourToolSettings.Instance.* then call .Save().
        /// </summary>
        void ApplySettings(string groupId, string settingId, object value);
    }
}
