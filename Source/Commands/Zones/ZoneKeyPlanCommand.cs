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
    /// Opens Key Plans from Zones. The seed legends and loaded links are captured here on the
    /// Revit main thread — a legend is required because Revit exposes no way to create one.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ZoneKeyPlanCommand : IExternalCommand
    {
        private static StepFlowWindow? _window;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var uiApp = commandData?.Application;
            var doc   = uiApp?.ActiveUIDocument?.Document;

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
                    if (_window != null) return Result.Succeeded;
                }
                catch (Exception ex)
                {
                    DiagnosticsLog.Swallowed("ZoneKeyPlanCommand: reactivate existing window", ex);
                    _window = null;
                }
            }

            ZoneKeyPlanViewModel BuildTool()
            {
                var legends  = new List<ZoneKeyPlanViewModel.NamedId>();
                var links    = new List<ZoneKeyPlanViewModel.NamedId>();
                var patterns = new List<string>();
                var d = uiApp?.ActiveUIDocument?.Document;
                if (d != null)
                {
                    try
                    {
                        foreach (var v in new FilteredElementCollector(d).OfClass(typeof(View)).Cast<View>()
                                     .Where(v => !v.IsTemplate && v.ViewType == ViewType.Legend)
                                     .OrderBy(v => v.Name, NaturalOrderComparer.OrdinalIgnoreCase))
                            legends.Add(new ZoneKeyPlanViewModel.NamedId { Id = v.Id, Name = v.Name });
                    }
                    catch (Exception ex) { DiagnosticsLog.Error("ZoneKeyPlanCommand: collect legends", ex); }

                    try
                    {
                        foreach (var li in new FilteredElementCollector(d).OfClass(typeof(RevitLinkInstance))
                                     .Cast<RevitLinkInstance>())
                        {
                            var ld = li.GetLinkDocument();
                            if (ld == null) continue;
                            links.Add(new ZoneKeyPlanViewModel.NamedId { Id = li.Id, Name = ld.Title ?? li.Name });
                        }
                    }
                    catch (Exception ex) { DiagnosticsLog.Error("ZoneKeyPlanCommand: collect links", ex); }

                    // DRAFTING patterns only. A filled region in a legend is annotation, and a
                    // model pattern would scale with the view rather than the paper.
                    try
                    {
                        foreach (var f in new FilteredElementCollector(d)
                                     .OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>())
                        {
                            FillPattern? fp = null;
                            try { fp = f.GetFillPattern(); }
                            catch (Exception ex)
                            {
                                DiagnosticsLog.Swallowed($"ZoneKeyPlanCommand: read pattern '{f.Name}'", ex);
                            }
                            if (fp == null || fp.Target != FillPatternTarget.Drafting) continue;
                            if (string.IsNullOrEmpty(f.Name)) continue;
                            patterns.Add(f.Name);
                        }
                        patterns = patterns.Distinct(StringComparer.OrdinalIgnoreCase)
                                           .OrderBy(n => n, NaturalOrderComparer.OrdinalIgnoreCase)
                                           .ToList();
                    }
                    catch (Exception ex) { DiagnosticsLog.Error("ZoneKeyPlanCommand: collect fill patterns", ex); }

                    // A zero result is reported rather than presented as an empty picker.
                    DiagnosticsLog.Info("ZoneKeyPlanCommand",
                        $"Found {legends.Count} legend(s), {links.Count} loaded link(s) and " +
                        $"{patterns.Count} drafting fill pattern(s) for key plans.");
                }

                return new ZoneKeyPlanViewModel(App.ZoneKeyPlanRunHandler, App.ZoneKeyPlanRunEvent,
                                                legends, links, patterns);
            }

            var vm    = BuildTool();
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
    }
}
