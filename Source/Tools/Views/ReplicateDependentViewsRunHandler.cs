using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Framework.Naming;

namespace LemoineTools.Tools.LinkViews
{
    public sealed class ReplicateDependentViewsRunHandler : IExternalEventHandler
    {
        // ── Inputs set before Raise() ─────────────────────────────────
        public SourceViewEntry       SourceEntry   { get; set; } = null!;
        public List<TargetViewEntry> TargetEntries { get; set; } = new List<TargetViewEntry>();

        /// <summary>Naming pattern, e.g. "{HostLevel} - {SourceViewName} - {DepSuffix}".</summary>
        public string NamePattern { get; set; } = "{HostLevel} - {SourceViewName} - {DepSuffix}";

        // ── Callbacks ─────────────────────────────────────────────────
        public Action<string, string>?     PushLog    { get; set; }
        public Action<int, int, int, int>? OnProgress { get; set; }
        public Action<int, int, int>?      OnComplete { get; set; }

        public string GetName() => "LemoineTools.Tools.LinkViews.ReplicateDependentViewsRunHandler";

        public void Execute(UIApplication app)
        {
            var doc  = app.ActiveUIDocument.Document;
            long __issues0 = DiagnosticsLog.IssueCount;
            int pass = 0, fail = 0, skip = 0;
            try
            {
                try { Run(doc, ref pass, ref fail, ref skip); }
                catch (Exception ex) { DiagnosticsLog.Error("ReplicateDependentViews: run aborted", ex); Log(AppStrings.T("linkviews.replicateDependent.log.error", ex.Message), "fail"); fail++; }
                Progress(100, pass, fail, skip);
                long __issues = DiagnosticsLog.IssuesSince(__issues0);
                if (__issues > 0) Log(AppStrings.T("linkviews.replicateDependent.log.nonFatal", __issues), "warn");
                Complete(pass, fail, skip);
            }
            finally
            {
                // Session-long static handler (App.ReplicateDependentViewsRunHandler) — drop the
                // run's payload (the entries carry view/crop data, not just ids).
                SourceEntry   = null!;
                TargetEntries = new List<TargetViewEntry>();
            }
        }

        private void Run(Document doc, ref int pass, ref int fail, ref int skip)
        {
            if (SourceEntry == null || TargetEntries.Count == 0)
            {
                Log(AppStrings.T("linkviews.replicateDependent.log.nothingToDo"), "info");
                return;
            }

            var deps = SourceEntry.Deps ?? new List<DepEntry>();
            if (deps.Count == 0)
            {
                Log(AppStrings.T("linkviews.replicateDependent.log.noDeps"), "info");
                return;
            }

            int total = TargetEntries.Count * deps.Count;
            int done  = 0;

            using (var tx = new Transaction(doc, "Replicate Dependent Views"))
            {
                ConfigureFailures(tx);
                tx.Start();

                bool cancelled = false;
                foreach (var target in TargetEntries)
                {
                    if (cancelled) break;
                    if (RunState.CancelRequested)
                    {
                        Log(AppStrings.T("common.log.stoppedByUser", done, total), "warn");
                        break;   // falls through to the existing tx.Commit() below
                    }

                    View? targetView = doc.GetElement(target.ViewId) as View;
                    if (targetView == null)
                    {
                        Log(AppStrings.T("linkviews.replicateDependent.log.skipNotFound", target.Name), "info");
                        skip += deps.Count;
                        done += deps.Count;
                        Progress((int)(done * 90.0 / Math.Max(total, 1)), pass, fail, skip);
                        continue;
                    }

                    // ── Log existing dependents on this target ─────────
                    if (target.ExistingDepCount > 0)
                    {
                        Log(AppStrings.T("linkviews.replicateDependent.log.infoExisting", target.Name, target.ExistingDepCount), "info");
                    }

                    if (target.OrientationWarning)
                    {
                        Log(AppStrings.T("linkviews.replicateDependent.log.warnOrient", target.Name), "info");
                    }

                    foreach (var dep in deps)
                    {
                        if (RunState.CancelRequested)
                        {
                            Log(AppStrings.T("common.log.stoppedByUser", done, total), "warn");
                            cancelled = true;
                            break;   // breaks inner loop; outer loop guard breaks too → existing tx.Commit() runs
                        }

                        string newName = BuildDepName(SourceEntry, target, dep.Suffix);

                        // Skip if already exists
                        if (ViewNameExists(doc, newName))
                        {
                            Log(AppStrings.T("linkviews.replicateDependent.log.skipExists", newName), "info");
                            skip++;
                            done++;
                            Progress((int)(done * 90.0 / Math.Max(total, 1)), pass, fail, skip);
                            continue;
                        }

                        try
                        {
                            // Duplicate as dependent
                            ElementId newId  = targetView.Duplicate(ViewDuplicateOption.AsDependent);
                            View?     newDep = doc.GetElement(newId) as View;
                            if (newDep == null) throw new InvalidOperationException("Duplicate returned null.");

                            // Name it
                            TrySetName(newDep, newName, doc);

                            // Apply crop
                            if (dep.HasCrop)
                                ApplyCrop(newDep, dep, doc);

                            Log(AppStrings.T("linkviews.replicateDependent.log.created", newName), "pass");
                            pass++;
                        }
                        catch (Exception e)
                        {
                            Log(AppStrings.T("linkviews.replicateDependent.log.fail", newName, e.Message), "fail");
                            fail++;
                        }

                        done++;
                        Progress((int)(done * 90.0 / Math.Max(total, 1)), pass, fail, skip);
                    }
                }

                tx.Commit();
            }

            Log(AppStrings.T("linkviews.replicateDependent.log.complete", pass, skip, fail), "pass");
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
                catch (Exception __lex) { DiagnosticsLog.Swallowed($"ReplicateDependentViews run: apply scope box to view {newDep.Id.Value}", __lex); }
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
                try { view.Name = $"{name} ({view.Id.Value})"; } catch (Exception __lex) { DiagnosticsLog.Swallowed($"ReplicateDependentViews run: set fallback name on view {view.Id.Value}", __lex); }
            }
        }

        private static bool ViewNameExists(Document doc, string name) =>
            new FilteredElementCollector(doc)
                .OfClass(typeof(View)).Cast<View>()
                .Any(v => !v.IsTemplate && string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));

        /// <summary>Resolves the naming pattern for one dependent-view copy.</summary>
        private string BuildDepName(SourceViewEntry source, TargetViewEntry target, string suffix)
        {
            var ctx = new TokenContext();
            ctx.Computed["HostLevel"]      = target.LevelName ?? "";
            ctx.Computed["SourceViewName"] = source.Name ?? "";
            ctx.Computed["TargetViewName"] = target.Name ?? "";
            ctx.Computed["ViewType"]       = target.TypeLabel ?? "";
            ctx.Computed["DepSuffix"]      = suffix ?? "";

            string resolved = TokenResolver.Resolve(NamePattern, ctx, msg => Log(msg, "warn"));
            return TokenResolver.GuardDegenerate(resolved, ctx, $"{target.Name} - {suffix}", msg => Log(msg, "warn"));
        }

        private void ConfigureFailures(Transaction tx)
        {
            var opts = tx.GetFailureHandlingOptions();
            opts.SetClearAfterRollback(true);
            opts.SetDelayedMiniWarnings(true);
            opts.SetFailuresPreprocessor(new SuppressWarningsPreprocessor(Log));
            tx.SetFailureHandlingOptions(opts);
        }

        /// <summary>
        /// Resolves warnings so the modeless batch never blocks on a dialog, but reports each distinct
        /// warning to the run log and diagnostics first — so a real one (e.g. "view range invalid") is
        /// surfaced rather than silently swallowed alongside the expected copy-monitor noise.
        /// </summary>
        private sealed class SuppressWarningsPreprocessor : IFailuresPreprocessor
        {
            private readonly Action<string, string>? _log;
            private readonly HashSet<string> _seen = new HashSet<string>(StringComparer.Ordinal);
            public SuppressWarningsPreprocessor(Action<string, string>? log) { _log = log; }

            public FailureProcessingResult PreprocessFailures(FailuresAccessor fa)
            {
                foreach (var msg in fa.GetFailureMessages()
                                      .Where(m => m.GetSeverity() == FailureSeverity.Warning))
                {
                    string desc;
                    try { desc = msg.GetDescriptionText(); }
                    catch (Exception ex)
                    {
                        DiagnosticsLog.Swallowed("ReplicateDependentViews: read warning text", ex);
                        desc = "(unreadable warning)";
                    }
                    if (_seen.Add(desc))   // report each distinct warning once, then resolve it
                    {
                        _log?.Invoke($"[warning] {desc}", "warn");
                        DiagnosticsLog.Warn("ReplicateDependentViews", desc);
                    }
                    fa.DeleteWarning(msg);
                }
                return FailureProcessingResult.Continue;
            }
        }

        private void Log(string t, string s) => PushLog?.Invoke(t, s);
        private void Progress(int p, int pa, int f, int s) => OnProgress?.Invoke(p, pa, f, s);
        private void Complete(int p, int f, int s) => OnComplete?.Invoke(p, f, s);
    }
}
