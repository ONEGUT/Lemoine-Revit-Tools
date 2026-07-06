using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace LemoineTools.Tools.ScopeBoxes
{
    /// <summary>Lightweight room record collected on a scan pass, in HOST coordinates.</summary>
    public sealed class RoomInfo
    {
        public string LevelName { get; set; } = null!;
        /// <summary>Elevation of the room's level in HOST coordinates (link transform applied),
        /// used to reconcile link rooms to host levels by height when names differ.</summary>
        public double LevelElevation { get; set; }
        /// <summary>Display title of the source document the room came from.</summary>
        public string DocName   { get; set; } = "";
        public double CentroidX { get; set; }
        public double CentroidY { get; set; }
        public double MinX      { get; set; }
        public double MinY      { get; set; }
        public double MaxX      { get; set; }
        public double MaxY      { get; set; }
    }

    /// <summary>
    /// Room search + building clustering shared by the Scope Box Creator and the
    /// Bulk Views tooling. Read-only queries only — no Revit transactions here.
    /// (Extracted from LinkViewsLevelHelpers so scope-box and view tools share
    /// one searching implementation.)
    /// </summary>
    public static class RoomClusterSearch
    {
        // ── Room collection ───────────────────────────────────────────────────────

        /// <summary>
        /// Collects all placed rooms from each document in <paramref name="sourceDocs"/>
        /// and returns them in host-project coordinates.
        /// </summary>
        public static List<RoomInfo> CollectRooms(Document hostDoc, IEnumerable<Document> sourceDocs)
        {
            var result = new List<RoomInfo>();
            foreach (var doc in sourceDocs)
            {
                bool isLink = !doc.Equals(hostDoc);
                Transform tf = Transform.Identity;

                if (isLink)
                {
                    // Find the first loaded RevitLinkInstance for this document
                    var li = new FilteredElementCollector(hostDoc)
                        .OfClass(typeof(RevitLinkInstance))
                        .Cast<RevitLinkInstance>()
                        .FirstOrDefault(l =>
                        {
                            var ld = l.GetLinkDocument();
                            return ld != null && ld.Equals(doc);
                        });
                    if (li != null) tf = li.GetTotalTransform();
                }

                var rooms = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .WhereElementIsNotElementType()
                    .Cast<Room>()
                    .Where(r => r.Area > 0 && r.Location != null);

                foreach (var r in rooms)
                {
                    var loc = (r.Location as LocationPoint)?.Point;
                    if (loc == null) continue;

                    var bb = r.get_BoundingBox(null);
                    if (bb == null) continue;

                    XYZ center = isLink ? tf.OfPoint(loc) : loc;
                    XYZ bMin   = isLink ? tf.OfPoint(bb.Min) : bb.Min;
                    XYZ bMax   = isLink ? tf.OfPoint(bb.Max) : bb.Max;

                    // Room level elevation in host coordinates (so link levels reconcile by height).
                    Level? roomLevel = r.Level;
                    double levelElevHost = roomLevel != null
                        ? (isLink ? tf.OfPoint(new XYZ(0, 0, roomLevel.Elevation)).Z : roomLevel.Elevation)
                        : center.Z;

                    result.Add(new RoomInfo
                    {
                        LevelName = r.Level?.Name ?? string.Empty,
                        LevelElevation = levelElevHost,
                        DocName   = doc.Title ?? string.Empty,
                        CentroidX = center.X,
                        CentroidY = center.Y,
                        MinX = Math.Min(bMin.X, bMax.X),
                        MinY = Math.Min(bMin.Y, bMax.Y),
                        MaxX = Math.Max(bMin.X, bMax.X),
                        MaxY = Math.Max(bMin.Y, bMax.Y),
                    });
                }
            }
            return result;
        }

        // ── Host-level reconciliation ─────────────────────────────────────────────

        /// <summary>Tolerance (ft) for matching a room's level elevation to a host level. Generous
        /// enough to absorb arch/struct datum offsets, but well under a typical floor-to-floor.</summary>
        public const double LevelMatchToleranceFt = 4.0;

        /// <summary>
        /// Reconciles each room to a HOST level by elevation, so rooms in links whose level NAMES
        /// differ from the host still attach to the correct floor. A room whose level name already
        /// matches a host level is left untouched; otherwise it is retargeted to the nearest host
        /// level within <paramref name="tolFt"/> by setting its <see cref="RoomInfo.LevelName"/> to
        /// that host level's name. Only the backing association changes — everything the user sees
        /// stays keyed by host level name.
        /// </summary>
        public static void AssignHostLevelsByElevation(
            List<RoomInfo> rooms, IList<Level> hostLevels, double tolFt)
        {
            if (rooms == null || hostLevels == null || hostLevels.Count == 0) return;
            var hostNames = new HashSet<string>(hostLevels.Select(l => l.Name), StringComparer.Ordinal);

            foreach (var r in rooms)
            {
                if (!string.IsNullOrEmpty(r.LevelName) && hostNames.Contains(r.LevelName))
                    continue;   // already a host level by name — keep it

                Level? best = null;
                double bestD = double.MaxValue;
                foreach (var hl in hostLevels)
                {
                    double d = Math.Abs(hl.Elevation - r.LevelElevation);
                    if (d < bestD) { bestD = d; best = hl; }
                }
                if (best != null && bestD <= tolFt)
                    r.LevelName = best.Name;
            }
        }

        // ── Union-find clustering ─────────────────────────────────────────────────

        /// <summary>
        /// Groups rooms into building clusters.  Two rooms are in the same cluster
        /// if their bounding boxes are within <paramref name="threshold"/> feet of
        /// each other (union-find over all pairs).
        /// </summary>
        public static List<List<RoomInfo>> ClusterRooms(List<RoomInfo> rooms, double threshold)
        {
            int n = rooms.Count;
            int[] parent = Enumerable.Range(0, n).ToArray();

            int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
            void Union(int a, int b) { parent[Find(a)] = Find(b); }

            for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
            {
                if (BoxDistance(rooms[i], rooms[j]) <= threshold)
                    Union(i, j);
            }

            return rooms
                .Select((r, i) => (r, root: Find(i)))
                .GroupBy(t => t.root)
                .Select(g => g.Select(t => t.r).ToList())
                .ToList();
        }

        private static double BoxDistance(RoomInfo a, RoomInfo b)
        {
            double dx = Math.Max(0, Math.Max(a.MinX - b.MaxX, b.MinX - a.MaxX));
            double dy = Math.Max(0, Math.Max(a.MinY - b.MaxY, b.MinY - a.MaxY));
            return Math.Sqrt(dx * dx + dy * dy);
        }

        // ── Cluster bounding box ──────────────────────────────────────────────────

        /// <summary>Returns the XY bounding box of a cluster, expanded by <paramref name="buffer"/>.</summary>
        public static (double x0, double y0, double x1, double y1) ClusterBoundsXY(
            List<RoomInfo> cluster, double buffer)
        {
            double x0 = cluster.Min(r => r.MinX) - buffer;
            double y0 = cluster.Min(r => r.MinY) - buffer;
            double x1 = cluster.Max(r => r.MaxX) + buffer;
            double y1 = cluster.Max(r => r.MaxY) + buffer;
            return (x0, y0, x1, y1);
        }

        // ── Naming helpers ────────────────────────────────────────────────────────

        /// <summary>Converts a 0-based cluster index to A, B, C … Z, AA, AB …</summary>
        public static string BldgLetter(int index)
        {
            string result = string.Empty;
            do
            {
                result = (char)('A' + index % 26) + result;
                index  = index / 26 - 1;
            }
            while (index >= 0);
            return result;
        }
    }
}
