using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

using WpfVisibility = System.Windows.Visibility;

namespace LemoineTools.Framework.Controls
{
    /// <summary>
    /// Collapsible bordered section for the Manage Settings window — a clickable header
    /// (title + expand caret) over a lazily-built body, so a tab with several tools' worth
    /// of defaults can be scanned by header alone. Chrome matches <see cref="SectionCard"/>
    /// (LemoineRadius_MD / LemoineRaised / LemoineBorder); this variant adds the caret toggle.
    /// </summary>
    internal static class ToolSection
    {
        public static Border Build(string title, Action<StackPanel> buildBody, bool startExpanded = true)
        {
            const string expandedGlyph  = "▾"; // ▾
            const string collapsedGlyph = "▸"; // ▸

            var body = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            buildBody(body);
            body.Visibility = startExpanded ? WpfVisibility.Visible : WpfVisibility.Collapsed;

            var caret = new TextBlock
            {
                Text = startExpanded ? expandedGlyph : collapsedGlyph,
                Width = 16, VerticalAlignment = VerticalAlignment.Center,
            };
            caret.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            caret.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            caret.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");

            var titleText = new TextBlock
            {
                Text = title, FontWeight = FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center,
            };
            titleText.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            titleText.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
            titleText.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");

            var header = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Cursor      = Cursors.Hand,
                Background  = Brushes.Transparent, // hit-testable across the whole header row
            };
            header.Children.Add(caret);
            header.Children.Add(titleText);

            bool expanded = startExpanded;
            header.MouseLeftButtonDown += (s, e) =>
            {
                expanded = !expanded;
                body.Visibility = expanded ? WpfVisibility.Visible : WpfVisibility.Collapsed;
                caret.Text = expanded ? expandedGlyph : collapsedGlyph;
            };

            var inner = new StackPanel();
            inner.Children.Add(header);
            inner.Children.Add(body);

            var card = new Border
            {
                Padding = new Thickness(12),
                Margin  = new Thickness(0, 0, 0, 12),
                Child   = inner,
            };
            card.SetResourceReference(Border.CornerRadiusProperty, "LemoineRadius_MD");
            card.SetResourceReference(Border.BackgroundProperty,   "LemoineRaised");
            card.SetResourceReference(Border.BorderBrushProperty,  "LemoineBorder");
            card.BorderThickness = new Thickness(1);

            return card;
        }
    }
}
