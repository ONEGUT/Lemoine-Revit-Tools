using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace LemoineTools.Lemoine.Controls
{
    /// <summary>
    /// Read-only summary card grid + optional chip row.
    /// Mirrors JS ReviewSummary exactly.
    ///
    /// API:
    ///   SetItems(items, values, chips)
    ///     items  — list of (id, label) pairs
    ///     values — dict of id → display string
    ///     chips  — optional list of chip strings shown below the cards
    /// </summary>
    public partial class LemoineReviewSummary : UserControl
    {
        public LemoineReviewSummary() => InitializeComponent();

        /// <summary>Accessible name announced by screen readers when the control receives focus.</summary>
        public string AccessibleName { set => AutomationProperties.SetName(this, value ?? string.Empty); }

        public void SetItems(
            IList<(string id, string label)> items,
            IDictionary<string, string>      values,
            IList<string>?                   chips = null)
        {
            _root.Children.Clear();

            // 2-column card grid
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition());

            int rows = (int)Math.Ceiling(items.Count / 2.0);
            for (int i = 0; i < rows; i++)
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            for (int i = 0; i < items.Count; i++)
            {
                var (id, label) = items[i];
                values.TryGetValue(id, out var display);

                var card = new Border
                {
                    Margin          = new Thickness(i % 2 == 1 ? 4 : 0, i >= 2 ? 4 : 0, 0, 0),
                    BorderThickness = new Thickness(1),
                    CornerRadius    = new CornerRadius(3),
                    Padding         = new Thickness(10, 7, 10, 7),
                };
                card.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");
                card.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

                var lbl = new TextBlock
                {
                    Text   = label.ToUpper(),
                    Margin = new Thickness(0, 0, 0, 2),
                };
                lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                lbl.SetResourceReference(TextBlock.FontFamilyProperty,  "LemoineUiFont");

                var val = new TextBlock
                {
                    Text         = display ?? "—",
                    FontWeight   = FontWeights.Medium,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                val.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
                val.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                val.SetResourceReference(TextBlock.FontFamilyProperty,  "LemoineMonoFont");

                var sp = new StackPanel();
                sp.Children.Add(lbl);
                sp.Children.Add(val);
                card.Child = sp;

                Grid.SetRow(card, i / 2);
                Grid.SetColumn(card, i % 2);
                grid.Children.Add(card);
            }

            _root.Children.Add(grid);

            // Optional chip row
            if (chips != null && chips.Count > 0)
            {
                var chipBorder = new Border
                {
                    Margin          = new Thickness(0, 6, 0, 0),
                    BorderThickness = new Thickness(1),
                    CornerRadius    = new CornerRadius(3),
                    Padding         = new Thickness(10, 7, 10, 7),
                };
                chipBorder.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");
                chipBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

                var chipHdr = new TextBlock
                {
                    Text   = "ITEMS",
                    
                    Margin = new Thickness(0, 0, 0, 5),
                };
                chipHdr.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                chipHdr.SetResourceReference(TextBlock.FontFamilyProperty,  "LemoineUiFont");

                var chipWrap = new WrapPanel { Orientation = Orientation.Horizontal };
                foreach (var chip in chips)
                {
                    var tag = new Border
                    {
                        Margin  = new Thickness(0, 0, 4, 4),
                        Padding = new Thickness(6, 1, 6, 1),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(2),
                    };
                    tag.SetResourceReference(Border.BackgroundProperty,  "LemoineSurface");
                    tag.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

                    var tagText = new TextBlock { Text = chip };
                    tagText.SetResourceReference(TextBlock.FontSizeProperty, "LemoineFS_SM");
                    tagText.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
                    tagText.SetResourceReference(TextBlock.FontFamilyProperty,  "LemoineMonoFont");
                    tag.Child = tagText;
                    chipWrap.Children.Add(tag);
                }

                var chipInner = new StackPanel();
                chipInner.Children.Add(chipHdr);
                chipInner.Children.Add(chipWrap);
                chipBorder.Child = chipInner;
                _root.Children.Add(chipBorder);
            }
        }
    }
}
