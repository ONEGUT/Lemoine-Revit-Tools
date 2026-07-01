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
                private UIElement BuildSpecContent(ILemoineToolSettings? vm, string tabLabel)
                {
                    var scroll = new ScrollViewer
                    {
                        VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    };
                    var panel = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };
                    panel.Children.Add(ContentHeader(tabLabel));
        
                    var spec = vm?.GetSettingsSpec();
                    if (spec?.Groups == null || spec.Groups.Count == 0)
                    {
                        panel.Children.Add(NoSettings());
                        scroll.Content = panel;
                        return scroll;
                    }
        
                    foreach (var group in spec.Groups)
                    {
                        if (!string.IsNullOrEmpty(group.Title))
                        {
                            var grpHdr = new TextBlock
                            {
                                Text = group.Title, FontWeight = FontWeights.Medium,
                                Margin = new Thickness(0, 16, 0, 6), TextWrapping = TextWrapping.Wrap,  // +4px top
                            };
                            grpHdr.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
                            grpHdr.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
                            grpHdr.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                            panel.Children.Add(grpHdr);
                        }
                        if (!string.IsNullOrEmpty(group.Hint))
                        {
                            var hint = new TextBlock
                            {
                                Text = group.Hint, TextWrapping = TextWrapping.Wrap,
                                Margin = new Thickness(0, 0, 0, 6),
                            };
                            hint.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                            hint.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
                            hint.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                            panel.Children.Add(hint);
                        }
        
                        foreach (var s in group.Settings)
                        {
                            if (!string.IsNullOrEmpty(s.Label))
                            {
                                var lbl = new TextBlock
                                {
                                    Text = s.Label, TextWrapping = TextWrapping.Wrap,
                                    Margin = new Thickness(0, 0, 0, 4),
                                };
                                lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
                                lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                                lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                                panel.Children.Add(lbl);
                            }
        
                            var ctrl = BuildGlobalSpecControl(s, group.Id, vm!);
                            if (ctrl != null) panel.Children.Add(ctrl);
                            panel.Children.Add(new Rectangle { Height = 10 });
                        }
                    }
                    scroll.Content = panel;
                    return scroll;
                }
        
        private UIElement BuildNoSettingsContent()
                {
                    var panel = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };
                    panel.Children.Add(NoSettings());
                    return panel;
                }
        
        // ═════════════════════════════════════════════════════════════════════
                private static UIElement? BuildGlobalSpecControl(LemoineSettingDef setting, string groupId, ILemoineToolSettings ts)
                {
                    switch (setting.Kind)
                    {
                        case "toggle":
                        {
                            bool on = setting.Default is bool b && b;
                            var tog = new Controls.LemoineToggleSwitches();
                            tog.SetItems(new List<Controls.ToggleItem>
                            {
                                new Controls.ToggleItem { Id = setting.Id, Label = setting.Label ?? setting.Id,
                                    Desc = setting.Hint ?? "", DefaultOn = on }
                            });
                            tog.StateChanged += state =>
                            {
                                if (state.TryGetValue(setting.Id, out bool v)) ts?.ApplySettings(groupId, setting.Id, v);
                            };
                            return tog;
                        }
                        case "number":
                        {
                            var opts = setting.Options as NumberOpts;
                            var row = new StackPanel { Orientation = Orientation.Horizontal };
                            var tb = new TextBox { Text = setting.Default?.ToString() ?? "", TextAlignment = TextAlignment.Right };
                            tb.SetResourceReference(TextBox.WidthProperty, "LemoineW_NumInput");
                            tb.SetResourceReference(TextBox.HeightProperty,     "LemoineH_Input");
                            tb.SetResourceReference(TextBox.PaddingProperty,    "LemoineTh_InputPad");
                            tb.SetResourceReference(TextBox.BackgroundProperty, "LemoineSelectBg");
                            tb.SetResourceReference(TextBox.ForegroundProperty, "LemoineText");
                            tb.SetResourceReference(TextBox.FontFamilyProperty, "LemoineMonoFont");
                            tb.SetResourceReference(TextBox.FontSizeProperty,   "LemoineFS_MD");
                            tb.LostFocus += (s, e) => { if (double.TryParse(tb.Text, out double v)) ts?.ApplySettings(groupId, setting.Id, v); };
                            row.Children.Add(tb);
                            if (!string.IsNullOrEmpty(opts?.Unit))
                            {
                                var u = new TextBlock { Text = "  " + opts!.Unit, VerticalAlignment = VerticalAlignment.Center };
                                u.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                                u.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                                u.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                                row.Children.Add(u);
                            }
                            return row;
                        }
                        case "color":
                        {
                            string currentHex = setting.Default as string ?? "#569cd6";
                            return LemoineColorPickerWindow.BuildColorPickerSwatch(
                                getHex: () => currentHex,
                                setHex: h =>
                                {
                                    currentHex = h;
                                    ts?.ApplySettings(groupId, setting.Id, h);
                                });
                        }
                        case "text":
                        {
                            var opts = setting.Options as TextOpts;
                            var tb = new TextBox { Text = setting.Default?.ToString() ?? "" };
                            tb.SetResourceReference(TextBox.WidthProperty, "LemoineW_TextInput");
                            tb.SetResourceReference(TextBox.HeightProperty,     "LemoineH_Input");
                            tb.SetResourceReference(TextBox.PaddingProperty,    "LemoineTh_InputPad");
                            tb.SetResourceReference(TextBox.BackgroundProperty, "LemoineSelectBg");
                            tb.SetResourceReference(TextBox.ForegroundProperty, "LemoineText");
                            tb.SetResourceReference(TextBox.FontSizeProperty,   "LemoineFS_MD");
                            tb.SetResourceReference(TextBox.FontFamilyProperty, (opts?.Mono == true) ? "LemoineMonoFont" : "LemoineUiFont");
                            tb.LostFocus += (s, e) => ts?.ApplySettings(groupId, setting.Id, tb.Text);
                            return tb;
                        }
                        case "info":
                        {
                            var opts = setting.Options as InfoOpts;
                            var tb = new TextBlock { Text = opts?.Text ?? "", TextWrapping = TextWrapping.Wrap, FontStyle = FontStyles.Italic };
                            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
                            return tb;
                        }
                        default: return null;
                    }
                }
        
        private static UIElement NoSettings()
                {
                    var tb = new TextBlock
                    {
                        Text = LemoineStrings.T("globalSettings.spec.noSettings"), FontStyle = FontStyles.Italic,
                        Margin = new Thickness(0, 2, 0, 2),
                    };
                    tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
                    tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                    tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                    return tb;
                }
    }
}
