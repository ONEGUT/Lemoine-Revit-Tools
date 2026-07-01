using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Windows.Automation;
using System.Windows.Shapes;
using LemoineTools.Lemoine.Controls;
using LemoineTools.Tools.Ceilings;
using LemoineTools.Tools.LinkViews;

namespace LemoineTools.Lemoine
{
    public partial class GlobalSettingsWindow
    {
        // ═════════════════════════════════════════════════════════════════════
        private UIElement BuildGeneralContent()
        {
            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };
            var panel = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };

            panel.Children.Add(ContentHeader(LemoineStrings.T("globalSettings.general.header")));
            panel.Children.Add(SubLabel(LemoineStrings.T("globalSettings.general.colorTheme")));
            _themeRows.Clear();

            panel.Children.Add(ThemeSectionLabel(LemoineStrings.T("globalSettings.general.dark")));
            panel.Children.Add(BuildThemeCardGrid(LemoineTheme.All.Where(t => IsThemeDark(t))));
            panel.Children.Add(HSep(8));

            panel.Children.Add(ThemeSectionLabel(LemoineStrings.T("globalSettings.general.light")));
            panel.Children.Add(BuildThemeCardGrid(LemoineTheme.All.Where(t => !IsThemeDark(t))));

            panel.Children.Add(HSep(12));
            panel.Children.Add(SubLabel(LemoineStrings.T("globalSettings.general.uiSize")));
            _sizeRows.Clear();
            foreach (LemoineUiSize sz in Enum.GetValues(typeof(LemoineUiSize)))
            {
                var row = BuildSizeRow(sz);
                _sizeRows.Add((row, sz));
                panel.Children.Add(row);
            }

            panel.Children.Add(HSep(12));
            panel.Children.Add(SubLabel(LemoineStrings.T("globalSettings.general.language")));
            _languageRows.Clear();
            var cultures = LemoineStrings.AvailableCultures();
            if (cultures.Count == 0) cultures = new List<string> { LemoineSettings.Instance.Language };
            foreach (var culture in cultures)
            {
                var row = BuildLanguageRow(culture);
                _languageRows.Add((row, culture));
                panel.Children.Add(row);
            }
            panel.Children.Add(SubLabel(LemoineStrings.T("globalSettings.general.languageHint")));

            panel.Children.Add(HSep(12));
            panel.Children.Add(SubLabel(LemoineStrings.T("globalSettings.general.diagnostics")));
            panel.Children.Add(BuildDiagnosticsSection());

            scroll.Content = panel;
            return scroll;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Diagnostics — surfaces the durable log of swallowed/best-effort failures
        // so a half-applied result is never invisible to the user.
        private UIElement BuildDiagnosticsSection()
        {
            var stack = new StackPanel();

            var desc = new TextBlock
            {
                Text = LemoineStrings.T("globalSettings.general.diagnosticsDesc"),
                FontStyle    = FontStyles.Italic,
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 0, 0, 6),
            };
            desc.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            desc.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            desc.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            stack.Children.Add(desc);

            var path = new TextBlock
            {
                Text         = LemoineLog.LogFilePath,
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 0, 0, 10),
            };
            path.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            path.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            path.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            stack.Children.Add(path);

            var btn = BuildFlatButton(LemoineStrings.T("globalSettings.general.openLog"));
            btn.HorizontalAlignment = HorizontalAlignment.Left;
            // OpenInDefaultViewer records its own failure to the log, so the bool
            // result is intentionally not surfaced again here.
            btn.Click += (s, e) => LemoineLog.OpenInDefaultViewer();
            stack.Children.Add(btn);

            return stack;
        }

        // ─────────────────────────────────────────────────────────────────────
        private static bool IsThemeDark(LemoineTheme t)
        {
            var c = t.Bg.Color;
            return 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B < 128;
        }

        private UIElement BuildThemeCardGrid(IEnumerable<LemoineTheme> themes)
        {
            var grid = new UniformGrid { Columns = 2 };
            var list = themes.ToList();
            for (int i = 0; i < list.Count; i++)
            {
                var card = BuildThemeCard(list[i], i);
                _themeRows.Add((card, list[i]));
                grid.Children.Add(card);
            }
            return grid;
        }

        private Border BuildThemeCard(LemoineTheme theme, int index)
        {
            bool active = theme == LemoineSettings.Instance.ActiveTheme;
            bool isLeft = index % 2 == 0;

            var card = new Border
            {
                CornerRadius    = new CornerRadius(8),
                BorderThickness = new Thickness(active ? 2 : 1),
                BorderBrush     = active ? theme.Accent : theme.Border,
                Background      = theme.Bg,
                Padding         = new Thickness(12),
                Margin          = isLeft
                    ? new Thickness(0, 0, 4, 8)
                    : new Thickness(4, 0, 0, 8),
                Cursor          = Cursors.Hand,
            };

            // ── Mini preview ──────────────────────────────────────────────
            var preview = new Border
            {
                Background   = theme.Raised,
                CornerRadius = new CornerRadius(4),
                Padding      = new Thickness(10, 8, 10, 8),
                Margin       = new Thickness(0, 0, 0, 10),
            };
            var previewStack = new StackPanel();

            previewStack.Children.Add(new Border
            {
                Height       = 4,
                Background   = theme.BorderMid,
                CornerRadius = new CornerRadius(2),
                Margin       = new Thickness(0, 0, 0, 8),
            });

            var swatchRow = new StackPanel { Orientation = Orientation.Horizontal };
            swatchRow.Children.Add(ThemeColorSwatch(theme.Accent, 32, 8, new Thickness(0)));
            swatchRow.Children.Add(ThemeColorSwatch(theme.Green,   8, 8, new Thickness(4, 0, 0, 0)));
            swatchRow.Children.Add(ThemeColorSwatch(theme.Red,     8, 8, new Thickness(4, 0, 0, 0)));
            previewStack.Children.Add(swatchRow);

            preview.Child = previewStack;

            // ── Name ─────────────────────────────────────────────────────
            var name = new TextBlock
            {
                Text       = theme.Name,
                Foreground = theme.Text,
                FontWeight = FontWeights.SemiBold,
                FontFamily = theme.UiFont,
            };
            name.SetResourceReference(TextBlock.FontSizeProperty, "LemoineFS_LG");

            // ── Active badge ──────────────────────────────────────────────
            var badge = new Border
            {
                CornerRadius        = new CornerRadius(999),
                BorderThickness     = new Thickness(1),
                BorderBrush         = theme.Accent,
                Padding             = new Thickness(8, 2, 8, 2),
                Margin              = new Thickness(0, 6, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                Visibility          = active ? Visibility.Visible : Visibility.Collapsed,
            };
            var badgeTb = new TextBlock { Text = LemoineStrings.T("globalSettings.general.active"), Foreground = theme.Accent };
            badgeTb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            badgeTb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            badge.Child = badgeTb;

            // Store for RefreshThemeRows()
            card.Tag = badge;

            var content = new StackPanel();
            content.Children.Add(preview);
            content.Children.Add(name);
            content.Children.Add(badge);
            card.Child = content;

            card.MouseLeftButtonDown += (s, e) => { LemoineSettings.Instance.SetTheme(theme); RefreshThemeRows(); };
            return card;
        }

        private static Border ThemeColorSwatch(SolidColorBrush fill, double w, double h, Thickness margin)
            => new Border
            {
                Width        = w,
                Height       = h,
                Background   = fill,
                CornerRadius = new CornerRadius(h / 2),
                Margin       = margin,
            };

        private static TextBlock ThemeSectionLabel(string text)
        {
            var tb = new TextBlock { Text = text, Margin = new Thickness(0, 4, 0, 8) };
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            return tb;
        }

        // ═════════════════════════════════════════════════════════════════════
        private Border BuildSizeRow(LemoineUiSize size)
        {
            bool active = size == LemoineSettings.Instance.UiSize;
            var row = ToggleRowShell(active);
            var pill = Pill(active);
            var name = new TextBlock { Text = size.ToString(), FontWeight = FontWeights.Medium };
            name.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_LG");
            name.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            name.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            var desc = new TextBlock
            {
                Text = size == LemoineUiSize.Small       ? LemoineStrings.T("globalSettings.general.sizeSmall")         :
                       size == LemoineUiSize.Large       ? LemoineStrings.T("globalSettings.general.sizeLarge")     :
                       size == LemoineUiSize.ExtraLarge  ? LemoineStrings.T("globalSettings.general.sizeExtraLarge")  : LemoineStrings.T("globalSettings.general.sizeMedium"),
                FontStyle = FontStyles.Italic, Margin = new Thickness(0, 2, 0, 0),
            };
            desc.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            desc.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            desc.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            var inner = new StackPanel();
            inner.Children.Add(name); inner.Children.Add(desc);
            var dp = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(pill, Dock.Right);
            dp.Children.Add(pill); dp.Children.Add(inner);
            row.Child = dp;
            row.MouseLeftButtonDown += (s, e) => { LemoineSettings.Instance.SetUiSize(size); RefreshSizeRows(); };
            return row;
        }

        // ═════════════════════════════════════════════════════════════════════
        private Border BuildLanguageRow(string culture)
        {
            bool active = culture == LemoineSettings.Instance.Language;
            var row = ToggleRowShell(active);
            var pill = Pill(active);
            var name = new TextBlock { Text = LanguageDisplayName(culture), FontWeight = FontWeights.Medium };
            name.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_LG");
            name.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            name.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            var desc = new TextBlock
            {
                Text      = culture,
                FontStyle = FontStyles.Italic, Margin = new Thickness(0, 2, 0, 0),
            };
            desc.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            desc.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            desc.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            var inner = new StackPanel();
            inner.Children.Add(name); inner.Children.Add(desc);
            var dp = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(pill, Dock.Right);
            dp.Children.Add(pill); dp.Children.Add(inner);
            row.Child = dp;
            row.MouseLeftButtonDown += (s, e) => { LemoineSettings.Instance.SetLanguage(culture); RefreshLanguageRows(); };
            return row;
        }

        // Best-effort friendly name for a culture folder — falls back to the raw folder
        // name when it isn't a recognizable BCP-47 tag (e.g. a custom culture folder).
        private static string LanguageDisplayName(string culture)
        {
            try
            {
                return new System.Globalization.CultureInfo(culture).NativeName;
            }
            catch (Exception ex)
            {
                LemoineLog.Swallowed($"GlobalSettingsWindow: resolve culture display name for '{culture}'", ex);
                return culture;
            }
        }

        private void RefreshThemeRows()
        {
            foreach (var (row, theme) in _themeRows)
            {
                bool active         = theme == LemoineSettings.Instance.ActiveTheme;
                row.BorderBrush     = active ? theme.Accent : theme.Border;
                row.BorderThickness = new Thickness(active ? 2 : 1);
                if (row.Tag is Border badge)
                    badge.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void RefreshSizeRows()
        {
            foreach (var (row, size) in _sizeRows)
            {
                bool active = size == LemoineSettings.Instance.UiSize;
                if (active) row.SetResourceReference(Border.BackgroundProperty, "LemoineRaised");
                else row.Background = Brushes.Transparent;
                row.SetResourceReference(Border.BorderBrushProperty, active ? "LemoineAccent" : "LemoineBorder");
                if (row.Child is DockPanel dp && dp.Children[0] is Border pill)
                    UpdatePill(pill, active);
            }
        }

        private void RefreshLanguageRows()
        {
            foreach (var (row, culture) in _languageRows)
            {
                bool active = culture == LemoineSettings.Instance.Language;
                if (active) row.SetResourceReference(Border.BackgroundProperty, "LemoineRaised");
                else row.Background = Brushes.Transparent;
                row.SetResourceReference(Border.BorderBrushProperty, active ? "LemoineAccent" : "LemoineBorder");
                if (row.Child is DockPanel dp && dp.Children[0] is Border pill)
                    UpdatePill(pill, active);
            }
        }

        private static UIElement HSep(double margin)
        {
            var r = new Rectangle { Height = 1, Margin = new Thickness(0, margin, 0, margin) };
            r.SetResourceReference(Rectangle.FillProperty, "LemoineBorder");
            return r;
        }

        private static Border ToggleRowShell(bool active)
        {
            var b = new Border
            {
                Padding         = new Thickness(12, 10, 12, 10),
                Margin          = new Thickness(0, 0, 0, 4),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(6),
                Cursor          = Cursors.Hand,
            };
            if (active) b.SetResourceReference(Border.BackgroundProperty, "LemoineRaised");
            else b.Background = Brushes.Transparent;
            b.SetResourceReference(Border.BorderBrushProperty, active ? "LemoineAccent" : "LemoineBorder");
            return b;
        }

        private static Border Pill(bool active)
        {
            var b = new Border
            {
                Width             = 8,
                Height            = 8,
                CornerRadius      = new CornerRadius(4),
                VerticalAlignment = VerticalAlignment.Center,
                BorderThickness   = new Thickness(1),
            };
            if (active) b.SetResourceReference(Border.BackgroundProperty, "LemoineAccent");
            else b.Background = Brushes.Transparent;
            b.SetResourceReference(Border.BorderBrushProperty, active ? "LemoineAccent" : "LemoineBorderMid");
            return b;
        }

        private static void UpdatePill(Border pill, bool active)
        {
            if (active) pill.SetResourceReference(Border.BackgroundProperty, "LemoineAccent");
            else pill.Background = Brushes.Transparent;
            pill.SetResourceReference(Border.BorderBrushProperty, active ? "LemoineAccent" : "LemoineBorderMid");
        }
    }
}
