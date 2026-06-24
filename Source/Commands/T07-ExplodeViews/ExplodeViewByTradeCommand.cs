using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Threading;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Helpers;
using LemoineTools.Lemoine;
using LemoineTools.Tools.AutoFilters;
using LemoineTools.Tools.ExplodeViews;

namespace LemoineTools.Commands
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ExplodeViewByTradeCommand : IExternalCommand
    {
        private static StepFlowWindow? _window;

        public Result Execute(
            ExternalCommandData commandData,
            ref string          message,
            ElementSet          elements)
        {
            // Bring existing window to front if already open.
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

            Document doc = commandData.Application.ActiveUIDocument.Document;

            // ── Eligible 3D views + id→name map (main thread — safe) ──────────────
            var eligibleViewIds = new List<long>();
            var viewNames       = new Dictionary<long, string>();
            foreach (View3D v in new FilteredElementCollector(doc)
                .OfClass(typeof(View3D)).Cast<View3D>()
                .Where(v => !v.IsTemplate))
            {
                eligibleViewIds.Add(v.Id.Value);
                viewNames[v.Id.Value] = v.Name;
            }

            // ── Trades + which already have created filters in this project ───────
            var existingFilterNames = new HashSet<string>(
                new FilteredElementCollector(doc)
                    .OfClass(typeof(ParameterFilterElement)).Cast<ParameterFilterElement>()
                    .Select(f => f.Name),
                System.StringComparer.OrdinalIgnoreCase);

            var trades = new List<(string Id, string Label, bool HasFilters)>();
            foreach (var trade in AutoFiltersSettings.Instance.Trades)
            {
                bool hasFilters = trade.Rules
                    .Where(r => r.Enabled && AutoFiltersSettings.RuleProducesFilter(r))
                    .Any(r => existingFilterNames.Contains(
                        AutoFiltersSettings.MakeFilterName(trade.Id, r.Name)));
                trades.Add((trade.Id, string.IsNullOrWhiteSpace(trade.Label) ? trade.Id : trade.Label, hasFilters));
            }

            // ── Spin up dedicated STA thread for the WPF window ───────────────────
            var vm = new ExplodeViewByTradeViewModel(
                App.ExplodeViewByTradeHandler!, App.ExplodeViewByTradeEvent!,
                eligibleViewIds, BrowserTreeCapture.Capture(doc), trades, viewNames);

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
    }
}
