using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Threading;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Tools.Ceilings;

namespace LemoineTools.Commands
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class MakeCeilingGridsCommand : IExternalCommand
    {
        private static StepFlowWindow? _window;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
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

            var uiApp = commandData.Application;
            MakeCeilingGridsViewModel BuildTool()
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Collect available documents on the Revit main thread
                var availableDocs = new List<MakeCeilingGridsViewModel.DocEntry>
                {
                    new MakeCeilingGridsViewModel.DocEntry
                    {
                        Label      = "(Host document)",
                        IsHost     = true,
                        LinkInstId = ElementId.InvalidElementId,
                    }
                };

                var seenDocIds = new System.Collections.Generic.HashSet<string>
                    { doc.PathName ?? doc.Title };

                foreach (var li in new FilteredElementCollector(doc)
                    .OfClass(typeof(RevitLinkInstance))
                    .Cast<RevitLinkInstance>()
                    .Where(l => l.GetLinkDocument() != null))
                {
                    var ld = li.GetLinkDocument();
                    string key = ld.PathName ?? ld.Title;
                    if (!seenDocIds.Add(key)) continue;

                    availableDocs.Add(new MakeCeilingGridsViewModel.DocEntry
                    {
                        Label      = ld.Title ?? li.Name,
                        IsHost     = false,
                        LinkInstId = li.Id,
                    });
                }

                var vm = new MakeCeilingGridsViewModel(
                    App.MakeCeilingGridsPhase1Handler!, App.MakeCeilingGridsPhase1Event!,
                    App.MakeCeilingGridsRunHandler!,    App.MakeCeilingGridsRunEvent!,
                    availableDocs);

                return vm;
            }
            var vm = BuildTool();
            var ready          = new ManualResetEventSlim(false);
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
