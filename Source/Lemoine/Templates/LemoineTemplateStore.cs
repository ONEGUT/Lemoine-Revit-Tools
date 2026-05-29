using System;
using System.Collections.Generic;
using System.IO;
using LemoineTools.Lemoine;

namespace LemoineTools.Lemoine.Templates
{
    // =========================================================================
    // LemoineTemplateStore<T>
    //
    // Reusable, file-backed template store usable by any Lemoine tool.
    //
    // Usage pattern:
    //   var store = new LemoineTemplateStore<List<FilterTradeConfig>>(
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
    /// Returned by <see cref="LemoineTemplateStore{T}.List"/>.
    /// </summary>
    public class LemoineTemplateInfo
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
    public class LemoineTemplateStore<T>
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
        public LemoineTemplateStore(
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
                try { System.IO.Directory.CreateDirectory(dir); } catch (Exception __lex) { LemoineLog.Swallowed("TemplateStore: create storage directory", __lex); }
                return dir;
            }
        }

        // ── Name → file slug ─────────────────────────────────────────────────

        /// <summary>
        /// Converts a human-readable template name to a safe file-name slug.
        /// Invalid path characters are replaced with underscores.
        /// </summary>
        public static string ToSlug(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "template";
            var chars   = Path.GetInvalidFileNameChars();
            var cleaned = string.Join("_", name.Split(chars)).Trim();
            return string.IsNullOrEmpty(cleaned) ? "template" : cleaned;
        }

        /// <summary>
        /// Converts a file-name slug back to a display name by replacing
        /// underscores with spaces and applying title-case trimming.
        /// </summary>
        public static string FromSlug(string slug) => slug.Replace('_', ' ').Trim();

        // ── Core operations ───────────────────────────────────────────────────

        /// <summary>
        /// Returns all saved templates, sorted newest-first by file creation date.
        /// Returns an empty list (never throws) if the directory does not exist or
        /// cannot be read.
        /// </summary>
        public List<LemoineTemplateInfo> List()
        {
            var result = new List<LemoineTemplateInfo>();
            try
            {
                foreach (var f in System.IO.Directory.GetFiles(Directory, "*.xml"))
                {
                    result.Add(new LemoineTemplateInfo
                    {
                        Name     = FromSlug(Path.GetFileNameWithoutExtension(f)),
                        FilePath = f,
                        Created  = File.GetCreationTime(f),
                    });
                }
            }
            catch { /* directory unreadable — return empty list */ }

            result.Sort((a, b) => b.Created.CompareTo(a.Created)); // newest first
            return result;
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
        public bool Load(LemoineTemplateInfo info, out T? data, out string? error)
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
        public bool Delete(LemoineTemplateInfo info, out string? error)
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
