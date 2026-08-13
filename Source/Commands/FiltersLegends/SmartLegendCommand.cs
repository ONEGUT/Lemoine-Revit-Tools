using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Threading;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Helpers;
using LemoineTools.Framework;
using LemoineTools.Tools.FiltersLegends.SmartLegend;

namespace LemoineTools.Commands
{
    /// <summary>
    /// Opens the Smart Legend window on a dedicated STA thread. Sheets, text styles and the
    /// Project Browser tree are collected on Revit's main thread and handed to the ViewModel —
    /// the tool window has no document access of its own.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class SmartLegendCommand : IExternalCommand
    {
        private static StepFlowWindow? _window;

        public Result Execute(ExternalCommandData commandData,
                              ref string message, ElementSet elements)
        {
            // Commands run on the Revit main thread with the document in hand; tool windows
            // do not, so the project-scoped settings key is refreshed here.
            DocumentKey.SetCurrent(commandData.Application.ActiveUIDocument?.Document);
            LemoineTools.Framework.Project.ProjectLibraries.LoadForDocument(
                commandData.Application.ActiveUIDocument?.Document);

            if (_window != null)
            {
                try
                {
                    _window.Dispatcher.Invoke(() =>
                    {
                        if (_window.IsVisible) _window.Activate();
                        else _window = null;
                    });
                    if (_window != null) return Result.Succeeded;
                }
                catch (System.Exception ex)
                {
                    DiagnosticsLog.Swallowed("SmartLegendCommand: reuse open window", ex);
                    _window = null;
                }
            }

            var uiApp = commandData.Application;

            SmartLegendViewModel BuildTool()
            {
                var doc = uiApp.ActiveUIDocument.Document;

                var sheets = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSheet))
                    .Cast<ViewSheet>()
                    .Where(s => !s.IsTemplate)
                    .OrderBy(s => s.SheetNumber, NaturalOrderComparer.OrdinalIgnoreCase)
                    .Select(s => new SmartLegendViewModel.SheetEntry
                    {
                        Id     = s.Id,
                        Number = s.SheetNumber ?? "",
                        Name   = s.Name ?? "",
                    })
                    .ToList();

                var textTypes = new FilteredElementCollector(doc)
                    .OfClass(typeof(TextNoteType))
                    .Cast<TextNoteType>()
                    .OrderBy(t => t.Name, System.StringComparer.OrdinalIgnoreCase)
                    .Select(t => (t.Id, t.Name))
                    .ToList();

                DiagnosticsLog.Info("SmartLegend",
                    sheets.Count > 0
                        ? $"Found {sheets.Count} sheet(s) and {textTypes.Count} text style(s)."
                        : "No sheets found in this project — the picker will say so.");

                return new SmartLegendViewModel(
                    App.SmartLegendRunHandler!, App.SmartLegendRunEvent!,
                    sheets, textTypes, BrowserTreeCapture.Capture(doc));
            }

            var vm = BuildTool();
            var ready = new ManualResetEventSlim(false);
            StepFlowWindow? win = null;

            var thread = new Thread(() =>
            {
                win = new StepFlowWindow(vm, BuildTool);
                win.Closed += (s, e) =>
                {
                    _window = null;
                    Dispatcher.CurrentDispatcher.InvokeShutdown();
                };
                win.Show();
                ready.Set();
                Dispatcher.Run();
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();

            ready.Wait();
            _window = win;
            return Result.Succeeded;
        }
    }
}
