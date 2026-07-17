using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Framework.Web;
using LemoineTools.Tools.AutoFilters;

namespace LemoineTools.Commands
{
    /// <summary>
    /// Phase 3 wave-1: Delete Filters from Project in the HTML WebStepFlowWindow
    /// (DeleteFiltersWebTool). Reuses the same ExternalEvent handler as the WPF launch command.
    /// Parallel (Developer panel) until verified on Windows (rule R25).
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class WebDeleteFiltersCommand : IExternalCommand
    {
        private static WebStepFlowWindow? _window;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (_window != null)
            {
                try
                {
                    WebUiThread.Invoke(() =>
                    {
                        var w = _window;
                        if (w != null && w.IsVisible) w.Activate();
                        else _window = null;
                    });
                    if (_window != null) return Result.Succeeded;
                }
                catch (Exception ex)
                {
                    DiagnosticsLog.Swallowed("WebDeleteFiltersCommand: activate existing window", ex);
                    _window = null;
                }
            }

            var doc = commandData.Application.ActiveUIDocument?.Document;
            var names = doc == null
                ? new List<string>()
                : new FilteredElementCollector(doc)
                    .OfClass(typeof(ParameterFilterElement))
                    .Cast<ParameterFilterElement>()
                    .OrderBy(f => f.Name)
                    .Select(f => f.Name)
                    .ToList();

            var vm = new DeleteFiltersWebTool(App.DeleteFiltersFromProjectHandler!, App.DeleteFiltersFromProjectEvent!, names);

            WebUiThread.Invoke(() =>
            {
                var win = new WebStepFlowWindow(vm);
                win.Closed += (s, e) => { _window = null; };
                win.Show();
                _window = win;
            });
            return Result.Succeeded;
        }
    }
}
