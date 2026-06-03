using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using LemoineTools.Lemoine;
using LemoineTools.Tools.Testing.AutoDimension.Resolvers;

namespace LemoineTools.Tools.Testing.AutoDimension
{
    /// <summary>
    /// Lets the wizard pick ONE slab/floor (host or linked) up front, before the run. PickObject
    /// must run on Revit's main thread, so this is raised as an ExternalEvent; the result is handed
    /// back via <see cref="OnPicked"/> (the caller marshals UI updates onto its own dispatcher).
    /// </summary>
    public class SlabPickEventHandler : IExternalEventHandler
    {
        /// <summary>
        /// When true, picks the floor INSIDE a loaded Revit link (<see cref="ObjectType.LinkedElement"/>);
        /// otherwise picks a host floor (<see cref="ObjectType.Element"/>). A single PickObject call cannot
        /// reach both, so the caller sets this per press before raising the event.
        /// </summary>
        public bool InLinks { get; set; }

        /// <summary>Called on Revit's main thread with the picked scope (null = cancelled / not a floor) and a display name.</summary>
        public Action<SlabScope?, string>? OnPicked { get; set; }
        public Action<string, string>? PushLog { get; set; }

        public string GetName() => "LemoineTools.Tools.Testing.AutoDimension.SlabPickEventHandler";

        public void Execute(UIApplication app)
        {
            SlabScope? scope = null;
            string name = "";
            try
            {
                var uidoc = app.ActiveUIDocument;
                if (uidoc != null)
                {
                    // ObjectType.Element returns the whole link instance (no LinkedElementId) when you
                    // click into a link, so a linked floor can never be resolved that way — drilling
                    // into a link requires ObjectType.LinkedElement (mirrors ClashPickEventHandler).
                    var ot = InLinks ? ObjectType.LinkedElement : ObjectType.Element;
                    string prompt = InLinks
                        ? "Pick the linked slab/floor to dimension to."
                        : "Pick the slab/floor to dimension to.";
                    var r = uidoc.Selection.PickObject(ot,
                        new SlabScopePicker.FloorFilter(uidoc.Document), prompt);
                    scope = SlabScopePicker.ResolveScope(uidoc.Document, r, out name);
                    if (scope == null) PushLog?.Invoke("Picked element is not a floor.", "info");
                }
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                // Esc — keep whatever was already chosen.
            }
            catch (Exception ex)
            {
                LemoineLog.Error("SlabPickEventHandler: pick", ex);
                PushLog?.Invoke($"Slab pick failed: {ex.Message}", "fail");
            }

            OnPicked?.Invoke(scope, name);
        }
    }
}
