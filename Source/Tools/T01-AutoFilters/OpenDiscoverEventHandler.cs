using System;
using Autodesk.Revit.UI;
using LemoineTools.Commands;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.AutoFilters
{
    /// <summary>
    /// Opens the Discover Rules window from inside another tool window (the Auto Filters window's
    /// "Discover" button). Window setup must run on Revit's main thread — it reads the active
    /// document for category capture and link enumeration — so the request is marshalled here via
    /// an ExternalEvent and delegated to the shared <see cref="DiscoverLaunchCommand.Open"/> launcher.
    /// </summary>
    public class OpenDiscoverEventHandler : IExternalEventHandler
    {
        public string GetName() => "LemoineTools.Tools.AutoFilters.OpenDiscoverEventHandler";

        public void Execute(UIApplication app)
        {
            try
            {
                DiscoverLaunchCommand.Open(app);
            }
            catch (Exception ex)
            {
                LemoineLog.Error("OpenDiscover: failed to open Discover window", ex);
            }
        }
    }
}
