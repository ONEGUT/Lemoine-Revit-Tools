using System;
using System.Collections.Generic;
using System.Windows.Interop;
using LemoineTools.Lemoine;
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

        public static void Open(ILemoineTool tool)
        {
            var key = tool.GetType();

            if (_open.TryGetValue(key, out var existing) && existing != null)
            {
                try { existing.Activate(); return; }
                catch (Exception ex)
                {
                    LemoineLog.Swallowed("NavisToolWindow: activate existing", ex);
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
                LemoineLog.Swallowed("NavisToolWindow: set window owner", ex);
            }

            _open[key] = win;
            win.Show();
        }
    }
}
