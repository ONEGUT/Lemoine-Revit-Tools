using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Threading;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;

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

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            // Keep the project-scoped settings key fresh: commands run on the Revit main
            // thread with the document in hand, tool windows do not.
            DocumentKey.SetCurrent(commandData.Application.ActiveUIDocument?.Document);
            // Libraries live in the .rvt so they travel with the project and every team
            // member sees the same ones. Pull this document's in before any window opens.
            LemoineTools.Framework.Project.ProjectLibraries.LoadForDocument(
                commandData.Application.ActiveUIDocument?.Document);

            // Bring an existing window to front.
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
            // Keyed by NAME: legend entries persist a style name, not an ElementId, so the
            // choice survives moving between projects.
            var typeCapInches = new Dictionary<string, double>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var t in textNoteTypes)
            {
                try
                {
                    double feet = t.get_Parameter(BuiltInParameter.TEXT_SIZE)?.AsDouble() ?? 0;
                    if (feet > 0) typeCapInches[t.Name] = feet * 12.0;
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
                    .OrderBy(v => v.Name, NaturalOrderComparer.OrdinalIgnoreCase)
                    .Select(v => (v.Id, v.Name))
                    .ToList();

            // Which legends in THIS document belong to which legend entry, read from the
            // stamp on each legend view. Replaces LegendEntry.RevitViewId, a raw ElementId
            // kept machine-wide that made every project claim the first project's legend.
            var legendLinks = doc == null
                ? new Dictionary<string, long>(System.StringComparer.Ordinal)
                : LemoineTools.Tools.FiltersLegends.LegendCreator.LegendLinkSchema.ReadLinks(doc);

            DiagnosticsLog.Info("OpenLegendSettings",
                legendLinks.Count > 0
                    ? $"Found {legendLinks.Count} legend(s) in this document linked to a legend entry."
                    : "No legend-entry links found in this document — entries will offer Create.");

            var ready = new ManualResetEventSlim(false);
            LegendSettingsWindow? win = null;

            var thread = new System.Threading.Thread(() =>
            {
                win = new LegendSettingsWindow(textTypes, legendViews);
                win.SetLegendLinks(legendLinks);
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
