using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace LemoineTools.Framework.Zones
{
    // =========================================================================
    // ZoneViewRangeApplier — reads and writes a plan's view range from a view def.
    //
    // This is the "RCP and floor plans already set and ready to use" half of a
    // zone: a scope box can say WHERE a plan looks, but only a view range says
    // what it cuts through, and Revit has no way to carry one as a reusable,
    // project-level convention.
    //
    // API surface, confirmed against libs/RevitAPI.dll metadata (2024) rather
    // than assumed — the signatures matter as much as the names:
    //
    //   static ElementId              PlanViewRange.Current / LevelAbove /
    //                                 LevelBelow / Unlimited
    //   ElementId                     PlanViewRange.GetLevelId(PlanViewPlane)
    //   void                          PlanViewRange.SetLevelId(PlanViewPlane, ElementId)
    //   double                        PlanViewRange.GetOffset(PlanViewPlane)
    //   void                          PlanViewRange.SetOffset(PlanViewPlane, double)
    //   IList<PlanViewRangeError>     ViewPlan.CheckPlanViewRangeValidity(PlanViewRange)
    //   PlanViewRange                 ViewPlan.GetViewRange()
    //   void                          ViewPlan.SetViewRange(PlanViewRange)
    //
    // CheckPlanViewRangeValidity is why a bad range is REPORTED rather than
    // thrown: an impossible combination (top below the cut plane, say) is a
    // configuration error in the view def, and the user needs to be told which
    // plane is wrong, not handed a stack trace.
    //
    // PlanViewPlane.UnderlayBottom is deliberately never written. It belongs to
    // the underlay, not the view range proper, and changing it would silently
    // alter a setting the user never asked this tool to touch.
    // =========================================================================
    public static class ZoneViewRangeApplier
    {
        /// <summary>
        /// Resolves a stored level-reference token to the ElementId a PlanViewRange wants.
        ///
        /// The four special tokens map onto Revit's own sentinels. Anything else is a level
        /// NAME — because a stored ElementId would be meaningless in another document, which
        /// is exactly the identity rule the rest of the zone model follows.
        ///
        /// Returns false when a named level cannot be found, so the caller reports it instead
        /// of silently substituting "Current" and producing a plausible but wrong plan.
        /// </summary>
        public static bool TryResolveLevelRef(string? token, Document doc,
                                              out ElementId id, out string? problem)
        {
            problem = null;
            id = ElementId.InvalidElementId;

            switch (token)
            {
                case null:
                case "":
                case ZoneLevelRef.Current:   id = PlanViewRange.Current;    return true;
                case ZoneLevelRef.Above:     id = PlanViewRange.LevelAbove; return true;
                case ZoneLevelRef.Below:     id = PlanViewRange.LevelBelow; return true;
                case ZoneLevelRef.Unlimited: id = PlanViewRange.Unlimited;  return true;
            }

            try
            {
                var level = new FilteredElementCollector(doc)
                    .OfClass(typeof(Level)).Cast<Level>()
                    .FirstOrDefault(l => string.Equals(l.Name, token, StringComparison.OrdinalIgnoreCase));

                if (level == null)
                {
                    problem = $"no level named '{token}' in this document";
                    return false;
                }
                id = level.Id;
                return true;
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed($"ZoneViewRangeApplier: resolve level '{token}'", ex);
                problem = $"could not resolve level '{token}': {ex.Message}";
                return false;
            }
        }

        /// <summary>Maps a resolved ElementId back to a stored token, for capture.</summary>
        public static string LevelRefTokenFor(ElementId id, Document doc)
        {
            try
            {
                if (id == PlanViewRange.Current)    return ZoneLevelRef.Current;
                if (id == PlanViewRange.LevelAbove) return ZoneLevelRef.Above;
                if (id == PlanViewRange.LevelBelow) return ZoneLevelRef.Below;
                if (id == PlanViewRange.Unlimited)  return ZoneLevelRef.Unlimited;

                var level = doc.GetElement(id) as Level;
                if (level != null && !string.IsNullOrEmpty(level.Name)) return level.Name;
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed("ZoneViewRangeApplier: map level id to token", ex);
            }
            // An unrecognised id is reported as Current rather than as a bogus name, and the
            // caller's own log line says the range was only partly captured.
            return ZoneLevelRef.Current;
        }

        /// <summary>
        /// Applies a stored view range to a plan view. Requires an open transaction.
        ///
        /// Validates BEFORE writing: an invalid range is named plane-by-plane and the view is
        /// left exactly as it was, rather than half-written or thrown out of.
        /// Returns true only when the range was actually written.
        /// </summary>
        public static bool Apply(ViewPlan? view, ZoneViewRange? range, Document doc,
                                 Action<string, string>? log = null)
        {
            if (view == null || range == null || doc == null) return false;

            void Say(string msg, string tone) { try { log?.Invoke(msg, tone); } catch (Exception ex) { DiagnosticsLog.Swallowed("ZoneViewRangeApplier: log", ex); } }

            PlanViewRange pvr;
            try
            {
                pvr = view.GetViewRange();
                if (pvr == null)
                {
                    Say($"'{view.Name}': this view has no view range to set.", "warn");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Say($"'{view.Name}': could not read the current view range — left unchanged.", "warn");
                DiagnosticsLog.Swallowed($"ZoneViewRangeApplier: GetViewRange on '{view.Name}'", ex);
                return false;
            }

            var planes = new (PlanViewPlane Plane, ZoneViewRangePlane Value, string Label)[]
            {
                (PlanViewPlane.TopClipPlane,    range.Top,       "Top"),
                (PlanViewPlane.CutPlane,        range.CutPlane,  "Cut plane"),
                (PlanViewPlane.BottomClipPlane, range.Bottom,    "Bottom"),
                (PlanViewPlane.ViewDepthPlane,  range.ViewDepth, "View depth"),
            };

            foreach (var p in planes)
            {
                if (p.Value == null) continue;
                if (!TryResolveLevelRef(p.Value.LevelRef, doc, out ElementId levelId, out string? problem))
                {
                    Say($"'{view.Name}': {p.Label} references a level that does not exist here " +
                        $"({problem}). The view range was NOT changed.", "fail");
                    return false;
                }

                try
                {
                    pvr.SetLevelId(p.Plane, levelId);
                    pvr.SetOffset(p.Plane, p.Value.OffsetFt);
                }
                catch (Exception ex)
                {
                    Say($"'{view.Name}': Revit rejected the {p.Label} setting — the view range was NOT changed.", "fail");
                    DiagnosticsLog.Error($"ZoneViewRangeApplier: set {p.Label} on '{view.Name}'", ex);
                    return false;
                }
            }

            // Validate before committing it to the view. An impossible range is a view def
            // problem, and naming the offending plane is the only useful thing to say.
            try
            {
                IList<PlanViewRangeError> errors = view.CheckPlanViewRangeValidity(pvr);
                if (errors != null && errors.Count > 0)
                {
                    Say($"'{view.Name}': the viewDef's view range is not valid ({DescribeErrors(errors)}). " +
                        "The view range was NOT changed.", "fail");
                    return false;
                }
            }
            catch (Exception ex)
            {
                // A validity check that itself fails is not a reason to write an unchecked
                // range — but it is also not proof the range is bad. Say so and continue.
                Say($"'{view.Name}': could not validate the view range; applying it anyway.", "warn");
                DiagnosticsLog.Swallowed($"ZoneViewRangeApplier: CheckPlanViewRangeValidity on '{view.Name}'", ex);
            }

            try
            {
                view.SetViewRange(pvr);
                return true;
            }
            catch (Exception ex)
            {
                Say($"'{view.Name}': Revit refused the view range.", "fail");
                DiagnosticsLog.Error($"ZoneViewRangeApplier: SetViewRange on '{view.Name}'", ex);
                return false;
            }
        }

        /// <summary>Reads a plan's current view range into a storable record. Null on failure.</summary>
        public static ZoneViewRange? Capture(ViewPlan? view, Document doc)
        {
            if (view == null || doc == null) return null;
            try
            {
                var pvr = view.GetViewRange();
                if (pvr == null) return null;

                return new ZoneViewRange
                {
                    Top       = CapturePlane(pvr, PlanViewPlane.TopClipPlane,    doc),
                    CutPlane  = CapturePlane(pvr, PlanViewPlane.CutPlane,        doc),
                    Bottom    = CapturePlane(pvr, PlanViewPlane.BottomClipPlane, doc),
                    ViewDepth = CapturePlane(pvr, PlanViewPlane.ViewDepthPlane,  doc),
                };
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed($"ZoneViewRangeApplier: capture range from '{view.Name}'", ex);
                return null;
            }
        }

        private static ZoneViewRangePlane CapturePlane(PlanViewRange pvr, PlanViewPlane plane, Document doc)
            => new ZoneViewRangePlane(
                   LevelRefTokenFor(pvr.GetLevelId(plane), doc),
                   pvr.GetOffset(plane));

        /// <summary>Turns Revit's validity errors into something a user can act on.</summary>
        private static string DescribeErrors(IList<PlanViewRangeError> errors)
        {
            var parts = new List<string>();
            foreach (var e in errors)
            {
                switch (e)
                {
                    case PlanViewRangeError.TopClipBelowCutPlane:
                        parts.Add("the top is below the cut plane"); break;
                    case PlanViewRangeError.BottomClipAboveCutPlane:
                        parts.Add("the bottom is above the cut plane"); break;
                    case PlanViewRangeError.ViewDepthAboveBottomClip:
                        parts.Add("the view depth is above the bottom"); break;
                    case PlanViewRangeError.ViewDepthBelowTopClip:
                        parts.Add("the view depth is below the top"); break;
                    default:
                        parts.Add(e.ToString()); break;
                }
            }
            return string.Join("; ", parts);
        }
    }
}
