using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace LemoineTools.Framework.Web
{
    /// <summary>
    /// Serves the HTML asset folder and injects the live design system (rules R12-R16).
    ///
    /// Pages are loose <c>.html</c>/<c>.css</c>/<c>.js</c> files under the deploy dir's
    /// <c>Web\</c> folder (copied there by the csproj, like <c>Strings\</c>). They are served to
    /// the control over a virtual host (<c>https://lemoine.app/</c> -&gt; <c>Web\</c>, rule R16),
    /// so a page can pull the shared bridge/library scripts with ordinary relative URLs.
    ///
    /// Pages style exclusively through <c>var(--l-*)</c> CSS custom properties (rule R12). Each
    /// file carries a dark fallback <c>:root</c> block so it also renders standalone in a plain
    /// browser (rules R14/R38 - headless-Chromium screenshot verification with no Revit); C#
    /// then overrides those variables live from the active <see cref="ThemePalette"/> +
    /// <see cref="AppSettings"/> scale via <see cref="ApplyVariablesLive"/> after navigation, and
    /// again on every theme / UI-size change with no reload (mirroring WPF DynamicResource).
    ///
    /// Templates are ASCII-only with HTML entities for any non-ASCII glyph (rule R13) so the
    /// Edit tool can always match them.
    /// </summary>
    public static class WebAssets
    {
        /// <summary>Virtual host name the <c>Web\</c> folder is served under (rule R16).</summary>
        public const string VirtualHost = "lemoine.app";

        /// <summary>Absolute path of the deployed <c>Web\</c> asset folder (next to LemoineTools.dll).</summary>
        public static string WebRoot
        {
            get
            {
                string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
                return Path.Combine(dir, "Web");
            }
        }

        /// <summary>The served URL for a page, e.g. <c>PageUrl("debug/stepper.html")</c> -&gt;
        /// <c>https://lemoine.app/debug/stepper.html</c>.</summary>
        public static string PageUrl(string relativePath) =>
            "https://" + VirtualHost + "/" + relativePath.TrimStart('/');

        /// <summary>
        /// Maps the virtual host to the deploy <c>Web\</c> folder for this control. Call once
        /// after the control is initialized, before navigating to a <see cref="PageUrl"/>.
        /// Never throws - a mapping failure is logged and surfaces as a failed navigation.
        /// </summary>
        public static void MapVirtualHost(CoreWebView2 core)
        {
            try
            {
                core.SetVirtualHostNameToFolderMapping(
                    VirtualHost, WebRoot, CoreWebView2HostResourceAccessKind.Allow);
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed("WebAssets: map virtual host", ex);
            }
        }

        // ── CSS variables ─────────────────────────────────────────────────────

        /// <summary>The full <c>--l-*</c> variable set for the given settings (theme + scale).</summary>
        public static IReadOnlyDictionary<string, string> Variables(AppSettings settings)
        {
            var t  = settings.ActiveTheme;
            double sc = settings.Scale;
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["--l-bg"]          = Hex(t.Bg),
                ["--l-page-bg"]     = Hex(t.PageBg),
                ["--l-surface"]     = Hex(t.Surface),
                ["--l-raised"]      = Hex(t.Raised),
                ["--l-select-bg"]   = Hex(t.SelectBg),
                ["--l-border"]      = Hex(t.Border),
                ["--l-border-mid"]  = Hex(t.BorderMid),
                ["--l-text"]        = Hex(t.Text),
                ["--l-text-sub"]    = Hex(t.TextSub),
                ["--l-text-dim"]    = Hex(t.TextDim),
                ["--l-accent"]      = Hex(t.Accent),
                ["--l-accent-dim"]  = Hex(t.AccentDim),
                ["--l-green"]       = Hex(t.Green),
                ["--l-green-dim"]   = Hex(t.GreenDim),
                ["--l-red"]         = Hex(t.Red),
                ["--l-red-dim"]     = Hex(t.RedDim),
                ["--l-knob-on"]     = Hex(t.KnobOn),
                ["--l-knob-off"]    = Hex(t.KnobOff),
                ["--l-ui-font"]     = FontStack(t.UiFont, "'Segoe UI', sans-serif"),
                ["--l-mono-font"]   = FontStack(t.MonoFont, "'Consolas', monospace"),
                ["--l-scale"]       = sc.ToString("0.###", CultureInfo.InvariantCulture),
                ["--l-fs-sm"]       = Px(10.5 * sc),
                ["--l-fs-md"]       = Px(12.0 * sc),
                ["--l-fs-lg"]       = Px(13.0 * sc),
                ["--l-fs-xl"]       = Px(15.0 * sc),
                ["--l-h-input"]     = Px(30 * sc),
                ["--l-h-btn"]       = Px(28 * sc),
                ["--l-h-toolbar"]   = Px(38 * sc),
                ["--l-h-footer"]    = Px(42 * sc),
                ["--l-radius-sm"]   = "3px",
                ["--l-radius-md"]   = "4px",
                ["--l-radius-card"] = "10px",
            };
        }

        /// <summary>
        /// Pushes the current theme/scale variables into an already-navigated page live, with no
        /// reload (rule R12). Call from <c>NavigationCompleted</c> and from the window's
        /// ThemeChanged / UiSizeChanged handlers. Never throws.
        /// </summary>
        public static async void ApplyVariablesLive(WebView2 view)
        {
            try
            {
                if (view?.CoreWebView2 == null) return;
                var sb = new StringBuilder("(function(){var r=document.documentElement.style;");
                foreach (var kv in Variables(AppSettings.Instance))
                    sb.Append("r.setProperty(").Append(WebJson.Serialize(kv.Key)).Append(',')
                      .Append(WebJson.Serialize(kv.Value)).Append(");");
                sb.Append("})();");
                await view.CoreWebView2.ExecuteScriptAsync(sb.ToString());
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed("WebAssets: apply variables live", ex);
            }
        }

        // ── Helpers ─────────────────────────────────────────────────────────────

        private static string Hex(System.Windows.Media.SolidColorBrush brush)
        {
            var c = brush.Color;
            return "#" + c.R.ToString("x2", CultureInfo.InvariantCulture)
                       + c.G.ToString("x2", CultureInfo.InvariantCulture)
                       + c.B.ToString("x2", CultureInfo.InvariantCulture);
        }

        private static string Px(double v) =>
            Math.Round(v, 1).ToString("0.#", CultureInfo.InvariantCulture) + "px";

        private static string FontStack(System.Windows.Media.FontFamily family, string fallback)
        {
            // FontFamily.Source may itself be a comma list ("Nunito Sans, Segoe UI"); append the
            // generic fallback so an uninstalled font still resolves.
            string src = family?.Source ?? "";
            return string.IsNullOrWhiteSpace(src) ? fallback : src + ", " + fallback;
        }
    }
}
