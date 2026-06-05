using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.LinkViews
{
    /// <summary>Lightweight room record collected on the Phase 1 scan pass.</summary>
    public sealed class RoomInfo
    {
        public string LevelName { get; set; } = null!;
        /// <summary>Elevation of the room's level in HOST coordinates (link transform applied),
        /// used to reconcile link rooms to host levels by height when names differ.</summary>
        public double LevelElevation { get; set; }
        public double CentroidX { get; set; }
        public double CentroidY { get; set; }
        public double MinX      { get; set; }
        public double MinY      { get; set; }
        public double MaxX      { get; set; }
        public double MaxY      { get; set; }
    }

    /// <summary>
    /// Static helpers shared between <see cref="LinkViewsLevelPhase1Handler"/>
    /// and <see cref="LinkViewsLevelRunHandler"/>.  No Revit transactions here —
    /// read-only queries only.
    /// </summary>
    internal static class LinkViewsLevelHelpers
    {
        /// <summary>
        /// Fallback Z half-extent used for the top/bottom levels so section boxes
        /// and view ranges always fully contain the model.
        /// </summary>
        public const double UnlimitedZ = 500.0; // feet

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
                    Level roomLevel = r.Level;
                    double levelElevHost = roomLevel != null
                        ? (isLink ? tf.OfPoint(new XYZ(0, 0, roomLevel.Elevation)).Z : roomLevel.Elevation)
                        : center.Z;

                    result.Add(new RoomInfo
                    {
                        LevelName = r.Level?.Name ?? string.Empty,
                        LevelElevation = levelElevHost,
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

                Level best = null;
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

        // ── View existence checks ─────────────────────────────────────────────────

        public static bool View3dExists(Document doc, string name) =>
            new FilteredElementCollector(doc)
                .OfClass(typeof(View3D)).Cast<View3D>()
                .Any(v => !v.IsTemplate && string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));

        public static bool PlanExists(Document doc, string name, ViewFamily family) =>
            new FilteredElementCollector(doc)
                .OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .Any(v => !v.IsTemplate
                          && string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase)
                          && v.ViewType == (family == ViewFamily.FloorPlan ? ViewType.FloorPlan : ViewType.CeilingPlan));

        // ── View creation helpers ─────────────────────────────────────────────────

        public static ViewFamilyType FindVFT(Document doc, ViewFamily family) =>
            new FilteredElementCollector(doc)
                .OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .FirstOrDefault(t => t.ViewFamily == family);

        public static View3D Create3d(Document doc, string name, ElementId vftId)
        {
            View3D v = View3D.CreateIsometric(doc, vftId);
            try { v.Name = name; } catch (Exception __lex) { LemoineLog.Swallowed($"LinkViews level: set name on view {v.Id.Value}", __lex); }
            return v;
        }

        // ── Print set helper ──────────────────────────────────────────────────────

        /// <summary>
        /// Gets or creates a <see cref="ViewSheetSet"/> named <paramref name="setName"/>
        /// and adds all <paramref name="views"/> to it.  Must be called inside an open
        /// transaction.
        /// </summary>
        public static void GetOrCreateViewSheetSet(
            Document doc, string setName, List<View> views, List<string> log)
        {
            if (views.Count == 0) return;
            try
            {
                var pm = doc.PrintManager;
                pm.PrintRange = Autodesk.Revit.DB.PrintRange.Select;

                ViewSheetSetting vss = pm.ViewSheetSetting;

                // Find existing set
                ViewSheetSet existing = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSheetSet)).Cast<ViewSheetSet>()
                    .FirstOrDefault(s => s.Name == setName);

                var viewSet = new ViewSet();
                if (existing != null)
                {
                    // Merge existing views into the set before saving
                    foreach (View v in existing.Views) viewSet.Insert(v);
                }
                foreach (var v in views) viewSet.Insert(v);

                vss.CurrentViewSheetSet.Views = viewSet;
                vss.SaveAs(setName);

                log.Add($"Print set '{setName}' updated ({viewSet.Size} view(s)).");
            }
            catch (Exception e)
            {
                log.Add($"[PrintSet] '{setName}': {e.Message}");
            }
        }
    }
}
