using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;

namespace LemoineTools.Commands
{
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class OpenOverviewCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            // Keep the project-scoped settings key fresh: commands run on the Revit main
            // thread with the document in hand, tool windows do not.
            DocumentKey.SetCurrent(commandData.Application.ActiveUIDocument?.Document);
            // Libraries live in the .rvt so they travel with the project and every team
            // member sees the same ones. Pull this document's in before any window opens.
            LemoineTools.Framework.Project.ProjectLibraries.LoadForDocument(
                commandData.Application.ActiveUIDocument?.Document);

            if (App.Overview != null && App.Overview.IsVisible)
            {
                App.Overview.Activate();
                return Result.Succeeded;
            }

            CaptureSamples(commandData);
            App.Overview = new ToolsOverviewWindow();
            App.Overview.Closed += (s, e) => { App.Overview = null; OverviewSamples.Clear(); ToolsOverviewDemos.DropCache(); };
            App.Overview.Show();
            return Result.Succeeded;
        }

        // Snapshot the active document (main thread) so the dummy runs present real
        // view/sheet/level/link names. A capture failure must not block the overview —
        // the demos just fall back to canned sample data.
        private static void CaptureSamples(ExternalCommandData commandData)
        {
            try
            {
                OverviewSamples.Set(ToolsOverviewSampleCapture.Capture(commandData.Application.ActiveUIDocument));
            }
            catch (Exception ex)
            {
                OverviewSamples.Clear();
                DiagnosticsLog.Error("OpenOverview: sample capture", ex);
            }
        }
    }
}
