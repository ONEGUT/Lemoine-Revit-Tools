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

            string docTitle = doc?.Title ?? "";

            var ready = new ManualResetEventSlim(false);
            ZoneManagerWindow? win = null;

            var thread = new Thread(() =>
            {
                win = new ZoneManagerWindow(docTitle, titleBlocks, scopeBoxes, levelNames);
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
    }
}
