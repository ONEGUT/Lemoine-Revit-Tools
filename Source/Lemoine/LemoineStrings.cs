using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;

namespace LemoineTools.Lemoine
{
    /// <summary>
    /// Central store for every string a user sees during a normal run — ribbon labels,
    /// tool window chrome, and Output-log lines. Text lives in editable per-language JSON
    /// files under a <c>Strings\&lt;culture&gt;\</c> folder shipped next to the assembly, so
    /// wording (and additional languages) can be changed without recompiling.
    ///
    /// <para>Revit-free, like <see cref="LemoineLog"/>, so any layer can call it.</para>
    ///
    /// <para>Threading: <see cref="Load"/> runs once at startup and replaces the lookup
    /// tables wholesale with new immutable dictionaries; the per-STA-thread tool windows only
    /// ever read, so no locking is needed. Changing language reloads the tables and applies to
    /// tool windows opened afterwards (already-open windows are not rebuilt).</para>
    /// </summary>
    public static class LemoineStrings
    {
        // Folder name (next to the assembly) that holds the per-culture sub-folders.
        private const string RootFolderName = "Strings";

        // Fallback culture: shipped, complete, and the source of truth for keys.
        private const string FallbackCulture = "en";

        // Flattened "file.section.key" -> text. Replaced wholesale on each Load (never mutated in place).
        private static IReadOnlyDictionary<string, string> _active   = EmptyMap();
        private static IReadOnlyDictionary<string, string> _fallback = EmptyMap();

        /// <summary>The culture folder currently loaded (e.g. "en", "fr"). Empty until <see cref="Load"/> runs.</summary>
        public static string ActiveCulture { get; private set; } = "";

        private static Dictionary<string, string> EmptyMap()
            => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Returns the culture folders present under <c>Strings\</c> (the set the user can choose from).
        /// Always includes <see cref="FallbackCulture"/> if it exists. Empty if the folder is missing.
        /// </summary>
        public static IReadOnlyList<string> AvailableCultures()
        {
            var list = new List<string>();
            try
            {
                string root = RootFolder();
                if (Directory.Exists(root))
                    foreach (var dir in Directory.GetDirectories(root))
                        list.Add(Path.GetFileName(dir));
            }
            catch (Exception ex)
            {
                LemoineLog.Swallowed("LemoineStrings.AvailableCultures", ex);
            }
            return list;
        }

        /// <summary>
        /// Loads the fallback (English) tables plus the requested culture's tables. Call once at
        /// startup, and again when the user changes language. A missing or unparsable file is logged
        /// and skipped — the affected keys simply fall through to English (then to the key literal).
        /// </summary>
        /// <param name="culture">Culture folder to load (e.g. "en", "fr"). Falls back to "en" if absent.</param>
        public static void Load(string culture)
        {
            _fallback = LoadCulture(FallbackCulture);

            if (string.IsNullOrWhiteSpace(culture) || culture.Equals(FallbackCulture, StringComparison.OrdinalIgnoreCase))
            {
                _active = _fallback;
                ActiveCulture = FallbackCulture;
            }
            else
            {
                _active = LoadCulture(culture);
                ActiveCulture = culture;
            }
        }

        /// <summary>
        /// Returns the localized text for <paramref name="key"/> (e.g. "ceilings.projectGrids.title").
        /// When <paramref name="args"/> are supplied, the text is treated as a <see cref="string.Format"/>
        /// template ("Found {0} of {1}"). Lookup order: active culture → English → the key literal
        /// (which is also logged via <see cref="LemoineLog"/> so a missing/mistyped key surfaces in
        /// diagnostics rather than showing blank).
        /// </summary>
        public static string T(string key, params object[] args)
        {
            string text = Resolve(key);
            if (args == null || args.Length == 0) return text;
            try
            {
                return string.Format(CultureInfo.CurrentCulture, text, args);
            }
            catch (FormatException ex)
            {
                LemoineLog.Swallowed($"LemoineStrings.T: bad format template for key '{key}'", ex);
                return text;
            }
        }

        private static string Resolve(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            if (_active.TryGetValue(key, out var v))   return v;
            if (_fallback.TryGetValue(key, out var f)) return f;
            LemoineLog.Warn("LemoineStrings", $"missing string key: {key}");
            return key;
        }

        // ── Loading ──────────────────────────────────────────────────────────────

        private static string RootFolder()
        {
            string asmDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
            return Path.Combine(asmDir, RootFolderName);
        }

        private static IReadOnlyDictionary<string, string> LoadCulture(string culture)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string dir = Path.Combine(RootFolder(), culture);

            if (!Directory.Exists(dir))
            {
                LemoineLog.Warn("LemoineStrings", $"culture folder not found: {dir}");
                return map;
            }

            var serializer = new JavaScriptSerializer();
            foreach (var file in Directory.GetFiles(dir, "*.json"))
            {
                // Each file is its own namespace: "ceilings.projectGrids.json" -> prefix "ceilings.projectGrids".
                string prefix = Path.GetFileNameWithoutExtension(file);
                try
                {
                    string raw     = File.ReadAllText(file);
                    string cleaned = StripComments(raw);
                    if (serializer.DeserializeObject(cleaned) is Dictionary<string, object> root)
                        Flatten(prefix, root, map);
                    else
                        LemoineLog.Warn("LemoineStrings", $"file is not a JSON object, skipped: {file}");
                }
                catch (Exception ex)
                {
                    // Skip the bad file rather than aborting the whole load — its keys fall through to English.
                    LemoineLog.Error($"LemoineStrings: failed to load '{file}'", ex);
                }
            }
            return map;
        }

        // Recursively flatten nested JSON objects into "prefix.key" entries. String leaves are stored
        // verbatim; numeric/bool leaves are stringified; nulls are skipped.
        private static void Flatten(string prefix, Dictionary<string, object> node, Dictionary<string, string> map)
        {
            foreach (var kv in node)
            {
                string key = prefix.Length == 0 ? kv.Key : prefix + "." + kv.Key;
                switch (kv.Value)
                {
                    case null:
                        break;
                    case Dictionary<string, object> child:
                        Flatten(key, child, map);
                        break;
                    case string s:
                        map[key] = s;
                        break;
                    default:
                        map[key] = Convert.ToString(kv.Value, CultureInfo.InvariantCulture) ?? "";
                        break;
                }
            }
        }

        /// <summary>
        /// Removes <c>//</c> comments so the framework JSON reader (which rejects them) can parse the file,
        /// while never touching a <c>//</c> that appears inside a string value (e.g. a URL). Works for both
        /// whole-line and trailing comments by tracking string/escape state as it scans.
        /// </summary>
        internal static string StripComments(string json)
        {
            var sb = new StringBuilder(json.Length);
            bool inString = false, escaped = false;
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];

                if (inString)
                {
                    sb.Append(c);
                    if (escaped)            escaped = false;
                    else if (c == '\\')     escaped = true;
                    else if (c == '"')      inString = false;
                    continue;
                }

                if (c == '"')
                {
                    inString = true;
                    sb.Append(c);
                    continue;
                }

                // Outside a string: a "//" starts a comment that runs to end of line.
                if (c == '/' && i + 1 < json.Length && json[i + 1] == '/')
                {
                    while (i < json.Length && json[i] != '\n') i++;
                    if (i < json.Length) sb.Append('\n'); // keep line numbers aligned for error reports
                    continue;
                }

                sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
