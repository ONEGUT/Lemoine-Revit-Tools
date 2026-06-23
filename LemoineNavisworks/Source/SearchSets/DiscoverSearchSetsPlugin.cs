using System;
using System.Windows;
using Autodesk.Navisworks.Api.Plugins;
using LemoineTools.Lemoine;

namespace LemoineNavisworks.SearchSets
{
    // =========================================================================
    // DiscoverSearchSetsPlugin — ribbon button (Add-Ins tab) for the
    // Discover -> Search Sets tool.
    // =========================================================================
    [Plugin("LemoineTools.DiscoverSearchSets", "LMNE",
            DisplayName = "Discover Search Sets",
            ToolTip = "Scan models by category/property and create or update search sets")]
    public class DiscoverSearchSetsPlugin : AddInPlugin
    {
        public override int Execute(params string[] parameters)
        {
            try
            {
                NavisToolWindow.Open(new DiscoverSearchSetsViewModel());
                return 0;
            }
            catch (Exception ex)
            {
                LemoineLog.Error("DiscoverSearchSetsPlugin.Execute", ex);
                MessageBox.Show(
                    "Failed to open Discover Search Sets:\n\n" + ex.Message,
                    "Lemoine Tools", MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }
        }
    }
}
