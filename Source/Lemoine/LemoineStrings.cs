using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

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

            foreach (var file in Directory.GetFiles(dir, "*.json"))
            {
                // Each file is its own namespace: "ceilings.projectGrids.json" -> prefix "ceilings.projectGrids".
                string prefix = Path.GetFileNameWithoutExtension(file);
                try
                {
                    string raw     = File.ReadAllText(file);
                    string cleaned = StripComments(raw);
                    if (MiniJson.Parse(cleaned) is Dictionary<string, object?> root)
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
        private static void Flatten(string prefix, Dictionary<string, object?> node, Dictionary<string, string> map)
        {
            foreach (var kv in node)
            {
                string key = prefix.Length == 0 ? kv.Key : prefix + "." + kv.Key;
                switch (kv.Value)
                {
                    case null:
                        break;
                    case Dictionary<string, object?> child:
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

    /// <summary>
    /// Minimal, dependency-free JSON reader. The full framework JSON readers either need a NuGet
    /// package (System.Text.Json / Newtonsoft) or drag System.Web into the WPF XAML compiler
    /// (System.Web.Extensions → MC1000), so this small recursive-descent parser keeps the resource
    /// loader truly zero-dependency. It is deliberately read-only and only as permissive as the
    /// resource files need: objects, arrays, strings, numbers, true/false/null. Comments are removed
    /// upstream by <see cref="LemoineStrings.StripComments"/> before parsing.
    /// </summary>
    internal static class MiniJson
    {
        /// <summary>Parses a JSON document into nested <see cref="Dictionary{TKey,TValue}"/> /
        /// <see cref="List{T}"/> / string / double / bool / null. Throws <see cref="FormatException"/>
        /// on malformed input.</summary>
        public static object? Parse(string json)
        {
            int i = 0;
            object? value = ParseValue(json, ref i);
            SkipWs(json, ref i);
            if (i != json.Length)
                throw new FormatException($"unexpected trailing characters at position {i}");
            return value;
        }

        private static object? ParseValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) throw new FormatException("unexpected end of input");
            char c = s[i];
            switch (c)
            {
                case '{': return ParseObject(s, ref i);
                case '[': return ParseArray(s, ref i);
                case '"': return ParseString(s, ref i);
                case 't': Expect(s, ref i, "true");  return true;
                case 'f': Expect(s, ref i, "false"); return false;
                case 'n': Expect(s, ref i, "null");  return null;
                default:  return ParseNumber(s, ref i);
            }
        }

        private static Dictionary<string, object?> ParseObject(string s, ref int i)
        {
            var obj = new Dictionary<string, object?>(StringComparer.Ordinal);
            i++; // consume '{'
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return obj; }
            while (true)
            {
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != '"') throw new FormatException($"expected key string at position {i}");
                string key = ParseString(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new FormatException($"expected ':' at position {i}");
                i++;
                obj[key] = ParseValue(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("unterminated object");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; break; }
                throw new FormatException($"expected ',' or '}}' at position {i}");
            }
            return obj;
        }

        private static List<object?> ParseArray(string s, ref int i)
        {
            var arr = new List<object?>();
            i++; // consume '['
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return arr; }
            while (true)
            {
                arr.Add(ParseValue(s, ref i));
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("unterminated array");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; break; }
                throw new FormatException($"expected ',' or ']' at position {i}");
            }
            return arr;
        }

        private static string ParseString(string s, ref int i)
        {
            var sb = new StringBuilder();
            i++; // consume opening quote
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c == '\\')
                {
                    if (i >= s.Length) break;
                    char e = s[i++];
                    switch (e)
                    {
                        case '"':  sb.Append('"');  break;
                        case '\\': sb.Append('\\'); break;
                        case '/':  sb.Append('/');  break;
                        case 'b':  sb.Append('\b'); break;
                        case 'f':  sb.Append('\f'); break;
                        case 'n':  sb.Append('\n'); break;
                        case 'r':  sb.Append('\r'); break;
                        case 't':  sb.Append('\t'); break;
                        case 'u':
                            if (i + 4 > s.Length) throw new FormatException("incomplete \\u escape");
                            sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16));
                            i += 4;
                            break;
                        default: throw new FormatException($"invalid escape '\\{e}' at position {i - 1}");
                    }
                }
                else sb.Append(c);
            }
            throw new FormatException("unterminated string");
        }

        private static double ParseNumber(string s, ref int i)
        {
            int start = i;
            while (i < s.Length && (char.IsDigit(s[i]) || "+-.eE".IndexOf(s[i]) >= 0)) i++;
            string token = s.Substring(start, i - start);
            if (double.TryParse(token, System.Globalization.NumberStyles.Any,
                                CultureInfo.InvariantCulture, out double d))
                return d;
            throw new FormatException($"invalid number '{token}' at position {start}");
        }

        private static void Expect(string s, ref int i, string literal)
        {
            if (i + literal.Length > s.Length || s.Substring(i, literal.Length) != literal)
                throw new FormatException($"expected '{literal}' at position {i}");
            i += literal.Length;
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }
    }
}
