using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Framework.Controls;

namespace LemoineTools.Tools.LinkViews
{
    /// <summary>
    /// Bulk Views — "By Link" mode. Creates one 3D view per selected Revit link showing ONLY
    /// that link (all other links hidden, and every host model/annotation/analytical/imported
    /// category hidden), so the view isolates a single linked file. Uses
    /// <see cref="View.SetLinkOverrides"/> / <see cref="RevitLinkGraphicsSettings"/>, which are
    /// present from Revit 2024 onward (verified against the checked-in RevitAPI.dll).
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
                        string viewName = TokenInput.Resolve(NamePattern,
                            new Dictionary<string, string> { ["LinkName"] = linkName });
                        if (string.IsNullOrWhiteSpace(viewName)) viewName = linkName;

                        var view = View3D.CreateIsometric(doc, all3dType.Id);
                        try { view.Name = viewName; }
                        catch (Exception ex) { DiagnosticsLog.Swallowed($"ViewsByLink: name conflict for '{viewName}'", ex); }

                        if (TemplateId != ElementId.InvalidElementId)
                        {
                            try { view.ViewTemplateId = TemplateId; }
                            catch (Exception ex) { DiagnosticsLog.Swallowed($"ViewsByLink: apply template to '{viewName}'", ex); }
                        }

                        // Hide every OTHER link instance.
                        var othersToHide = allLinkInstances
                            .Where(li => li.Id.Value != linkId.Value && li.CanBeHidden(view))
                            .Select(li => li.Id).ToList();
                        if (othersToHide.Count > 0)
                        {
                            try { view.HideElements(othersToHide); }
                            catch (Exception ex) { DiagnosticsLog.Swallowed($"ViewsByLink: hide other links in '{viewName}'", ex); }
                        }

                        // Hide every host model/annotation/analytical/imported category so only
                        // the target link's own geometry remains visible. LinkVisibility.Custom
                        // detaches the link's category visibility from the host view's — with no
                        // per-category hides set on the settings object below, the link renders
                        // normally regardless of the host category hides applied further down.
                        // (LinkVisibility has only ByHostView/Custom in the 2024 API — there is no
                        // "render using one of the link's own views" mode.)
                        bool customOverrideApplied = false;
                        if (link != null)
                        {
                            try
                            {
                                var settings = new RevitLinkGraphicsSettings
                                {
                                    LinkVisibilityType = LinkVisibility.Custom,
                                };
                                view.SetLinkOverrides(linkId, settings);
                                customOverrideApplied = true;
                            }
                            catch (Exception ex)
                            {
                                DiagnosticsLog.Swallowed($"ViewsByLink: set link overrides for '{viewName}'", ex);
                            }
                        }

                        try
                        {
                            view.AreAnnotationCategoriesHidden      = true;
                            view.AreAnalyticalModelCategoriesHidden = true;
                            view.AreImportCategoriesHidden          = true;
                            foreach (Category cat in doc.Settings.Categories)
                            {
                                if (cat.CategoryType != CategoryType.Model) continue;
                                if (!cat.get_AllowsVisibilityControl(view)) continue;
                                try { view.SetCategoryHidden(cat.Id, true); }
                                catch (Exception ex) { DiagnosticsLog.Swallowed($"ViewsByLink: hide category {cat.Id.Value} in '{viewName}'", ex); }
                            }
                        }
                        catch (Exception ex) { DiagnosticsLog.Swallowed($"ViewsByLink: hide host categories in '{viewName}'", ex); }

                        if (!customOverrideApplied)
                            Log(AppStrings.T("linkviews.bulkViews.log.byLinkNoOverride", viewName), "warn");

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

        private void Log(string t, string s) => PushLog?.Invoke(t, s);
        private void Progress(int p, int pa, int f, int s) => OnProgress?.Invoke(p, pa, f, s);
        private void Complete(int p, int f, int s) => OnComplete?.Invoke(p, f, s);
    }
}
