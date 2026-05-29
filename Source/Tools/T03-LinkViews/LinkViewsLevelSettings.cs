using System;
using System.IO;
using System.Xml.Serialization;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.LinkViews
{
    [XmlRoot("LinkViewsLevelSettings")]
    public sealed class LinkViewsLevelSettings
    {
        private static readonly Lazy<LinkViewsLevelSettings> _lazy =
            new Lazy<LinkViewsLevelSettings>(Load);
        public static LinkViewsLevelSettings Instance => _lazy.Value;

        public LinkViewsLevelSettings() { }

        /// <summary>XY margin added around each cluster bounding box (feet).</summary>
        public double BufferXY         { get; set; } = 10.0;
        /// <summary>Max room-edge gap for union-find cluster merging (feet).</summary>
        public double ClusterThreshold { get; set; } = 20.0;
        /// <summary>Height above level elevation for the plan cut plane (feet).</summary>
        public double CutOffset        { get; set; } =  4.0;

        private static string FilePath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "LemoineTools");
                try { Directory.CreateDirectory(dir); } catch (Exception __lex) { LemoineLog.Swallowed("LinkViewsLevelSettings: create config directory", __lex); }
                return Path.Combine(dir, "LinkViewsLevelSettings.xml");
            }
        }

        public void Save()
        {
            try
            {
                var xs = new XmlSerializer(typeof(LinkViewsLevelSettings));
                using (var w = new StreamWriter(FilePath))
                    xs.Serialize(w, this);
            }
            catch (Exception __lex) { LemoineLog.Swallowed("LinkViewsLevelSettings.Save", __lex); }
        }

        private static LinkViewsLevelSettings Load()
        {
            try
            {
                string path = FilePath;
                if (File.Exists(path))
                {
                    var xs = new XmlSerializer(typeof(LinkViewsLevelSettings));
                    using (var r = new StreamReader(path))
                        return (LinkViewsLevelSettings)xs.Deserialize(r)!;
                }
            }
            catch (Exception __lex) { LemoineLog.Swallowed("LinkViewsLevelSettings.Load", __lex); }
            return new LinkViewsLevelSettings();
        }
    }
}
