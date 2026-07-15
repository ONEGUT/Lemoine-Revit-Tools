using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Framework.Naming;

namespace LemoineTools.Commands
{
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class OpenSettingsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (App.GlobalSettings != null && App.GlobalSettings.IsVisible)
            {
                App.GlobalSettings.Activate();
                return Result.Succeeded;
            }

            // Main-thread-only capture, same pattern as
            // AutoFiltersSettings.CaptureFilterableCategories(doc) — the window itself never
            // touches Revit. No document open still opens the page (manual entry only).
            var doc = commandData.Application.ActiveUIDocument?.Document;
            var namingSnapshot = ParameterCatalog.Capture(doc);

            App.GlobalSettings = new GlobalSettingsWindow(namingSnapshot);
            App.GlobalSettings.Closed += (s, e) => App.GlobalSettings = null;
            App.GlobalSettings.Show();
            return Result.Succeeded;
        }
    }
}
