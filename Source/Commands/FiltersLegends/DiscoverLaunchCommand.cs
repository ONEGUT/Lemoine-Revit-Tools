using System.Linq;
using System.Threading;
using System.Windows.Threading;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Framework.Web;
using LemoineTools.Tools.AutoFilters;

namespace LemoineTools.Commands
{
    /// <summary>
    /// Thin launcher for the Discover Rules step-flow window.
    ///
    /// Queries loaded RevitLinkInstances on the Revit main thread,
    /// then opens the window on a dedicated STA thread so real-time
    /// progress and colour pickers work correctly.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class DiscoverLaunchCommand : IExternalCommand
    {
        public Result Execute(
            ExternalCommandData commandData,
            ref string          message,
            ElementSet          elements)
        {
            Open(commandData.Application);
            return Result.Succeeded;
        }

        // Window handle is shared so any caller (ribbon command or the Auto Filters window's
        // Discover button via an ExternalEvent) reuses/brings-to-front a single Discover window.
        private static StepFlowWindow? _window;

        /// <summary>
        /// Opens (or brings to front) the Discover Rules window. Must run on Revit's main thread
        /// — it reads the active document for category capture and link enumeration before
        /// spawning the window on its own STA thread.
        /// </summary>
        public static void Open(UIApplication uiApp)
        {
            // Bring existing window to front (STA-safe via Dispatcher)
            if (_window != null)
            {
                try
                {
                    _window.Dispatcher.Invoke(() =>
                    {
                        if (_window.IsVisible) _window.Activate();
                        else _window = null;
                    });
                    if (_window != null) return;
                }
                catch (System.Exception ex)
                {
                    DiagnosticsLog.Swallowed("DiscoverLaunch: bring-to-front", ex);
                    _window = null;
                }
            }

            DiscoverViewModel BuildTool()
            {
                var doc = uiApp.ActiveUIDocument.Document;

                // Capture the exact filterable-category list so the category pickers mirror
                // Revit's own "Edit Filters → Categories" list (read on the main thread).
                AutoFiltersSettings.CaptureFilterableCategories(doc);

                // Collect loaded link instances on the Revit main thread.
                // The host document is offered first (DiscoverEventHandler maps LinkId -1 to it),
                // so users can discover rules from the current model, not only from links.
                var links = new System.Collections.Generic.List<DiscoverViewModel.LinkEntry>
                {
                    new DiscoverViewModel.LinkEntry(ElementId.InvalidElementId, "Host Model"),
                };
                foreach (RevitLinkInstance li in
                    new FilteredElementCollector(doc)
                        .OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
                {
                    var ld = li.GetLinkDocument();
                    if (ld == null) continue;
                    string label = ld.Title ?? li.Name;
                    if (!links.Any(x => x.Label == label))
                        links.Add(new DiscoverViewModel.LinkEntry(li.Id, label));
                }

                return new DiscoverViewModel(
                    App.DiscoverHandler!,
                    App.DiscoverEvent!,
                    links,
                    App.AutoFiltersHandler,
                    App.AutoFiltersEvent);
            }

            if (WebToolLauncher.Enabled)
            {
                WebToolLauncher.Open("discover", () =>
                {
                    var doc = uiApp.ActiveUIDocument.Document;
                    AutoFiltersSettings.CaptureFilterableCategories(doc);

                    var links = new System.Collections.Generic.List<DiscoverViewModel.LinkEntry>
                    {
                        new DiscoverViewModel.LinkEntry(ElementId.InvalidElementId, "Host Model"),
                    };
                    foreach (RevitLinkInstance li in
                        new FilteredElementCollector(doc)
                            .OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
                    {
                        var ld = li.GetLinkDocument();
                        if (ld == null) continue;
                        string label = ld.Title ?? li.Name;
                        if (!links.Any(x => x.Label == label))
                            links.Add(new DiscoverViewModel.LinkEntry(li.Id, label));
                    }

                    return new DiscoverWebTool(
                        App.DiscoverHandler!, App.DiscoverEvent!, links,
                        App.AutoFiltersHandler, App.AutoFiltersEvent);
                });
                return Result.Succeeded;
            }

            var vm = BuildTool();
            var ready = new ManualResetEventSlim(false);
            StepFlowWindow? win = null;

            var thread = new Thread(() =>
            {
                win = new StepFlowWindow(vm, BuildTool);
                win.Closed += (s, e) =>
                {
                    _window = null;
                    Dispatcher.CurrentDispatcher.InvokeShutdown();
                };
                win.Show();
                ready.Set();
                Dispatcher.Run(); // ← message pump; keeps STA thread alive while window is open
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();

            ready.Wait();
            _window = win;
        }
    }
}
