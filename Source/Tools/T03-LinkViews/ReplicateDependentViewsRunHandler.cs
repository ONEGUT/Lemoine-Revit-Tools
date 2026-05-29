using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.LinkViews
{
    public sealed class ReplicateDependentViewsRunHandler : IExternalEventHandler
    {
        // ── Inputs set before Raise() ─────────────────────────────────
        public SourceViewEntry       SourceEntry   { get; set; } = null!;
        public List<TargetViewEntry> TargetEntries { get; set; } = new List<TargetViewEntry>();

        // ── Naming options ─────────────────────────────────────────────
        public string NamingFront        { get; set; } = "Host Level";
        public string NamingFrontCustom  { get; set; } = "";
        public string NamingCenter       { get; set; } = "Source View Name";
        public string NamingCenterCustom { get; set; } = "";
        public string NamingEnd          { get; set; } = "None";
        public string NamingEndCustom    { get; set; } = "";

        // ── Callbacks ─────────────────────────────────────────────────
        public Action<string, string>?     PushLog    { get; set; }
        public Action<int, int, int, int>? OnProgress { get; set; }
        public Action<int, int, int>?      OnComplete { get; set; }

        public string GetName() => "LemoineTools.Tools.LinkViews.ReplicateDependentViewsRunHandler";

        public void Execute(UIApplication app)
        {
            var doc  = app.ActiveUIDocument.Document;
            int pass = 0, fail = 0, skip = 0;
            try { Run(doc, ref pass, ref fail, ref skip); }
            catch (Exception ex) { Log($"Fatal: {ex.Message}", "fail"); fail++; }
            Progress(100, pass, fail, skip);
            Complete(pass, fail, skip);
        }

        private void Run(Document doc, ref int pass, ref int fail, ref int skip)
        {
            if (SourceEntry == null || TargetEntries.Count == 0)
            {
                Log("Nothing to do: no source or targets.", "info");
                return;
            }

            var deps = SourceEntry.Deps ?? new List<DepEntry>();
            if (deps.Count == 0)
            {
                Log("Source view has no dependents to copy.", "info");
                return;
            }

            int total = TargetEntries.Count * deps.Count;
            int done  = 0;

            using (var tx = new Transaction(doc, "Replicate Dependent Views"))
            {
                ConfigureFailures(tx);
                tx.Start();

                foreach (var target in TargetEntries)
                {
                    View targetView = doc.GetElement(target.ViewId) as View;
                    if (targetView == null)
                    {
                        Log($"[SKIP] Target view not found: {target.Name}", "info");
                        skip += deps.Count;
                        done += deps.Count;
                        Progress((int)(done * 90.0 / Math.Max(total, 1)), pass, fail, skip);
                        continue;
                    }

                    // ── Log existing dependents on this target ─────────
                    if (target.ExistingDepCount > 0)
                    {
                        Log($"[INFO] '{target.Name}' already has {target.ExistingDepCount} " +
                            $"dependent view{(target.ExistingDepCount != 1 ? "s" : "")}.", "info");
                    }

                    if (target.OrientationWarning)
                    {
                        Log($"[WARN] '{target.Name}' has a different view orientation — " +
                            "crop regions may not transfer correctly.", "info");
                    }

                    foreach (var dep in deps)
                    {
                        string newName = BuildDepName(SourceEntry, target, dep.Suffix);

                        // Skip if already exists
                        if (ViewNameExists(doc, newName))
                        {
                            Log($"[SKIP] '{newName}' already exists.", "info");
                            skip++;
                            done++;
                            Progress((int)(done * 90.0 / Math.Max(total, 1)), pass, fail, skip);
                            continue;
                        }

                        try
                        {
                            // Duplicate as dependent
                            ElementId newId  = targetView.Duplicate(ViewDuplicateOption.AsDependent);
                            View      newDep = doc.GetElement(newId) as View;
                            if (newDep == null) throw new InvalidOperationException("Duplicate returned null.");

                            // Name it
                            TrySetName(newDep, newName, doc);

                            // Apply crop
                            if (dep.HasCrop)
                                ApplyCrop(newDep, dep, doc);

                            Log($"Created: {newName}", "pass");
                            pass++;
                        }
                        catch (Exception e)
                        {
                            Log($"[FAIL] '{newName}': {e.Message}", "fail");
                            fail++;
                        }

                        done++;
                        Progress((int)(done * 90.0 / Math.Max(total, 1)), pass, fail, skip);
                    }
                }

                tx.Commit();
            }

            Log($"Complete — {pass} created, {skip} skipped, {fail} failed.", "pass");
        }

        // ── Helpers ───────────────────────────────────────────────────

        private static void ApplyCrop(View newDep, DepEntry dep, Document doc)
        {
            if (dep.WorldMin == null || dep.WorldMax == null) return;

            newDep.CropBoxActive  = true;
            newDep.CropBoxVisible = true;

            BoundingBoxXYZ cb = newDep.CropBox;
            cb.Min = dep.WorldMin;
            cb.Max = dep.WorldMax;
            newDep.CropBox = cb;

            // Restore scope box if one was captured
            if (dep.ScopeBoxId != null && dep.ScopeBoxId != ElementId.InvalidElementId)
            {
                try
                {
                    var scopeParam = newDep.get_Parameter(
                        BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP);
                    if (scopeParam != null && !scopeParam.IsReadOnly)
                        scopeParam.Set(dep.ScopeBoxId);
                }
                catch (Exception __lex) { LemoineLog.Swallowed($"ReplicateDependentViews run: apply scope box to view {newDep.Id.Value}", __lex); }
            }
        }

        private static void TrySetName(View view, string name, Document doc)
        {
            // Revit may append a number suffix if the name is taken;
            // attempt clean name first, then disambiguate.
            try
            {
                view.Name = name;
            }
            catch
            {
                try { view.Name = $"{name} ({view.Id.Value})"; } catch (Exception __lex) { LemoineLog.Swallowed($"ReplicateDependentViews run: set fallback name on view {view.Id.Value}", __lex); }
            }
        }

        private static bool ViewNameExists(Document doc, string name) =>
            new FilteredElementCollector(doc)
                .OfClass(typeof(View)).Cast<View>()
                .Any(v => !v.IsTemplate && v.Name == name);

        /// <summary>
        /// Assembles the new dependent view name from the naming slot configuration.
        /// If all slots are None, falls back to "{target.Name} - {suffix}".
        /// The dep suffix is always appended last.
        /// </summary>
        private string BuildDepName(SourceViewEntry source, TargetViewEntry target, string suffix)
        {
            var slots = new[] { NamingFront, NamingCenter, NamingEnd };
            bool anySet = slots.Any(s => s != "None");

            if (!anySet)
                return $"{target.Name} - {suffix}";

            var customs = new[] { NamingFrontCustom, NamingCenterCustom, NamingEndCustom };
            var parts = slots
                .Select((slot, i) => ResolveSlot(slot, customs[i], source, target))
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();
            parts.Add(suffix);
            return string.Join(" - ", parts);
        }

        private string ResolveSlot(string slot, string custom, SourceViewEntry source, TargetViewEntry target)
        {
            switch (slot)
            {
                case "Host Level":       return target.LevelName ?? "";
                case "Source View Name": return source.Name ?? "";
                case "Target View Name": return target.Name ?? "";
                case "View Type":        return target.TypeLabel ?? "";
                case "Dep Suffix":       return "";   // suffix is appended outside; skip here
                case "Custom":           return string.IsNullOrWhiteSpace(custom) ? "" : custom.Trim();
                default:                 return "";
            }
        }

        private static void ConfigureFailures(Transaction tx)
        {
            var opts = tx.GetFailureHandlingOptions();
            opts.SetClearAfterRollback(true);
            opts.SetDelayedMiniWarnings(true);
            opts.SetFailuresPreprocessor(new SuppressWarningsPreprocessor());
            tx.SetFailureHandlingOptions(opts);
        }

        /// <summary>Silently discards copy-monitor and other non-error warnings.</summary>
        private sealed class SuppressWarningsPreprocessor : IFailuresPreprocessor
        {
            public FailureProcessingResult PreprocessFailures(FailuresAccessor fa)
            {
                foreach (var msg in fa.GetFailureMessages()
                                      .Where(m => m.GetSeverity() == FailureSeverity.Warning))
                    fa.DeleteWarning(msg);
                return FailureProcessingResult.Continue;
            }
        }

        private void Log(string t, string s) => PushLog?.Invoke(t, s);
        private void Progress(int p, int pa, int f, int s) => OnProgress?.Invoke(p, pa, f, s);
        private void Complete(int p, int f, int s) => OnComplete?.Invoke(p, f, s);
    }
}
