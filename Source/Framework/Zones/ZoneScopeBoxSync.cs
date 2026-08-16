using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace LemoineTools.Framework.Zones
{
    // =========================================================================
    // ZoneScopeBoxSync — reconciles adopted scope boxes with what the model
    // actually holds now.
    //
    // Adoption is a LIVE LINK, not a one-time copy. An area records the box's
    // NAME and caches its extents; every load re-reads the named box and
    // compares. Someone can resize a scope box in the Project Browser without
    // ever opening this tool, and a cached extent that silently disagreed with
    // the model would place drawings using numbers that are no longer true.
    //
    // Divergence is REPORTED, never silently corrected. Three outcomes, all
    // logged, none destructive:
    //
    //   Match    → nothing to do
    //   Resized  → warn; the user re-adopts explicitly (which may invalidate
    //              stored placements, so it cannot be automatic)
    //   Missing  → warn and mark the area unresolved. The area is NEVER deleted:
    //              the library outlives any one box, and a box that vanished is
    //              far more likely to be an accident than an instruction.
    //
    // Why re-adoption cannot be automatic: a stored placement pairs a WORLD
    // anchor with a SHEET coordinate. Re-solving extents moves an ExtentsCentre
    // anchor, and a moved anchor against an unchanged sheet coordinate shifts
    // the drawing — measured at 5/8" on a 1/8" plot for a 10 ft extents change.
    // See SheetAnchorMath.
    //
    // Read-only. No transaction. Main thread.
    // =========================================================================
    public static class ZoneScopeBoxSync
    {
        /// <summary>One scope box as it exists in the document right now.</summary>
        public sealed class BoxInfo
        {
            public ElementId Id   = ElementId.InvalidElementId;
            public string    Name = "";
            public double    MinX, MinY, MinZ, MaxX, MaxY, MaxZ;
            public bool      HasBounds;

            public double WidthFt  => MaxX - MinX;
            public double DepthFt  => MaxY - MinY;
            public double HeightFt => MaxZ - MinZ;
        }

        /// <summary>What reconciling one area against the model found.</summary>
        public enum Status
        {
            /// <summary>The area does not adopt a scope box at all.</summary>
            NotAdopted,
            /// <summary>Box found and its extents match what was cached.</summary>
            Match,
            /// <summary>Box found but its extents differ from the cache.</summary>
            Resized,
            /// <summary>No box of that name in the document.</summary>
            Missing,
            /// <summary>Adopted, box found, but nothing was cached yet — first resolve.</summary>
            FirstResolve,
        }

        public sealed class AreaSync
        {
            public string  AreaId    = "";
            public string  AreaName  = "";
            public string  BoxName   = "";
            public Status  Status    = Status.NotAdopted;
            public BoxInfo? Box      = null;
            /// <summary>Largest single-edge difference, in feet, when Resized.</summary>
            public double  DriftFt   = 0;
            /// <summary>True when placements already exist for this area — a resize then matters more.</summary>
            public bool    HasPlacements;
        }

        /// <summary>
        /// Collects every scope box in the document. Read-only.
        ///
        /// Self-contained rather than calling the Scope Box Creator's collector: this is
        /// framework code and must not depend on a tool assembly. It is a dozen lines, and the
        /// alternative is a layering inversion for no real saving.
        /// </summary>
        public static List<BoxInfo> CollectBoxes(Document? doc)
        {
            var list = new List<BoxInfo>();
            if (doc == null) return list;
            try
            {
                foreach (var e in new FilteredElementCollector(doc)
                             .OfCategory(BuiltInCategory.OST_VolumeOfInterest)
                             .WhereElementIsNotElementType())
                {
                    var info = new BoxInfo { Id = e.Id, Name = e.Name ?? "" };
                    BoundingBoxXYZ? bb = null;
                    try { bb = e.get_BoundingBox(null); }
                    catch (Exception ex)
                    {
                        DiagnosticsLog.Swallowed($"ZoneScopeBoxSync: bounds of scope box '{info.Name}'", ex);
                    }
                    if (bb != null)
                    {
                        info.MinX = bb.Min.X; info.MinY = bb.Min.Y; info.MinZ = bb.Min.Z;
                        info.MaxX = bb.Max.X; info.MaxY = bb.Max.Y; info.MaxZ = bb.Max.Z;
                        info.HasBounds = true;
                    }
                    list.Add(info);
                }
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("ZoneScopeBoxSync: collect scope boxes", ex);
            }
            return list;
        }

        /// <summary>
        /// Reconciles every adopted area in the library against the document.
        ///
        /// Reports a zero result explicitly — a silent empty list is indistinguishable from a
        /// broken collector, and that silence has hidden real bugs in this repo before.
        /// Mutates nothing: the caller decides what to do about each divergence.
        /// </summary>
        public static List<AreaSync> Reconcile(Document? doc, ZoneLibrary? library,
                                               Action<string, string>? log = null)
        {
            var results = new List<AreaSync>();
            if (doc == null || library == null) return results;

            void Say(string msg, string tone)
            {
                try { log?.Invoke(msg, tone); }
                catch (Exception ex) { DiagnosticsLog.Swallowed("ZoneScopeBoxSync: log", ex); }
            }

            var boxes = CollectBoxes(doc);
            var byName = new Dictionary<string, BoxInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var b in boxes)
                if (!string.IsNullOrEmpty(b.Name) && !byName.ContainsKey(b.Name))
                    byName[b.Name] = b;

            if (boxes.Count == 0)
                Say("No scope boxes found in this document.", "warn");

            int missing = 0, resized = 0, matched = 0;

            foreach (var area in library.Areas)
            {
                if (area == null) continue;

                var sync = new AreaSync
                {
                    AreaId        = area.Id,
                    AreaName      = area.Name,
                    BoxName       = area.ScopeBoxName ?? "",
                    HasPlacements = HasAnyPlacement(library, area.Id),
                };

                if (area.Definition != ZoneExtentMode.ScopeBox || string.IsNullOrEmpty(sync.BoxName))
                {
                    sync.Status = Status.NotAdopted;
                    results.Add(sync);
                    continue;
                }

                if (!byName.TryGetValue(sync.BoxName, out var box) || !box.HasBounds)
                {
                    sync.Status = Status.Missing;
                    missing++;
                    Say($"Area '{area.Name}': scope box '{sync.BoxName}' is not in this document — " +
                        "the area is left in place but cannot be used until it is re-pointed.", "warn");
                    results.Add(sync);
                    continue;
                }

                sync.Box = box;

                if (!area.HasExtents)
                {
                    sync.Status = Status.FirstResolve;
                    results.Add(sync);
                    continue;
                }

                double drift = Math.Max(
                    Math.Max(Math.Abs(box.MinX - area.MinX), Math.Abs(box.MaxX - area.MaxX)),
                    Math.Max(Math.Abs(box.MinY - area.MinY), Math.Abs(box.MaxY - area.MaxY)));

                // A hair of difference is model noise, not a resize. Anything a user could have
                // dragged is well above this.
                const double tolFt = 1e-4;
                if (drift <= tolFt)
                {
                    sync.Status = Status.Match;
                    matched++;
                }
                else
                {
                    sync.Status  = Status.Resized;
                    sync.DriftFt = drift;
                    resized++;

                    string extra = sync.HasPlacements
                        ? " Sheet placements already exist for this area, so re-adopting will move where its views land."
                        : "";
                    Say($"Area '{area.Name}': scope box '{sync.BoxName}' has been resized since it was " +
                        $"adopted (largest edge difference {drift:0.##}')." + extra, "warn");
                }

                results.Add(sync);
            }

            Say($"Scope box check: {matched} unchanged, {resized} resized, {missing} missing " +
                $"across {results.Count} area(s).", resized + missing > 0 ? "warn" : "info");

            return results;
        }

        /// <summary>Copies a box's current extents onto the area. Caller decides when — never automatic.</summary>
        public static void AdoptExtents(ZoneArea? area, BoxInfo? box)
        {
            if (area == null || box == null || !box.HasBounds) return;
            area.MinX = box.MinX; area.MinY = box.MinY;
            area.MaxX = box.MaxX; area.MaxY = box.MaxY;
            area.HasExtents = true;

            // Keep the anchor consistent with the mode. A GridIntersection anchor is
            // deliberately left alone — its whole value is that it does not move when
            // extents do.
            if (area.AnchorMode == ZoneAnchorMode.ExtentsCentre || !area.HasAnchor)
            {
                area.AnchorX = (area.MinX + area.MaxX) / 2.0;
                area.AnchorY = (area.MinY + area.MaxY) / 2.0;
                area.HasAnchor = true;
            }
        }

        private static bool HasAnyPlacement(ZoneLibrary library, string areaId)
        {
            if (library.Placements == null || string.IsNullOrEmpty(areaId)) return false;
            foreach (var p in library.Placements)
                if (p != null && string.Equals(p.AreaId, areaId, StringComparison.Ordinal))
                    return true;
            return false;
        }
    }
}
