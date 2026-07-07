using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.CopyFromLink
{
    /// <summary>
    /// Copies selected grids and levels from a linked document into the host, applying the link
    /// transform. Grid and level names are both unique in Revit and their setters throw on a
    /// duplicate, so any datum whose name already exists in the host is skipped and logged rather
    /// than copied (CLAUDE.md uniqueness rule). One transaction, one regen for both kinds.
    /// </summary>
    public sealed class CopyDatumsRunHandler : IExternalEventHandler
    {
        public long       LinkInstId   { get; set; }
        public List<long> GridElemIds  { get; set; } = new List<long>();
        public List<long> LevelElemIds { get; set; } = new List<long>();

        public Action<string, string>?     PushLog    { get; set; }
        public Action<int, int, int, int>? OnProgress { get; set; }
        public Action<int, int, int>?      OnComplete { get; set; }

        public string GetName() => "LemoineTools.Tools.CopyFromLink.CopyDatumsRunHandler";

        private void Log(string t, string s) => PushLog?.Invoke(t, s);

        public void Execute(UIApplication app)
        {
            int pass = 0, fail = 0, skip = 0;
            long issues0 = LemoineLog.IssueCount;
            try
            {
                var doc = app.ActiveUIDocument?.Document;
                if (doc == null) { Log(LemoineStrings.T("copy.datums.log.noDoc"), "fail"); OnComplete?.Invoke(0, 1, 0); return; }

                var src = CopyLinearSource.Resolve(doc, LinkInstId);
                if (src?.Doc == null || src.Link == null) { Log(LemoineStrings.T("copy.datums.log.srcNotLoaded"), "fail"); OnComplete?.Invoke(0, 1, 0); return; }

                // Host grid/level names — the copy must not clash with an existing datum name.
                var hostGridNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var g in new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>())
                    hostGridNames.Add(g.Name);
                var hostLevelNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var lvl in new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>())
                    hostLevelNames.Add(lvl.Name);

                var gridsToCopy = new List<ElementId>();
                foreach (long id in GridElemIds ?? new List<long>())
                {
                    var g = src.Doc.GetElement(new ElementId(id)) as Grid;
                    if (g == null) { skip++; continue; }
                    if (hostGridNames.Contains(g.Name))
                    {
                        skip++;
                        Log(LemoineStrings.T("copy.datums.log.gridExists", g.Name), "info");
                        continue;
                    }
                    gridsToCopy.Add(g.Id);
                }

                var levelsToCopy = new List<ElementId>();
                foreach (long id in LevelElemIds ?? new List<long>())
                {
                    var lvl = src.Doc.GetElement(new ElementId(id)) as Level;
                    if (lvl == null) { skip++; continue; }
                    if (hostLevelNames.Contains(lvl.Name))
                    {
                        skip++;
                        Log(LemoineStrings.T("copy.datums.log.levelExists", lvl.Name), "info");
                        continue;
                    }
                    levelsToCopy.Add(lvl.Id);
                }

                if (gridsToCopy.Count == 0 && levelsToCopy.Count == 0)
                {
                    Log(LemoineStrings.T("copy.datums.log.noDatumsToCopy"), "warn");
                    OnProgress?.Invoke(100, 0, 0, skip);
                    OnComplete?.Invoke(0, 0, skip);
                    return;
                }

                using (var tx = new Transaction(doc, "Copy Datums from Link"))
                {
                    tx.Start();
                    ConfigureFailures(tx);

                    var opts = new CopyPasteOptions();
                    opts.SetDuplicateTypeNamesHandler(new UseDestinationTypes());

                    bool stopped = false;

                    if (gridsToCopy.Count > 0)
                    {
                        try
                        {
                            var copied = ElementTransformUtils.CopyElements(src.Doc, gridsToCopy, doc, src.Transform, opts);
                            pass += copied?.Count ?? 0;
                        }
                        catch (Exception ex)
                        {
                            // Fall back to per-grid copy so one bad grid doesn't lose the whole batch.
                            LemoineLog.Swallowed("CopyDatums: grid batch copy failed, retrying per grid", ex);
                            foreach (var id in gridsToCopy)
                            {
                                if (LemoineRun.CancelRequested) { stopped = true; break; }
                                try
                                {
                                    var c = ElementTransformUtils.CopyElements(src.Doc, new List<ElementId> { id }, doc, src.Transform, opts);
                                    pass += c?.Count ?? 0;
                                }
                                catch (Exception ex2)
                                {
                                    fail++;
                                    LemoineLog.Error("CopyDatums: copy grid", ex2);
                                    Log(LemoineStrings.T("copy.datums.log.gridFail", id, ex2.Message), "fail");
                                }
                            }
                        }
                    }

                    if (!stopped && LemoineRun.CancelRequested) stopped = true;

                    if (!stopped && levelsToCopy.Count > 0)
                    {
                        try
                        {
                            var copied = ElementTransformUtils.CopyElements(src.Doc, levelsToCopy, doc, src.Transform, opts);
                            pass += copied?.Count ?? 0;
                        }
                        catch (Exception ex)
                        {
                            // Fall back to per-level copy so one bad level doesn't lose the whole batch.
                            LemoineLog.Swallowed("CopyDatums: level batch copy failed, retrying per level", ex);
                            foreach (var id in levelsToCopy)
                            {
                                if (LemoineRun.CancelRequested) { stopped = true; break; }
                                try
                                {
                                    var c = ElementTransformUtils.CopyElements(src.Doc, new List<ElementId> { id }, doc, src.Transform, opts);
                                    pass += c?.Count ?? 0;
                                }
                                catch (Exception ex2)
                                {
                                    fail++;
                                    LemoineLog.Error("CopyDatums: copy level", ex2);
                                    Log(LemoineStrings.T("copy.datums.log.levelFail", id, ex2.Message), "fail");
                                }
                            }
                        }
                    }

                    // Cancellation always falls through to a single regen + commit, preserving
                    // whatever was copied before the stop request (CLAUDE.md cancellation rule).
                    if (stopped) Log(LemoineStrings.T("copy.datums.log.stopped", pass), "warn");

                    doc.Regenerate();
                    tx.Commit();
                }

                long issues = LemoineLog.IssuesSince(issues0);
                if (issues > 0) Log(LemoineStrings.T("copy.datums.log.nonFatal", issues), "warn");
                Log(LemoineStrings.T("copy.datums.log.done", pass, skip, fail), fail > 0 ? "warn" : "pass");
                OnProgress?.Invoke(100, pass, fail, skip);
                OnComplete?.Invoke(pass, fail, skip);
            }
            catch (Exception ex)
            {
                LemoineLog.Error("CopyDatumsRunHandler.Execute", ex);
                Log(LemoineStrings.T("copy.datums.log.aborted", ex.Message), "fail");
                OnComplete?.Invoke(pass, fail + 1, skip);
            }
            finally
            {
                // Session-long static handler — drop the run's payload.
                GridElemIds  = new List<long>();
                LevelElemIds = new List<long>();
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
            catch (Exception ex) { LemoineLog.Swallowed("CopyDatums: configure failure handling", ex); }
        }

        private sealed class UseDestinationTypes : IDuplicateTypeNamesHandler
        {
            public DuplicateTypeAction OnDuplicateTypeNamesFound(DuplicateTypeNamesHandlerArgs args)
                => DuplicateTypeAction.UseDestinationTypes;
        }
    }
}
