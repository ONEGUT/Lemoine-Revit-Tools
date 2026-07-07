using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Threading;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Helpers;
using LemoineTools.Framework;
using LemoineTools.Tools.LinkViews;

namespace LemoineTools.Commands
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ReplicateDependentViewsCommand : IExternalCommand
    {
        private static StepFlowWindow? _window;

        public Result Execute(ExternalCommandData commandData,
                              ref string message, ElementSet elements)
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
            ReplicateDependentViewsViewModel BuildTool()
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // ── Capture all data on the main Revit thread ──────────────

                // 1. All non-template views that can be duplicated as dependent
                var allViews = new FilteredElementCollector(doc)
                    .OfClass(typeof(View)).Cast<View>()
                    .Where(v => !v.IsTemplate
                             && v.GetPrimaryViewId() == ElementId.InvalidElementId  // not already a dep
                             && v.CanViewBeDuplicated(ViewDuplicateOption.AsDependent))
                    .OrderBy(v => v.Name)
                    .ToList();

                // 2. Identify source views: those with at least one dependent
                var allSources = new List<SourceViewEntry>();
                foreach (var v in allViews)
                {
                    var depIds = v.GetDependentViewIds().ToList();
                    if (depIds.Count == 0) continue;

                    var deps = new List<DepEntry>();
                    foreach (var depId in depIds)
                    {
                        View? depView = doc.GetElement(depId) as View;
                        if (depView == null) continue;

                        // Determine suffix
                        string suffix = depView.Name.StartsWith(v.Name + " - ", StringComparison.OrdinalIgnoreCase)
                            ? depView.Name.Substring(v.Name.Length + 3).Trim()
                            : depView.Name;

                        // Read crop
                        bool hasCrop = depView.CropBoxActive;
                        XYZ? min = null, max = null;
                        try
                        {
                            var cb = depView.CropBox;
                            if (cb != null) { min = cb.Min; max = cb.Max; }
                        }
                        catch (Exception __lex) { DiagnosticsLog.Swallowed("ReplicateDependentViews: read source crop box", __lex); }

                        // Scope box
                        ElementId scopeBoxId = ElementId.InvalidElementId;
                        try
                        {
                            var sp = depView.get_Parameter(BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP);
                            if (sp != null) scopeBoxId = sp.AsElementId() ?? ElementId.InvalidElementId;
                        }
                        catch (Exception __lex) { DiagnosticsLog.Swallowed("ReplicateDependentViews: read source scope box", __lex); }

                        deps.Add(new DepEntry
                        {
                            DepViewId  = depId,
                            Suffix     = suffix,
                            HasCrop    = hasCrop,
                            WorldMin   = min,
                            WorldMax   = max,
                            ScopeBoxId = scopeBoxId,
                        });
                    }

                    if (deps.Count == 0) continue;

                    allSources.Add(new SourceViewEntry
                    {
                        ViewId    = v.Id,
                        Name      = v.Name,
                        ViewType  = v.ViewType,
                        TypeLabel = ViewTypeLabel(v.ViewType),
                        Deps      = deps,
                        BasisX    = SafeRightDirection(v),
                    });
                }

                // 3. All potential target views (same logic — non-template, can-be-dep, not already a dep)
                var allTargets = allViews
                    .Select(v => new TargetViewEntry
                    {
                        ViewId             = v.Id,
                        Name               = v.Name,
                        ViewType           = v.ViewType,
                        LevelName          = GetViewLevelName(v),
                        ExistingDepCount   = v.GetDependentViewIds().Count,
                        OrientationWarning = false,
                    })
                    .ToList();

                // Orientation warnings: computed here against each source would be expensive.
                // Instead the ViewModel flags them for the selected source in S3 via a helper.
                // We enrich TargetViewEntry.OrientationWarning relative to each source on-the-fly
                // by passing BasisX through the RunHandler at run time; for now leave false.

                var vm = new ReplicateDependentViewsViewModel(
                    App.ReplicateDependentViewsRunHandler!,
                    App.ReplicateDependentViewsRunEvent!,
                    allSources,
                    allTargets,
                    BrowserTreeCapture.Capture(doc));

                // Inject orientation warnings into each target relative to the selected source
                // — done by storing all targets' BasisX for the ViewModel to compare lazily.
                // The ViewModel's BuildS3() re-evaluates OrientationWarning using the source's BasisX
                // through the helper below; we store raw directions in an extended list for it.
                vm.TargetBasisXMap = allViews.ToDictionary(
                    v => v.Id.Value,
                    v => SafeRightDirection(v));

                return vm;
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

        // ── Helpers ───────────────────────────────────────────────────

        private static string ViewTypeLabel(ViewType vt)
        {
            switch (vt)
            {
                case ViewType.FloorPlan:    return "Floor Plans";
                case ViewType.CeilingPlan:  return "Ceiling Plans";
                case ViewType.Elevation:    return "Elevations";
                case ViewType.Section:      return "Sections";
                case ViewType.Detail:       return "Detail Views";
                case ViewType.AreaPlan:     return "Area Plans";
                default:                   return vt.ToString();
            }
        }

        private static XYZ SafeRightDirection(View v)
        {
            try { return v.RightDirection; }
            catch { return XYZ.BasisX; }
        }

        private static string GetViewLevelName(View v)
        {
            try
            {
                var p = v.get_Parameter(BuiltInParameter.PLAN_VIEW_LEVEL);
                if (p != null)
                {
                    string name = p.AsString();
                    if (!string.IsNullOrEmpty(name)) return name;
                }
            }
            catch (Exception __lex) { DiagnosticsLog.Swallowed("ReplicateDependentViews: resolve view level name", __lex); }
            return "";
        }
    }
}
