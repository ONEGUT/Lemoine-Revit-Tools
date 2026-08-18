using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Threading;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Helpers;
using LemoineTools.Framework;
using LemoineTools.Tools.Sheets.PlaceDependentViews;

namespace LemoineTools.Commands
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class PlaceDependentViewsCommand : IExternalCommand
    {
        private static StepFlowWindow? _window;

        /// <summary>View types that can host callout/section/elevation markers — the
        /// candidates offered as composite-mode source views.</summary>
        private static readonly HashSet<ViewType> CompositeSourceTypes = new HashSet<ViewType>
        {
            ViewType.FloorPlan,
            ViewType.CeilingPlan,
            ViewType.AreaPlan,
            ViewType.EngineeringPlan,
            ViewType.Section,
            ViewType.Elevation,
            ViewType.Detail,
        };

        /// <summary>View types that cannot be placed on a sheet as a viewport — excluded from the
        /// Place Views (one-view-per-sheet) candidate list. Everything else non-template is offered.</summary>
        private static readonly HashSet<ViewType> NonPlaceableTypes = new HashSet<ViewType>
        {
            ViewType.Schedule,
            ViewType.Legend,
            ViewType.DrawingSheet,
            ViewType.ProjectBrowser,
            ViewType.SystemBrowser,
            ViewType.Internal,
            ViewType.Undefined,
        };

        public Result Execute(
            ExternalCommandData commandData,
            ref string          message,
            ElementSet          elements)
        {
            // Reuse the existing open window if any.
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
            PlaceDependentViewsViewModel BuildTool()
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // ── Title blocks ──────────────────────────────────────────────────
                var titleblocks = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_TitleBlocks)
                    .WhereElementIsElementType()
                    .Cast<FamilySymbol>()
                    .OrderBy(tb => tb.FamilyName)
                    .ThenBy(tb => tb.Name)
                    .ToList();

                // ── Candidate views, one collector pass ───────────────────────────
                // parents: primary views that own dependents (dependents mode).
                // composites: view types that can host callout/section/elevation markers
                // (composite mode) — including dependent views, since a dependent shows its
                // own crop of the markers and is a valid composite source. Sub views are
                // discovered at run time, so no per-view marker scan happens here.
                var parents    = new List<ParentViewEntry>();
                var composites = new List<ParentViewEntry>();
                var placeable  = new List<ParentViewEntry>();
                foreach (var v in new FilteredElementCollector(doc)
                             .OfClass(typeof(View)).Cast<View>()
                             .Where(v => !v.IsTemplate))
                {
                    string level = "";
                    try { level = v.GenLevel?.Name ?? ""; }
                    catch (System.Exception ex) { DiagnosticsLog.Swallowed($"PlaceDependentViews: read GenLevel on view {v.Id.Value}", ex); }

                    // Only primaries can be a dependents-mode parent.
                    if (v.GetPrimaryViewId() == ElementId.InvalidElementId)
                    {
                        var deps = v.GetDependentViewIds();
                        if (deps != null && deps.Count > 0)
                            parents.Add(new ParentViewEntry(v.Id, v.Name, v.ViewType.ToString(), level, deps.Count));
                    }

                    if (CompositeSourceTypes.Contains(v.ViewType))
                        composites.Add(new ParentViewEntry(v.Id, v.Name, v.ViewType.ToString(), level, -1));

                    // Place Views mode: any non-template, sheet-placeable graphical view.
                    if (!NonPlaceableTypes.Contains(v.ViewType))
                        placeable.Add(new ParentViewEntry(v.Id, v.Name, v.ViewType.ToString(), level, -1));
                }

                // ── Title-block paper sizes ───────────────────────────────────────
                // Read here, on the Revit thread, because the tool window runs on its own STA
                // thread and cannot touch the document. A family that does not publish these is
                // recorded as "unknown" rather than given an invented size.
                var tbSizes = new Dictionary<string, (double W, double H)>();
                foreach (var tb in titleblocks)
                {
                    string label = $"{tb.FamilyName} : {tb.Name}";
                    if (tbSizes.ContainsKey(label)) continue;
                    double w = ReadLengthInches(tb, BuiltInParameter.SHEET_WIDTH);
                    double h = ReadLengthInches(tb, BuiltInParameter.SHEET_HEIGHT);
                    tbSizes[label] = (w, h);
                }

                // ── Existing sheet numbers ────────────────────────────────────────
                // The numbering preview must show the numbers the run will really assign, so the
                // collision skipping happens in the ViewModel against this set — not silently
                // inside the handler where the user could never see it.
                var sheets = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSheet)).Cast<ViewSheet>().ToList();
                var usedNumbers = new HashSet<string>(
                    sheets.Select(vs => vs.SheetNumber ?? ""), System.StringComparer.OrdinalIgnoreCase);

                var vm = new PlaceDependentViewsViewModel(
                    App.PlaceDependentViewsHandler!, App.PlaceDependentViewsEvent!,
                    parents, composites, titleblocks,
                    BrowserTreeCapture.Capture(doc), placeable,
                    tbSizes, usedNumbers, CaptureSheetTextParams(doc, sheets));

                return vm;
            }
            var vm = BuildTool();
            var ready = new ManualResetEventSlim(false);
            StepFlowWindow? win = null;

            var thread = new System.Threading.Thread(() =>
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

        /// <summary>A length-valued type parameter in paper INCHES, or 0 when the family does not
        /// carry it. Revit stores lengths in decimal feet.</summary>
        private static double ReadLengthInches(Element el, BuiltInParameter bip)
        {
            try
            {
                var p = el?.get_Parameter(bip);
                if (p == null || p.StorageType != StorageType.Double) return 0;
                double ft = p.AsDouble();
                return ft > 0 ? ft * 12.0 : 0;
            }
            catch (System.Exception ex)
            {
                DiagnosticsLog.Swallowed($"PlaceDependentViews: read {bip} on {el?.Id.Value}", ex);
                return 0;
            }
        }

        /// <summary>
        /// Every writable TEXT parameter a sheet carries, with the values already in use for each.
        ///
        /// Read off a real sheet whenever the project has one — that is exactly the set the write
        /// will see, identity included. With no sheets yet there is nothing to read, so the
        /// project's parameter bindings are walked instead for definitions bound to Sheets; those
        /// have no existing values to offer, which is correct rather than a gap.
        /// </summary>
        private static List<SheetSeriesParam> CaptureSheetTextParams(Document doc, List<ViewSheet> sheets)
        {
            var found = new List<SheetSeriesParam>();
            var byKey = new Dictionary<string, SheetSeriesParam>(System.StringComparer.Ordinal);

            try
            {
                var sample = sheets.FirstOrDefault();
                if (sample != null)
                {
                    foreach (Parameter p in sample.Parameters)
                    {
                        var rec = Describe(p);
                        if (rec == null) continue;
                        string key = Key(rec);
                        if (byKey.ContainsKey(key)) continue;
                        byKey[key] = rec;
                        found.Add(rec);
                    }

                    // Values already in use, so the user picks an existing series instead of
                    // re-typing one and inventing a near-duplicate.
                    foreach (var sheet in sheets)
                    {
                        foreach (var rec in found)
                        {
                            string v = rec.Resolve(sheet)?.AsString() ?? "";
                            if (!string.IsNullOrWhiteSpace(v) && !rec.ExistingValues.Contains(v))
                                rec.ExistingValues.Add(v);
                        }
                    }
                    foreach (var rec in found)
                        rec.ExistingValues.Sort(NaturalOrderComparer.OrdinalIgnoreCase);
                }
                else
                {
                    var it = doc.ParameterBindings.ForwardIterator();
                    while (it.MoveNext())
                    {
                        if (!(it.Key is Definition def)) continue;
                        if (!(it.Current is ElementBinding eb)) continue;
                        bool onSheets = false;
                        foreach (Category c in eb.Categories)
                            if (c != null && c.Id.Value == (long)BuiltInCategory.OST_Sheets) { onSheets = true; break; }
                        if (!onSheets) continue;

                        var internalDef = def as InternalDefinition;
                        var shared = internalDef != null
                            ? new FilteredElementCollector(doc).OfClass(typeof(SharedParameterElement))
                                .Cast<SharedParameterElement>()
                                .FirstOrDefault(sp => sp.Id == internalDef.Id)
                            : null;
                        found.Add(new SheetSeriesParam(
                            def.Name,
                            shared != null,
                            shared?.GuidValue ?? System.Guid.Empty,
                            internalDef?.Id ?? ElementId.InvalidElementId));
                    }
                }
            }
            catch (System.Exception ex)
            {
                DiagnosticsLog.Error("PlaceDependentViews: capture sheet text parameters", ex);
            }

            found.Sort((a, b) => NaturalOrderComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
            return found;
        }

        private static string Key(SheetSeriesParam p) =>
            p.IsShared ? "g:" + p.SharedGuid.ToString("N") : "d:" + p.DefinitionId.Value.ToString();

        /// <summary>A writable, text-valued parameter as a capture record; null for anything else.</summary>
        private static SheetSeriesParam? Describe(Parameter p)
        {
            try
            {
                if (p == null || p.IsReadOnly) return null;
                if (p.StorageType != StorageType.String) return null;
                if (!(p.Definition is Definition def) || string.IsNullOrEmpty(def.Name)) return null;

                // Parameter.GUID throws for a non-shared parameter, so IsShared gates it.
                System.Guid guid = System.Guid.Empty;
                if (p.IsShared) guid = p.GUID;

                var id = (def as InternalDefinition)?.Id ?? ElementId.InvalidElementId;
                return new SheetSeriesParam(def.Name, p.IsShared, guid, id);
            }
            catch (System.Exception ex)
            {
                DiagnosticsLog.Swallowed("PlaceDependentViews: describe sheet parameter", ex);
                return null;
            }
        }
    }
}
