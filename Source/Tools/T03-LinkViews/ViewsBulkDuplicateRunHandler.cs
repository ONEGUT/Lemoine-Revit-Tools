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
    /// <see cref="IExternalEventHandler"/> that bulk-duplicates the selected views using the
    /// chosen <see cref="ViewDuplicateOption"/> and names each copy from the token pattern.
    /// The ViewModel sets the public inputs before calling <c>Raise()</c>.
    /// </summary>
    public sealed class ViewsBulkDuplicateRunHandler : IExternalEventHandler
    {
        // ── Inputs set before Raise() ─────────────────────────────────
        /// <summary>Source views to duplicate (one copy each).</summary>
        public List<ElementId> SelectedViewIds { get; set; } = new List<ElementId>();
        /// <summary>Duplicate-mode label (one of the ViewsBulkDuplicateViewModel.Mode* constants).</summary>
        public string          Mode            { get; set; } = ViewsBulkDuplicateViewModel.ModeWithDetailing;
        /// <summary>Token pattern for the copy's name ({ViewName}, {ViewType}).</summary>
        public string          NamePattern     { get; set; } = "{ViewName} - Copy";

        // ── Callbacks ─────────────────────────────────────────────────
        public Action<string, string>?     PushLog    { get; set; }
        public Action<int, int, int, int>? OnProgress { get; set; }
        public Action<int, int, int>?      OnComplete { get; set; }

        public string GetName() => "LemoineTools.Tools.LinkViews.ViewsBulkDuplicateRunHandler";

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

                try { RunDuplicates(doc, ref pass, ref fail, ref skip); }
                catch (Exception ex)
                {
                    LemoineLog.Error("Bulk duplicate views: run aborted", ex);
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
                // Session-long static handler (App.ViewsBulkDuplicateRunHandler) — drop the run's payload.
                SelectedViewIds = new List<ElementId>();
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
                Log("Nothing to do — no valid views selected.", "info");
                return;
            }

            // Existing view names (unique across the document); track names created this run too.
            var usedNames = new HashSet<string>(
                new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                    .Select(v => v.Name),
                StringComparer.OrdinalIgnoreCase);

            int total = views.Count;
            int done  = 0;

            using (var tx = new Transaction(doc, "Bulk Duplicate Views"))
            {
                ConfigureFailures(tx);
                tx.Start();

                foreach (var view in views)
                {
                    if (LemoineRun.CancelRequested)
                    {
                        Log($"Stopped by user — {done} of {total} processed; work so far preserved.", "warn");
                        break;   // falls through to the existing tx.Commit() below
                    }

                    string name = LemoineTokenInput.Resolve(NamePattern,
                        new Dictionary<string, string>
                        {
                            ["ViewName"] = view.Name,
                            ["ViewType"] = ViewsByTemplateRunHandler.ViewTypeLabel(view.ViewType),
                        }).Trim();

                    done++;
                    Progress((int)(done * 95.0 / Math.Max(total, 1)), pass, fail, skip);

                    if (string.IsNullOrWhiteSpace(name))
                    {
                        Log($"Skip '{view.Name}' — empty name.", "info");
                        skip++;
                        continue;
                    }
                    if (usedNames.Contains(name))
                    {
                        Log($"Skip '{name}' (name already exists).", "info");
                        skip++;
                        continue;
                    }
                    if (!view.CanViewBeDuplicated(option))
                    {
                        Log($"Skip '{view.Name}' — cannot {Mode.ToLowerInvariant()}.", "info");
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
                        Log($"Created '{name}'", "pass");
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
                                LemoineLog.Swallowed(
                                    $"Bulk duplicate views: delete orphan duplicate {newId.Value}", delEx);
                            }
                        }
                        Log($"Failed '{view.Name}': {e.Message}", "fail");
                        fail++;
                    }
                }

                tx.Commit();
            }

            Log($"Complete — {pass} created, {skip} skipped, {fail} failed.", "pass");
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
