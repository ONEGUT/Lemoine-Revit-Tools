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
using LemoineTools.Tools.Testing;

namespace LemoineTools.Commands
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ClashDimensionCommand : IExternalCommand
    {
        private static StepFlowWindow? _window;

        public Result Execute(
            ExternalCommandData commandData,
            ref string          message,
            ElementSet          elements)
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

            var doc        = commandData.Application.ActiveUIDocument.Document;
            var activeView = commandData.Application.ActiveUIDocument.ActiveView;

            // Level elevation of the active view (for filtering linked floors by level)
            double? activeElev = null;
            if (activeView is ViewPlan vp && vp.GenLevel != null)
                activeElev = vp.GenLevel.Elevation;

            // Host elements visible in the active view
            var activeViewHostGridIds  = new HashSet<long>();
            var activeViewHostFloorIds = new HashSet<long>();
            foreach (var g in new FilteredElementCollector(doc, activeView.Id)
                .OfCategory(BuiltInCategory.OST_Grids).OfClass(typeof(Grid)).Cast<Grid>())
                activeViewHostGridIds.Add(g.Id.Value);
            foreach (var f in new FilteredElementCollector(doc, activeView.Id)
                .OfCategory(BuiltInCategory.OST_Floors).OfClass(typeof(Floor)).Cast<Floor>())
                activeViewHostFloorIds.Add(f.Id.Value);

            // Dimension types
            var dimStyleNames = new FilteredElementCollector(doc)
                .OfClass(typeof(DimensionType))
                .Cast<DimensionType>()
                .Select(dt => dt.Name)
                .OrderBy(n => n)
                .ToList();

            // Plan views only
            var allViews = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => !v.IsTemplate
                         && (v.ViewType == ViewType.FloorPlan
                          || v.ViewType == ViewType.CeilingPlan))
                .OrderBy(v => v.Name)
                .ToList();

            var activeViewData = new GridFloorData();
            var allData        = new GridFloorData();
            var usedGridNames  = new HashSet<string>();
            var usedFloorNames = new HashSet<string>();

            // Host grids
            foreach (var g in new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Grids).OfClass(typeof(Grid))
                .Cast<Grid>().OrderBy(g => g.Name))
            {
                string name = UniqueName(g.Name, usedGridNames);
                allData.AddGrid(name, g.Id.Value, 0L);
                if (activeViewHostGridIds.Contains(g.Id.Value))
                    activeViewData.AddGrid(name, g.Id.Value, 0L);
            }

            // Host floors — "(Host)" prefix so origin model is always visible
            foreach (var f in new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Floors).OfClass(typeof(Floor))
                .Cast<Floor>().OrderBy(f => f.Name))
            {
                var lvl = doc.GetElement(f.LevelId) as Level;
                string display = lvl != null
                    ? $"(Host) {f.Name} — {lvl.Name}"
                    : $"(Host) {f.Name}";
                string name = UniqueName(display, usedFloorNames);
                allData.AddFloor(name, f.Id.Value, 0L);
                if (activeViewHostFloorIds.Contains(f.Id.Value))
                    activeViewData.AddFloor(name, f.Id.Value, 0L);
            }

            // Linked elements
            foreach (var li in new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                var ld = li.GetLinkDocument();
                if (ld == null) continue;

                bool linkVisibleInView = !li.IsHidden(activeView);
                var linkTx             = li.GetTotalTransform();
                string prefix          = "[" + Path.GetFileNameWithoutExtension(ld.Title) + "] ";

                // Linked grids — visible if link is not hidden in the active view
                foreach (var g in new FilteredElementCollector(ld)
                    .OfCategory(BuiltInCategory.OST_Grids).OfClass(typeof(Grid))
                    .Cast<Grid>().OrderBy(g => g.Name))
                {
                    string name = UniqueName(prefix + g.Name, usedGridNames);
                    allData.AddGrid(name, g.Id.Value, li.Id.Value);
                    if (linkVisibleInView)
                        activeViewData.AddGrid(name, g.Id.Value, li.Id.Value);
                }

                // Linked floors — visible if link not hidden AND level elevation matches active view (±3 ft)
                foreach (var f in new FilteredElementCollector(ld)
                    .OfCategory(BuiltInCategory.OST_Floors).OfClass(typeof(Floor))
                    .Cast<Floor>().OrderBy(f => f.Name))
                {
                    var lvl = ld.GetElement(f.LevelId) as Level;
                    string display = lvl != null
                        ? $"{prefix}{f.Name} — {lvl.Name}"
                        : $"{prefix}{f.Name}";
                    string name = UniqueName(display, usedFloorNames);
                    allData.AddFloor(name, f.Id.Value, li.Id.Value);

                    if (linkVisibleInView)
                    {
                        bool elevMatch = activeElev == null;   // no level info → include all
                        if (!elevMatch && lvl != null)
                        {
                            double hostElev = linkTx.OfPoint(new XYZ(0, 0, lvl.Elevation)).Z;
                            elevMatch = Math.Abs(hostElev - activeElev!.Value) < 3.0;
                        }
                        if (elevMatch)
                            activeViewData.AddFloor(name, f.Id.Value, li.Id.Value);
                    }
                }
            }

            // Line style names
            var lineStyleNames = new List<string>();
            var linesCat = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
            if (linesCat != null)
            {
                foreach (Category sub in linesCat.SubCategories)
                    lineStyleNames.Add(sub.Name);
                lineStyleNames.Sort();
            }

            var vm = new ClashDimensionViewModel(
                App.ClashDimensionHandler!, App.ClashDimensionEvent!,
                dimStyleNames, allViews,
                activeViewData, allData,
                lineStyleNames);

            var ready = new ManualResetEventSlim(false);
            StepFlowWindow? win = null;

            var thread = new Thread(() =>
            {
                win = new StepFlowWindow(vm);
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

        private static string UniqueName(string candidate, HashSet<string> used)
        {
            if (used.Add(candidate)) return candidate;
            int n = 2;
            while (!used.Add($"{candidate} ({n})")) n++;
            return $"{candidate} ({n})";
        }
    }
}
