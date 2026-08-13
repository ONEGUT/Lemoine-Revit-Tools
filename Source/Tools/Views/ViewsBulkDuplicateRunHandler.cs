using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Framework.Naming;

namespace LemoineTools.Tools.LinkViews
{
    /// <summary>
    /// <see cref="IExternalEventHandler"/> that bulk-duplicates the selected views using the
    /// chosen <see cref="ViewDuplicateOption"/> and names each copy from the token pattern.
    /// The ViewModel sets the public inputs before calling <c>Raise()</c>.
    /// </summary>
    public sealed class ViewsBulkDuplicateRunHandler : IExternalEventHandler
    {
        /// <summary>One scope box a run fans out to — id plus the name used by {ScopeBoxName}.</summary>
        public sealed class ScopeBoxTarget
        {
            public ElementId Id   { get; set; } = ElementId.InvalidElementId;
            public string    Name { get; set; } = string.Empty;
        }

        // ── Inputs set before Raise() ─────────────────────────────────
        /// <summary>Source views to duplicate (one copy each, or one per scope box).</summary>
        public List<ElementId> SelectedViewIds { get; set; } = new List<ElementId>();
        /// <summary>Duplicate-mode label (one of the ViewsBulkDuplicateViewModel.Mode* constants).</summary>
        public string          Mode            { get; set; } = ViewsBulkDuplicateViewModel.ModeWithDetailing;
        /// <summary>Token pattern for the copy's name ({ViewName}, {ViewType}, and in
        /// per-scope-box mode {ScopeBoxName}).</summary>
        public string          NamePattern     { get; set; } = "{ViewName} - Copy";
        /// <summary>When true, each source view is duplicated once per <see cref="SelectedScopeBoxes"/>
        /// entry and every copy is bound to its scope box, instead of one copy per view.</summary>
        public bool            BindToScopeBoxes  { get; set; } = false;
        /// <summary>Scope boxes to fan out across. Only read when <see cref="BindToScopeBoxes"/>.</summary>
        public List<ScopeBoxTarget> SelectedScopeBoxes { get; set; } = new List<ScopeBoxTarget>();

        // ── Callbacks ─────────────────────────────────────────────────
        public Action<string, string>?     PushLog    { get; set; }
        public Action<int, int, int, int>? OnProgress { get; set; }
        public Action<int, int, int>?      OnComplete { get; set; }

        public string GetName() => "LemoineTools.Tools.LinkViews.ViewsBulkDuplicateRunHandler";

        public void Execute(UIApplication app)
        {
            var doc = app.ActiveUIDocument?.Document;
            long __issues0 = DiagnosticsLog.IssueCount;
            int pass = 0, fail = 0, skip = 0;

            try
            {
                if (doc == null)
                {
                    Log(AppStrings.T("linkviews.duplicate.log.noDoc"), "fail");
                    Complete(0, 1, 0);
                    return;
                }

                try { RunDuplicates(doc, ref pass, ref fail, ref skip); }
                catch (Exception ex)
                {
                    DiagnosticsLog.Error("Bulk duplicate views: run aborted", ex);
                    Log(AppStrings.T("linkviews.duplicate.log.error", ex.Message), "fail");
                    fail++;
                }

                Progress(100, pass, fail, skip);
                long __issues = DiagnosticsLog.IssuesSince(__issues0);
                if (__issues > 0) Log(AppStrings.T("linkviews.duplicate.log.nonFatal", __issues), "warn");
                Complete(pass, fail, skip);
            }
            finally
            {
                // Session-long static handler (App.ViewsBulkDuplicateRunHandler) — drop the run's payload.
                SelectedViewIds    = new List<ElementId>();
                SelectedScopeBoxes = new List<ScopeBoxTarget>();
            }
        }

        // ── Main logic ─────────────────────────────────────────────────
        private void RunDuplicates(Document doc, ref int pass, ref int fail, ref int skip)
        {
            var option = MapMode(Mode);

            var views = SelectedViewIds
                .Select(id => doc.GetElement(id) as View)
                .Where(v => v != null && !v.IsTemplate)
                .Cast<View>()
                .ToList();

            if (views.Count == 0)
            {
                Log(AppStrings.T("linkviews.duplicate.log.nothingToDo"), "info");
                return;
            }

            // Per-scope-box mode: one copy per (view × scope box). A run with the mode on but
            // no boxes would otherwise produce nothing with no explanation.
            var boxes = BindToScopeBoxes ? SelectedScopeBoxes : null;
            if (BindToScopeBoxes)
            {
                if (boxes == null || boxes.Count == 0)
                {
                    Log(AppStrings.T("linkviews.duplicate.log.noScopeBoxes"), "info");
                    return;
                }
                Log(AppStrings.T("linkviews.duplicate.log.planScoped",
                    views.Count, boxes.Count, views.Count * boxes.Count), "info");
            }

            // One work item per copy: (source view, scope box or null).
            var items = new List<(View View, ScopeBoxTarget? Box)>();
            foreach (var srcView in views)
            {
                if (BindToScopeBoxes)
                    foreach (var b in boxes!) items.Add((srcView, b));
                else
                    items.Add((srcView, null));
            }

            // Existing view names (unique across the document); track names created this run too.
            var usedNames = new HashSet<string>(
                new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                    .Select(v => v.Name),
                StringComparer.OrdinalIgnoreCase);

            int total = items.Count;
            int done  = 0;

            using (var tx = new Transaction(doc, "Bulk Duplicate Views"))
            {
                ConfigureFailures(tx);
                tx.Start();

                foreach (var item in items)
                {
                    if (RunState.CancelRequested)
                    {
                        Log(AppStrings.T("common.log.stoppedByUser", done, total), "warn");
                        break;   // falls through to the existing tx.Commit() below
                    }

                    View            view = item.View;
                    ScopeBoxTarget? box  = item.Box;

                    var ctx = new TokenContext { Doc = doc, Target = view };
                    ctx.Computed["ViewType"] = ViewsByTemplateRunHandler.ViewTypeLabel(view.ViewType);
                    if (box != null) ctx.Computed["ScopeBoxName"] = box.Name ?? "";
                    string name = TokenResolver.Resolve(NamePattern, ctx, msg => Log(msg, "warn")).Trim();
                    // Fallback keeps the scope box in the name, so sibling copies can't collide.
                    string fallback = box != null ? $"{view.Name} - {box.Name}" : view.Name;
                    name = TokenResolver.GuardDegenerate(name, ctx, fallback, msg => Log(msg, "warn"));

                    done++;
                    Progress((int)(done * 95.0 / Math.Max(total, 1)), pass, fail, skip);

                    if (usedNames.Contains(name))
                    {
                        Log(AppStrings.T("linkviews.duplicate.log.skipExists", name), "info");
                        skip++;
                        continue;
                    }
                    if (!view.CanViewBeDuplicated(option))
                    {
                        Log(AppStrings.T("linkviews.duplicate.log.skipMode", view.Name, Mode.ToLowerInvariant()), "info");
                        skip++;
                        continue;
                    }

                    ElementId newId = ElementId.InvalidElementId;
                    try
                    {
                        newId = view.Duplicate(option);
                        var dup = doc.GetElement(newId) as View;
                        if (dup == null)
                            throw new InvalidOperationException("Duplicate returned no view.");

                        dup.Name = name;

                        usedNames.Add(name);

                        // Bind the scope box AFTER naming: a refusal here still leaves a valid,
                        // correctly-named (if uncropped) view, reported as a warning.
                        if (box != null) AssignScopeBox(dup, box, name);

                        Log(AppStrings.T("linkviews.duplicate.log.created", name), "pass");
                        pass++;
                    }
                    catch (Exception e)
                    {
                        // Naming/duplication failed — remove the orphan copy so the doc stays clean.
                        if (newId != ElementId.InvalidElementId)
                        {
                            try { doc.Delete(newId); }
                            catch (Exception delEx)
                            {
                                DiagnosticsLog.Swallowed(
                                    $"Bulk duplicate views: delete orphan duplicate {newId.Value}", delEx);
                            }
                        }
                        Log(AppStrings.T("linkviews.duplicate.log.failed", view.Name, e.Message), "fail");
                        fail++;
                    }
                }

                tx.Commit();
            }

            Log(AppStrings.T("linkviews.duplicate.log.complete", pass, skip, fail), "pass");
        }

        /// <summary>
        /// Binds the new view to its scope box. Assigning the Scope Box parameter is the WHOLE
        /// operation — Revit derives the view's crop from the box ("live extents"), so nothing
        /// writes <c>View.CropBox</c> here. (A scope box's bounds are world-space while
        /// <c>CropBox.Min/Max</c> are view-local, so copying them across would mis-place the crop.)
        /// Same approach as LinkViewsLevelRunHandler, which already creates scope-box-bound views.
        ///
        /// A view type that refuses the parameter, or a template that locks it, leaves a valid
        /// uncropped view — reported as a warning rather than failing the item.
        /// </summary>
        private void AssignScopeBox(View view, ScopeBoxTarget box, string viewName)
        {
            try
            {
                var p = view.get_Parameter(BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP);
                if (p == null || p.IsReadOnly)
                    Log(AppStrings.T("linkviews.duplicate.log.boxAssignRefused", viewName, box.Name,
                        AppStrings.T("linkviews.duplicate.log.boxParamUnavailable")), "warn");
                // Parameter.Set reports success as a bool — an ignored false would leave the
                // view silently unbound, which is the one thing this mode exists to do.
                else if (!p.Set(box.Id))
                    Log(AppStrings.T("linkviews.duplicate.log.boxAssignRefused", viewName, box.Name,
                        AppStrings.T("linkviews.duplicate.log.boxSetRejected")), "warn");
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed(
                    $"Bulk duplicate views: assign scope box {box.Id.Value} to view {view.Id.Value}", ex);
                Log(AppStrings.T("linkviews.duplicate.log.boxAssignRefused", viewName, box.Name, ex.Message), "warn");
            }
        }

        private static ViewDuplicateOption MapMode(string mode)
        {
            if (mode == ViewsBulkDuplicateViewModel.ModeDuplicate)   return ViewDuplicateOption.Duplicate;
            if (mode == ViewsBulkDuplicateViewModel.ModeAsDependent) return ViewDuplicateOption.AsDependent;
            return ViewDuplicateOption.WithDetailing; // default / ModeWithDetailing
        }

        private static void ConfigureFailures(Transaction tx)
        {
            var opts = tx.GetFailureHandlingOptions();
            opts.SetClearAfterRollback(true);
            opts.SetDelayedMiniWarnings(true);
            tx.SetFailureHandlingOptions(opts);
        }

        private void Log(string t, string s)             => PushLog?.Invoke(t, s);
        private void Progress(int p, int pa, int f, int s) => OnProgress?.Invoke(p, pa, f, s);
        private void Complete(int p, int f, int s)        => OnComplete?.Invoke(p, f, s);
    }
}
