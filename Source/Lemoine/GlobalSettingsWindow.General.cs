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
using LemoineTools.Tools.AutoFilters;
using LemoineTools.Tools.Testing.LegendCreator;
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
        
                    panel.Children.Add(ContentHeader("General"));
                    panel.Children.Add(SubLabel("Colour theme"));
                    _themeRows.Clear();
                    foreach (var t in LemoineTheme.All)
                    {
                        var row = BuildThemeRow(t);
                        _themeRows.Add((row, t));
                        panel.Children.Add(row);
                    }
                    panel.Children.Add(HSep(12));
                    panel.Children.Add(SubLabel("UI size"));
                    _sizeRows.Clear();
                    foreach (LemoineUiSize sz in Enum.GetValues(typeof(LemoineUiSize)))
                    {
                        var row = BuildSizeRow(sz);
                        _sizeRows.Add((row, sz));
                        panel.Children.Add(row);
                    }
        
                    scroll.Content = panel;
                    return scroll;
                }
        
        // ═════════════════════════════════════════════════════════════════════
                private Border BuildThemeRow(LemoineTheme theme)
                {
                    bool active = theme == LemoineSettings.Instance.ActiveTheme;
                    var row = ToggleRowShell(active);
                    var swatch = new Border
                    {
                        Width = 18, Height = 18, CornerRadius = new CornerRadius(9),
                        Background = theme.Swatch, BorderThickness = new Thickness(2),
                        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0),
                    };
                    swatch.SetResourceReference(Border.BorderBrushProperty, active ? "LemoineAccent" : "LemoineBorderMid");
                    var pill = Pill(active);
                    var name = new TextBlock { Text = theme.Name, FontWeight = FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center };
                    name.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_LG");
                    name.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                    name.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                    var dp = new DockPanel { LastChildFill = true };
                    DockPanel.SetDock(swatch, Dock.Left);
                    DockPanel.SetDock(pill,   Dock.Right);
                    dp.Children.Add(swatch); dp.Children.Add(pill); dp.Children.Add(name);
                    row.Child = dp;
                    row.MouseLeftButtonDown += (s, e) => { LemoineSettings.Instance.SetTheme(theme); RefreshThemeRows(); };
                    return row;
                }
        
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
                        Text = size == LemoineUiSize.Small ? "Small — Compact" :
                               size == LemoineUiSize.Large ? "Large — Comfortable" : "Medium — Default",
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
        
        private void RefreshThemeRows()
                {
                    foreach (var (row, theme) in _themeRows)
                    {
                        bool active = theme == LemoineSettings.Instance.ActiveTheme;
                        if (active) row.SetResourceReference(Border.BackgroundProperty, "LemoineRaised");
                        else row.Background = Brushes.Transparent;
                        row.SetResourceReference(Border.BorderBrushProperty, active ? "LemoineAccent" : "LemoineBorder");
                        if (row.Child is DockPanel dp && dp.Children[0] is Border swatch)
                            swatch.SetResourceReference(Border.BorderBrushProperty, active ? "LemoineAccent" : "LemoineBorderMid");
                        if (row.Child is DockPanel dp2 && dp2.Children[1] is Border pill)
                            UpdatePill(pill, active);
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
                        Padding = new Thickness(12, 10, 12, 10),
                        Margin  = new Thickness(0, 0, 0, 4),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(6),
                        Cursor = Cursors.Hand,
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
                        Width = 8, Height = 8, CornerRadius = new CornerRadius(4),
                        VerticalAlignment = VerticalAlignment.Center,
                        BorderThickness = new Thickness(1),
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
