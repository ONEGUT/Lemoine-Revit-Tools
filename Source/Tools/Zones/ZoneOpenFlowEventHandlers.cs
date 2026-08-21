using System;
using Autodesk.Revit.UI;
using LemoineTools.Commands;
using LemoineTools.Framework;

namespace LemoineTools.Tools.Zones
{
    /// <summary>
    /// Opens "Create Views from Zones" from the Zone Manager's toolbar.
    ///
    /// The manager runs on its own STA thread and never touches the Revit API, while the step
    /// flow's setup reads the active document — so the request is marshalled here through an
    /// ExternalEvent and delegated to the shared launcher. Same shape as
    /// <see cref="ZoneOpenDiscoverEventHandler"/>.
    /// </summary>
    public class ZoneOpenViewsEventHandler : IExternalEventHandler
    {
        public string GetName() => "LemoineTools.Tools.Zones.ZoneOpenViewsEventHandler";

        public void Execute(UIApplication app)
        {
            try
            {
                ZoneViewsCommand.Open(app);
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("ZoneOpenViews: failed to open Create Views from Zones", ex);
            }
        }
    }

    /// <summary>Opens "Build Sheets from Zones" from the Zone Manager's toolbar.</summary>
    public class ZoneOpenSheetsEventHandler : IExternalEventHandler
    {
        public string GetName() => "LemoineTools.Tools.Zones.ZoneOpenSheetsEventHandler";

        public void Execute(UIApplication app)
        {
            try
            {
                ZoneSheetsCommand.Open(app);
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("ZoneOpenSheets: failed to open Build Sheets from Zones", ex);
            }
        }
    }

    /// <summary>
    /// Opens "Key Plans from Zones" from the Zone Manager's toolbar. Key Plans has no ribbon
    /// button of its own — like Discover, it is reached only through the manager it belongs to.
    /// </summary>
    public class ZoneOpenKeyPlanEventHandler : IExternalEventHandler
    {
        public string GetName() => "LemoineTools.Tools.Zones.ZoneOpenKeyPlanEventHandler";

        public void Execute(UIApplication app)
        {
            try
            {
                ZoneKeyPlanCommand.Open(app);
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("ZoneOpenKeyPlan: failed to open Key Plans from Zones", ex);
            }
        }
    }
}
