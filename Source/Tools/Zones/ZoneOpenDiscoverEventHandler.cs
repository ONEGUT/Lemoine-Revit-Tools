using System;
using Autodesk.Revit.UI;
using LemoineTools.Commands;
using LemoineTools.Framework;

namespace LemoineTools.Tools.Zones
{
    /// <summary>
    /// Opens Zone Discover from inside the Zone Manager's "Discover" button.
    ///
    /// Window setup must run on Revit's main thread — it reads the active document to
    /// enumerate link instances — while the Zone Manager lives on its own STA thread and
    /// never touches the Revit API. The request is therefore marshalled here through an
    /// ExternalEvent and delegated to the shared <see cref="ZoneDiscoverCommand.Open"/>
    /// launcher. Same shape as Auto Filters' OpenDiscoverEventHandler.
    /// </summary>
    public class ZoneOpenDiscoverEventHandler : IExternalEventHandler
    {
        public string GetName() => "LemoineTools.Tools.Zones.ZoneOpenDiscoverEventHandler";

        public void Execute(UIApplication app)
        {
            try
            {
                ZoneDiscoverCommand.Open(app);
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("ZoneOpenDiscover: failed to open Zone Discover window", ex);
            }
        }
    }
}
