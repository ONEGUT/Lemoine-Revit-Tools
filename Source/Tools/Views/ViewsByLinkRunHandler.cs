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
    /// Each link is displayed with <see cref="LinkVisibility.ByLinkView"/> aimed at a 3D view in
    /// the link's own document. <see cref="LinkVisibility.Custom"/> does NOT work here: a Custom
    /// link's "Model categories" dropdown always defaults to &lt;By host view&gt; and no API can
    /// change it (<see cref="RevitLinkGraphicsSettings"/> carries only LinkVisibilityType and
    /// LinkedViewId, and nothing else in RevitAPI.dll reaches a link's per-category visibility),
    /// so a Custom link inherits the host category hides this tool applies and renders nothing.
    /// ByLinkView is the only mode the host view's category hides cannot reach.
    /// </summary>
    public sealed class ViewsByLinkRunHandler : IExternalEventHandler
    {
        // Internal mode tokens compared with ==; not user-facing text.
        private const string LinkModeByLinkView = "ByLinkView";
        private const string LinkModeCustom     = "Custom";
        private const string LinkModeNone       = "None";

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

                        // ---- Why ByLinkView and not Custom --------------------------------
                        // A link on LinkVisibility.Custom keeps its "Model categories" dropdown on
                        // <By host view>, and that dropdown ALWAYS defaults there no matter what
                        // the host view looks like when the switch happens. It has no API surface
                        // whatsoever — RevitLinkGraphicsSettings carries only LinkVisibilityType
                        // and LinkedViewId, and nothing anywhere else in RevitAPI.dll reaches a
                        // link's per-category visibility (verified against the assembly metadata).
                        // So a Custom link still inherits the host's category hides applied below,
                        // and the view renders nothing.
                        //
                        // ByLinkView is the one mode that genuinely detaches the link: it renders
                        // using a view from the link's OWN document, whose visibility settings the
                        // host view's category hides cannot reach.

                        // 1. Point the link at one of its own 3D views.
                        string linkMode = ApplyLinkDisplay(view, linkId, linkDoc, viewName, out string linkedViewName);

                        // 2. Hide every OTHER link instance. Element-level hiding, so it does not
                        //    disturb the link display settings applied above.
                        var othersToHide = allLinkInstances
                            .Where(li => li.Id.Value != linkId.Value && li.CanBeHidden(view))
                            .Select(li => li.Id).ToList();
                        if (othersToHide.Count > 0)
                        {
                            try { view.HideElements(othersToHide); }
                            catch (Exception ex) { DiagnosticsLog.Swallowed($"ViewsByLink: hide other links in '{viewName}'", ex); }
                        }

                        // 3. Now black out the host's own categories. A ByLinkView link is
                        //    unaffected; RVT Links itself is deliberately left visible.
                        int lockedOut = HideHostCategories(doc, view, rvtLinksCatId, viewName, out int hideAttempted);

                        if (linkMode == LinkModeNone)
                            Log(AppStrings.T("linkviews.bulkViews.log.byLinkNoOverride", viewName), "warn");
                        else if (linkMode == LinkModeCustom)
                            Log(AppStrings.T("linkviews.bulkViews.log.byLinkFellBackToCustom", viewName), "warn");
                        if (lockedOut > 0)
                            Log(AppStrings.T("linkviews.bulkViews.log.byLinkCategoriesLocked",
                                             viewName, lockedOut, hideAttempted), "warn");

                        pass++;
                        Log(linkMode == LinkModeByLinkView
                                ? AppStrings.T("linkviews.bulkViews.log.byLinkCreatedByLinkView", viewName, linkName, linkedViewName)
                                : AppStrings.T("linkviews.bulkViews.log.byLinkCreated", viewName, linkName), "pass");
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
        /// Puts the link on <see cref="LinkVisibility.ByLinkView"/> pointed at one of its own 3D
        /// views, so the host view's category hides cannot reach it. Falls back to
        /// <see cref="LinkVisibility.Custom"/> when the link has no usable 3D view of its own —
        /// which leaves its "Model categories" dropdown on &lt;By host view&gt; (there is no API
        /// for that dropdown), so the caller warns that the view needs a manual fix.
        /// Returns <see cref="LinkModeByLinkView"/>, <see cref="LinkModeCustom"/> or
        /// <see cref="LinkModeNone"/>.
        /// </summary>
        private static string ApplyLinkDisplay(View view, ElementId linkId, Document? linkDoc, string viewName, out string linkedViewName)
        {
            linkedViewName = "";

            View3D? linkedView = linkDoc != null ? PickLinkedView3D(linkDoc, viewName) : null;
            if (linkedView != null)
            {
                try
                {
                    // LinkedViewId is assigned FIRST — setting the mode to ByLinkView while the
                    // view id is still invalid is rejected.
                    var settings = new RevitLinkGraphicsSettings
                    {
                        LinkedViewId       = linkedView.Id,
                        LinkVisibilityType = LinkVisibility.ByLinkView,
                    };
                    view.SetLinkOverrides(linkId, settings);

                    if (LinkModeIs(view, linkId, LinkVisibility.ByLinkView, viewName))
                    {
                        try { linkedViewName = linkedView.Name; }
                        catch (Exception ex) { DiagnosticsLog.Swallowed($"ViewsByLink: read linked view name for '{viewName}'", ex); }
                        return LinkModeByLinkView;
                    }
                }
                catch (Exception ex)
                {
                    DiagnosticsLog.Swallowed($"ViewsByLink: set ByLinkView overrides for '{viewName}'", ex);
                }
            }

            try
            {
                var settings = new RevitLinkGraphicsSettings
                {
                    LinkVisibilityType = LinkVisibility.Custom,
                };
                view.SetLinkOverrides(linkId, settings);
                if (LinkModeIs(view, linkId, LinkVisibility.Custom, viewName))
                    return LinkModeCustom;
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed($"ViewsByLink: set Custom overrides for '{viewName}'", ex);
            }

            return LinkModeNone;
        }

        /// <summary>
        /// Picks the 3D view inside a linked document that is most likely to show the whole model:
        /// orthographic over perspective, no section box over cropped, and Revit's default
        /// <c>{3D}</c> view ahead of the rest.
        /// </summary>
        private static View3D? PickLinkedView3D(Document linkDoc, string viewName)
        {
            List<View3D> candidates;
            try
            {
                candidates = new FilteredElementCollector(linkDoc)
                    .OfClass(typeof(View3D)).Cast<View3D>()
                    .Where(v => v != null && !v.IsTemplate)
                    .ToList();
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed($"ViewsByLink: collect 3D views in the link document for '{viewName}'", ex);
                return null;
            }

            if (candidates.Count == 0) return null;

            return candidates
                .OrderBy(v => SafeIsPerspective(v) ? 1 : 0)
                .ThenBy(v => SafeIsSectionBoxActive(v) ? 1 : 0)
                .ThenBy(v => SafeName(v).StartsWith("{3D", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(v => SafeName(v), NaturalOrderComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }

        private static bool SafeIsPerspective(View3D v)
        {
            try { return v.IsPerspective; }
            catch (Exception ex) { DiagnosticsLog.Swallowed($"ViewsByLink: read IsPerspective of linked view {v.Id.Value}", ex); return false; }
        }

        private static bool SafeIsSectionBoxActive(View3D v)
        {
            try { return v.IsSectionBoxActive; }
            catch (Exception ex) { DiagnosticsLog.Swallowed($"ViewsByLink: read IsSectionBoxActive of linked view {v.Id.Value}", ex); return false; }
        }

        private static string SafeName(View3D v)
        {
            try { return v.Name ?? ""; }
            catch (Exception ex) { DiagnosticsLog.Swallowed("ViewsByLink: read linked view name", ex); return ""; }
        }

        /// <summary>
        /// Reads the link's display settings back — <c>SetLinkOverrides</c> can be refused without
        /// throwing, and a silently-ignored write would look identical to a working one.
        /// </summary>
        private static bool LinkModeIs(View view, ElementId linkId, LinkVisibility expected, string viewName)
        {
            try
            {
                RevitLinkGraphicsSettings? readBack = view.GetLinkOverrides(linkId);
                return readBack != null && readBack.LinkVisibilityType == expected;
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
