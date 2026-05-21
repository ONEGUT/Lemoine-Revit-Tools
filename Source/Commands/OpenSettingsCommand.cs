using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;

namespace LemoineTools.Commands
{
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class OpenSettingsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            // Query live fill and line pattern names from the active document.
            var fillNames = new List<string>();
            var lineNames = new List<string> { "Solid" };  // Solid always first

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

            // If already open, refresh the pattern lists and bring to front.
            if (App.GlobalSettings != null && App.GlobalSettings.IsVisible)
            {
                App.GlobalSettings.SetPatternLists(fillNames, lineNames);
                App.GlobalSettings.Activate();
                return Result.Succeeded;
            }

            App.GlobalSettings = new GlobalSettingsWindow();
            App.GlobalSettings.SetPatternLists(fillNames, lineNames);
            App.GlobalSettings.Closed += (s, e) => App.GlobalSettings = null;
            App.GlobalSettings.Show();
            return Result.Succeeded;
        }
    }
}
