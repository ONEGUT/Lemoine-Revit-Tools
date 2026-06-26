using System;
using Autodesk.Revit.UI;
using LemoineTools.Commands;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.AutoFilters
{
    /// <summary>
    /// Opens the Delete Filters from Project window from inside the Auto Filters window (its
    /// toolbar "Delete from Project" button). The window setup enumerates the project's filters on
    /// Revit's main thread, so the request is marshalled here via an ExternalEvent and delegated to
    /// the shared <see cref="DeleteFiltersFromProjectLaunchCommand.Open"/> launcher.
    /// </summary>
    public class OpenDeleteFromProjectEventHandler : IExternalEventHandler
    {
        public string GetName() => "LemoineTools.Tools.AutoFilters.OpenDeleteFromProjectEventHandler";

        public void Execute(UIApplication app)
        {
            try
            {
                DeleteFiltersFromProjectLaunchCommand.Open(app);
            }
            catch (Exception ex)
            {
                LemoineLog.Error("OpenDeleteFromProject: failed to open window", ex);
            }
        }
    }
}
