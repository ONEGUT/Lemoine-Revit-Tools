using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;

namespace LemoineTools.Framework.Sheets
{
    /// <summary>
    /// Persisted drawing-area margins for one title block, in paper inches. Public root — an
    /// internal type makes <see cref="XmlSerializer"/> throw at construction, and because that
    /// throw sits inside a try/catch every save and load then fails SILENTLY (the cause of the
    /// theme-resetting bug). Keep every type in this file public.
    /// </summary>
    public sealed class SheetMarginDto
    {
        /// <summary>Title-block identity: "{FamilyName} : {TypeName}". A name, not an ElementId —
        /// which is exactly why this belongs in a machine-wide file and carries across projects.</summary>
        [XmlAttribute] public string TitleBlock { get; set; } = "";
        [XmlAttribute] public double Top        { get; set; } = 0.5;
        [XmlAttribute] public double Bottom     { get; set; } = 0.5;
        [XmlAttribute] public double Left       { get; set; } = 0.5;
        [XmlAttribute] public double Right      { get; set; } = 0.5;
    }

    [XmlRoot("SheetMargins")]
    public sealed class SheetMarginsFileDto
    {
        [XmlElement("TitleBlock")] public List<SheetMarginDto> Margins { get; set; } = new List<SheetMarginDto>();

        /// <summary>Gap between placed views, paper inches. One value for every title block, by
        /// explicit choice — unlike the margins, which are a property of the sheet border itself.</summary>
        [XmlAttribute] public double Gap { get; set; } = 0.25;
    }

    /// <summary>
    /// Remembers each title block's sheet margins so the same border always lays out the same way,
    /// in every project on this machine — <c>%AppData%\LemoineTools\SheetMargins.xml</c>.
    ///
    /// Storage tier: the key is a title-block NAME, which names nothing inside one specific model,
    /// so the machine-wide file is the right home (an ElementId here would be the mistake this
    /// project has shipped four times). Cross-project reuse is the point of the feature.
    /// </summary>
    public sealed class SheetMarginStore
    {
        private static readonly Lazy<SheetMarginStore> _lazy = new Lazy<SheetMarginStore>(() => new SheetMarginStore());
        public static SheetMarginStore Instance => _lazy.Value;

        public const double DefaultMarginIn = 0.5;
        public const double DefaultGapIn    = 0.25;

        private readonly SheetMarginsFileDto _file;

        private SheetMarginStore() { _file = Load(); }

        /// <summary>The four margins saved for <paramref name="titleBlock"/>, or the defaults when
        /// this title block has not been configured yet.</summary>
        public (double Top, double Bottom, double Left, double Right) GetMargins(string? titleBlock)
        {
            var dto = Find(titleBlock);
            return dto == null
                ? (DefaultMarginIn, DefaultMarginIn, DefaultMarginIn, DefaultMarginIn)
                : (dto.Top, dto.Bottom, dto.Left, dto.Right);
        }

        /// <summary>Saves the margins for one title block immediately (settings auto-save on
        /// change — this UI has no Apply button, by house convention).</summary>
        public void SetMargins(string? titleBlock, double top, double bottom, double left, double right)
        {
            if (string.IsNullOrWhiteSpace(titleBlock)) return;
            var dto = Find(titleBlock);
            if (dto == null)
            {
                dto = new SheetMarginDto { TitleBlock = titleBlock! };
                _file.Margins.Add(dto);
            }
            dto.Top = top; dto.Bottom = bottom; dto.Left = left; dto.Right = right;
            Save();
        }

        /// <summary>The gap between placed views, paper inches — global, not per title block.</summary>
        public double Gap
        {
            get => _file.Gap;
            set { _file.Gap = value; Save(); }
        }

        private SheetMarginDto? Find(string? titleBlock) =>
            string.IsNullOrWhiteSpace(titleBlock)
                ? null
                : _file.Margins.FirstOrDefault(d => string.Equals(d.TitleBlock, titleBlock, StringComparison.OrdinalIgnoreCase));

        // ── Persistence ───────────────────────────────────────────────────────
        private static string FilePath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "LemoineTools");
                try { Directory.CreateDirectory(dir); }
                catch (Exception ex) { DiagnosticsLog.Swallowed("SheetMarginStore: create config directory", ex); }
                return Path.Combine(dir, "SheetMargins.xml");
            }
        }

        private void Save()
        {
            try
            {
                var xs = new XmlSerializer(typeof(SheetMarginsFileDto));
                using (var w = new StreamWriter(FilePath))
                    xs.Serialize(w, _file);
            }
            catch (Exception ex) { DiagnosticsLog.Swallowed("SheetMarginStore.Save", ex); }
        }

        private static SheetMarginsFileDto Load()
        {
            try
            {
                string path = FilePath;
                if (File.Exists(path))
                {
                    var xs = new XmlSerializer(typeof(SheetMarginsFileDto));
                    using (var r = new StreamReader(path))
                        return (SheetMarginsFileDto)xs.Deserialize(r)! ?? new SheetMarginsFileDto();
                }
            }
            catch (Exception ex) { DiagnosticsLog.Swallowed("SheetMarginStore.Load", ex); }
            return new SheetMarginsFileDto();
        }
    }
}
