using System;
using System.IO;
using System.Xml.Serialization;

namespace LemoineTools.Framework.Web
{
    /// <summary>
    /// Machine-wide flag that flips every migrated tool between its WPF window (OFF, the
    /// default) and its WebView2 window (ON). Toggled from the Developer ribbon panel.
    /// This is the R25 parallel-verify mechanism at scale: production commands branch on
    /// the flag instead of the ribbon growing a parallel button per migrated tool.
    /// </summary>
    public sealed class WebUiSettings
    {
        private static WebUiSettings? _instance;
        private static readonly object _gate = new object();

        public static WebUiSettings Instance
        {
            get { lock (_gate) { return _instance ??= Load(); } }
        }

        /// <summary>
        /// WPF-only distribution build: the Web UI is hard-disabled. This getter always
        /// reports <c>false</c>, so every production command and bespoke window opens its
        /// WPF window; the setter is a no-op so a persisted flag can never turn it back on.
        /// (The web stack still compiles — see plan-wpf-only-distribution.md.)
        /// </summary>
        public bool Enabled
        {
            get => false;
            set { /* no-op: the Web UI cannot be enabled in this build. */ }
        }

        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LemoineTools", "WebUi.xml");

        private static WebUiSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var ser = new XmlSerializer(typeof(WebUiSettingsDto));
                    using var fs = File.OpenRead(FilePath);
                    if (ser.Deserialize(fs) is WebUiSettingsDto dto)
                        return new WebUiSettings { Enabled = dto.Enabled };
                }
            }
            catch (Exception ex) { DiagnosticsLog.Swallowed("WebUiSettings: load", ex); }
            return new WebUiSettings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var ser = new XmlSerializer(typeof(WebUiSettingsDto));
                using var fs = File.Create(FilePath);
                ser.Serialize(fs, new WebUiSettingsDto { Enabled = Enabled });
            }
            catch (Exception ex) { DiagnosticsLog.Swallowed("WebUiSettings: save", ex); }
        }
    }

    /// <summary>XmlSerializer DTO — must be PUBLIC (an internal root type makes every
    /// save/load fail silently; see CLAUDE.md "XmlSerializer requires public types").</summary>
    public class WebUiSettingsDto
    {
        public bool Enabled { get; set; }
    }
}
