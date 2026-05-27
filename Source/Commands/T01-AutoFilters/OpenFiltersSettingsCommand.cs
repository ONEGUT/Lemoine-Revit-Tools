using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;

namespace LemoineTools.Commands
{
    /// <summary>
    /// Opens the GlobalSettingsWindow directly on the "Filters / Color" tab.
    ///
    /// If the window is already open, refreshes pattern lists and activates
    /// the filters tab without reopening.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class OpenFiltersSettingsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var fillNames = new List<string>();
            var lineNames = new List<string> { "Solid" };

            var doc = commandData.Application.ActiveUIDocument?.Document;
            if (doc != null)
            {
                fillNames.AddRange(
                    new FilteredElementCollector(doc)
                        .OfClass(typeof(FillPatternElement))
                        .Cast<FillPatternElement>()
                        .Select(fp => fp.Name)
                        .OrderBy(n => n, System.StringComparer.OrdinalIgnoreCase));

                lineNames.AddRange(
                    new FilteredElementCollector(doc)
                        .OfClass(typeof(LinePatternElement))
                        .Cast<LinePatternElement>()
                        .Select(lp => lp.Name)
                        .OrderBy(n => n, System.StringComparer.OrdinalIgnoreCase));
            }

            if (App.GlobalSettings != null && App.GlobalSettings.IsVisible)
            {
                App.GlobalSettings.SetPatternLists(fillNames, lineNames);
                App.GlobalSettings.ActivateTab("filters");
                App.GlobalSettings.Activate();
                return Result.Succeeded;
            }

            App.GlobalSettings = new GlobalSettingsWindow();
            App.GlobalSettings.SetPatternLists(fillNames, lineNames);
            App.GlobalSettings.Closed += (s, e) => App.GlobalSettings = null;
            // Activate the filters tab AFTER the window has loaded its content
            App.GlobalSettings.Loaded += (s, e) => App.GlobalSettings?.ActivateTab("filters");
            App.GlobalSettings.Show();
            return Result.Succeeded;
        }
    }
}
