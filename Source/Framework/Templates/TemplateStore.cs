using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using LemoineTools.Framework;

namespace LemoineTools.Framework.Templates
{
    // =========================================================================
    // TemplateStore<T>
    //
    // Reusable, file-backed template store usable by any Lemoine tool.
    //
    // Usage pattern:
    //   var store = new TemplateStore<List<FilterTradeConfig>>(
    //       toolId:      "AutoFilters",
    //       serialize:   (data, path) => AutoFiltersSettings.ExportTo(path, data),
    //       deserialize: path => AutoFiltersSettings.TryLoadTrades(path));
    //
    //   store.Save("MEP Standard", myTrades, out _);
    //   var list = store.List();
    //   store.Load(list[0], out var trades, out _);
    //   store.Delete(list[0], out _);
    //
    // Template files live at:
    //   %AppData%\LemoineTools\Templates\{toolId}\{slug}.xml
    //
    // The store is intentionally thin: it owns only the directory,
    // name-to-slug mapping, and file I/O.  Serialization logic stays
    // in the settings class for the tool, keeping concerns separate.
    // =========================================================================

    /// <summary>
    /// Metadata describing a single saved template on disk.
    /// Returned by <see cref="TemplateStore{T}.List"/>.
    /// </summary>
    public class TemplateInfo
    {
        /// <summary>Human-readable display name (derived from file name).</summary>
        public string   Name     { get; internal set; } = "";

        /// <summary>Full path to the template XML file.</summary>
        public string   FilePath { get; internal set; } = "";

        /// <summary>File creation timestamp (used for sort order — newest first).</summary>
        public DateTime Created  { get; internal set; }
    }

    /// <summary>
    /// Generic, file-backed template store.
    /// <para>
    /// Each Lemoine tool creates one instance and passes in delegates for
    /// serializing and deserializing its specific settings type <typeparamref name="T"/>.
    /// </para>
    /// </summary>
    /// <typeparam name="T">The settings type to snapshot (e.g. <c>List&lt;FilterTradeConfig&gt;</c>).</typeparam>
    public class TemplateStore<T>
    {
        private readonly string         _toolId;
        private readonly Action<T, string>  _serialize;    // (data, destinationPath)
        private readonly Func<string, T?>   _deserialize;  // (sourcePath) → data | null

        /// <param name="toolId">
        /// Subfolder name under <c>%AppData%\LemoineTools\Templates\</c>.
        /// Use a short, stable identifier like <c>"AutoFilters"</c>.
        /// </param>
        /// <param name="serialize">
        /// Delegate that writes <typeparamref name="T"/> to the given file path.
        /// Called by <see cref="Save"/>.
        /// </param>
        /// <param name="deserialize">
        /// Delegate that reads and returns <typeparamref name="T"/> from the given file path,
        /// or <see langword="null"/> on failure. Called by <see cref="Load"/>.
        /// </param>
        public TemplateStore(
            string toolId,
            Action<T, string> serialize,
            Func<string, T?> deserialize)
        {
            _toolId      = toolId;
            _serialize   = serialize;
            _deserialize = deserialize;
        }

        // ── Directory resolution ──────────────────────────────────────────────

        /// <summary>
        /// Returns (and creates if necessary) the directory where this tool's
        /// templates are stored.
        /// </summary>
        public string Directory
        {
            get
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "LemoineTools", "Templates", _toolId);
                try { System.IO.Directory.CreateDirectory(dir); } catch (Exception __lex) { DiagnosticsLog.Swallowed("TemplateStore: create storage directory", __lex); }
                return dir;
            }
        }

        // ── Name → file slug ─────────────────────────────────────────────────

        /// <summary>
        /// Converts a human-readable template name to a safe file-name slug.
        /// <para>
        /// Invalid path characters (and the escape character itself) are percent-encoded
        /// as <c>%XX</c>. This is a <b>reversible, injective</b> mapping: two distinct
        /// names never collapse to the same slug, so saving one template can never
        /// silently overwrite another, and <see cref="FromSlug"/> recovers the exact
        /// original name. (The previous scheme replaced every invalid char with an
        /// underscore, so e.g. "A/B" and "A_B" shared one file and round-tripping
        /// corrupted any real underscore into a space.)
        /// </para>
        /// </summary>
        public static string ToSlug(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "template";
            var invalid = new HashSet<char>(Path.GetInvalidFileNameChars());
            var sb = new StringBuilder(name.Length);
            foreach (char c in name.Trim())
            {
                if (c == '%' || invalid.Contains(c))
                    sb.Append('%').Append(((int)c).ToString("X2", CultureInfo.InvariantCulture));
                else
                    sb.Append(c);
            }
            var slug = sb.ToString();
            return string.IsNullOrEmpty(slug) ? "template" : slug;
        }

        /// <summary>
        /// Converts a file-name slug back to its exact display name by reversing the
        /// percent-encoding applied by <see cref="ToSlug"/>. Slugs with no escapes
        /// (the common case, and every legacy template) are returned unchanged.
        /// </summary>
        public static string FromSlug(string slug)
        {
            if (string.IsNullOrEmpty(slug)) return "";
            var sb = new StringBuilder(slug.Length);
            for (int i = 0; i < slug.Length; i++)
            {
                if (slug[i] == '%' && i + 2 < slug.Length &&
                    int.TryParse(slug.Substring(i + 1, 2), NumberStyles.HexNumber,
                                 CultureInfo.InvariantCulture, out int code))
                {
                    sb.Append((char)code);
                    i += 2;
                }
                else
                {
                    sb.Append(slug[i]);
                }
            }
            return sb.ToString();
        }

        // ── Core operations ───────────────────────────────────────────────────

        /// <summary>
        /// Returns all saved templates, sorted newest-first by file creation date.
        /// Returns an empty list (never throws) if the directory does not exist or
        /// cannot be read.
        /// </summary>
        public List<TemplateInfo> List()
        {
            var result = new List<TemplateInfo>();
            try
            {
                foreach (var f in System.IO.Directory.GetFiles(Directory, "*.xml"))
                {
                    result.Add(new TemplateInfo
                    {
                        Name     = FromSlug(Path.GetFileNameWithoutExtension(f)),
                        FilePath = f,
                        Created  = File.GetCreationTime(f),
                    });
                }
            }
            catch (Exception __lex) { DiagnosticsLog.Swallowed("TemplateStore: list templates (directory unreadable)", __lex); }

            var order = ReadOrder();
            if (order.Count > 0)
            {
                // Honour the saved manual order; templates absent from the index (newly
                // saved) fall to the end, newest first.
                result.Sort((a, b) =>
                {
                    int ia = order.IndexOf(SlugOf(a)), ib = order.IndexOf(SlugOf(b));
                    if (ia < 0 && ib < 0) return b.Created.CompareTo(a.Created);
                    if (ia < 0) return 1;
                    if (ib < 0) return -1;
                    return ia.CompareTo(ib);
                });
            }
            else
            {
                result.Sort((a, b) => b.Created.CompareTo(a.Created)); // newest first
            }
            return result;
        }

        private static string SlugOf(TemplateInfo info)
            => Path.GetFileNameWithoutExtension(info.FilePath);

        // Sidecar index ('.order', not a *.xml template) holding the manual display order
        // as one slug per line. Templates support drag-to-reorder; this persists it.
        private string OrderFilePath => Path.Combine(Directory, ".order");

        /// <summary>Persists a manual display order (by template slug) so it survives restarts.</summary>
        public void SaveOrder(IEnumerable<TemplateInfo> orderedTemplates)
        {
            try
            {
                var slugs = new List<string>();
                foreach (var t in orderedTemplates) slugs.Add(SlugOf(t));
                File.WriteAllLines(OrderFilePath, slugs);
            }
            catch (Exception ex) { DiagnosticsLog.Swallowed("TemplateStore: save order", ex); }
        }

        private List<string> ReadOrder()
        {
            try
            {
                if (!File.Exists(OrderFilePath)) return new List<string>();
                var list = new List<string>();
                foreach (var line in File.ReadAllLines(OrderFilePath))
                    if (!string.IsNullOrWhiteSpace(line)) list.Add(line);
                return list;
            }
            catch (Exception ex) { DiagnosticsLog.Swallowed("TemplateStore: read order", ex); return new List<string>(); }
        }

        /// <summary>
        /// Saves <paramref name="data"/> as a template named <paramref name="name"/>.
        /// If a template with the same slug already exists it is overwritten.
        /// </summary>
        /// <param name="name">Display name for the template (e.g. "MEP Standard").</param>
        /// <param name="data">The settings snapshot to store.</param>
        /// <param name="error">
        /// On failure, contains the exception message; <see langword="null"/> on success.
        /// </param>
        /// <returns><see langword="true"/> on success; <see langword="false"/> on failure.</returns>
        public bool Save(string name, T data, out string? error)
        {
            error = null;
            try
            {
                var path = Path.Combine(Directory, ToSlug(name) + ".xml");
                _serialize(data, path);

                // Update the creation timestamp so newly saved templates sort to the top.
                // (Overwriting an existing file preserves the old timestamp on Windows.)
                File.SetCreationTime(path, DateTime.Now);
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        /// <summary>
        /// Loads and deserializes the template described by <paramref name="info"/>.
        /// </summary>
        /// <param name="info">Template metadata returned by <see cref="List"/>.</param>
        /// <param name="data">The deserialized data on success; <see langword="null"/> on failure.</param>
        /// <param name="error">
        /// On failure, contains the exception message; <see langword="null"/> on success.
        /// </param>
        /// <returns><see langword="true"/> on success; <see langword="false"/> on failure.</returns>
        public bool Load(TemplateInfo info, out T? data, out string? error)
        {
            data  = default;
            error = null;
            try
            {
                data = _deserialize(info.FilePath);
                if (data == null) { error = "Deserializer returned null."; return false; }
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        /// <summary>
        /// Deletes the template file described by <paramref name="info"/>.
        /// </summary>
        /// <returns><see langword="true"/> on success; <see langword="false"/> on failure.</returns>
        public bool Delete(TemplateInfo info, out string? error)
        {
            error = null;
            try { File.Delete(info.FilePath); return true; }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        /// <summary>
        /// Returns <see langword="true"/> if a template with the given name
        /// (slug-equivalent) already exists on disk.
        /// </summary>
        public bool Exists(string name)
            => File.Exists(Path.Combine(Directory, ToSlug(name) + ".xml"));
    }
}
