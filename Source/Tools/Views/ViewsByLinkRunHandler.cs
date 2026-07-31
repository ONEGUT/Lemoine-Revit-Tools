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
    /// Bulk Views — "By Link" mode. Creates one 3D view per selected Revit link showing ONLY
    /// that link (all other links hidden, and every host model/annotation/analytical/imported
    /// category hidden), so the view isolates a single linked file. Uses
    /// <see cref="View.SetLinkOverrides"/> / <see cref="RevitLinkGraphicsSettings"/>, which are
    /// present from Revit 2024 onward (verified against the checked-in RevitAPI.dll).
    ///
    /// The per-view sequence is show-all → switch link to Custom → hide host, and that order is
    /// load-bearing: a link moved to <see cref="LinkVisibility.Custom"/> inherits the host view's
    /// category visibility AT THAT MOMENT as its own independent set. Hiding the host's
    /// categories first therefore gives the link an all-unchecked set and it renders nothing.
    /// <see cref="RevitLinkGraphicsSettings"/> carries only LinkVisibilityType and LinkedViewId,
    /// so that inherited snapshot is the only way to reach the link's model categories at all.
    /// </summary>
    public sealed class ViewsByLinkRunHandler : IExternalEventHandler
    {
        public List<ElementId> LinkInstanceIds { get; set; } = new List<ElementId>();
        public string           NamePattern     { get; set; } = "{LinkName}";
        public ElementId        TemplateId      { get; set; } = ElementId.InvalidElementId;

        public Action<string, string>?     PushLog    { get; set; }
        public Action<int, int, int, int>? OnProgress { get; set; }
        public Action<int, int, int>?      OnComplete { get; set; }

        public string GetName() => "LemoineTools.Tools.LinkViews.ViewsByLinkRunHandler";

        public void Execute(UIApplication app)
        {
            var doc = app.ActiveUIDocument.Document;
            int pass = 0, fail = 0, skip = 0;
            try { Run(doc, ref pass, ref fail, ref skip); }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("ViewsByLink: run aborted", ex);
                Log(AppStrings.T("linkviews.bulkViews.log.byLinkError", ex.Message), "fail");
                fail++;
            }
            finally
            {
                LinkInstanceIds = new List<ElementId>();
            }
            Progress(100, pass, fail, skip);
            Complete(pass, fail, skip);
        }

        private void Run(Document doc, ref int pass, ref int fail, ref int skip)
        {
            if (LinkInstanceIds == null || LinkInstanceIds.Count == 0)
            {
                Log(AppStrings.T("linkviews.bulkViews.log.byLinkNoLinks"), "fail");
                fail++;
                return;
            }

            var all3dType = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .FirstOrDefault(t => t.ViewFamily == ViewFamily.ThreeDimensional);
            if (all3dType == null)
            {
                Log(AppStrings.T("linkviews.bulkViews.log.byLinkNo3dType"), "fail");
                fail++;
                return;
            }

            var allLinkInstances = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>().ToList();

            // The RVT Links category governs whether ANY link renders in the view, so it must
            // survive the host-category blackout below — hiding it blanks the very link the
            // view exists to show.
            ElementId? rvtLinksCatId = null;
            try { rvtLinksCatId = Category.GetCategory(doc, BuiltInCategory.OST_RvtLinks)?.Id; }
            catch (Exception ex) { DiagnosticsLog.Swallowed("ViewsByLink: resolve RVT Links category", ex); }
            if (rvtLinksCatId == null)
                rvtLinksCatId = new ElementId(BuiltInCategory.OST_RvtLinks);

            int total = LinkInstanceIds.Count, done = 0;
            using (var tx = new Transaction(doc, "Create By-Link Views"))
            {
                var opts = tx.GetFailureHandlingOptions();
                opts.SetClearAfterRollback(true);
                opts.SetDelayedMiniWarnings(true);
                tx.SetFailureHandlingOptions(opts);
                tx.Start();

                foreach (var linkId in LinkInstanceIds)
                {
                    if (RunState.CancelRequested)
                    {
                        Log(AppStrings.T("common.log.stoppedByUser", done, total), "warn");
                        break;
                    }

                    var link = doc.GetElement(linkId) as RevitLinkInstance;
                    var linkDoc = link?.GetLinkDocument();
                    string linkName = linkDoc != null
                        ? System.IO.Path.GetFileNameWithoutExtension(linkDoc.Title)
                        : link?.Name ?? linkId.Value.ToString();

                    try
                    {
                        var ctx = new TokenContext { Doc = doc, Source = link };
                        ctx.Computed["LinkName"] = linkName;
                        string viewName = TokenResolver.Resolve(NamePattern, ctx, msg => Log(msg, "warn"));
                        viewName = TokenResolver.GuardDegenerate(viewName, ctx, linkName, msg => Log(msg, "warn"));

                        var view = View3D.CreateIsometric(doc, all3dType.Id);
                        try { view.Name = viewName; }
                        catch (Exception ex) { DiagnosticsLog.Swallowed($"ViewsByLink: name conflict for '{viewName}'", ex); }

                        if (TemplateId != ElementId.InvalidElementId)
                        {
                            try { view.ViewTemplateId = TemplateId; }
                            catch (Exception ex) { DiagnosticsLog.Swallowed($"ViewsByLink: apply template to '{viewName}'", ex); }
                        }

                        // ---- Ordering matters, and it is the whole fix -------------------
                        // A link switched to LinkVisibility.Custom takes the host view's CURRENT
                        // category visibility as the starting state for its own (thereafter
                        // independent) category set. RevitLinkGraphicsSettings exposes only
                        // LinkVisibilityType and LinkedViewId — the "Model categories: <Custom>"
                        // checkbox grid inside RVT Link Display Settings has no API surface at
                        // all — so that inherited snapshot is the only lever available.
                        //
                        // Hiding the host's categories BEFORE the switch therefore hands the link
                        // an all-unchecked category set and the link renders nothing, which is
                        // exactly the bug this order fixes. So: show everything, flip the link to
                        // Custom while it is all visible, and only then black out the host.

                        // 1. Show every category (and sub-category) in the view.
                        int notShown = ShowAllCategories(doc, view, viewName);

                        // 2. Flip the link to Custom while the view is still all-visible, so the
                        //    link's own category set is captured fully checked.
                        bool customOverrideApplied = false;
                        if (link != null)
                            customOverrideApplied = ApplyCustomLinkDisplay(view, linkId, viewName);

                        // 3. Hide every OTHER link instance. Element-level hiding, so it does not
                        //    disturb the category state captured above.
                        var othersToHide = allLinkInstances
                            .Where(li => li.Id.Value != linkId.Value && li.CanBeHidden(view))
                            .Select(li => li.Id).ToList();
                        if (othersToHide.Count > 0)
                        {
                            try { view.HideElements(othersToHide); }
                            catch (Exception ex) { DiagnosticsLog.Swallowed($"ViewsByLink: hide other links in '{viewName}'", ex); }
                        }

                        // 4. Now black out the host's own categories. The target link keeps its
                        //    own (all-checked) set from step 2.
                        int lockedOut = HideHostCategories(doc, view, rvtLinksCatId, viewName, out int hideAttempted);

                        if (notShown > 0)
                            Log(AppStrings.T("linkviews.bulkViews.log.byLinkShowAllFailed", viewName, notShown), "warn");
                        if (!customOverrideApplied)
                            Log(AppStrings.T("linkviews.bulkViews.log.byLinkNoOverride", viewName), "warn");
                        if (lockedOut > 0)
                            Log(AppStrings.T("linkviews.bulkViews.log.byLinkCategoriesLocked",
                                             viewName, lockedOut, hideAttempted), "warn");

                        pass++;
                        Log(AppStrings.T("linkviews.bulkViews.log.byLinkCreated", viewName, linkName), "pass");
                    }
                    catch (Exception ex)
                    {
                        fail++;
                        DiagnosticsLog.Error($"ViewsByLink: create view for link '{linkName}'", ex);
                        Log(AppStrings.T("linkviews.bulkViews.log.byLinkFailed", linkName, ex.Message), "fail");
                    }

                    done++;
                    Progress((int)(done * 100.0 / total), pass, fail, skip);
                }

                tx.Commit();
            }

            Log(AppStrings.T("linkviews.bulkViews.log.byLinkDone", pass, fail), pass > 0 ? "pass" : "warn");
        }

        /// <summary>
        /// Checks every category (and sub-category) on in the view, so a link switched to
        /// <see cref="LinkVisibility.Custom"/> straight afterwards inherits a fully-checked
        /// category set of its own. Returns the number of MODEL categories that were still
        /// hidden afterwards — non-zero means the capture in step 2 cannot be trusted, because
        /// the link will inherit an incomplete set (normally a template-governed view).
        /// </summary>
        private static int ShowAllCategories(Document doc, View view, string viewName)
        {
            // Group master switches first — an individual category cannot be un-hidden while
            // its whole group is switched off.
            try
            {
                view.AreModelCategoriesHidden           = false;
                view.AreAnnotationCategoriesHidden      = false;
                view.AreAnalyticalModelCategoriesHidden = false;
                view.AreImportCategoriesHidden          = false;
            }
            catch (Exception ex) { DiagnosticsLog.Swallowed($"ViewsByLink: show category groups in '{viewName}'", ex); }

            foreach (Category cat in doc.Settings.Categories)
            {
                if (cat == null) continue;
                TrySetCategoryHidden(view, cat, false, viewName);

                CategoryNameMap? subs = null;
                try { subs = cat.SubCategories; }
                catch (Exception ex) { DiagnosticsLog.Swallowed($"ViewsByLink: read sub-categories of {cat.Id.Value} in '{viewName}'", ex); }
                if (subs == null) continue;

                foreach (Category sub in subs)
                    TrySetCategoryHidden(view, sub, false, viewName);
            }

            // Read back the model categories — this pass is the precondition for the whole
            // isolate-a-link approach, so a silently-refused un-hide must reach the run log.
            int stillHidden = 0;
            foreach (Category cat in doc.Settings.Categories)
            {
                if (cat == null || cat.CategoryType != CategoryType.Model) continue;
                try
                {
                    if (!cat.get_AllowsVisibilityControl(view)) continue;
                    if (view.GetCategoryHidden(cat.Id)) stillHidden++;
                }
                catch (Exception ex) { DiagnosticsLog.Swallowed($"ViewsByLink: verify show of {cat.Id.Value} in '{viewName}'", ex); }
            }
            return stillHidden;
        }

        /// <summary>
        /// Hides every host MODEL category except RVT Links, plus the annotation, analytical and
        /// imported groups. Returns the number of categories that were still visible after the
        /// write (i.e. the view refused the override — normally because a view template governs
        /// its Visibility/Graphics); <paramref name="attempted"/> receives the number tried.
        /// </summary>
        private static int HideHostCategories(Document doc, View view, ElementId? rvtLinksCatId, string viewName, out int attempted)
        {
            attempted = 0;
            int stillVisible = 0;

            // NOTE: AreModelCategoriesHidden is deliberately left FALSE — the host is blacked
            // out category by category instead. That property is the master switch for the whole
            // model group ("Show model categories in this view"), and setting it true risks
            // taking the RVT Links category, and therefore the target link, down with the host.
            try
            {
                view.AreAnnotationCategoriesHidden      = true;
                view.AreAnalyticalModelCategoriesHidden = true;
                view.AreImportCategoriesHidden          = true;
            }
            catch (Exception ex) { DiagnosticsLog.Swallowed($"ViewsByLink: hide category groups in '{viewName}'", ex); }

            foreach (Category cat in doc.Settings.Categories)
            {
                if (cat == null) continue;
                if (cat.CategoryType != CategoryType.Model) continue;
                if (rvtLinksCatId != null && cat.Id.Value == rvtLinksCatId.Value) continue;

                bool allows;
                try { allows = cat.get_AllowsVisibilityControl(view); }
                catch (Exception ex)
                {
                    DiagnosticsLog.Swallowed($"ViewsByLink: query visibility control of {cat.Id.Value} in '{viewName}'", ex);
                    continue;
                }
                if (!allows) continue;

                attempted++;
                TrySetCategoryHidden(view, cat, true, viewName);

                // Read back — a template-governed view can refuse the write without throwing,
                // and a silently-ignored hide is indistinguishable from a working one otherwise.
                try { if (!view.GetCategoryHidden(cat.Id)) stillVisible++; }
                catch (Exception ex) { DiagnosticsLog.Swallowed($"ViewsByLink: verify hide of {cat.Id.Value} in '{viewName}'", ex); }
            }

            return stillVisible;
        }

        private static void TrySetCategoryHidden(View view, Category cat, bool hide, string viewName)
        {
            if (cat == null) return;
            try
            {
                if (!cat.get_AllowsVisibilityControl(view)) return;
                if (view.GetCategoryHidden(cat.Id) == hide) return;
                view.SetCategoryHidden(cat.Id, hide);
            }
            catch (Exception ex)
            {
                string verb = hide ? "hide" : "show";
                DiagnosticsLog.Swallowed($"ViewsByLink: {verb} category {cat.Id.Value} in '{viewName}'", ex);
            }
        }

        /// <summary>
        /// Switches the link to <see cref="LinkVisibility.Custom"/> and reads the setting back to
        /// confirm it stuck — <c>SetLinkOverrides</c> can be refused without throwing.
        /// </summary>
        private static bool ApplyCustomLinkDisplay(View view, ElementId linkId, string viewName)
        {
            try
            {
                var settings = new RevitLinkGraphicsSettings
                {
                    LinkVisibilityType = LinkVisibility.Custom,
                };
                view.SetLinkOverrides(linkId, settings);
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed($"ViewsByLink: set link overrides for '{viewName}'", ex);
                return false;
            }

            try
            {
                RevitLinkGraphicsSettings? readBack = view.GetLinkOverrides(linkId);
                return readBack != null && readBack.LinkVisibilityType == LinkVisibility.Custom;
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed($"ViewsByLink: verify link overrides for '{viewName}'", ex);
                return false;
            }
        }

        private void Log(string t, string s) => PushLog?.Invoke(t, s);
        private void Progress(int p, int pa, int f, int s) => OnProgress?.Invoke(p, pa, f, s);
        private void Complete(int p, int f, int s) => OnComplete?.Invoke(p, f, s);
    }
}
