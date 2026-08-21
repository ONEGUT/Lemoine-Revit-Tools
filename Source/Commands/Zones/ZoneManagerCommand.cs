using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Threading;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Framework.Zones;
using LemoineTools.Tools.Zones.Windows;

namespace LemoineTools.Commands
{
    /// <summary>
    /// Opens the Zone Manager on its own STA thread.
    ///
    /// Everything the window needs from the model — title block types with their declared
    /// sheet sizes, scope box names, host level names — is captured HERE, on Revit's main
    /// thread with the document in hand. The window runs on its own thread and never touches
    /// the Revit API.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ZoneManagerCommand : IExternalCommand
    {
        private static ZoneManagerWindow? _window;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var doc = commandData?.Application?.ActiveUIDocument?.Document;

            // Project-scoped settings resolve by this key, and it must be set before any
            // library is loaded. First line of every command, per the house rule.
            DocumentKey.SetCurrent(doc);

            // Zones live in the .rvt. Pull this document's library in before the window opens —
            // an empty payload clears the library rather than leaving the previous project's.
            LemoineTools.Framework.Project.ProjectLibraries.LoadForDocument(doc);

            // Bring an existing window to the front rather than opening a second one.
            if (_window != null)
            {
                try
                {
                    _window.Dispatcher.Invoke(() =>
                    {
                        if (_window.IsVisible) _window.Activate();
                        else _window = null;
                    });
                    if (_window != null) return Result.Succeeded;
                }
                catch (Exception ex)
                {
                    // A window whose thread has already gone is not an error worth surfacing,
                    // but it must not pass silently either — it means the handle leaked.
                    DiagnosticsLog.Swallowed("ZoneManagerCommand: reactivate existing window", ex);
                    _window = null;
                }
            }

            // ── Main-thread capture ───────────────────────────────────────────
            var titleBlocks  = new List<ZoneTitleBlocks.TitleBlockType>();
            // Scope boxes are handed over WITH THEIR BOUNDS. Capturing only the names was why
            // picking a box in the manager could never resolve an area's extents — the numbers
            // were read here and then thrown away, so no later code could have fixed it.
            var scopeBoxes   = new List<ZoneScopeBoxSync.BoxInfo>();
            var levelNames   = new List<string>();

            if (doc != null)
            {
                titleBlocks = ZoneTitleBlocks.Collect(doc);

                scopeBoxes = ZoneScopeBoxSync.CollectBoxes(doc)
                    .Where(b => !string.IsNullOrEmpty(b.Name))
                    .OrderBy(b => b.Name, NaturalOrderComparer.OrdinalIgnoreCase)
                    .ToList();

                try
                {
                    levelNames = new FilteredElementCollector(doc)
                        .OfClass(typeof(Level)).Cast<Level>()
                        .OrderBy(l => l.Elevation)
                        .Select(l => l.Name)
                        .Where(n => !string.IsNullOrEmpty(n))
                        .ToList();
                }
                catch (Exception ex)
                {
                    DiagnosticsLog.Error("ZoneManagerCommand: collect levels", ex);
                }

                // A zero result is reported rather than presented as an empty picker — a silent
                // empty list is indistinguishable from a broken collector.
                int withBounds = scopeBoxes.Count(b => b.HasBounds);
                DiagnosticsLog.Info("ZoneManagerCommand",
                    $"Captured {titleBlocks.Count} title block type(s), {scopeBoxes.Count} scope box(es) " +
                    $"({withBounds} with readable bounds), {levelNames.Count} level(s) for the Zone Manager.");
            }

            // Per-level building outlines. The window has no Revit access, so if it is not
            // captured here it can never be drawn — and this is the SAME ZoneSlabOutline path
            // the key plan uses, so the plan on screen and the plan in a title block are
            // produced by one collector rather than two that can disagree.
            var snapshot = CaptureGeometry(doc, scopeBoxes.Count, titleBlocks.Count, levelNames.Count);

            string docTitle = doc?.Title ?? "";

            var ready = new ManualResetEventSlim(false);
            ZoneManagerWindow? win = null;

            var thread = new Thread(() =>
            {
                win = new ZoneManagerWindow(docTitle, titleBlocks, scopeBoxes, levelNames, snapshot);
                win.Closed += (s, e) =>
                {
                    _window = null;
                    Dispatcher.CurrentDispatcher.InvokeShutdown();
                };
                win.Show();
                ready.Set();
                Dispatcher.Run();
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();

            ready.Wait();
            _window = win;
            return Result.Succeeded;
        }

        /// <summary>
        /// Reads one slab outline per zone level, on Revit's main thread.
        ///
        /// Cost is one collection per zone level at window open. A level whose outline cannot be
        /// read is recorded with no rings rather than skipped — the canvas then draws that
        /// level's areas over an empty surface, which is the documented contract, instead of
        /// showing a blank pane with nothing to explain it.
        /// </summary>
        private static ZoneGeometrySnapshot CaptureGeometry(Document? doc,
                                                            int scopeBoxCount, int titleBlockCount, int levelCount)
        {
            var snap = new ZoneGeometrySnapshot();
            if (doc == null) return snap;

            var links = new List<RevitLinkInstance>();
            try
            {
                links = new FilteredElementCollector(doc)
                    .OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>().ToList();
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("ZoneManagerCommand: collect link instances", ex);
            }

            snap.Counts = new ZoneGeometrySnapshot.ModelCounts
            {
                ScopeBoxes      = scopeBoxCount,
                TitleBlockTypes = titleBlockCount,
                HostLevels      = levelCount,
                LinkedModels    = links.Count,
            };

            var lib = ZoneSettings.Instance.Library;
            int drawn = 0;

            foreach (var level in lib.Levels)
            {
                if (level == null || string.IsNullOrEmpty(level.Id)) continue;

                string levelName = !string.IsNullOrEmpty(level.HostLevelName) ? level.HostLevelName : level.Name;

                // The level remembers which document Discover read it from. Cross-document
                // identity is the document NAME here, the same rule the rest of the zone model
                // follows; an unresolved key falls back to the host rather than drawing nothing.
                Document srcDoc = doc;
                Transform tf    = Transform.Identity;

                if (!string.IsNullOrEmpty(level.SourceLinkKey))
                {
                    foreach (var li in links)
                    {
                        Document? ld = null;
                        try { ld = li.GetLinkDocument(); }
                        catch (Exception ex) { DiagnosticsLog.Swallowed("ZoneManagerCommand: read link document", ex); }

                        if (ld == null) continue;
                        if (!string.Equals(ld.Title, level.SourceLinkKey, StringComparison.OrdinalIgnoreCase)) continue;

                        srcDoc = ld;
                        try { tf = li.GetTotalTransform(); }
                        catch (Exception ex) { DiagnosticsLog.Swallowed("ZoneManagerCommand: link transform", ex); }
                        break;
                    }
                }

                var entry = new ZoneGeometrySnapshot.LevelOutline
                {
                    LevelId = level.Id,
                    HostLevelName = levelName,
                };

                try
                {
                    var res = ZoneSlabOutline.Collect(srcDoc, tf, levelName);
                    entry.From = res.From.ToString();

                    if (res.Ok)
                    {
                        foreach (var ring in res.Rings)
                        {
                            var pts = new List<ZoneGeometrySnapshot.PlanPoint>();
                            foreach (var p in ring.Points) pts.Add(new ZoneGeometrySnapshot.PlanPoint(p.X, p.Y));
                            if (pts.Count >= 3) entry.Rings.Add(pts);
                        }
                        entry.MinX = res.MinX; entry.MinY = res.MinY;
                        entry.MaxX = res.MaxX; entry.MaxY = res.MaxY;
                        if (entry.Rings.Count > 0) drawn++;
                    }
                }
                catch (Exception ex)
                {
                    // One unreadable level must not cost the window every other level's plan.
                    DiagnosticsLog.Error($"ZoneManagerCommand: outline for level '{levelName}'", ex);
                }

                snap.Levels[level.Id] = entry;
            }

            // Stated even at zero: a silent empty capture is indistinguishable from a collector
            // that is simply broken, and that silence has hidden real bugs before.
            DiagnosticsLog.Info("ZoneManagerCommand",
                $"Captured {drawn} building outline(s) across {lib.Levels.Count} zone level(s) " +
                $"and {links.Count} linked model(s) for the Zone Manager canvas.");

            return snap;
        }
    }
}
