namespace LemoineTools.Tools.AutoFilters
{
    /// <summary>
    /// One Revit VIEW TEMPLATE, captured on the Revit main thread so the Auto Filters window
    /// (which runs on its own STA / WebUi thread) can list templates without touching the API.
    ///
    /// Deliberately Revit-free — <see cref="Id"/> is the raw <c>ElementId.Value</c> rather than
    /// an <c>ElementId</c> — so the Revit-free web view model (<c>WebAutoFilters</c>) can hold
    /// these without gaining an Autodesk dependency. The Revit-side caller converts back with
    /// <c>new ElementId(entry.Id)</c>. Capture lives in the launching command, which is already
    /// on the Revit thread and already imports the API.
    ///
    /// Note the naming: "view template" here is a Revit view template, NOT an Auto Filters
    /// preset. The window's existing "Templates" button means the latter
    /// (<c>AutoFiltersSettings.Templates</c>, saved to %AppData%). Every identifier and
    /// user-facing string for this feature says "view template" so the two never blur.
    /// </summary>
    public sealed class ViewTemplateEntry
    {
        /// <summary>Raw <c>ElementId.Value</c> of the template (long in Revit 2024).</summary>
        public long Id { get; set; }

        /// <summary>Template name as shown in Revit.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// The template's ViewType name, used to group the picker. A filter whose categories
        /// don't apply to this view type is rejected by AddFilter at run time — the run logs
        /// and skips that one target rather than failing the whole run.
        /// </summary>
        public string ViewTypeName { get; set; } = string.Empty;
    }
}
