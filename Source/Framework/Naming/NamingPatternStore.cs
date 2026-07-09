using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;

namespace LemoineTools.Framework.Naming
{
    /// <summary>Persisted last-used pattern for one tool. Public root — see the
    /// XmlSerializer note on <see cref="UserTokenDto"/>.</summary>
    public sealed class NamingPatternDto
    {
        [XmlAttribute] public string ToolId { get; set; } = "";
        [XmlAttribute] public string Pattern { get; set; } = "";
    }

    [XmlRoot("NamingPatterns")]
    public sealed class NamingPatternsFileDto
    {
        [XmlElement("Pattern")] public List<NamingPatternDto> Patterns { get; set; } = new List<NamingPatternDto>();
    }

    /// <summary>
    /// Remembers each tool's last-used naming pattern, the same way Bulk Export already
    /// persists its own filename patterns — so every migrated tool gets that behavior for
    /// free instead of always resetting to its compiled-in default. Bulk Export keeps its
    /// own settings file (already working; migrating it would be pure churn).
    /// </summary>
    public sealed class NamingPatternStore
    {
        private static readonly Lazy<NamingPatternStore> _lazy = new Lazy<NamingPatternStore>(() => new NamingPatternStore());
        public static NamingPatternStore Instance => _lazy.Value;

        private readonly List<NamingPatternDto> _dtos;

        private NamingPatternStore()
        {
            _dtos = Load();
        }

        /// <summary>Returns the persisted pattern for <paramref name="toolId"/>, or
        /// <paramref name="defaultPattern"/> when nothing has been saved yet.</summary>
        public string GetOrDefault(string toolId, string defaultPattern)
        {
            var match = _dtos.FirstOrDefault(d => string.Equals(d.ToolId, toolId, StringComparison.Ordinal));
            return match != null && !string.IsNullOrEmpty(match.Pattern) ? match.Pattern : defaultPattern;
        }

        /// <summary>Saves the current pattern for <paramref name="toolId"/> immediately
        /// (settings auto-save on change — no separate Apply step).</summary>
        public void Set(string toolId, string pattern)
        {
            var match = _dtos.FirstOrDefault(d => string.Equals(d.ToolId, toolId, StringComparison.Ordinal));
            if (match != null) match.Pattern = pattern ?? "";
            else _dtos.Add(new NamingPatternDto { ToolId = toolId, Pattern = pattern ?? "" });
            Save();
        }

        // ── Persistence ───────────────────────────────────────────────────────
        private static string FilePath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "LemoineTools");
                try { Directory.CreateDirectory(dir); }
                catch (Exception ex) { DiagnosticsLog.Swallowed("NamingPatternStore: create config directory", ex); }
                return Path.Combine(dir, "NamingPatterns.xml");
            }
        }

        private void Save()
        {
            try
            {
                var xs = new XmlSerializer(typeof(NamingPatternsFileDto));
                using (var w = new StreamWriter(FilePath))
                    xs.Serialize(w, new NamingPatternsFileDto { Patterns = _dtos });
            }
            catch (Exception ex) { DiagnosticsLog.Swallowed("NamingPatternStore.Save", ex); }
        }

        private static List<NamingPatternDto> Load()
        {
            try
            {
                string path = FilePath;
                if (File.Exists(path))
                {
                    var xs = new XmlSerializer(typeof(NamingPatternsFileDto));
                    using (var r = new StreamReader(path))
                        return ((NamingPatternsFileDto)xs.Deserialize(r)!).Patterns ?? new List<NamingPatternDto>();
                }
            }
            catch (Exception ex) { DiagnosticsLog.Swallowed("NamingPatternStore.Load", ex); }
            return new List<NamingPatternDto>();
        }
    }
}
