using System;
using System.Collections.Generic;
using System.Windows.Interop;
using LemoineTools.Framework;
using NavisApp = Autodesk.Navisworks.Api.Application;

namespace LemoineNavisworks
{
    // =========================================================================
    // NavisToolWindow — shared launcher for any Lemoine tool inside Navisworks.
    //
    // One live window per tool type (re-activates instead of stacking copies),
    // owned to the Navisworks main window so it does not fall behind on Alt+Tab.
    // Navisworks runs plugin code on the main UI thread, so a modeless window
    // shown here stays alive and pumped by Navisworks' own message loop.
    // =========================================================================
    internal static class NavisToolWindow
    {
        private static readonly Dictionary<Type, StepFlowWindow> _open =
            new Dictionary<Type, StepFlowWindow>();

        private static bool _bootstrapped;

        /// <summary>
        /// Host startup that Revit does in App.OnStartup and this add-in has no equivalent for.
        /// Runs once, before the first window is built.
        ///
        /// StepFlowWindow applies the theme and control styles itself, so only two things are
        /// missing here:
        ///   • AppStrings — without the load, every AppStrings.T() falls back to the key literal
        ///     and the UI renders as "navis.levelModels.title" instead of "Level Models".
        ///   • ToolReloadBridge — Revit installs a marshaller that hops the rebuild onto its main
        ///     thread via an ExternalEvent. Navisworks plugin code already runs on the main STA
        ///     thread with the API callable, so the factory is invoked directly.
        /// </summary>
        private static void EnsureBootstrapped()
        {
            if (_bootstrapped) return;
            _bootstrapped = true;

            try { AppStrings.Load(AppSettings.Instance.Language); }
            catch (Exception ex)
            {
                // Not fatal: lookups fall back to English and then to the key literal, which is
                // visible in the UI — but it must be traceable rather than a mystery.
                DiagnosticsLog.Error("NavisToolWindow: load AppStrings", ex);
            }

            try { LegacyFileCleanup.RunOnce(); }
            catch (Exception ex) { DiagnosticsLog.Swallowed("NavisToolWindow: legacy file cleanup", ex); }

            ToolReloadBridge.Marshal = (factory, onBuilt) =>
            {
                IStepFlowTool? rebuilt = null;
                try { rebuilt = factory(); }
                catch (Exception ex)
                {
                    // The bridge's contract: the host logs WHY before handing back null, so the
                    // window can report "reload failed" without swallowing the cause.
                    DiagnosticsLog.Error("NavisToolWindow: rebuild tool for reload", ex);
                }
                onBuilt(rebuilt);
            };
        }

        public static void Open(IStepFlowTool tool)
        {
            EnsureBootstrapped();
            var key = tool.GetType();

            if (_open.TryGetValue(key, out var existing) && existing != null)
            {
                try { existing.Activate(); return; }
                catch (Exception ex)
                {
                    DiagnosticsLog.Swallowed("NavisToolWindow: activate existing", ex);
                    _open.Remove(key);
                }
            }

            var win = new StepFlowWindow(tool);
            win.Closed += (s, e) =>
            {
                if (_open.TryGetValue(key, out var w) && ReferenceEquals(w, win))
                    _open.Remove(key);
            };

            // Own to the Navisworks main window. Guarded: if the GUI API surface
            // differs by version, the window still opens (unowned) rather than failing.
            try
            {
                var owner = NavisApp.Gui.MainWindow.Handle;
                new WindowInteropHelper(win) { Owner = owner };
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed("NavisToolWindow: set window owner", ex);
            }

            _open[key] = win;
            win.Show();
        }
    }
}
