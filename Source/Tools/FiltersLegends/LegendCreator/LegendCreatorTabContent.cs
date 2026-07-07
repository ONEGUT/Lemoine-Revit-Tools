using System.Windows;
using LemoineTools.Lemoine.Controls;
using LemoineTools.Tools.AutoFilters;

namespace LemoineTools.Tools.FiltersLegends.LegendCreator
{
    /// <summary>
    /// Static factory + editing-buffer holder for the T08 settings tab in GlobalSettingsWindow.
    ///
    /// Lifecycle:
    ///   - BuildContent(owner): called once when the user switches to the tab.
    ///     Constructs a LemoineLegendBuilder, hands it a deep copy of Legends[0],
    ///     and stashes a reference so Apply() can read the editing buffer back out.
    ///   - Apply(): the global Apply button calls this. Pushes the editing buffer
    ///     into Legends[0] and saves.
    /// </summary>
    public static class LegendCreatorTabContent
    {
        private static LemoineLegendBuilder? _builder;
        private static string? _entryId;

        public static UIElement BuildContent(Window owner)
        {
            var s     = LegendCreatorSettings.Instance;
            LegendEntry entry;
            if (s.Legends.Count > 0)
            {
                entry = s.Legends[0];
            }
            else
            {
                entry = new LegendEntry { Id = LegendIdGen.New("legend") };
                s.Legends.Add(entry);
            }
            _entryId = entry.Id;

            _builder = new LemoineLegendBuilder();
            _builder.LoadFrom(entry);
            return _builder;
        }

        public static void Apply()
        {
            if (_builder == null) return;
            var s = LegendCreatorSettings.Instance;
            var entry = s.Legends.Find(e => e.Id == _entryId)
                     ?? (s.Legends.Count > 0 ? s.Legends[0] : null);
            if (entry == null) return;
            entry.Layout         = _builder.Layout;
            entry.Rows           = _builder.Rows;
            entry.PreviewVisible = _builder.PreviewVisible;
            s.Save();
        }

        public static void DiscardEdits()
        {
            if (_builder != null)
                AutoFiltersSettings.Saved -= _builder.OnFiltersSaved;
            _builder = null;
            _entryId = null;
        }
    }
}
