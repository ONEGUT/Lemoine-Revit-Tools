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
using LemoineTools.Tools.ScopeBoxes;

namespace LemoineTools.Commands
{
    /// <summary>
    /// Opens the merged Bulk Views window — Bulk Views by Level, Duplicate Views, Bulk Views
    /// by Template, Replicate Dependent Views, and By Link, behind one mode dropdown (see
    /// BulkViewsViewModel). Combines the main-thread capture that used to live in the four
    /// separate commands (LinkViewsLevelCommand, ViewsBulkDuplicateCommand,
    /// ViewsByTemplateCommand, ReplicateDependentViewsCommand — now removed) plus the new
    /// By Link mode's link list.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class BulkViewsCommand : IExternalCommand
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
            BulkViewsViewModel BuildTool()
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var browserTree = BrowserTreeCapture.Capture(doc);

                // ── By Level ────────────────────────────────────────────────
                var levels = new FilteredElementCollector(doc)
                    .OfClass(typeof(Level)).Cast<Level>()
                    .OrderBy(l => l.Elevation)
                    .Select(l => new LinkViewsLevelViewModel.LevelEntry
                    {
                        Id = l.Id, Name = l.Name,
                        ElevationFt = Math.Round(UnitUtils.ConvertFromInternalUnits(l.Elevation, UnitTypeId.Feet), 2),
                    })
                    .ToList();
                var scopeBoxes  = ScopeBoxCreatorScanHandler.CollectScopeBoxes(doc);
                var templates3D  = CollectViewTemplates(doc, ViewType.ThreeD);
                var templatesFP  = CollectViewTemplates(doc, ViewType.FloorPlan);
                var templatesRCP = CollectViewTemplates(doc, ViewType.CeilingPlan);
                var byLevel = new LinkViewsLevelViewModel(
                    App.LinkViewsLevelRunHandler!, App.LinkViewsLevelRunEvent!,
                    levels, scopeBoxes, templates3D, templatesFP, templatesRCP);

                // ── Duplicate / By Template — shared eligible-view collection ──
                var eligibleForDuplicate = CollectDuplicatableViews(doc);
                var duplicate = new ViewsBulkDuplicateViewModel(
                    App.ViewsBulkDuplicateRunHandler!, App.ViewsBulkDuplicateRunEvent!,
                    eligibleForDuplicate, browserTree);

                var eligibleForTemplate = new FilteredElementCollector(doc)
                    .OfClass(typeof(View)).Cast<View>()
                    .Where(v => !v.IsTemplate
                             && v.ViewType != ViewType.Schedule
                             && v.ViewType != ViewType.Legend
                             && v.ViewType != ViewType.DrawingSheet
                             && v.ViewType != ViewType.ProjectBrowser
                             && v.ViewType != ViewType.SystemBrowser
                             && v.ViewType != ViewType.Internal
                             && v.ViewType != ViewType.Undefined
                             && v.CanViewBeDuplicated(ViewDuplicateOption.WithDetailing))
                    .Select(v => new ViewsByTemplateViewModel.ViewEntry
                        { Id = v.Id, Name = v.Name, TypeLabel = ViewsByTemplateRunHandler.ViewTypeLabel(v.ViewType) })
                    .OrderBy(v => v.TypeLabel).ThenBy(v => v.Name).ToList();
                var viewTemplates = new FilteredElementCollector(doc)
                    .OfClass(typeof(View)).Cast<View>()
                    .Where(v => v.IsTemplate)
                    .Select(v => new ViewsByTemplateViewModel.TemplateEntry
                        { Id = v.Id, Name = v.Name, TypeLabel = ViewsByTemplateRunHandler.ViewTypeLabel(v.ViewType) })
                    .OrderBy(t => t.TypeLabel).ThenBy(t => t.Name).ToList();
                var byTemplate = new ViewsByTemplateViewModel(
                    App.ViewsByTemplateRunHandler!, App.ViewsByTemplateRunEvent!,
                    eligibleForTemplate, viewTemplates, browserTree);

                // ── Replicate Dependent Views ───────────────────────────────
                var (allSources, allTargets, basisXMap) = CollectReplicateDependentsData(doc);
                var replicateDeps = new ReplicateDependentViewsViewModel(
                    App.ReplicateDependentViewsRunHandler!, App.ReplicateDependentViewsRunEvent!,
                    allSources, allTargets, browserTree);
                replicateDeps.TargetBasisXMap = basisXMap;

                // ── By Link ─────────────────────────────────────────────────
                var linkEntries = new FilteredElementCollector(doc)
                    .OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>()
                    .Select(li => new ViewsByLinkViewModel.LinkEntry
                    {
                        Id = li.Id,
                        Name = li.GetLinkDocument() != null
                            ? System.IO.Path.GetFileNameWithoutExtension(li.GetLinkDocument().Title)
                            : li.Name,
                    })
                    .OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var byLinkTemplates = templates3D
                    .Select(t => new ViewsByLinkViewModel.TemplateEntry { Id = t.Id, Name = t.Name })
                    .ToList();
                var byLink = new ViewsByLinkViewModel(
                    App.ViewsByLinkRunHandler!, App.ViewsByLinkRunEvent!, linkEntries, byLinkTemplates);

                return new BulkViewsViewModel(byLevel, duplicate, byTemplate, replicateDeps, byLink);
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

        private static List<ViewsBulkDuplicateViewModel.ViewEntry> CollectDuplicatableViews(Document doc) =>
            new FilteredElementCollector(doc)
                .OfClass(typeof(View)).Cast<View>()
                .Where(v => !v.IsTemplate
                         && v.ViewType != ViewType.Schedule
                         && v.ViewType != ViewType.Legend
                         && v.ViewType != ViewType.DrawingSheet
                         && v.ViewType != ViewType.ProjectBrowser
                         && v.ViewType != ViewType.SystemBrowser
                         && v.ViewType != ViewType.Internal
                         && v.ViewType != ViewType.Undefined
                         && (v.CanViewBeDuplicated(ViewDuplicateOption.Duplicate)
                          || v.CanViewBeDuplicated(ViewDuplicateOption.WithDetailing)
                          || v.CanViewBeDuplicated(ViewDuplicateOption.AsDependent)))
                .Select(v => new ViewsBulkDuplicateViewModel.ViewEntry
                    { Id = v.Id, Name = v.Name, TypeLabel = ViewsByTemplateRunHandler.ViewTypeLabel(v.ViewType) })
                .OrderBy(v => v.TypeLabel).ThenBy(v => v.Name)
                .ToList();

        private static List<LinkViewsLevelViewModel.ViewTemplateEntry> CollectViewTemplates(Document doc, ViewType vt) =>
            new FilteredElementCollector(doc)
                .OfClass(typeof(View)).Cast<View>()
                .Where(v => v.IsTemplate && v.ViewType == vt)
                .OrderBy(v => v.Name)
                .Select(v => new LinkViewsLevelViewModel.ViewTemplateEntry { Id = v.Id, Name = v.Name })
                .ToList();

        private static (List<SourceViewEntry>, List<TargetViewEntry>, Dictionary<long, XYZ>) CollectReplicateDependentsData(Document doc)
        {
            var allViews = new FilteredElementCollector(doc)
                .OfClass(typeof(View)).Cast<View>()
                .Where(v => !v.IsTemplate
                         && v.GetPrimaryViewId() == ElementId.InvalidElementId
                         && v.CanViewBeDuplicated(ViewDuplicateOption.AsDependent))
                .OrderBy(v => v.Name)
                .ToList();

            var allSources = new List<SourceViewEntry>();
            foreach (var v in allViews)
            {
                var depIds = v.GetDependentViewIds().ToList();
                if (depIds.Count == 0) continue;

                var deps = new List<DepEntry>();
                foreach (var depId in depIds)
                {
                    var depView = doc.GetElement(depId) as View;
                    if (depView == null) continue;

                    string suffix = depView.Name.StartsWith(v.Name + " - ", StringComparison.OrdinalIgnoreCase)
                        ? depView.Name.Substring(v.Name.Length + 3).Trim()
                        : depView.Name;

                    bool hasCrop = depView.CropBoxActive;
                    XYZ? min = null, max = null;
                    try
                    {
                        var cb = depView.CropBox;
                        if (cb != null) { min = cb.Min; max = cb.Max; }
                    }
                    catch (Exception ex) { DiagnosticsLog.Swallowed("BulkViews: read source crop box", ex); }

                    ElementId scopeBoxId = ElementId.InvalidElementId;
                    try
                    {
                        var sp = depView.get_Parameter(BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP);
                        if (sp != null) scopeBoxId = sp.AsElementId() ?? ElementId.InvalidElementId;
                    }
                    catch (Exception ex) { DiagnosticsLog.Swallowed("BulkViews: read source scope box", ex); }

                    deps.Add(new DepEntry
                    {
                        DepViewId = depId, Suffix = suffix, HasCrop = hasCrop,
                        WorldMin = min, WorldMax = max, ScopeBoxId = scopeBoxId,
                    });
                }
                if (deps.Count == 0) continue;

                allSources.Add(new SourceViewEntry
                {
                    ViewId = v.Id, Name = v.Name, ViewType = v.ViewType,
                    TypeLabel = ReplicateViewTypeLabel(v.ViewType), Deps = deps,
                    BasisX = SafeRightDirection(v),
                });
            }

            var allTargets = allViews
                .Select(v => new TargetViewEntry
                {
                    ViewId = v.Id, Name = v.Name, ViewType = v.ViewType,
                    LevelName = GetViewLevelName(v),
                    ExistingDepCount = v.GetDependentViewIds().Count,
                    OrientationWarning = false,
                })
                .ToList();

            var basisXMap = allViews.ToDictionary(v => v.Id.Value, v => SafeRightDirection(v));
            return (allSources, allTargets, basisXMap);
        }

        private static string ReplicateViewTypeLabel(ViewType vt)
        {
            switch (vt)
            {
                case ViewType.FloorPlan:   return "Floor Plans";
                case ViewType.CeilingPlan: return "Ceiling Plans";
                case ViewType.Elevation:   return "Elevations";
                case ViewType.Section:     return "Sections";
                case ViewType.Detail:      return "Detail Views";
                case ViewType.AreaPlan:    return "Area Plans";
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
            catch (Exception ex) { DiagnosticsLog.Swallowed("BulkViews: resolve view level name", ex); }
            return "";
        }
    }
}
