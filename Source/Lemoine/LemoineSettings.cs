using System;
using System.IO;
using System.Windows;
using System.Xml.Serialization;

namespace LemoineTools.Lemoine
{
    /// <summary>Discrete UI scale presets available to the user.</summary>
    public enum LemoineUiSize { Small, Medium, Large, ExtraLarge }

    /// <summary>Persisted UI settings DTO — written to %AppData%\LemoineTools\UISettings.xml.</summary>
    /// <remarks>Must be <c>public</c>: <see cref="XmlSerializer"/> cannot process a non-public root type,
    /// which previously made every theme/UI-size save throw and silently drop to defaults on restart.</remarks>
    [XmlRoot("LemoineUISettings")]
    public sealed class UISettingsDto
    {
        [XmlAttribute] public string Theme  { get; set; } = nameof(LemoineTheme.DarkMono);
        [XmlAttribute] public string UiSize { get; set; } = nameof(LemoineUiSize.Medium);
    }

    /// <summary>Singleton facade for persisted UI preferences (theme and scale). Changes are saved to disk immediately.</summary>
    public sealed class LemoineSettings
    {
        /// <summary>The single shared instance of <see cref="LemoineSettings"/>; initialised once and loaded from disk on first access.</summary>
        public static LemoineSettings Instance { get; } = new LemoineSettings();

        /// <summary>Currently active colour theme applied to all Lemoine windows.</summary>
        public LemoineTheme  ActiveTheme { get; private set; } = LemoineTheme.DarkMono;

        /// <summary>Currently active UI size preset that drives the <see cref="Scale"/> multiplier.</summary>
        public LemoineUiSize UiSize      { get; private set; } = LemoineUiSize.Medium;

        /// <summary>Fired after <see cref="ActiveTheme"/> changes; the new theme is passed as the argument.</summary>
        public event Action<LemoineTheme>?  ThemeChanged;

        /// <summary>Fired after <see cref="UiSize"/> changes; the new size is passed as the argument.</summary>
        public event Action<LemoineUiSize>? UiSizeChanged;

        private LemoineSettings()
        {
            LoadFromDisk();
        }

        /// <summary>Switches the active theme, raises <see cref="ThemeChanged"/>, and persists the choice to disk. No-op if the theme is unchanged.</summary>
        /// <param name="theme">The theme to activate.</param>
        public void SetTheme(LemoineTheme theme)
        {
            if (theme == ActiveTheme) return;
            ActiveTheme = theme;
            ThemeChanged?.Invoke(theme);
            SaveToDisk();
        }

        /// <summary>Switches the active UI size, raises <see cref="UiSizeChanged"/>, and persists the choice to disk. No-op if the size is unchanged.</summary>
        /// <param name="size">The size preset to activate.</param>
        public void SetUiSize(LemoineUiSize size)
        {
            if (size == UiSize) return;
            UiSize = size;
            UiSizeChanged?.Invoke(size);
            SaveToDisk();
        }

        /// <summary>Multiplier: Small=0.85, Medium=1.0, Large=1.2, ExtraLarge=1.40</summary>
        public double Scale => UiSize switch
        {
            LemoineUiSize.Small      => 0.85,
            LemoineUiSize.Large      => 1.20,
            LemoineUiSize.ExtraLarge => 1.40,
            _                        => 1.00,
        };

        // ── Animation durations (ms) — type-safe for TimeSpan.FromMilliseconds() ──
        /// <summary>Duration in milliseconds for fast transitions such as hover highlights (180 ms).</summary>
        public double AnimFast     => 180;
        /// <summary>Duration in milliseconds for standard transitions such as panel fades (220 ms).</summary>
        public double AnimMed      => 220;
        /// <summary>Duration in milliseconds for expand/collapse animations (280 ms).</summary>
        public double AnimExpand   => 280;
        /// <summary>Duration in milliseconds for progress-bar fill animations (350 ms).</summary>
        public double AnimProgress => 350;
        /// <summary>Duration in milliseconds for the press/tap scale feedback — snappy (90 ms).</summary>
        public double AnimPress    => 90;

        // ── Convenience scaled helpers ────────────────────────────────────────
        /// <summary>Scales a raw design-pixel value by <see cref="Scale"/> and rounds to one decimal place.</summary>
        /// <param name="v">The unscaled design-pixel value.</param>
        /// <returns>The scaled value rounded to one decimal place.</returns>
        public double S(double v) => Math.Round(v * Scale, 1);

        /// <summary>Creates a <see cref="Thickness"/> with each side scaled by <see cref="Scale"/> and rounded to one decimal place.</summary>
        /// <param name="l">Left inset in design pixels.</param>
        /// <param name="t">Top inset in design pixels.</param>
        /// <param name="r">Right inset in design pixels.</param>
        /// <param name="b">Bottom inset in design pixels.</param>
        /// <returns>A <see cref="Thickness"/> with each value scaled and rounded.</returns>
        public Thickness Th(double l, double t, double r, double b)
            => new Thickness(Math.Round(l*Scale,1), Math.Round(t*Scale,1),
                             Math.Round(r*Scale,1), Math.Round(b*Scale,1));

        // ── Row heights (for manual RowDefinition updates) ────────────────────
        /// <summary>Scaled toolbar row height in pixels (base 38 px), rounded to the nearest whole pixel.</summary>
        public double ToolbarHeight => Math.Round(38 * Scale);

        /// <summary>Scaled footer row height in pixels (base 42 px), rounded to the nearest whole pixel.</summary>
        public double FooterHeight  => Math.Round(42 * Scale);

        // ── Apply everything to a ResourceDictionary ──────────────────────────
        /// <summary>Applies both the active theme colours and scale resources to <paramref name="r"/>.</summary>
        /// <param name="r">The <see cref="ResourceDictionary"/> to update in place.</param>
        public void ApplyTo(ResourceDictionary r)
        {
            ActiveTheme.ApplyTo(r);
            ApplyScaleTo(r);

            // ── Warning banner aliases ────────────────────────────────────────
            // LemoineWarnBg/Border/Text are semantic aliases for the Red family.
            // No separate amber tier exists; destructive warnings use Red.
            r["LemoineWarnBg"]     = r["LemoineRedDim"];
            r["LemoineWarnBorder"] = r["LemoineRed"];
            r["LemoineWarnText"]   = r["LemoineRed"];
        }

        /// <summary>Writes all scale-dependent resource keys (font sizes, heights, corner radii, thickness values) to <paramref name="r"/> using the current <see cref="Scale"/>.</summary>
        /// <param name="r">The <see cref="ResourceDictionary"/> to update in place.</param>
        public void ApplyScaleTo(ResourceDictionary r)
        {
            double sc = Scale;

            // ── Font sizes ────────────────────────────────────────────────────
            r["LemoineFS_SM"]      = Math.Round(10.5 * sc, 1);
            r["LemoineFS_XS"]      = r["LemoineFS_SM"];   // XS removed — alias to SM
            r["LemoineFS_MD"]      = Math.Round(12.0 * sc, 1);
            r["LemoineFS_LG"]      = Math.Round(13.0 * sc, 1);
            r["LemoineFS_XL"]      = Math.Round(15.0 * sc, 1);
            r["LemoineFS_Chevron"] = Math.Round(20.0 * sc, 1);

            // ── Heights ───────────────────────────────────────────────────────
            r["LemoineH_Toolbar"]  = Math.Round(38 * sc);
            r["LemoineH_Footer"]   = Math.Round(42 * sc);
            r["LemoineH_BtnMin"]   = Math.Round(28 * sc);
            r["LemoineH_BtnSm"]    = Math.Round(26 * sc);
            r["LemoineH_Input"]    = Math.Round(30 * sc);
            r["LemoineH_Circle"]   = Math.Round(18 * sc);
            r["LemoineH_Pip"]      = Math.Round( 3 * sc, 1);
            r["LemoineH_ProgBar"]  = Math.Round( 4 * sc, 1);
            r["LemoineH_Icon_SM"]  = Math.Round(13 * sc, 1);
            r["LemoineH_Icon_MD"]  = Math.Round(20 * sc, 1);
            r["LemoineH_Pill_W"]   = Math.Round(28 * sc, 1);
            r["LemoineH_Pill_H"]   = Math.Round(15 * sc, 1);
            r["LemoineH_Knob"]     = Math.Round(11 * sc, 1);
            r["LemoineH_Sep"]      = 1.0;
            r["LemoineH_LogArea"]  = Math.Round(90 * sc);
            r["LemoineW_NumInput"] = Math.Round(90  * sc);
            r["LemoineW_TextInput"]= Math.Round(240 * sc);

            // ── Corner radii (design constants — not scaled) ──────────────────
            r["LemoineRadius_SM"]   = new System.Windows.CornerRadius(3);
            r["LemoineRadius_MD"]   = new System.Windows.CornerRadius(4);
            r["LemoineRadius_Chip"] = new System.Windows.CornerRadius(10);

            // ── Thickness resources ───────────────────────────────────────────
            r["LemoineTh_ToolbarMar"]   = Th(14, 0, 14, 0);
            r["LemoineTh_StepListMar"]  = Th(12, 0, 12, 4);
            r["LemoineTh_ProgressMar"]  = Th(12, 8, 12, 0);
            r["LemoineTh_FooterPad"]    = Th(12, 0, 12, 0);
            r["LemoineTh_RowPad"]       = Th(12, 8, 12, 8);
            r["LemoineTh_ContentPad"]   = Th(14, 0, 14, 12);
            r["LemoineTh_CardPad"]      = Th(10, 7, 10, 7);
            r["LemoineTh_CardMar"]      = Th( 0, 4,  0,  0);
            r["LemoineTh_SectionPad"]   = Th(14, 10, 14, 14);
            r["LemoineTh_SectionHdrPad"]= Th(14, 12, 14, 12);
            r["LemoineTh_BtnPad"]       = Th(14, 0, 14, 0);
            r["LemoineTh_BtnSmPad"]     = Th( 8, 3,  8,  3);
            r["LemoineTh_InputPad"]     = Th( 7, 5,  7,  5);
            r["LemoineTh_LogPad"]       = Th( 8, 6,  8,  6);
            r["LemoineTh_LogMar"]       = Th(12, 8, 12,  8);
            r["LemoineTh_ItemMar"]      = Th( 0, 0,  0,  5);
            r["LemoineTh_HeaderMar"]    = Th( 0, 0,  0, 16);
            r["LemoineTh_SubLabelMar"]  = Th( 0, 0,  0,  8);
            r["LemoineTh_ProgCountMar"] = Th(10, 0, 10,  0);
            r["LemoineTh_CircleMar"]    = Th( 0, 0,  8,  0);
            r["LemoineTh_GearMar"]      = Th( 0, 0, 10,  0);
            r["LemoineTh_NavPillPad"]   = Th(12, 6, 12,  6);
        }

        // ── Disk persistence ─────────────────────────────────────────────────

        private static string SettingsFilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LemoineTools", "UISettings.xml");

        private void LoadFromDisk()
        {
            try
            {
                string path = SettingsFilePath;
                if (!File.Exists(path)) return;
                var xs = new XmlSerializer(typeof(UISettingsDto));
                using (var r = new StreamReader(path))
                {
                    var dto = (UISettingsDto)xs.Deserialize(r)!;
                    var t = System.Array.Find(LemoineTheme.All, x => x.Name == dto.Theme);
                    if (t != null) ActiveTheme = t;
                    if (Enum.TryParse<LemoineUiSize>(dto.UiSize, out var s))
                        UiSize = s;
                }
            }
            catch (Exception ex)
            {
                LemoineLog.Swallowed("LemoineSettings: load UISettings.xml (using defaults)", ex);
            }
        }

        private void SaveToDisk()
        {
            try
            {
                string path = SettingsFilePath;
                string? dir = Path.GetDirectoryName(path);
                if (dir != null) Directory.CreateDirectory(dir);
                var dto = new UISettingsDto { Theme = ActiveTheme.Name, UiSize = UiSize.ToString() };
                var xs = new XmlSerializer(typeof(UISettingsDto));
                using (var w = new StreamWriter(path)) xs.Serialize(w, dto);
            }
            catch (Exception ex)
            {
                LemoineLog.Error("LemoineSettings: save UISettings.xml failed — theme/size will not persist", ex);
            }
        }
    }
}
