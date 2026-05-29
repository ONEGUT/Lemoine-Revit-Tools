using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.AutoFilters
{
    // =========================================================================
    // ColorMemory — parameter-value → hex-color lookup, persisted to disk.
    // Stored in %AppData%\LemoineTools\ColorMemory.xml
    //
    // Three-priority color lookup (used by DiscoverEventHandler.ResolveColor):
    //   1. ColorMemory (explicit user overrides)
    //   2. Existing AutoFiltersSettings rules (keyword match on parameter value)
    //   3. Auto-palette (20-colour cycling set)
    // =========================================================================

    [XmlRoot("ColorMemory")]
    public class ColorMemoryData
    {
        [XmlArray("Entries"), XmlArrayItem("Entry")]
        public List<ColorMemoryEntry> Entries { get; set; } = new List<ColorMemoryEntry>();
    }

    public class ColorMemoryEntry
    {
        [XmlAttribute] public string Value { get; set; } = "";
        [XmlAttribute] public string Hex   { get; set; } = "#888888";
    }

    /// <summary>
    /// Singleton that maps raw Revit parameter values → hex colors across sessions.
    /// Updated whenever the user changes a color in the Discover Rules tool.
    /// </summary>
    public sealed class ColorMemory
    {
        private static ColorMemory? _instance;
        public static ColorMemory Instance => _instance ?? (_instance = new ColorMemory());

        private readonly ColorMemoryData _data;
        private readonly Dictionary<string, ColorMemoryEntry> _index;

        private static string FilePath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "LemoineTools");
                try { Directory.CreateDirectory(dir); } catch (Exception __lex) { LemoineLog.Swallowed("ColorMemory: create config directory", __lex); }
                return Path.Combine(dir, "ColorMemory.xml");
            }
        }

        private ColorMemory()
        {
            _data  = Load();
            _index = new Dictionary<string, ColorMemoryEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in _data.Entries)
                if (!_index.ContainsKey(e.Value))
                    _index[e.Value] = e;
        }

        /// <summary>
        /// Returns true and sets <paramref name="hex"/> if a remembered colour exists for this value.
        /// </summary>
        public bool TryGetColor(string value, out string hex)
        {
            if (!string.IsNullOrEmpty(value) && _index.TryGetValue(value, out var e))
            {
                hex = e.Hex;
                return true;
            }
            hex = "#888888";
            return false;
        }

        /// <summary>
        /// Associates <paramref name="value"/> with <paramref name="hex"/> and persists immediately.
        /// </summary>
        public void SetColor(string value, string hex)
        {
            if (string.IsNullOrEmpty(value)) return;
            if (_index.TryGetValue(value, out var e))
                e.Hex = hex;
            else
            {
                var entry = new ColorMemoryEntry { Value = value, Hex = hex };
                _data.Entries.Add(entry);
                _index[value] = entry;
            }
            Save();
        }

        private void Save()
        {
            try
            {
                var xs = new XmlSerializer(typeof(ColorMemoryData));
                using (var w = new StreamWriter(FilePath))
                    xs.Serialize(w, _data);
            }
            catch (Exception __lex) { LemoineLog.Swallowed("ColorMemory.Save", __lex); }
        }

        private static ColorMemoryData Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var xs = new XmlSerializer(typeof(ColorMemoryData));
                    using (var r = new StreamReader(FilePath))
                        return (ColorMemoryData)xs.Deserialize(r)!;
                }
            }
            catch (Exception __lex) { LemoineLog.Swallowed("ColorMemory.Load", __lex); }
            return new ColorMemoryData();
        }
    }
}
