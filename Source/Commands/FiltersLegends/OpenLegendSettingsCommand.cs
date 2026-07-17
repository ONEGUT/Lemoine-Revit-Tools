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
    /// Opens the standalone Legend Creator Settings window on a dedicated STA thread.
    /// Queries TextNoteTypes and existing Legend views on the Revit main thread before
    /// opening the window so they are available in the text-style pickers.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class OpenLegendSettingsCommand : IExternalCommand
    {
        private static LegendSettingsWindow?    _window;
        private static WebLegendCreatorWindow?  _webWindow;

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
                catch (System.Exception ex) { DiagnosticsLog.Swallowed("OpenLegendSettings: activate web window", ex); _webWindow = null; }
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

            var doc = commandData.Application.ActiveUIDocument?.Document;

            // Query TextNoteTypes on Revit main thread
            var textNoteTypes = doc == null
                ? new List<TextNoteType>()
                : new FilteredElementCollector(doc)
                    .OfClass(typeof(TextNoteType))
                    .Cast<TextNoteType>()
                    .OrderBy(t => t.Name, System.StringComparer.OrdinalIgnoreCase)
                    .ToList();

            var textTypes = textNoteTypes.Select(t => (t.Id, t.Name)).ToList();

            // Capture each type's real text height (TEXT_SIZE is paper-space feet) as paper
            // inches, so the preview (which runs off the Revit thread) can size each role's
            // text to the real type instead of a single font-point fallback.
            var typeCapInches = new Dictionary<long, double>();
            foreach (var t in textNoteTypes)
            {
                try
                {
                    double feet = t.get_Parameter(BuiltInParameter.TEXT_SIZE)?.AsDouble() ?? 0;
                    if (feet > 0) typeCapInches[t.Id.Value] = feet * 12.0;
                }
                catch (System.Exception ex)
                {
                    DiagnosticsLog.Swallowed("LegendCreator: read text-note type size", ex);
                }
            }
            LemoineTools.Tools.FiltersLegends.LegendCreator.LegendTextTypeSizes.Set(typeCapInches);

            // Query existing Legend views on Revit main thread
            var legendViews = doc == null
                ? new List<(ElementId, string)>()
                : new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Views)
                    .Cast<View>()
                    .Where(v => v.ViewType == ViewType.Legend)
                    .OrderBy(v => v.Name, System.StringComparer.OrdinalIgnoreCase)
                    .Select(v => (v.Id, v.Name))
                    .ToList();

            // Web branch: open on the shared WebUiThread behind the Web UI flag; the WPF
            // window stays as the flag-off fallback (rule R25).
            if (web)
            {
                var webTextTypes = textTypes.Select(t => (t.Item1.Value, t.Item2)).ToList();
                WebUiThread.Invoke(() =>
                {
                    var w = new WebLegendCreatorWindow(webTextTypes);
                    w.Closed += (s2, e2) => { _webWindow = null; };
                    w.Show();
                    _webWindow = w;
                });
                return Result.Succeeded;
            }

            var ready = new ManualResetEventSlim(false);
            LegendSettingsWindow? win = null;

            var thread = new System.Threading.Thread(() =>
            {
                win = new LegendSettingsWindow(textTypes, legendViews);
                win.Closed += (s, e) =>
                {
                    _window = null;
                    Dispatcher.CurrentDispatcher.InvokeShutdown();
                };
                win.Show();
                ready.Set();
                Dispatcher.Run();
            });
            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();

            ready.Wait();
            _window = win;
            return Result.Succeeded;
        }
    }
}
