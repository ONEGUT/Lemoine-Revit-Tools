using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Threading;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Framework.Web;
using LemoineTools.Tools.Setup;

namespace LemoineTools.Commands
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class AlignCoordinatesCommand : IExternalCommand
    {
        private static StepFlowWindow? _window;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
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
                catch { _window = null; }
            }

            var uiApp = commandData.Application;
            AlignCoordinatesViewModel BuildTool()
            {
                var doc  = uiApp.ActiveUIDocument.Document;
                var data = CollectData(doc);
                return new AlignCoordinatesViewModel(App.AlignCoordinatesRunHandler, App.AlignCoordinatesRunEvent, data);
            }
            if (WebToolLauncher.Enabled)
            {
                WebToolLauncher.Open("alignCoordinates", () =>
                {
                    var doc  = uiApp.ActiveUIDocument.Document;
                    var data = CollectData(doc);
                    return new AlignCoordinatesWebTool(App.AlignCoordinatesRunHandler, App.AlignCoordinatesRunEvent, data);
                });
                return Result.Succeeded;
            }
            var vm = BuildTool();
            var ready = new ManualResetEventSlim(false);
            StepFlowWindow? win = null;

            var thread = new Thread(() =>
            {
                win = new StepFlowWindow(vm, BuildTool);
                win.Closed += (s, e) => { _window = null; Dispatcher.CurrentDispatcher.InvokeShutdown(); };
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

        private static AlignCoordinatesData CollectData(Document doc)
        {
            var data = new AlignCoordinatesData();
            if (doc == null) return data;

            try
            {
                var hostGrids = new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>()
                    .Where(g => !string.IsNullOrWhiteSpace(g.Name)).ToList();
                data.HostGridNames = hostGrids.Select(g => g.Name)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
                data.HostGrids = hostGrids.Select(CaptureGridGeom).ToList();
            }
            catch (Exception ex) { DiagnosticsLog.Swallowed("AlignCoordinatesCommand: read host grids", ex); }

            try
            {
                data.HostLevelNames = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                    .OrderBy(l => l.ProjectElevation).Select(l => l.Name).ToList();
            }
            catch (Exception ex) { DiagnosticsLog.Swallowed("AlignCoordinatesCommand: read host levels", ex); }

            foreach (var li in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                var ld = li.GetLinkDocument();
                if (ld == null) continue;

                var info = new AlignLinkInfo
                {
                    Name       = "[" + Path.GetFileNameWithoutExtension(ld.Title) + "]",
                    LinkInstId = li.Id.Value,
                };
                try
                {
                    foreach (var g in new FilteredElementCollector(ld).OfClass(typeof(Grid)).Cast<Grid>())
                    {
                        if (string.IsNullOrWhiteSpace(g.Name)) continue;
                        info.GridNames.Add(g.Name);
                        info.Grids.Add(CaptureGridGeom(g));
                    }
                }
                catch (Exception ex) { DiagnosticsLog.Swallowed($"AlignCoordinatesCommand: read grids in {info.Name}", ex); }

                // Every loaded link is selectable — grids are optional metadata for that link's own
                // Grid Intersection override, never a filter on whether the link can be aligned.
                data.Links.Add(info);
            }
            return data;
        }

        // Reads a grid's endpoint geometry in its OWN document's internal coordinates (host
        // grids from the host doc, a link's grids from that link's own doc — never the host's
        // transform). Used only for the Grid-2-crosses-Grid-1 filter in the ViewModel; a
        // non-Line curve (arc/spline) is reported IsLine=false and is never filtered out.
        private static GridGeom CaptureGridGeom(Grid g)
        {
            var geom = new GridGeom { Name = g.Name };
            try
            {
                if (g.Curve is Line line)
                {
                    var p0 = line.GetEndPoint(0);
                    var p1 = line.GetEndPoint(1);
                    geom.IsLine = true;
                    geom.X0 = p0.X; geom.Y0 = p0.Y;
                    geom.X1 = p1.X; geom.Y1 = p1.Y;
                }
            }
            catch (Exception ex) { DiagnosticsLog.Swallowed($"AlignCoordinatesCommand: read grid curve for '{g.Name}'", ex); }
            return geom;
        }
    }
}
