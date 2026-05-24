using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;

namespace LemoineTools.Preview
{
    [XmlRoot("LemoinePreviewState")]
    public sealed class PreviewState
    {
        // ── Controls gallery ──────────────────────────────────────────────────
        public string InlineText    { get; set; } = "Edit me";
        public string SingleSelect  { get; set; } = "Mechanical";
        public string SearchValue   { get; set; } = "";
        public string FilePath      { get; set; } = "";
        public int    StepperValue  { get; set; } = 5;
        public double RangeMin      { get; set; } = 100;
        public double RangeMax      { get; set; } = 500;
        public string DateFrom      { get; set; } = "";
        public string DateTo        { get; set; } = "";
        public string SelectedTags  { get; set; } = "Mechanical,Plumbing";
        public string SelectedMulti { get; set; } = "Level 1,Level 2";
        public string ToggleState   { get; set; } = "show_dims:true,show_tags:false,halftone:true";
        public string ColorHex      { get; set; } = "#4f8fc4";
        public string MatrixValues  { get; set; } = "";
        public string SwatchKind    { get; set; } = "square";
        public string SwatchFill    { get; set; } = "solid";

        // ── Demo tool ─────────────────────────────────────────────────────────
        public string DemoFilePath  { get; set; } = "";
        public string DemoLevels    { get; set; } = "Level 1,Level 2,Level 3";
        public int    DemoTagOffset { get; set; } = 25;
        public string DemoToggles   { get; set; } = "tag_room_name:true,tag_area:true,tag_number:false";

        // ── Helpers ───────────────────────────────────────────────────────────
        public List<string> SelectedTagList =>
            SelectedTags.Split(',').Where(s => !string.IsNullOrEmpty(s)).ToList();

        public List<string> SelectedMultiList =>
            SelectedMulti.Split(',').Where(s => !string.IsNullOrEmpty(s)).ToList();

        public Dictionary<string, bool> ToggleStateDict => ParseToggles(ToggleState);
        public Dictionary<string, bool> DemoToggleDict  => ParseToggles(DemoToggles);

        public Dictionary<string, string> MatrixDict => ParseMatrix(MatrixValues);

        public static Dictionary<string, bool> ParseToggles(string s)
        {
            var d = new Dictionary<string, bool>();
            if (string.IsNullOrEmpty(s)) return d;
            foreach (var part in s.Split(','))
            {
                var kv = part.Split(':');
                if (kv.Length == 2 && bool.TryParse(kv[1].Trim(), out var b))
                    d[kv[0].Trim()] = b;
            }
            return d;
        }

        public static string FormatToggles(IReadOnlyDictionary<string, bool> d)
            => string.Join(",", d.Select(kv => $"{kv.Key}:{kv.Value.ToString().ToLower()}"));

        public static Dictionary<string, string> ParseMatrix(string s)
        {
            var d = new Dictionary<string, string>();
            if (string.IsNullOrEmpty(s)) return d;
            foreach (var pair in s.Split(','))
            {
                var idx = pair.IndexOf(':');
                if (idx > 0) d[pair.Substring(0, idx)] = pair.Substring(idx + 1);
            }
            return d;
        }

        public static string FormatMatrix(IReadOnlyDictionary<string, string> d)
            => string.Join(",", d.Select(kv => $"{kv.Key}:{kv.Value}"));

        // ── Persistence ───────────────────────────────────────────────────────
        private static readonly string _path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LemoineTools", "PreviewState.xml");

        public static PreviewState Load()
        {
            try
            {
                if (!File.Exists(_path)) return new PreviewState();
                using var sr = new StreamReader(_path);
                return (PreviewState)new XmlSerializer(typeof(PreviewState)).Deserialize(sr)!;
            }
            catch { return new PreviewState(); }
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                using var sw = new StreamWriter(_path);
                new XmlSerializer(typeof(PreviewState)).Serialize(sw, this);
            }
            catch { }
        }
    }
}
