using System;
using System.Windows;
using Autodesk.Navisworks.Api.Plugins;
using LemoineTools.Lemoine;

namespace LemoineNavisworks
{
    // =========================================================================
    // NavisLauncherPlugin — Phase 1 smoke-test entry point.
    //
    // An AddInPlugin shows up automatically under the ribbon's "Add-Ins" tab.
    // Opens the HelloNavis placeholder tool to confirm the shared UI renders and
    // the active document can be read. The real tools have their own buttons.
    // =========================================================================
    [Plugin("LemoineTools.Launcher", "LMNE",
            DisplayName = "Lemoine Tools",
            ToolTip = "Open Lemoine Tools (connection check)")]
    public class NavisLauncherPlugin : AddInPlugin
    {
        public override int Execute(params string[] parameters)
        {
            try
            {
                NavisToolWindow.Open(new HelloNavisViewModel());
                return 0;
            }
            catch (Exception ex)
            {
                LemoineLog.Error("NavisLauncher.Execute", ex);
                MessageBox.Show(
                    "Failed to open Lemoine Tools:\n\n" + ex.Message,
                    "Lemoine Tools", MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }
        }
    }
}
