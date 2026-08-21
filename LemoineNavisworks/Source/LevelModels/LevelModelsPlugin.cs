using System;
using System.Windows;
using Autodesk.Navisworks.Api.Plugins;
using LemoineTools.Framework;

namespace LemoineNavisworks.LevelModels
{
    // =========================================================================
    // LevelModelsPlugin — Add-Ins ribbon button for Level Models: export one NWD
    // per level containing only the models assigned to that level.
    //
    // Replaces the old "Floor Splitter" button, which split the federation by Z
    // elevation bands. Assignment is now manual per level; the elevation band
    // survives as an optional per-level trim.
    // =========================================================================
    [Plugin("LemoineTools.LevelModels", "LMNE",
            DisplayName = "Level Models",
            ToolTip = "Export one NWD per level containing only the models assigned to it")]
    public class LevelModelsPlugin : AddInPlugin
    {
        public override int Execute(params string[] parameters)
        {
            try
            {
                NavisToolWindow.Open(new LevelModelsViewModel());
                return 0;
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("LevelModelsPlugin.Execute", ex);
                MessageBox.Show(
                    "Failed to open Level Models:\n\n" + ex.Message,
                    "Lemoine Tools", MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }
        }
    }
}
