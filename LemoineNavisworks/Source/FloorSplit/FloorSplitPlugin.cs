using System;
using System.Windows;
using Autodesk.Navisworks.Api.Plugins;
using LemoineTools.Lemoine;

namespace LemoineNavisworks.FloorSplit
{
    // =========================================================================
    // FloorSplitPlugin — ribbon button (Add-Ins tab) for the Floor Splitter:
    // split the open federated model into one NWD per floor.
    // =========================================================================
    [Plugin("LemoineTools.FloorSplit", "LMNE",
            DisplayName = "Floor Splitter",
            ToolTip = "Split the model into per-floor NWDs (hide out-of-band geometry, then export)")]
    public class FloorSplitPlugin : AddInPlugin
    {
        public override int Execute(params string[] parameters)
        {
            try
            {
                NavisToolWindow.Open(new FloorSplitViewModel());
                return 0;
            }
            catch (Exception ex)
            {
                LemoineLog.Error("FloorSplitPlugin.Execute", ex);
                MessageBox.Show(
                    "Failed to open Floor Splitter:\n\n" + ex.Message,
                    "Lemoine Tools", MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }
        }
    }
}
