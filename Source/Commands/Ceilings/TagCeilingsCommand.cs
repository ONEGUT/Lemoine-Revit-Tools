using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Threading;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Helpers;
using LemoineTools.Tools.Ceilings.CeilingTags;

namespace LemoineTools.Commands
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class TagCeilingsCommand : IExternalCommand
    {
        private static StepFlowWindow? _window;

        public Result Execute(
            ExternalCommandData commandData,
            ref string          message,
            ElementSet          elements)
        {
            // Commands run on the Revit main thread WITH the document; the tool window runs on
            // its own STA thread and cannot resolve it later.
            DocumentKey.SetCurrent(commandData.Application.ActiveUIDocument?.Document);

            // Bring an already-open window to the front rather than opening a second one.
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
                catch (Exception ex)
                {
                    DiagnosticsLog.Swallowed("TagCeilings: reactivate existing window", ex);
                    _window = null;
                }
            }

            var uiApp = commandData.Application;

            CeilingTagViewModel BuildTool()
            {
                Document doc = uiApp.ActiveUIDocument.Document;

                // ── Eligible views: ceiling plans (main thread — safe) ────────
                var rcpIds = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewPlan))
                    .Cast<ViewPlan>()
                    .Where(v =>
                    {
                        if (v.IsTemplate) return false;
                        var vft = doc.GetElement(v.GetTypeId()) as ViewFamilyType;
                        return vft?.ViewFamily == ViewFamily.CeilingPlan;
                    })
                    .Select(v => v.Id.Value)
                    .ToList();

                // ── Loaded ceiling tag types ──────────────────────────────────
                // An annotation FamilySymbol reports a null Category in some cases, so the
                // category is filtered through the collector rather than read off the type.
                var tagTypes = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_CeilingTags)
                    .OfClass(typeof(FamilySymbol))
                    .Cast<FamilySymbol>()
                    .Select(fs => new CeilingTagViewModel.TagTypeEntry
                    {
                        Id    = fs.Id,
                        Label = $"{fs.FamilyName} : {fs.Name}",
                    })
                    .OrderBy(t => t.Label, NaturalOrderComparer.OrdinalIgnoreCase)
                    .ToList();

                if (tagTypes.Count == 0)
                    DiagnosticsLog.Warn("CeilingTags",
                        "no ceiling tag types loaded in this project — the Options step will say so.");

                return new CeilingTagViewModel(
                    App.CeilingTagHandler!, App.CeilingTagEvent!,
                    BrowserTreeCapture.Capture(doc), rcpIds, tagTypes);
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
