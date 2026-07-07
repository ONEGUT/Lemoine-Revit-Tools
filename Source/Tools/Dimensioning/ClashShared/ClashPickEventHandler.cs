using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace LemoineTools.Tools.Dimensioning
{
    /// <summary>
    /// Lets the user pick elements (in the host model or inside links) for a clash
    /// group's "Select Elements" mode. PickObjects must run on Revit's main thread,
    /// so this is raised as an ExternalEvent. The result is handed back via OnPicked;
    /// the caller is responsible for marshalling UI updates onto its own dispatcher.
    /// </summary>
    public class ClashPickEventHandler : IExternalEventHandler
    {
        /// <summary>When true, picks elements inside loaded links; otherwise host elements.</summary>
        public bool InLinks { get; set; }

        /// <summary>Called on Revit's main thread with the picked (linkInstId, elemId) pairs (linkInstId 0 = host).</summary>
        public Action<IList<(long linkId, long elemId)>>? OnPicked { get; set; }

        public Action<string, string>? PushLog { get; set; }

        public string GetName() => "LemoineTools.Tools.Dimensioning.ClashPickEventHandler";

        public void Execute(UIApplication app)
        {
            var result = new List<(long, long)>();
            try
            {
                var uidoc = app.ActiveUIDocument;
                if (uidoc != null)
                {
                    var ot = InLinks ? ObjectType.LinkedElement : ObjectType.Element;
                    string prompt = InLinks
                        ? "Select linked elements for this clash group, then click Finish"
                        : "Select host elements for this clash group, then click Finish";

                    IList<Reference> refs = uidoc.Selection.PickObjects(ot, prompt);
                    foreach (var r in refs)
                    {
                        if (InLinks)
                            result.Add((r.ElementId.Value, r.LinkedElementId.Value));
                        else
                            result.Add((0L, r.ElementId.Value));
                    }
                }
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                // User pressed Esc — keep whatever was already picked (none from this call).
            }
            catch (Exception ex)
            {
                PushLog?.Invoke($"Element pick failed: {ex.Message}", "fail");
            }

            OnPicked?.Invoke(result);
        }
    }
}
