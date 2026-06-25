using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Threading;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;
using LemoineTools.Tools.AutoFilters;
using LemoineTools.Tools.CopyLinear;

namespace LemoineTools.Commands
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class CopyLinearCommand : IExternalCommand
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
            CopyLinearViewModel BuildTool()
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Capture the full filterable-category list on the Revit main thread (same as DiscoverLaunchCommand).
                AutoFiltersSettings.CaptureFilterableCategories(doc);

                var docs     = CollectDocs(doc);
                var families = CollectFamilies(doc);

                var vm = new CopyLinearViewModel(
                    App.CopyLinearScanHandler, App.CopyLinearScanEvent,
                    App.CopyLinearRunHandler,  App.CopyLinearRunEvent,
                    docs, families);

                return vm;
            }
            var vm = BuildTool();
            var ready = new ManualResetEventSlim(false);
            StepFlowWindow? win = null;

            var thread = new Thread(() =>
            {
                win = new StepFlowWindow(vm, BuildTool);
                win.Closed += (s, e) => { _window = null; Dispatcher.CurrentDispatcher.InvokeShutdown(); };
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

        // Host + every loaded link, each with its user worksets.
        private static List<CopyLinearDocInfo> CollectDocs(Document doc)
        {
            var docs = new List<CopyLinearDocInfo>();
            if (doc == null) return docs;

            docs.Add(new CopyLinearDocInfo
            {
                Name = "(Host) " + doc.Title, LinkInstId = 0L, LinkInstUid = "host",
                Worksets = ReadUserWorksets(doc),
            });

            foreach (var li in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                var ld = li.GetLinkDocument();
                if (ld == null) continue;
                docs.Add(new CopyLinearDocInfo
                {
                    Name = "[" + Path.GetFileNameWithoutExtension(ld.Title) + "]",
                    LinkInstId = li.Id.Value, LinkInstUid = li.UniqueId,
                    Worksets = ReadUserWorksets(ld),
                });
            }
            return docs;
        }

        private static List<CopyLinearWorksetInfo> ReadUserWorksets(Document d)
        {
            var list = new List<CopyLinearWorksetInfo>();
            try
            {
                if (d == null || !d.IsWorkshared) return list;
                foreach (var ws in new FilteredWorksetCollector(d).OfKind(WorksetKind.UserWorkset))
                    list.Add(new CopyLinearWorksetInfo { Id = ws.Id.IntegerValue, Name = ws.Name });
                list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex) { LemoineLog.Swallowed("CopyLinearCommand: read user worksets", ex); }
            return list;
        }

        // Placeable model family types in the host, as "Category — Family: Type".
        private static List<CopyLinearFamilyInfo> CollectFamilies(Document doc)
        {
            var list = new List<CopyLinearFamilyInfo>();
            if (doc == null) return list;
            try
            {
                foreach (var fs in new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>())
                {
                    var cat = fs.Category;
                    if (cat == null || cat.CategoryType != CategoryType.Model) continue;
                    string fam = fs.Family?.Name ?? "";
                    string key = $"{cat.Name} — {fam}: {fs.Name}";
                    list.Add(new CopyLinearFamilyInfo { Key = key, Category = cat.Name, SymbolId = fs.Id.Value });
                }
                list.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex) { LemoineLog.Swallowed("CopyLinearCommand: collect family symbols", ex); }
            return list;
        }
    }
}
