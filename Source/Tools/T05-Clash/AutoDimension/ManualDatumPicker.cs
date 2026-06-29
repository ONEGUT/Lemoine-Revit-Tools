using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using LemoineTools.Lemoine;
using LemoineTools.Tools.Clash.AutoDimension.Resolvers;

namespace LemoineTools.Tools.Clash.AutoDimension
{
    /// <summary>
    /// Manual-datum target mode: prompts the user to pick one datum edge per view (host or linked
    /// slab/floor edge), once — not per clash. Every clash in that view then dimensions out to the
    /// picked datum. Must run on Revit's main thread (inside the event handler), before the
    /// read-only plan build. Esc on a view skips it (that view gets no manual dimensions).
    /// </summary>
    public static class ManualDatumPicker
    {
        public static Dictionary<ElementId, List<ManualDatum>> PickForViews(
            UIDocument uidoc, IList<ElementId> viewIds, Action<string, string> log)
        {
            log = log ?? ((a, b) => { });
            var map = new Dictionary<ElementId, List<ManualDatum>>();
            if (uidoc == null || viewIds == null) return map;

            var doc = uidoc.Document;
            var before = PickerViewGuard.Snapshot(uidoc);
            try
            {
                foreach (var viewId in viewIds)
                {
                    if (!(doc.GetElement(viewId) is View view)) continue;

                    try { uidoc.ActiveView = view; }
                    catch (Exception ex) { LemoineLog.Swallowed("ManualDatumPicker: set active view", ex); }

                    try
                    {
                        var r = uidoc.Selection.PickObject(ObjectType.Edge,
                            $"Pick the datum edge for view '{view.Name}' (Esc to skip this view).");
                        var datum = BuildDatum(doc, r);
                        if (datum != null)
                        {
                            map[viewId] = new List<ManualDatum> { datum };
                            log($"View '{view.Name}': datum picked{(datum.WorldDir == null ? " (edge direction unread — serves every axis)" : "")}.", "info");
                        }
                    }
                    catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                    {
                        log($"View '{view.Name}': datum pick skipped.", "info");
                    }
                    catch (Exception ex)
                    {
                        LemoineLog.Error("ManualDatumPicker: pick", ex);
                        log($"View '{view.Name}': datum pick failed — {ex.Message}", "fail");
                    }
                }
            }
            finally
            {
                // Activating each view opened it in the UI — close what the pick pass opened
                // so the run doesn't leave dozens of views (and their graphics RAM) behind.
                PickerViewGuard.CloseOpenedViews(uidoc, before, log);
            }
            return map;
        }

        private static ManualDatum? BuildDatum(Document doc, Reference r)
        {
            if (r == null) return null;

            XYZ pt = XYZ.Zero;
            XYZ? dir = null;
            string key;
            try
            {
                pt = r.GlobalPoint ?? XYZ.Zero;

                if (r.LinkedElementId != ElementId.InvalidElementId)
                {
                    var li   = doc.GetElement(r.ElementId) as RevitLinkInstance;
                    var ldoc = li?.GetLinkDocument();
                    var le   = ldoc?.GetElement(r.LinkedElementId);
                    if (le?.GetGeometryObjectFromReference(r) is Edge edge && edge.AsCurve() is Curve c)
                    {
                        Transform tx = li!.GetTotalTransform();
                        dir = (tx.OfPoint(c.GetEndPoint(1)) - tx.OfPoint(c.GetEndPoint(0))).Normalize();
                    }
                    key = $"datum:{r.ElementId.Value}:{r.LinkedElementId.Value}";
                }
                else
                {
                    var e = doc.GetElement(r);
                    if (e?.GetGeometryObjectFromReference(r) is Edge edge && edge.AsCurve() is Curve c)
                        dir = (c.GetEndPoint(1) - c.GetEndPoint(0)).Normalize();
                    key = $"datum:{(e?.Id.Value ?? 0)}";
                }
            }
            catch (Exception ex)
            {
                LemoineLog.Swallowed("ManualDatumPicker: read datum edge geometry", ex);
                key = $"datum:{r.ElementId.Value}";
            }

            return new ManualDatum { Ref = r, WorldPoint = pt, WorldDir = dir, Key = key };
        }
    }
}
