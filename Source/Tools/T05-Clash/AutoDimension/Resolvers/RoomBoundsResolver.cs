using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.Clash.AutoDimension.Resolvers
{
    /// <summary>
    /// Finds the room containing a world point, searching the host document first and then
    /// every loaded link — on MEP projects the rooms usually live in the linked architectural
    /// model. Returns the room's bounding box as world-space corners so the callout tier can
    /// grow a dense-area callout to cover the whole room. Read-only and never throws: every
    /// Revit call is guarded, a miss just returns null.
    /// </summary>
    public sealed class RoomBoundsResolver
    {
        /// <summary>One room found at a probe point: its world-space bounding-box corners
        /// (link rotation already applied), a stable identity for dedupe, and a log label.</summary>
        public sealed class RoomHit
        {
            public IReadOnlyList<XYZ> Corners { get; set; } = new List<XYZ>();
            /// <summary>Unique per room across all searched documents (doc label + element id).</summary>
            public string Key { get; set; } = "";
            /// <summary>Human-readable name for the run log, e.g. "Office 101 (ARCH Model)".</summary>
            public string Label { get; set; } = "";
        }

        private sealed class SearchDoc
        {
            public Document Doc = null!;
            public Transform ToWorld = Transform.Identity;    // doc coords → host world
            public Transform FromWorld = Transform.Identity;  // host world → doc coords
            public Phase? Phase;                              // per-doc phase; null → last-phase overload
            public string Label = "";                         // "" for the host, link title otherwise
        }

        private readonly List<SearchDoc> _docs = new List<SearchDoc>();
        private readonly double? _fallbackZ;
        private readonly Dictionary<string, RoomHit?> _roomCache =
            new Dictionary<string, RoomHit?>(StringComparer.Ordinal);

        public RoomBoundsResolver(Document doc, View view)
        {
            Phase? hostPhase = null;
            try
            {
                var pid = view.get_Parameter(BuiltInParameter.VIEW_PHASE)?.AsElementId()
                       ?? ElementId.InvalidElementId;
                hostPhase = doc.GetElement(pid) as Phase;
            }
            catch (Exception ex) { LemoineLog.Swallowed("RoomBoundsResolver: read view phase", ex); }

            _docs.Add(new SearchDoc { Doc = doc, Phase = hostPhase });

            try
            {
                foreach (var li in new FilteredElementCollector(doc)
                             .OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
                {
                    var linkDoc = li.GetLinkDocument();
                    if (linkDoc == null) continue;   // unloaded link

                    // Phases are per-document — match the view's phase by name, else fall back
                    // to the parameterless GetRoomAtPoint (last phase of the link).
                    Phase? linkPhase = null;
                    if (hostPhase != null)
                    {
                        foreach (Phase p in linkDoc.Phases)
                            if (string.Equals(p.Name, hostPhase.Name, StringComparison.Ordinal))
                            { linkPhase = p; break; }
                    }

                    var toWorld = li.GetTotalTransform();
                    _docs.Add(new SearchDoc
                    {
                        Doc       = linkDoc,
                        ToWorld   = toWorld,
                        FromWorld = toWorld.Inverse,
                        Phase     = linkPhase,
                        Label     = linkDoc.Title,
                    });
                }
            }
            catch (Exception ex) { LemoineLog.Swallowed("RoomBoundsResolver: collect link instances", ex); }

            // A clash anchor can sit above the room's upper limit (a duct near the ceiling),
            // so keep a second probe elevation just above the plan view's level.
            try
            {
                var level = (view as ViewPlan)?.GenLevel;
                if (level != null) _fallbackZ = level.Elevation + 2.0;
            }
            catch (Exception ex) { LemoineLog.Swallowed("RoomBoundsResolver: read plan level", ex); }
        }

        /// <summary>The room containing <paramref name="worldPt"/> in any searched document
        /// (host first, then links), or null when no placed room contains it.</summary>
        public RoomHit? FindRoom(XYZ worldPt)
        {
            foreach (var sd in _docs)
            {
                Room? room = RoomAt(sd, worldPt);
                if (room == null && _fallbackZ.HasValue && Math.Abs(worldPt.Z - _fallbackZ.Value) > 1e-6)
                    room = RoomAt(sd, new XYZ(worldPt.X, worldPt.Y, _fallbackZ.Value));
                if (room == null) continue;

                string key = sd.Label + "|" + room.Id.Value;
                if (!_roomCache.TryGetValue(key, out var hit))
                {
                    hit = BuildHit(room, sd, key);
                    _roomCache[key] = hit;
                }
                if (hit != null) return hit;
            }
            return null;
        }

        private static Room? RoomAt(SearchDoc sd, XYZ worldPt)
        {
            try
            {
                XYZ pt = sd.FromWorld.OfPoint(worldPt);
                Room? room = sd.Phase != null
                    ? sd.Doc.GetRoomAtPoint(pt, sd.Phase)
                    : sd.Doc.GetRoomAtPoint(pt);
                // Unplaced/unbounded rooms report zero area and carry no usable extent.
                return room != null && room.Area > 1e-9 ? room : null;
            }
            catch (Exception ex)
            {
                LemoineLog.Swallowed("RoomBoundsResolver: GetRoomAtPoint", ex);
                return null;
            }
        }

        private static RoomHit? BuildHit(Room room, SearchDoc sd, string key)
        {
            try
            {
                var bb = room.get_BoundingBox(null);
                if (bb == null) return null;

                // All 8 corners through the box's own transform then the link transform, so a
                // rotated link still yields a correct (conservative) world-space footprint.
                var corners = new List<XYZ>(8);
                foreach (double x in new[] { bb.Min.X, bb.Max.X })
                    foreach (double y in new[] { bb.Min.Y, bb.Max.Y })
                        foreach (double z in new[] { bb.Min.Z, bb.Max.Z })
                            corners.Add(sd.ToWorld.OfPoint(bb.Transform.OfPoint(new XYZ(x, y, z))));

                string name = room.Name;
                try
                {
                    string? n = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString();
                    string? num = room.get_Parameter(BuiltInParameter.ROOM_NUMBER)?.AsString();
                    if (!string.IsNullOrWhiteSpace(n))
                        name = string.IsNullOrWhiteSpace(num) ? n! : $"{n} {num}";
                }
                catch (Exception ex) { LemoineLog.Swallowed("RoomBoundsResolver: read room name", ex); }

                return new RoomHit
                {
                    Corners = corners,
                    Key     = key,
                    Label   = string.IsNullOrEmpty(sd.Label) ? name : $"{name} ({sd.Label})",
                };
            }
            catch (Exception ex)
            {
                LemoineLog.Swallowed("RoomBoundsResolver: room bounding box", ex);
                return null;
            }
        }
    }
}
