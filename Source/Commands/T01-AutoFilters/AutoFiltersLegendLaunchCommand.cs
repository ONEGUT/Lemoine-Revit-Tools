using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Tools.Testing.LegendCreator;

namespace LemoineTools.Commands
{
    /// <summary>
    /// Triggers legend creation from the Legend Creator settings (T08).
    /// Flushes any in-progress edits, saves settings to disk, then raises
    /// App.LegendCreatorEvent so Revit's main thread writes the legend view.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class AutoFiltersLegendLaunchCommand : IExternalCommand
    {
        public Result Execute(
            ExternalCommandData commandData,
            ref string          message,
            ElementSet          elements)
        {
            LegendCreatorTabContent.CreateLegendInRevit();
            return Result.Succeeded;
        }
    }
}
