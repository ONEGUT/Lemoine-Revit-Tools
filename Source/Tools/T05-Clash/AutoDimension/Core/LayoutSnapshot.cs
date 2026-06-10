using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;

namespace LemoineTools.Tools.Clash.AutoDimension.Core
{
    /// <summary>
    /// Complete, self-describing dump of one view's layout problem AND solution — the data
    /// harvester for tuning the auto-dimension engine. Captures the scaled core config, every
    /// obstacle box, and every planned dimension with its final placement, segment states,
    /// planned tag positions, and per-dimension score breakdown. Everything needed to redraw
    /// or re-run the layout offline, with no Revit in the loop.
    /// Written per view when <c>AutoDimensionConfig.DumpLayoutSnapshots</c> is on.
    /// </summary>
    [XmlRoot("LayoutSnapshot")]
    public sealed class LayoutSnapshot
    {
        [XmlAttribute] public int    SchemaVersion { get; set; } = 1;
        [XmlAttribute] public string ViewName  { get; set; } = "";
        [XmlAttribute] public int    ViewScale { get; set; } = 1;
        [XmlAttribute] public string Timestamp { get; set; } = "";

        /// <summary>The SCALED core config the layout actually ran with (model-ft values).</summary>
        public LayoutConfig Config { get; set; } = new LayoutConfig();

        [XmlArray("Obstacles")] [XmlArrayItem("Box")]
        public List<SnapshotBox> Obstacles { get; set; } = new List<SnapshotBox>();

        [XmlArray("Dimensions")] [XmlArrayItem("Dim")]
        public List<SnapshotDim> Dims { get; set; } = new List<SnapshotDim>();

        [XmlArray("NearMisses")] [XmlArrayItem("Line")]
        public List<string> NearMisses { get; set; } = new List<string>();

        [XmlArray("Notes")] [XmlArrayItem("Line")]
        public List<string> Notes { get; set; } = new List<string>();
    }

    public sealed class SnapshotBox
    {
        [XmlAttribute] public double MinX { get; set; }
        [XmlAttribute] public double MinY { get; set; }
        [XmlAttribute] public double MaxX { get; set; }
        [XmlAttribute] public double MaxY { get; set; }
    }

    public sealed class SnapshotPoint
    {
        [XmlAttribute] public double X { get; set; }
        [XmlAttribute] public double Y { get; set; }
    }

    public sealed class SnapshotSeg
    {
        [XmlAttribute] public double LengthFt    { get; set; }
        [XmlAttribute] public double TextWidthFt { get; set; }
        [XmlAttribute] public string State       { get; set; } = "Inline";
        /// <summary>Planned moved-tag centre; NaN when the segment stayed inline.</summary>
        [XmlAttribute] public double TagX { get; set; } = double.NaN;
        [XmlAttribute] public double TagY { get; set; } = double.NaN;
    }

    public sealed class SnapshotDim
    {
        [XmlAttribute] public string SourceKey { get; set; } = "";
        [XmlAttribute] public string TargetKey { get; set; } = "";
        [XmlAttribute] public double AxisX { get; set; }
        [XmlAttribute] public double AxisY { get; set; }
        [XmlAttribute] public double SrcX  { get; set; }
        [XmlAttribute] public double SrcY  { get; set; }
        [XmlAttribute] public double TgtX  { get; set; }
        [XmlAttribute] public double TgtY  { get; set; }
        [XmlAttribute] public string Side  { get; set; } = "Positive";
        [XmlAttribute] public double OffsetFt { get; set; }
        [XmlAttribute] public int    TagColumnDir { get; set; } = 1;
        [XmlAttribute] public double Hard { get; set; }
        [XmlAttribute] public double Soft { get; set; }
        /// <summary>Per-constraint breakdown of the hard/soft score, human-readable.</summary>
        [XmlAttribute] public string ScoreDetail { get; set; } = "";

        [XmlArray("RefAnchors")] [XmlArrayItem("P")]
        public List<SnapshotPoint> RefAnchors { get; set; } = new List<SnapshotPoint>();

        [XmlArray("Segments")] [XmlArrayItem("Seg")]
        public List<SnapshotSeg> Segments { get; set; } = new List<SnapshotSeg>();
    }

    /// <summary>Builds and writes layout snapshots. Revit-free.</summary>
    public static class LayoutSnapshotWriter
    {
        /// <summary>Snapshot a finished layout (dims already placed/scored by the caller).</summary>
        public static LayoutSnapshot Build(
            string viewName, int viewScale, LayoutConfig cfg,
            IReadOnlyList<PlannedDimension> dims, IReadOnlyList<Box2> obstacles,
            LayoutScorer scorer, IEnumerable<string> nearMisses, IEnumerable<string> notes)
        {
            var snap = new LayoutSnapshot
            {
                ViewName  = viewName ?? "",
                ViewScale = viewScale,
                Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                Config    = cfg,
            };
            foreach (var ob in obstacles)
                snap.Obstacles.Add(new SnapshotBox { MinX = ob.MinX, MinY = ob.MinY, MaxX = ob.MaxX, MaxY = ob.MaxY });
            snap.NearMisses.AddRange(nearMisses ?? Array.Empty<string>());
            snap.Notes.AddRange(notes ?? Array.Empty<string>());

            foreach (var d in dims)
            {
                var detail = new ScoreDetail();
                var score  = scorer.Score(d, obstacles, dims, detail);
                var sd = new SnapshotDim
                {
                    SourceKey    = d.SourceKey,
                    TargetKey    = d.TargetKey,
                    AxisX        = d.AxisDir.X,
                    AxisY        = d.AxisDir.Y,
                    SrcX         = d.SourcePoint.X,
                    SrcY         = d.SourcePoint.Y,
                    TgtX         = d.TargetPoint.X,
                    TgtY         = d.TargetPoint.Y,
                    Side         = d.Side.ToString(),
                    OffsetFt     = d.OffsetFt,
                    TagColumnDir = d.TagColumnDir,
                    Hard         = score.Hard,
                    Soft         = score.Soft,
                    ScoreDetail  = detail.ToString(),
                };
                foreach (var p in d.RefAnchors ?? new List<Vec2>())
                    sd.RefAnchors.Add(new SnapshotPoint { X = p.X, Y = p.Y });
                foreach (var seg in d.Segments)
                {
                    sd.Segments.Add(new SnapshotSeg
                    {
                        LengthFt    = seg.LengthFt,
                        TextWidthFt = seg.TextWidthFt,
                        State       = seg.TextState.ToString(),
                        TagX        = seg.TagPos?.X ?? double.NaN,
                        TagY        = seg.TagPos?.Y ?? double.NaN,
                    });
                }
                snap.Dims.Add(sd);
            }
            return snap;
        }

        /// <summary>Writes the snapshot to %AppData%\LemoineTools\LayoutSnapshots and returns
        /// the path, or null when the write failed (the failure is logged, never thrown).</summary>
        public static string? Write(LayoutSnapshot snap)
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "LemoineTools", "LayoutSnapshots");
                Directory.CreateDirectory(dir);

                string safeView = string.Join("_", (snap.ViewName ?? "view").Split(Path.GetInvalidFileNameChars()));
                if (safeView.Length > 80) safeView = safeView.Substring(0, 80);
                string path = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd_HHmmss}_{safeView}.xml");

                var xs = new XmlSerializer(typeof(LayoutSnapshot));
                using (var w = new StreamWriter(path)) xs.Serialize(w, snap);
                return path;
            }
            catch (Exception ex)
            {
                LemoineTools.Lemoine.LemoineLog.Error("LayoutSnapshotWriter: write snapshot", ex);
                return null;
            }
        }
    }
}
