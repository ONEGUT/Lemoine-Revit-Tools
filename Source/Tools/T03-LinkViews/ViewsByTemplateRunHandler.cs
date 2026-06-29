using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;
using LemoineTools.Lemoine.Controls;

namespace LemoineTools.Tools.LinkViews
{
    /// <summary>
    /// <see cref="IExternalEventHandler"/> that cross-multiplies the selected source views by
    /// the selected view templates. For each view×template pair it duplicates the view (With
    /// Detailing), applies the template, and renames the duplicate from the token pattern.
    /// The ViewModel sets the public inputs before calling <c>Raise()</c>.
    /// </summary>
    public sealed class ViewsByTemplateRunHandler : IExternalEventHandler
    {
        // ── Inputs set before Raise() ─────────────────────────────────
        /// <summary>Source views to duplicate. Each is duplicated once per template.</summary>
        public List<ElementId> SelectedViewIds     { get; set; } = new List<ElementId>();
        /// <summary>View templates to apply, one duplicate per template per view.</summary>
        public List<ElementId> SelectedTemplateIds { get; set; } = new List<ElementId>();
        /// <summary>Token pattern for the duplicate's name ({ViewName}, {TemplateName}, {ViewType}).</summary>
        public string          NamePattern         { get; set; } = "{ViewName} - {TemplateName}";

        // ── Callbacks ─────────────────────────────────────────────────
        public Action<string, string>?     PushLog    { get; set; }
        public Action<int, int, int, int>? OnProgress { get; set; }
        public Action<int, int, int>?      OnComplete { get; set; }

        public string GetName() => "LemoineTools.Tools.LinkViews.ViewsByTemplateRunHandler";

        public void Execute(UIApplication app)
        {
            var doc = app.ActiveUIDocument?.Document;
            long __issues0 = LemoineLog.IssueCount;
            int pass = 0, fail = 0, skip = 0;

            try
            {
                if (doc == null)
                {
                    Log("No active Revit document.", "fail");
                    Complete(0, 1, 0);
                    return;
                }

                try { RunPairs(doc, ref pass, ref fail, ref skip); }
                catch (Exception ex)
                {
                    LemoineLog.Error("Views by template: run aborted", ex);
                    Log($"Error: {ex.Message}", "fail");
                    fail++;
                }

                Progress(100, pass, fail, skip);
                long __issues = LemoineLog.IssuesSince(__issues0);
                if (__issues > 0) Log($"{__issues} non-fatal issue(s) recorded — see diagnostics log.", "warn");
                Complete(pass, fail, skip);
            }
            finally
            {
                // Session-long static handler (App.ViewsByTemplateRunHandler) — drop the run's payload.
                SelectedViewIds     = new List<ElementId>();
                SelectedTemplateIds = new List<ElementId>();
            }
        }

        // ── Main logic ─────────────────────────────────────────────────
        private void RunPairs(Document doc, ref int pass, ref int fail, ref int skip)
        {
            var views = SelectedViewIds
                .Select(id => doc.GetElement(id) as View)
                .Where(v => v != null && !v.IsTemplate)
                .Cast<View>()
                .ToList();

            var templates = SelectedTemplateIds
                .Select(id => doc.GetElement(id) as View)
                .Where(v => v != null && v.IsTemplate)
                .Cast<View>()
                .ToList();

            if (views.Count == 0 || templates.Count == 0)
            {
                Log("Nothing to do — no valid views or templates selected.", "info");
                return;
            }

            // Existing view names (unique across the document); track names created this run too.
            var usedNames = new HashSet<string>(
                new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                    .Select(v => v.Name),
                StringComparer.OrdinalIgnoreCase);

            int total = views.Count * templates.Count;
            int done  = 0;

            using (var tx = new Transaction(doc, "Bulk Views by Template"))
            {
                ConfigureFailures(tx);
                tx.Start();

                bool cancelled = false;
                foreach (var view in views)
                {
                    if (cancelled) break;
                    foreach (var template in templates)
                    {
                        if (LemoineRun.CancelRequested)
                        {
                            Log($"Stopped by user — {done} of {total} processed; work so far preserved.", "warn");
                            cancelled = true;
                            break;   // breaks inner loop; outer loop guard breaks too → existing tx.Commit() runs
                        }

                        string name = LemoineTokenInput.Resolve(NamePattern,
                            new Dictionary<string, string>
                            {
                                ["ViewName"]     = view.Name,
                                ["ViewType"]     = ViewTypeLabel(view.ViewType),
                                ["TemplateName"] = template.Name,
                            }).Trim();

                        done++;
                        Progress((int)(done * 95.0 / Math.Max(total, 1)), pass, fail, skip);

                        if (string.IsNullOrWhiteSpace(name))
                        {
                            Log($"Skip '{view.Name}' × '{template.Name}' — empty name.", "info");
                            skip++;
                            continue;
                        }
                        if (usedNames.Contains(name))
                        {
                            Log($"Skip '{name}' (name already exists).", "info");
                            skip++;
                            continue;
                        }
                        if (!view.CanViewBeDuplicated(ViewDuplicateOption.WithDetailing))
                        {
                            Log($"Skip '{view.Name}' — view cannot be duplicated.", "info");
                            skip++;
                            continue;
                        }

                        ElementId newId = ElementId.InvalidElementId;
                        try
                        {
                            newId = view.Duplicate(ViewDuplicateOption.WithDetailing);
                            var dup = doc.GetElement(newId) as View;
                            if (dup == null)
                                throw new InvalidOperationException("Duplicate returned no view.");

                            dup.ViewTemplateId = template.Id;   // throws if template type ≠ view type
                            dup.Name           = name;

                            usedNames.Add(name);
                            Log($"Created '{name}'  ({template.Name})", "pass");
                            pass++;
                        }
                        catch (Exception e)
                        {
                            // Apply failed (e.g. incompatible template) — remove the orphan duplicate.
                            if (newId != ElementId.InvalidElementId)
                            {
                                try { doc.Delete(newId); }
                                catch (Exception delEx)
                                {
                                    LemoineLog.Swallowed(
                                        $"Views by template: delete orphan duplicate {newId.Value}", delEx);
                                }
                            }
                            Log($"Failed '{view.Name}' × '{template.Name}': {e.Message}", "fail");
                            fail++;
                        }
                    }
                }

                tx.Commit();
            }

            Log($"Complete — {pass} created, {skip} skipped, {fail} failed.", "pass");
        }

        // Friendly label used for the {ViewType} token, tab grouping, and skip/fail messages.
        internal static string ViewTypeLabel(ViewType vt)
        {
            switch (vt)
            {
                case ViewType.FloorPlan:      return "Floor Plan";
                case ViewType.CeilingPlan:    return "Ceiling Plan";
                case ViewType.EngineeringPlan:return "Structural Plan";
                case ViewType.AreaPlan:       return "Area Plan";
                case ViewType.Section:        return "Section";
                case ViewType.Elevation:      return "Elevation";
                case ViewType.Detail:         return "Detail";
                case ViewType.ThreeD:         return "3D View";
                case ViewType.DraftingView:   return "Drafting View";
                case ViewType.Rendering:      return "Rendering";
                case ViewType.Walkthrough:    return "Walkthrough";
                case ViewType.Legend:         return "Legend";
                default:                      return vt.ToString();
            }
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
