using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.CopyLinear
{
    /// <summary>
    /// Copies selected grids from a linked document into the host, applying the link transform.
    /// Grid names are unique in Revit and the setter throws on a duplicate, so any grid whose name
    /// already exists in the host is skipped and logged rather than copied (CLAUDE.md uniqueness
    /// rule). One transaction, one regen.
    /// </summary>
    public sealed class CopyGridsRunHandler : IExternalEventHandler
    {
        public long       LinkInstId  { get; set; }
        public List<long> GridElemIds { get; set; } = new List<long>();

        public Action<string, string>?     PushLog    { get; set; }
        public Action<int, int, int, int>? OnProgress { get; set; }
        public Action<int, int, int>?      OnComplete { get; set; }

        public string GetName() => "LemoineTools.Tools.CopyLinear.CopyGridsRunHandler";

        private void Log(string t, string s) => PushLog?.Invoke(t, s);

        public void Execute(UIApplication app)
        {
            int pass = 0, fail = 0, skip = 0;
            long issues0 = LemoineLog.IssueCount;
            try
            {
                var doc = app.ActiveUIDocument?.Document;
                if (doc == null) { Log("No active document.", "fail"); OnComplete?.Invoke(0, 1, 0); return; }

                var src = CopyLinearSource.Resolve(doc, LinkInstId);
                if (src?.Doc == null || src.Link == null) { Log("Source link is not loaded.", "fail"); OnComplete?.Invoke(0, 1, 0); return; }

                // Host grid names — the copy must not clash with an existing grid name.
                var hostNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var g in new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>())
                    hostNames.Add(g.Name);

                var toCopy = new List<ElementId>();
                foreach (long id in GridElemIds ?? new List<long>())
                {
                    var g = src.Doc.GetElement(new ElementId(id)) as Grid;
                    if (g == null) { skip++; continue; }
                    if (hostNames.Contains(g.Name))
                    {
                        skip++;
                        Log($"— Grid '{g.Name}' already exists in host, skipped.", "info");
                        continue;
                    }
                    toCopy.Add(g.Id);
                }

                if (toCopy.Count == 0)
                {
                    Log("No grids to copy (all selected grids already exist in the host).", "warn");
                    OnProgress?.Invoke(100, 0, 0, skip);
                    OnComplete?.Invoke(0, 0, skip);
                    return;
                }

                using (var tx = new Transaction(doc, "Copy Grids from Link"))
                {
                    tx.Start();
                    ConfigureFailures(tx);

                    var opts = new CopyPasteOptions();
                    opts.SetDuplicateTypeNamesHandler(new UseDestinationTypes());

                    ICollection<ElementId> copied;
                    try
                    {
                        copied = ElementTransformUtils.CopyElements(src.Doc, toCopy, doc, src.Transform, opts);
                        pass = copied?.Count ?? 0;
                    }
                    catch (Exception ex)
                    {
                        // Fall back to per-grid copy so one bad grid doesn't lose the whole batch.
                        LemoineLog.Swallowed("CopyGrids: batch copy failed, retrying per grid", ex);
                        foreach (var id in toCopy)
                        {
                            if (LemoineRun.CancelRequested)
                            {
                                Log($"Stopped by user — {pass} grid(s) copied so far; work preserved.", "warn");
                                break;   // falls through to doc.Regenerate() + tx.Commit() below
                            }
                            try
                            {
                                var c = ElementTransformUtils.CopyElements(src.Doc, new List<ElementId> { id }, doc, src.Transform, opts);
                                pass += c?.Count ?? 0;
                            }
                            catch (Exception ex2)
                            {
                                fail++;
                                LemoineLog.Error("CopyGrids: copy grid", ex2);
                                Log($"✗ Grid {id}: {ex2.Message}", "fail");
                            }
                        }
                    }

                    doc.Regenerate();
                    tx.Commit();
                }

                long issues = LemoineLog.IssuesSince(issues0);
                if (issues > 0) Log($"{issues} non-fatal issue(s) recorded — see diagnostics log.", "warn");
                Log($"Done. {pass} grid(s) copied, {skip} skipped, {fail} failed.", fail > 0 ? "warn" : "pass");
                OnProgress?.Invoke(100, pass, fail, skip);
                OnComplete?.Invoke(pass, fail, skip);
            }
            catch (Exception ex)
            {
                LemoineLog.Error("CopyGridsRunHandler.Execute", ex);
                Log($"Run aborted: {ex.Message}", "fail");
                OnComplete?.Invoke(pass, fail + 1, skip);
            }
            finally
            {
                // Session-long static handler — drop the run's payload.
                GridElemIds = new List<long>();
            }
        }

        private static void ConfigureFailures(Transaction tx)
        {
            try
            {
                var opts = tx.GetFailureHandlingOptions();
                opts.SetClearAfterRollback(true);
                opts.SetForcedModalHandling(false);
                tx.SetFailureHandlingOptions(opts);
            }
            catch (Exception ex) { LemoineLog.Swallowed("CopyGrids: configure failure handling", ex); }
        }

        private sealed class UseDestinationTypes : IDuplicateTypeNamesHandler
        {
            public DuplicateTypeAction OnDuplicateTypeNamesFound(DuplicateTypeNamesHandlerArgs args)
                => DuplicateTypeAction.UseDestinationTypes;
        }
    }
}
