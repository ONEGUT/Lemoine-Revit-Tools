using System.Windows;
using LemoineTools.Lemoine.Controls;
using LemoineTools.Tools.AutoFilters;

namespace LemoineTools.Tools.Testing.LegendCreator
{
    /// <summary>
    /// Static factory + editing-buffer holder for the T08 settings tab.
    ///
    /// Lifecycle:
    ///   - BuildContent(owner): GlobalSettingsWindow calls this once when the
    ///     user switches to the tab. We construct a LemoineLegendBuilder, hand
    ///     it a deep copy of the persisted settings, and stash a reference so
    ///     Apply() can read its editing buffer back out.
    ///   - Apply(): the global Apply button calls this. We push the editing
    ///     buffer into the singleton and save.
    ///
    /// Legend creation / update is handled by ribbon buttons
    /// (LegendCreatorCreateCommand / LegendCreatorUpdateCommand) which read the
    /// saved singleton and raise App.LegendCreatorEvent on the Revit main thread.
    /// </summary>
    public static class LegendCreatorTabContent
    {
        private static LemoineLegendBuilder? _builder;

        public static UIElement BuildContent(Window owner)
        {
            _builder = new LemoineLegendBuilder();
            _builder.LoadFrom(LegendCreatorSettings.Instance);
            return _builder;
        }

        public static void Apply()
        {
            if (_builder == null) return;
            var s = LegendCreatorSettings.Instance;
            s.Layout         = _builder.Layout;
            s.Rows           = _builder.Rows;
            s.PreviewVisible = _builder.PreviewVisible;
            s.Save();
        }

        public static void DiscardEdits()
        {
            if (_builder != null)
                AutoFiltersSettings.Saved -= _builder.OnFiltersSaved;
            _builder = null;
        }
    }
}
