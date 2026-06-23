using System;
using System.Windows;
using System.Windows.Interop;
using Autodesk.Navisworks.Api.Plugins;
using LemoineTools.Lemoine;
using NavisApp = Autodesk.Navisworks.Api.Application;

namespace LemoineNavisworks
{
    // =========================================================================
    // NavisLauncherPlugin — the Navisworks entry point.
    //
    // An AddInPlugin shows up automatically under the ribbon's "Add-Ins" tab
    // (no ribbon XAML needed), which is enough to launch and test the shared UI.
    //
    // Unlike Revit, Navisworks runs plugin code on its main UI thread and needs
    // no ExternalEvent: a modeless WPF window shown here stays alive and pumped
    // by Navisworks' own message loop, and the tool's Run() can call the
    // Navisworks API directly (it executes on this same main thread).
    // =========================================================================
    [Plugin("LemoineTools.Launcher", "LMNE",
            DisplayName = "Lemoine Tools",
            ToolTip = "Open Lemoine Tools for Navisworks")]
    public class NavisLauncherPlugin : AddInPlugin
    {
        // Single live window; reused/brought-forward instead of stacking copies.
        private static StepFlowWindow? _window;

        public override int Execute(params string[] parameters)
        {
            try
            {
                if (_window != null)
                {
                    try { _window.Activate(); return 0; }
                    catch (Exception ex)
                    {
                        // Stale handle (window torn down without our Closed firing) — recreate.
                        LemoineLog.Swallowed("NavisLauncher: activate existing window", ex);
                        _window = null;
                    }
                }

                var win = new StepFlowWindow(new HelloNavisViewModel());
                win.Closed += (s, e) => { if (ReferenceEquals(_window, win)) _window = null; };

                // Own the WPF window to the Navisworks main window so it does not fall
                // behind on Alt+Tab. Guarded: if the GUI API surface differs across
                // Navisworks versions, the window still opens (unowned) rather than failing.
                try
                {
                    var owner = NavisApp.Gui.MainWindow.Handle;
                    new WindowInteropHelper(win) { Owner = owner };
                }
                catch (Exception ex)
                {
                    LemoineLog.Swallowed("NavisLauncher: set window owner", ex);
                }

                _window = win;
                win.Show();
                return 0;
            }
            catch (Exception ex)
            {
                LemoineLog.Error("NavisLauncher.Execute", ex);
                MessageBox.Show(
                    "Failed to open Lemoine Tools:\n\n" + ex.Message,
                    "Lemoine Tools",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return 1;
            }
        }
    }
}
