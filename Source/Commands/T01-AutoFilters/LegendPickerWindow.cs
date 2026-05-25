using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using LemoineTools.Lemoine;
using WpfGrid = System.Windows.Controls.Grid;

namespace LemoineTools.Commands
{
    /// <summary>
    /// Modal picker that lets the user choose which existing legend view to update.
    /// Shown by <see cref="LegendCreatorUpdateCommand"/> when multiple legend views exist.
    /// </summary>
    internal sealed class LegendPickerWindow : Window
    {
        public ElementId? SelectedLegendId { get; private set; }

        private readonly ListBox _listBox;

        public LegendPickerWindow(IReadOnlyList<(ElementId Id, string Name)> items)
        {

            Title                   = "Select Legend to Update";
            Width                   = 380;
            SizeToContent           = SizeToContent.Height;
            ResizeMode              = ResizeMode.NoResize;
            WindowStartupLocation   = WindowStartupLocation.CenterScreen;
            ShowInTaskbar           = false;

            LemoineSettings.Instance.ApplyTo(Resources);
            LemoineControlStyles.InjectInto(Resources, scrollBarWidth: 5);

            var root = new WpfGrid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });     // prompt
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(180) }); // list
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });     // buttons
            root.Margin = new Thickness(12);
            Content = root;

            // Prompt label
            var prompt = new TextBlock
            {
                Text         = "Choose the legend view to update:",
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 0, 0, 8),
            };
            prompt.SetResourceReference(TextBlock.ForegroundProperty,  "LemoineText");
            prompt.SetResourceReference(TextBlock.FontFamilyProperty,  "LemoineUiFont");
            prompt.SetResourceReference(TextBlock.FontSizeProperty,    "LemoineFS_MD");
            WpfGrid.SetRow(prompt, 0);
            root.Children.Add(prompt);

            // List box
            _listBox = new ListBox { Margin = new Thickness(0, 0, 0, 12) };
            _listBox.SetResourceReference(ListBox.BackgroundProperty,   "LemoineSelectBg");
            _listBox.SetResourceReference(ListBox.ForegroundProperty,   "LemoineText");
            _listBox.SetResourceReference(ListBox.FontFamilyProperty,   "LemoineUiFont");
            _listBox.SetResourceReference(ListBox.FontSizeProperty,     "LemoineFS_MD");
            _listBox.SetResourceReference(ListBox.BorderBrushProperty,  "LemoineBorder");
            foreach (var (id, name) in items)
            {
                var item = new ListBoxItem { Content = name, Tag = id };
                _listBox.Items.Add(item);
            }
            if (_listBox.Items.Count > 0) _listBox.SelectedIndex = 0;
            _listBox.MouseDoubleClick += (s, e) => Confirm();
            WpfGrid.SetRow(_listBox, 1);
            root.Children.Add(_listBox);

            // Button row: [Cancel] [*] [Update]
            var btnRow = new WpfGrid();
            btnRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            btnRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            btnRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            WpfGrid.SetRow(btnRow, 2);
            root.Children.Add(btnRow);

            var cancelBtn = LemoineControlStyles.BuildButton("Cancel", LemoineControlStyles.LemoineButtonVariant.Ghost);
            cancelBtn.IsCancel = true;
            cancelBtn.Click += (s, e) => { SelectedLegendId = null; DialogResult = false; };
            WpfGrid.SetColumn(cancelBtn, 0);
            btnRow.Children.Add(cancelBtn);

            var updateBtn = LemoineControlStyles.BuildButton("Update", LemoineControlStyles.LemoineButtonVariant.Primary);
            updateBtn.IsDefault = true;
            updateBtn.HorizontalAlignment = HorizontalAlignment.Right;
            updateBtn.Click += (s, e) => Confirm();
            WpfGrid.SetColumn(updateBtn, 2);
            btnRow.Children.Add(updateBtn);

            // Set background
            SetResourceReference(BackgroundProperty, "LemoineSurface");
        }

        private void Confirm()
        {
            if (_listBox.SelectedItem is ListBoxItem item && item.Tag is ElementId id)
            {
                SelectedLegendId = id;
                DialogResult = true;
            }
        }
    }
}
