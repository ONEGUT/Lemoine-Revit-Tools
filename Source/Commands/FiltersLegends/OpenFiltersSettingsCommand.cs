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
    /// Opens the standalone Filters / Color Settings window.
    /// Queries fill and line patterns on the Revit main thread, then opens
    /// the window on a dedicated STA thread.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class OpenFiltersSettingsCommand : IExternalCommand
    {
        private static FiltersSettingsWindow?   _window;

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

            // Query pattern lists on the Revit main thread
            var fillNames = new List<string>();
            var lineNames = new List<string> { "Solid" };
            // View templates the user can pick as apply targets (Apply to... popup). Captured
            // here on the main thread; the window threads never touch the Revit API.
            var viewTemplates  = new List<LemoineTools.Tools.AutoFilters.ViewTemplateEntry>();
            var activeViewName = "";
            // Names of the filters THIS document records as ours, read from the ownership
            // stamp on each element. Replaces the old machine-wide CreatedFilterNames
            // manifest, which described whichever project last ran.
            var ownedFilterNames = new List<string>();

            var doc = commandData.Application.ActiveUIDocument?.Document;
            if (doc != null)
            {
                activeViewName = doc.ActiveView?.Name ?? "";

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
                            // Friendly label ("Ceiling Plan", "3D View", …) — reuses the same
                            // mapping Replicate Dependent Views already built, rather than a
                            // second copy of raw ViewType.ToString() → display-name pairs.
                            ViewTypeName = LemoineTools.Tools.LinkViews.ViewsByTemplateRunHandler.ViewTypeLabel(v.ViewType),
                        })
                        .OrderBy(t => t.Name, System.StringComparer.OrdinalIgnoreCase));

                // A zero-result capture is reported, never left as a silently blank picker —
                // an empty list is otherwise indistinguishable from a broken collector.
                DiagnosticsLog.Info("OpenFiltersSettings",
                    viewTemplates.Count > 0
                        ? $"Captured {viewTemplates.Count} view template(s) for the apply-target picker."
                        : "No view templates found in this document — apply targets will list the active view only.");

                ownedFilterNames.AddRange(
                    LemoineTools.Tools.AutoFilters.AutoFilterOwnerSchema.ReadAll(doc)
                        .Select(r => r.Name));

                DiagnosticsLog.Info("OpenFiltersSettings",
                    ownedFilterNames.Count > 0
                        ? $"Captured {ownedFilterNames.Count} Lemoine-owned filter(s) in this document."
                        : "No Lemoine-owned filters found in this document.");
            }

            // Open window on dedicated STA thread
            var ready = new ManualResetEventSlim(false);
            FiltersSettingsWindow? win = null;

            var thread = new Thread(() =>
            {
                win = new FiltersSettingsWindow();
                win.SetPatternLists(fillNames, lineNames);
                win.SetViewTemplates(viewTemplates, activeViewName);
                win.SetOwnedFilterNames(ownedFilterNames);
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
