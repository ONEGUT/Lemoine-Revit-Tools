using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace LemoineTools.Framework.Web
{
    /// <summary>
    /// Builds the serializable payload for the HTML Global Settings window (settings.html).
    /// Only the General tab (theme / UI size / language / diagnostics) is migrated to the web
    /// surface so far — the other ribbon-group tabs still render "managed in the classic
    /// window" placeholders. Theme / size / language changes go through the same
    /// <see cref="AppSettings"/> setters as the WPF window, so they persist and propagate live
    /// to every open web window via ThemeChanged / UiSizeChanged.
    /// </summary>
    public static class WebSettings
    {
        // Tab ids mirror GlobalSettingsWindow._navDefs; only "general" carries web content today.
        private static readonly (string Id, string LabelKey)[] Tabs =
        {
            ("general",      "globalSettings.nav.general"),
            ("naming",       "globalSettings.nav.naming"),
            ("setup",        "globalSettings.nav.setup"),
            ("copy",         "globalSettings.nav.copy"),
            ("ceilings",     "globalSettings.nav.ceilings"),
            ("views",        "globalSettings.nav.views"),
            ("filters",      "globalSettings.nav.filters"),
            ("dimensioning", "globalSettings.nav.dimensioning"),
            ("export",       "globalSettings.nav.export"),
        };

        public static Dictionary<string, object?> BuildPayload(string activeTabId)
        {
            return new Dictionary<string, object?>
            {
                ["title"]   = AppStrings.T("globalSettings.window.title"),
                ["close"]   = AppStrings.T("globalSettings.window.close"),
                ["build"]   = BuildInfo.Summary,
                ["active"]  = activeTabId,
                ["tabs"]    = Tabs.Select(t => new Dictionary<string, object?>
                {
                    ["id"]       = t.Id,
                    ["label"]    = AppStrings.T(t.LabelKey),
                    ["migrated"] = t.Id == "general",
                }).ToList(),
                ["general"] = BuildGeneral(),
                ["notMigrated"] = AppStrings.T("globalSettings.web.notMigrated"),
            };
        }

        private static Dictionary<string, object?> BuildGeneral()
        {
            var s = AppSettings.Instance;

            var themes = ThemePalette.All.Select(t => new Dictionary<string, object?>
            {
                ["name"]   = t.Name,
                ["active"] = t == s.ActiveTheme,
                ["dark"]   = IsDark(t),
                ["bg"]     = Hex(t.Bg),
                ["raised"] = Hex(t.Raised),
                ["border"] = Hex(t.Border),
                ["text"]   = Hex(t.Text),
                ["accent"] = Hex(t.Accent),
                ["green"]  = Hex(t.Green),
                ["red"]    = Hex(t.Red),
            }).ToList();

            var sizes = Enum.GetValues(typeof(UiSize)).Cast<UiSize>().Select(sz => new Dictionary<string, object?>
            {
                ["id"]     = sz.ToString(),
                ["name"]   = sz.ToString(),
                ["desc"]   = SizeDesc(sz),
                ["active"] = sz == s.UiSize,
            }).ToList();

            var cultures = AppStrings.AvailableCultures();
            if (cultures.Count == 0) cultures = new List<string> { s.Language };
            var languages = cultures.Select(c => new Dictionary<string, object?>
            {
                ["culture"] = c,
                ["name"]    = LanguageDisplayName(c),
                ["active"]  = c == s.Language,
            }).ToList();

            return new Dictionary<string, object?>
            {
                ["header"]      = AppStrings.T("globalSettings.general.header"),
                ["themeLabel"]  = AppStrings.T("globalSettings.general.colorTheme"),
                ["darkLabel"]   = AppStrings.T("globalSettings.general.dark"),
                ["lightLabel"]  = AppStrings.T("globalSettings.general.light"),
                ["sizeLabel"]   = AppStrings.T("globalSettings.general.uiSize"),
                ["langLabel"]   = AppStrings.T("globalSettings.general.language"),
                ["langHint"]    = AppStrings.T("globalSettings.general.languageHint"),
                ["diagLabel"]   = AppStrings.T("globalSettings.general.diagnostics"),
                ["diagDesc"]    = AppStrings.T("globalSettings.general.diagnosticsDesc"),
                ["logPath"]     = DiagnosticsLog.LogFilePath,
                ["openLog"]     = AppStrings.T("globalSettings.general.openLog"),
                ["activeBadge"] = AppStrings.T("globalSettings.general.active"),
                ["themes"]      = themes,
                ["sizes"]       = sizes,
                ["languages"]   = languages,
            };
        }

        private static string SizeDesc(UiSize sz) =>
            sz == UiSize.Small      ? AppStrings.T("globalSettings.general.sizeSmall") :
            sz == UiSize.Large      ? AppStrings.T("globalSettings.general.sizeLarge") :
            sz == UiSize.ExtraLarge ? AppStrings.T("globalSettings.general.sizeExtraLarge") :
                                      AppStrings.T("globalSettings.general.sizeMedium");

        private static bool IsDark(ThemePalette t)
        {
            var c = t.Bg.Color;
            return 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B < 128;
        }

        private static string Hex(System.Windows.Media.SolidColorBrush brush)
        {
            var c = brush.Color;
            return "#" + c.R.ToString("x2", CultureInfo.InvariantCulture)
                       + c.G.ToString("x2", CultureInfo.InvariantCulture)
                       + c.B.ToString("x2", CultureInfo.InvariantCulture);
        }

        private static string LanguageDisplayName(string culture)
        {
            try { return new CultureInfo(culture).NativeName; }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed($"WebSettings: resolve culture display name for '{culture}'", ex);
                return culture;
            }
        }

        // ── Actions from the page ─────────────────────────────────────────────

        public static void ApplyTheme(string name)
        {
            var theme = ThemePalette.All.FirstOrDefault(t => t.Name == name);
            if (theme != null) AppSettings.Instance.SetTheme(theme);
        }

        public static void ApplySize(string id)
        {
            if (Enum.TryParse<UiSize>(id, out var sz)) AppSettings.Instance.SetUiSize(sz);
        }

        public static void ApplyLanguage(string culture)
        {
            if (!string.IsNullOrWhiteSpace(culture)) AppSettings.Instance.SetLanguage(culture);
        }

        public static void OpenLog() => DiagnosticsLog.OpenInDefaultViewer();
    }
}
