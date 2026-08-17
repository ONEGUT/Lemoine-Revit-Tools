using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Threading;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Tools.Zones;

namespace LemoineTools.Commands
{
    /// <summary>
    /// Opens Zone Discover on its own STA thread. The selectable source documents are
    /// captured on Revit's main thread; the scan itself runs later through an ExternalEvent
    /// so the window never touches the Revit API.
    ///
    /// Zone Discover has NO ribbon button — it is reached from the Zone Manager's Discover
    /// button, exactly as Discover Rules is reached from inside Auto Filters. The opening
    /// logic therefore lives in the static <see cref="Open"/> so both the (still-registered)
    /// command and <see cref="LemoineTools.Tools.Zones.ZoneOpenDiscoverEventHandler"/> share
    /// one launcher.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ZoneDiscoverCommand : IExternalCommand
    {
        private static StepFlowWindow? _window;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            Open(commandData?.Application);
            return Result.Succeeded;
        }

        /// <summary>
        /// Opens (or re-activates) the Zone Discover window. MUST be called on Revit's main
        /// thread — it reads the active document to enumerate link instances.
        /// </summary>
        public static void Open(UIApplication? uiApp)
        {
            var doc = uiApp?.ActiveUIDocument?.Document;

            DocumentKey.SetCurrent(doc);
            LemoineTools.Framework.Project.ProjectLibraries.LoadForDocument(doc);

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
                catch (Exception ex)
                {
                    DiagnosticsLog.Swallowed("ZoneDiscoverCommand: reactivate existing window", ex);
                    _window = null;
                }
            }

            ZoneDiscoverViewModel BuildTool()
            {
                var docs = new List<ZoneDiscoverViewModel.DocEntry>();
                var d = uiApp?.ActiveUIDocument?.Document;
                if (d != null)
                {
                    docs.Add(new ZoneDiscoverViewModel.DocEntry
                    {
                        Label      = d.Title ?? "Host",
                        IsHost     = true,
                        LinkInstId = ElementId.InvalidElementId,
                    });

                    try
                    {
                        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var li in new FilteredElementCollector(d)
                                     .OfClass(typeof(RevitLinkInstance))
                                     .Cast<RevitLinkInstance>())
                        {
                            var ld = li.GetLinkDocument();
                            // An unloaded link cannot be read. It is skipped here rather than
                            // offered and then failing during the scan.
                            if (ld == null) continue;
                            string label = ld.Title ?? li.Name ?? "";
                            if (string.IsNullOrEmpty(label) || !seen.Add(label)) continue;

                            docs.Add(new ZoneDiscoverViewModel.DocEntry
                            {
                                Label = label, IsHost = false, LinkInstId = li.Id,
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        DiagnosticsLog.Error("ZoneDiscoverCommand: collect links", ex);
                    }

                    DiagnosticsLog.Info("ZoneDiscoverCommand",
                        $"Offering {docs.Count} source document(s) to Zone Discover.");
                }

                return new ZoneDiscoverViewModel(
                    App.ZoneDiscoverScanHandler, App.ZoneDiscoverScanEvent,
                    App.ZoneDiscoverRunHandler,  App.ZoneDiscoverRunEvent,
                    docs);
            }

            var vm    = BuildTool();
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
                Dispatcher.Run();
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();

            ready.Wait();
            _window = win;
        }
    }
}
