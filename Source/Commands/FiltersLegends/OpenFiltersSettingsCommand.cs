using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Threading;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Framework.Web;

namespace LemoineTools.Commands
{
    /// <summary>
    /// Opens the standalone Filters / Color Settings window.
    /// Queries fill and line patterns on the Revit main thread, then opens
    /// the window on a dedicated STA thread.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class OpenFiltersSettingsCommand : IExternalCommand
    {
        private static FiltersSettingsWindow?   _window;
        private static WebAutoFiltersWindow?    _webWindow;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            bool web = WebUiSettings.Instance.Enabled;

            // Bring an existing window (of the active flavour) to front.
            if (web && _webWindow != null)
            {
                try
                {
                    WebUiThread.Invoke(() =>
                    {
                        var w = _webWindow;
                        if (w != null && w.IsVisible) w.Activate();
                        else _webWindow = null;
                    });
                    if (_webWindow != null) return Result.Succeeded;
                }
                catch (System.Exception ex) { DiagnosticsLog.Swallowed("OpenFiltersSettings: activate web window", ex); _webWindow = null; }
            }
            else if (!web && _window != null)
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
                catch { _window = null; }
            }

            // Query pattern lists on the Revit main thread
            var fillNames = new List<string>();
            var lineNames = new List<string> { "Solid" };
            // View templates the user can pick as apply targets (Apply to... popup). Captured
            // here on the main thread; the window threads never touch the Revit API.
            var viewTemplates = new List<LemoineTools.Tools.AutoFilters.ViewTemplateEntry>();

            var doc = commandData.Application.ActiveUIDocument?.Document;
            if (doc != null)
            {
                // Capture the exact filterable-category list from this document so the rule
                // editor's category picker mirrors Revit's own "Edit Filters → Categories" list.
                LemoineTools.Tools.AutoFilters.AutoFiltersSettings.CaptureFilterableCategories(doc);

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

                // View templates are plain Views with IsTemplate set, and accept the same
                // AddFilter / SetFilterOverrides calls a normal view does.
                viewTemplates.AddRange(
                    new FilteredElementCollector(doc)
                        .OfClass(typeof(View))
                        .Cast<View>()
                        .Where(v => v.IsTemplate)
                        .Select(v => new LemoineTools.Tools.AutoFilters.ViewTemplateEntry
                        {
                            Id           = v.Id.Value,
                            Name         = v.Name,
                            ViewTypeName = v.ViewType.ToString(),
                        })
                        .OrderBy(t => t.Name, System.StringComparer.OrdinalIgnoreCase));

                // A zero-result capture is reported, never left as a silently blank picker —
                // an empty list is otherwise indistinguishable from a broken collector.
                DiagnosticsLog.Info("OpenFiltersSettings",
                    viewTemplates.Count > 0
                        ? $"Captured {viewTemplates.Count} view template(s) for the apply-target picker."
                        : "No view templates found in this document — apply targets will list the active view only.");
            }

            // Web branch: open on the shared WebUiThread behind the Web UI flag; the WPF
            // window stays as the flag-off fallback (rule R25).
            if (web)
            {
                WebUiThread.Invoke(() =>
                {
                    var w = new WebAutoFiltersWindow(fillNames, lineNames, viewTemplates);
                    w.Closed += (s2, e2) => { _webWindow = null; };
                    w.Show();
                    _webWindow = w;
                });
                return Result.Succeeded;
            }

            // Open window on dedicated STA thread
            var ready = new ManualResetEventSlim(false);
            FiltersSettingsWindow? win = null;

            var thread = new Thread(() =>
            {
                win = new FiltersSettingsWindow();
                win.SetPatternLists(fillNames, lineNames);
                win.SetViewTemplates(viewTemplates);
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
