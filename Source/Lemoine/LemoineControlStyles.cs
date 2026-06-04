using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;

namespace LemoineTools.Lemoine
{
    /// <summary>
    /// Shared WPF control style definitions for Lemoine windows.
    ///
    /// Both StepFlowWindow and GlobalSettingsWindow call InjectInto() with their own
    /// ResourceDictionary. The only per-window difference is scrollbar width — StepFlow
    /// uses 5px (narrow, beside text content) and GlobalSettings uses 8px (wider, beside
    /// the filter rule table).
    /// </summary>
    public static class LemoineControlStyles
    {
        /// <summary>
        /// Injects themed styles for ScrollBar, ComboBox, ComboBoxItem, TextBox,
        /// CheckBox, DatePicker, and DatePickerTextBox into <paramref name="resources"/>.
        /// </summary>
        /// <param name="resources">Target ResourceDictionary (the window's own Resources).</param>
        /// <param name="scrollBarWidth">Scrollbar width in pixels. StepFlow = 5, Settings = 8.</param>
        public static void InjectInto(ResourceDictionary resources, int scrollBarWidth = 5)
        {
            EnsureGlobalScrollBubbling();
            int scaledWidth = (int)Math.Round(scrollBarWidth * LemoineSettings.Instance.Scale);
            resources[typeof(ScrollBar)]    = ParseStyle(ScrollBarXaml(scaledWidth))!;
            resources[typeof(ComboBox)]     = ParseStyle(ComboBoxXaml)!;
            resources[typeof(ComboBoxItem)] = ParseStyle(ComboBoxItemXaml)!;
            resources[typeof(TextBox)]      = MakeTextBoxStyle();
            resources[typeof(CheckBox)]     = MakeCheckBoxStyle();
            resources[typeof(DatePicker)]   = MakeDatePickerStyle();
            resources[typeof(System.Windows.Controls.Primitives.DatePickerTextBox)]
                                            = ParseStyle(DatePickerTextBoxXaml)!;
        }

        // ─────────────────────────────────────────────────────────────────────
        private static Style? ParseStyle(string xaml)
        {
            var wrapped = $@"<ResourceDictionary
                xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
                xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml"">
                {xaml}
            </ResourceDictionary>";
            var dict = (ResourceDictionary)XamlReader.Parse(wrapped);
            foreach (var key in dict.Keys)
                return (Style)dict[key];
            return null;
        }

        // ── ScrollBar — pill thumb, no arrow buttons, both orientations ──────
        private static string ScrollBarXaml(int width) => $@"
<Style TargetType=""{{x:Type ScrollBar}}"">
  <Setter Property=""Background""      Value=""Transparent""/>
  <Setter Property=""BorderThickness"" Value=""0""/>
  <Setter Property=""Width""    Value=""{width}""/>
  <Setter Property=""MinWidth"" Value=""{width}""/>
  <Setter Property=""Template"">
    <Setter.Value>
      <ControlTemplate TargetType=""{{x:Type ScrollBar}}"">
        <Grid Background=""Transparent"" Width=""{width}"">
          <Track Name=""PART_Track"" IsDirectionReversed=""True"">
            <Track.DecreaseRepeatButton>
              <RepeatButton Command=""ScrollBar.LineUpCommand""
                            Background=""Transparent"" BorderThickness=""0""
                            Opacity=""0"" IsTabStop=""False"" Focusable=""False""/>
            </Track.DecreaseRepeatButton>
            <Track.IncreaseRepeatButton>
              <RepeatButton Command=""ScrollBar.LineDownCommand""
                            Background=""Transparent"" BorderThickness=""0""
                            Opacity=""0"" IsTabStop=""False"" Focusable=""False""/>
            </Track.IncreaseRepeatButton>
            <Track.Thumb>
              <Thumb>
                <Thumb.Style>
                  <Style TargetType=""Thumb"">
                    <Setter Property=""Template"">
                      <Setter.Value>
                        <ControlTemplate TargetType=""Thumb"">
                          <Border x:Name=""Bd"" CornerRadius=""3"" Margin=""1,2,1,2""
                                  Background=""{{DynamicResource LemoineBorder}}""/>
                          <ControlTemplate.Triggers>
                            <Trigger Property=""IsMouseOver"" Value=""True"">
                              <Setter TargetName=""Bd"" Property=""Background""
                                      Value=""{{DynamicResource LemoineAccent}}""/>
                            </Trigger>
                            <Trigger Property=""IsDragging"" Value=""True"">
                              <Setter TargetName=""Bd"" Property=""Background""
                                      Value=""{{DynamicResource LemoineAccent}}""/>
                            </Trigger>
                          </ControlTemplate.Triggers>
                        </ControlTemplate>
                      </Setter.Value>
                    </Setter>
                  </Style>
                </Thumb.Style>
              </Thumb>
            </Track.Thumb>
          </Track>
        </Grid>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
  <Style.Triggers>
    <Trigger Property=""Orientation"" Value=""Horizontal"">
      <Setter Property=""Width""    Value=""Auto""/>
      <Setter Property=""MinWidth"" Value=""0""/>
      <Setter Property=""Height""    Value=""8""/>
      <Setter Property=""MinHeight"" Value=""8""/>
      <Setter Property=""Template"">
        <Setter.Value>
          <ControlTemplate TargetType=""{{x:Type ScrollBar}}"">
            <Grid Background=""Transparent"" Height=""8"">
              <Track Name=""PART_Track"" IsDirectionReversed=""False"">
                <Track.DecreaseRepeatButton>
                  <RepeatButton Command=""ScrollBar.LineLeftCommand""
                                Background=""Transparent"" BorderThickness=""0""
                                Opacity=""0"" IsTabStop=""False"" Focusable=""False""/>
                </Track.DecreaseRepeatButton>
                <Track.IncreaseRepeatButton>
                  <RepeatButton Command=""ScrollBar.LineRightCommand""
                                Background=""Transparent"" BorderThickness=""0""
                                Opacity=""0"" IsTabStop=""False"" Focusable=""False""/>
                </Track.IncreaseRepeatButton>
                <Track.Thumb>
                  <Thumb>
                    <Thumb.Style>
                      <Style TargetType=""Thumb"">
                        <Setter Property=""Template"">
                          <Setter.Value>
                            <ControlTemplate TargetType=""Thumb"">
                              <Border x:Name=""Bd"" CornerRadius=""3"" Margin=""2,1,2,1""
                                      Background=""{{DynamicResource LemoineBorder}}""/>
                              <ControlTemplate.Triggers>
                                <Trigger Property=""IsMouseOver"" Value=""True"">
                                  <Setter TargetName=""Bd"" Property=""Background""
                                          Value=""{{DynamicResource LemoineAccent}}""/>
                                </Trigger>
                                <Trigger Property=""IsDragging"" Value=""True"">
                                  <Setter TargetName=""Bd"" Property=""Background""
                                          Value=""{{DynamicResource LemoineAccent}}""/>
                                </Trigger>
                              </ControlTemplate.Triggers>
                            </ControlTemplate>
                          </Setter.Value>
                        </Setter>
                      </Style>
                    </Thumb.Style>
                  </Thumb>
                </Track.Thumb>
              </Track>
            </Grid>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Trigger>
  </Style.Triggers>
</Style>";

        // ── ComboBox — themed dropdown with shadow, slide animation ───────────
        private const string ComboBoxXaml = @"
<Style TargetType=""{x:Type ComboBox}"">
  <Setter Property=""Foreground""      Value=""{DynamicResource LemoineText}""/>
  <Setter Property=""Background""      Value=""{DynamicResource LemoineSelectBg}""/>
  <Setter Property=""BorderBrush""     Value=""{DynamicResource LemoineBorderMid}""/>
  <Setter Property=""BorderThickness"" Value=""1""/>
  <Setter Property=""FontFamily""      Value=""{DynamicResource LemoineUiFont}""/>
  <Setter Property=""FontSize""        Value=""{DynamicResource LemoineFS_MD}""/>
  <Setter Property=""MinHeight""       Value=""{DynamicResource LemoineH_Input}""/>
  <Setter Property=""IsEditable""      Value=""True""/>
  <Setter Property=""IsTextSearchEnabled"" Value=""False""/>
  <Setter Property=""StaysOpenOnEdit"" Value=""True""/>
  <Setter Property=""Template"">
    <Setter.Value>
      <ControlTemplate TargetType=""{x:Type ComboBox}"">
        <Grid>
          <Grid.ColumnDefinitions>
            <ColumnDefinition Width=""*""/>
            <ColumnDefinition Width=""22""/>
          </Grid.ColumnDefinitions>

          <Border x:Name=""Bd"" Grid.ColumnSpan=""2""
                  CornerRadius=""3""
                  Background=""{TemplateBinding Background}""
                  BorderBrush=""{TemplateBinding BorderBrush}""
                  BorderThickness=""{TemplateBinding BorderThickness}""/>

          <TextBox x:Name=""PART_EditableTextBox""
                   Grid.Column=""0""
                   Margin=""1,0,0,0""
                   Padding=""{TemplateBinding Padding}""
                   Background=""Transparent""
                   BorderThickness=""0""
                   Foreground=""{TemplateBinding Foreground}""
                   FontSize=""{TemplateBinding FontSize}""
                   FontFamily=""{TemplateBinding FontFamily}""
                   VerticalContentAlignment=""Center""
                   IsReadOnly=""{Binding IsReadOnly,
                       RelativeSource={RelativeSource TemplatedParent}}"">
            <TextBox.FocusVisualStyle>
              <Style>
                <Setter Property=""Control.Template"">
                  <Setter.Value>
                    <ControlTemplate>
                      <Rectangle StrokeThickness=""1.5""
                                 Stroke=""{DynamicResource LemoineAccent}""
                                 SnapsToDevicePixels=""True""/>
                    </ControlTemplate>
                  </Setter.Value>
                </Setter>
              </Style>
            </TextBox.FocusVisualStyle>
          </TextBox>

          <!-- Non-editable display: shows the selected item when IsEditable=False
               (the editable TextBox above is hidden by the IsEditable trigger below). -->
          <ContentPresenter x:Name=""ContentSite""
                            Grid.Column=""0""
                            Margin=""8,0,0,0""
                            Content=""{TemplateBinding SelectionBoxItem}""
                            ContentTemplate=""{TemplateBinding SelectionBoxItemTemplate}""
                            VerticalAlignment=""Center""
                            IsHitTestVisible=""False""
                            Visibility=""Collapsed""
                            TextElement.Foreground=""{TemplateBinding Foreground}""/>

          <ToggleButton x:Name=""PART_ToggleButton""
                        Grid.Column=""1""
                        Focusable=""False""
                        ClickMode=""Press""
                        IsChecked=""{Binding IsDropDownOpen, Mode=TwoWay,
                            RelativeSource={RelativeSource TemplatedParent}}"">
            <ToggleButton.Template>
              <ControlTemplate TargetType=""{x:Type ToggleButton}"">
                <Border Background=""Transparent"">
                  <Path Data=""M 0 0 L 4 4 L 8 0 Z""
                        Fill=""{DynamicResource LemoineTextDim}""
                        Width=""8"" Height=""4""
                        HorizontalAlignment=""Center""
                        VerticalAlignment=""Center""/>
                </Border>
              </ControlTemplate>
            </ToggleButton.Template>
          </ToggleButton>

          <Popup x:Name=""PART_Popup""
                 Grid.ColumnSpan=""2""
                 Placement=""Bottom""
                 IsOpen=""{TemplateBinding IsDropDownOpen}""
                 AllowsTransparency=""True""
                 Focusable=""False""
                 PopupAnimation=""Slide"">
            <Border CornerRadius=""3""
                    BorderThickness=""1""
                    Padding=""0,3,0,3""
                    MinWidth=""{Binding ActualWidth,
                        RelativeSource={RelativeSource AncestorType=ComboBox}}""
                    MaxHeight=""{TemplateBinding MaxDropDownHeight}""
                    Background=""{DynamicResource LemoineRaised}""
                    BorderBrush=""{DynamicResource LemoineBorderMid}"">
              <Border.Effect>
                <DropShadowEffect BlurRadius=""14"" ShadowDepth=""4""
                                  Opacity=""0.4"" Color=""Black""/>
              </Border.Effect>
              <ScrollViewer MaxHeight=""200"">
                <ItemsPresenter/>
              </ScrollViewer>
            </Border>
          </Popup>
        </Grid>
        <ControlTemplate.Triggers>
          <Trigger Property=""IsEditable"" Value=""False"">
            <Setter TargetName=""PART_EditableTextBox"" Property=""Visibility"" Value=""Collapsed""/>
            <Setter TargetName=""ContentSite""          Property=""Visibility"" Value=""Visible""/>
          </Trigger>
          <Trigger Property=""IsMouseOver"" Value=""True"">
            <Setter TargetName=""Bd"" Property=""BorderBrush""
                    Value=""{DynamicResource LemoineAccent}""/>
          </Trigger>
          <Trigger Property=""IsKeyboardFocusWithin"" Value=""True"">
            <Setter TargetName=""Bd"" Property=""BorderBrush""
                    Value=""{DynamicResource LemoineAccent}""/>
          </Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>";

        // ── Read-only ComboBox — single-choice picker (no editable text box) ──
        // LemoineSingleSelect is a pick-one control, not a free-text combo. The global
        // ComboBox style above forces IsEditable=True, which renders a caret + editable
        // text box. This template shows the selected item as static content with a full-
        // width click target, so it reads as a dropdown, not a text field.
        public static Style BuildReadOnlyComboBoxStyle() => ParseStyle(ReadOnlyComboBoxXaml)!;

        private const string ReadOnlyComboBoxXaml = @"
<Style TargetType=""{x:Type ComboBox}"">
  <Setter Property=""Foreground""      Value=""{DynamicResource LemoineText}""/>
  <Setter Property=""Background""      Value=""{DynamicResource LemoineSelectBg}""/>
  <Setter Property=""BorderBrush""     Value=""{DynamicResource LemoineBorderMid}""/>
  <Setter Property=""BorderThickness"" Value=""1""/>
  <Setter Property=""FontFamily""      Value=""{DynamicResource LemoineUiFont}""/>
  <Setter Property=""FontSize""        Value=""{DynamicResource LemoineFS_MD}""/>
  <Setter Property=""MinHeight""       Value=""{DynamicResource LemoineH_Input}""/>
  <Setter Property=""IsEditable""      Value=""False""/>
  <Setter Property=""IsTextSearchEnabled"" Value=""False""/>
  <Setter Property=""Template"">
    <Setter.Value>
      <ControlTemplate TargetType=""{x:Type ComboBox}"">
        <Grid>
          <Grid.ColumnDefinitions>
            <ColumnDefinition Width=""*""/>
            <ColumnDefinition Width=""22""/>
          </Grid.ColumnDefinitions>

          <Border x:Name=""Bd"" Grid.ColumnSpan=""2""
                  CornerRadius=""3""
                  Background=""{TemplateBinding Background}""
                  BorderBrush=""{TemplateBinding BorderBrush}""
                  BorderThickness=""{TemplateBinding BorderThickness}""/>

          <!-- Full-width invisible click target opens the dropdown -->
          <ToggleButton x:Name=""PART_ToggleButton""
                        Grid.ColumnSpan=""2""
                        Focusable=""False""
                        ClickMode=""Press""
                        IsChecked=""{Binding IsDropDownOpen, Mode=TwoWay,
                            RelativeSource={RelativeSource TemplatedParent}}"">
            <ToggleButton.Template>
              <ControlTemplate TargetType=""{x:Type ToggleButton}"">
                <Border Background=""Transparent""/>
              </ControlTemplate>
            </ToggleButton.Template>
          </ToggleButton>

          <!-- Selected item, static (non-hit-testable so clicks reach the toggle) -->
          <ContentPresenter Grid.Column=""0""
                            Margin=""10,0,0,0""
                            Content=""{TemplateBinding SelectionBoxItem}""
                            ContentTemplate=""{TemplateBinding SelectionBoxItemTemplate}""
                            ContentTemplateSelector=""{TemplateBinding ItemTemplateSelector}""
                            VerticalAlignment=""Center""
                            IsHitTestVisible=""False""
                            TextElement.Foreground=""{TemplateBinding Foreground}""/>

          <Path Grid.Column=""1""
                Data=""M 0 0 L 4 4 L 8 0 Z""
                Fill=""{DynamicResource LemoineTextDim}""
                Width=""8"" Height=""4""
                HorizontalAlignment=""Center""
                VerticalAlignment=""Center""
                IsHitTestVisible=""False""/>

          <Popup x:Name=""PART_Popup""
                 Grid.ColumnSpan=""2""
                 Placement=""Bottom""
                 IsOpen=""{TemplateBinding IsDropDownOpen}""
                 AllowsTransparency=""True""
                 Focusable=""False""
                 PopupAnimation=""Slide"">
            <Border CornerRadius=""3""
                    BorderThickness=""1""
                    Padding=""0,3,0,3""
                    MinWidth=""{Binding ActualWidth,
                        RelativeSource={RelativeSource AncestorType=ComboBox}}""
                    MaxHeight=""{TemplateBinding MaxDropDownHeight}""
                    Background=""{DynamicResource LemoineRaised}""
                    BorderBrush=""{DynamicResource LemoineBorderMid}"">
              <Border.Effect>
                <DropShadowEffect BlurRadius=""14"" ShadowDepth=""4""
                                  Opacity=""0.4"" Color=""Black""/>
              </Border.Effect>
              <ScrollViewer MaxHeight=""200"">
                <ItemsPresenter/>
              </ScrollViewer>
            </Border>
          </Popup>
        </Grid>
        <ControlTemplate.Triggers>
          <Trigger Property=""IsMouseOver"" Value=""True"">
            <Setter TargetName=""Bd"" Property=""BorderBrush""
                    Value=""{DynamicResource LemoineAccent}""/>
          </Trigger>
          <Trigger Property=""IsKeyboardFocusWithin"" Value=""True"">
            <Setter TargetName=""Bd"" Property=""BorderBrush""
                    Value=""{DynamicResource LemoineAccent}""/>
          </Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>";

        // ── ComboBoxItem ──────────────────────────────────────────────────────
        private const string ComboBoxItemXaml = @"
<Style TargetType=""{x:Type ComboBoxItem}"">
  <Setter Property=""Foreground""  Value=""{DynamicResource LemoineText}""/>
  <Setter Property=""Background""  Value=""Transparent""/>
  <Setter Property=""FontFamily""  Value=""{DynamicResource LemoineUiFont}""/>
  <Setter Property=""FontSize""    Value=""{DynamicResource LemoineFS_MD}""/>
  <Setter Property=""FocusVisualStyle"">
    <Setter.Value>
      <Style>
        <Setter Property=""Control.Template"">
          <Setter.Value>
            <ControlTemplate>
              <Rectangle StrokeThickness=""1.5""
                         Stroke=""{DynamicResource LemoineAccent}""
                         SnapsToDevicePixels=""True""/>
            </ControlTemplate>
          </Setter.Value>
        </Setter>
      </Style>
    </Setter.Value>
  </Setter>
  <Setter Property=""Template"">
    <Setter.Value>
      <ControlTemplate TargetType=""{x:Type ComboBoxItem}"">
        <Border x:Name=""Bd"" Padding=""10,5,10,5""
                BorderThickness=""1""
                BorderBrush=""Transparent""
                Background=""{TemplateBinding Background}"">
          <ContentPresenter VerticalAlignment=""Center""/>
        </Border>
        <ControlTemplate.Triggers>
          <Trigger Property=""IsHighlighted"" Value=""True"">
            <Setter Property=""Background"" Value=""{DynamicResource LemoineAccentDim}""/>
          </Trigger>
          <Trigger Property=""IsSelected"" Value=""True"">
            <Setter Property=""Background"" Value=""{DynamicResource LemoineAccentDim}""/>
            <Setter Property=""Foreground"" Value=""{DynamicResource LemoineAccent}""/>
          </Trigger>
          <Trigger Property=""IsKeyboardFocused"" Value=""True"">
            <Setter TargetName=""Bd"" Property=""BorderBrush""
                    Value=""{DynamicResource LemoineAccent}""/>
            <Setter Property=""Background"" Value=""{DynamicResource LemoineAccentDim}""/>
          </Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>";

        // ── TextBox ───────────────────────────────────────────────────────────
        private const string TextBoxXaml = @"
<Style TargetType=""{x:Type TextBox}"">
  <Setter Property=""Background""       Value=""{DynamicResource LemoineSelectBg}""/>
  <Setter Property=""Foreground""       Value=""{DynamicResource LemoineText}""/>
  <Setter Property=""BorderBrush""      Value=""{DynamicResource LemoineBorderMid}""/>
  <Setter Property=""CaretBrush""       Value=""{DynamicResource LemoineText}""/>
  <Setter Property=""SelectionBrush""   Value=""{DynamicResource LemoineAccent}""/>
  <Setter Property=""FontFamily""       Value=""{DynamicResource LemoineUiFont}""/>
  <Setter Property=""FontSize""         Value=""{DynamicResource LemoineFS_MD}""/>
  <Setter Property=""VerticalContentAlignment"" Value=""Center""/>
  <Setter Property=""Template"">
    <Setter.Value>
      <ControlTemplate TargetType=""{x:Type TextBox}"">
        <Border x:Name=""Bd""
                Background=""{TemplateBinding Background}""
                BorderBrush=""{TemplateBinding BorderBrush}""
                BorderThickness=""{TemplateBinding BorderThickness}""
                CornerRadius=""3""
                Padding=""{TemplateBinding Padding}"">
          <ScrollViewer x:Name=""PART_ContentHost""
                        Focusable=""False""
                        VerticalAlignment=""Center""
                        HorizontalScrollBarVisibility=""Hidden""
                        VerticalScrollBarVisibility=""Hidden""/>
        </Border>
        <ControlTemplate.Triggers>
          <Trigger Property=""IsMouseOver"" Value=""True"">
            <Setter TargetName=""Bd"" Property=""BorderBrush""
                    Value=""{DynamicResource LemoineAccent}""/>
          </Trigger>
          <Trigger Property=""IsKeyboardFocused"" Value=""True"">
            <Setter TargetName=""Bd"" Property=""BorderBrush""
                    Value=""{DynamicResource LemoineAccent}""/>
          </Trigger>
          <Trigger Property=""IsEnabled"" Value=""False"">
            <Setter TargetName=""Bd"" Property=""Opacity"" Value=""0.45""/>
          </Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>";

        private static Style MakeTextBoxStyle() => ParseStyle(TextBoxXaml)!;

        // ── ListBoxItem — themed hover + selection (e.g. TagChipInput popup list) ─
        // Assigned per-ListBox via ItemContainerStyle so it doesn't disturb other lists.
        public static Style BuildListBoxItemStyle() => ParseStyle(ListBoxItemXaml)!;

        private const string ListBoxItemXaml = @"
<Style TargetType=""{x:Type ListBoxItem}"">
  <Setter Property=""Foreground"" Value=""{DynamicResource LemoineText}""/>
  <Setter Property=""Background"" Value=""Transparent""/>
  <Setter Property=""FontFamily"" Value=""{DynamicResource LemoineUiFont}""/>
  <Setter Property=""FontSize""   Value=""{DynamicResource LemoineFS_SM}""/>
  <Setter Property=""Cursor""     Value=""Hand""/>
  <Setter Property=""Template"">
    <Setter.Value>
      <ControlTemplate TargetType=""{x:Type ListBoxItem}"">
        <Border x:Name=""Bd""
                Padding=""8,4,8,4""
                CornerRadius=""3""
                Background=""{TemplateBinding Background}"">
          <ContentPresenter VerticalAlignment=""Center""/>
        </Border>
        <ControlTemplate.Triggers>
          <Trigger Property=""IsMouseOver"" Value=""True"">
            <Setter TargetName=""Bd"" Property=""Background""
                    Value=""{DynamicResource LemoineAccentDim}""/>
          </Trigger>
          <Trigger Property=""IsSelected"" Value=""True"">
            <Setter TargetName=""Bd"" Property=""Background""
                    Value=""{DynamicResource LemoineAccentDim}""/>
            <Setter Property=""Foreground"" Value=""{DynamicResource LemoineAccent}""/>
          </Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>";

        // ── CheckBox ──────────────────────────────────────────────────────────
        private static Style MakeCheckBoxStyle()
        {
            var s = new Style(typeof(CheckBox));
            s.Setters.Add(new Setter(CheckBox.ForegroundProperty,  new DynamicResourceExtension("LemoineText")));
            s.Setters.Add(new Setter(CheckBox.FontFamilyProperty,  new DynamicResourceExtension("LemoineUiFont")));
            s.Setters.Add(new Setter(CheckBox.FontSizeProperty,    new DynamicResourceExtension("LemoineFS_MD")));
            return s;
        }

        // ── DatePicker ────────────────────────────────────────────────────────
        private static Style MakeDatePickerStyle()
        {
            var s = new Style(typeof(DatePicker));
            s.Setters.Add(new Setter(DatePicker.BackgroundProperty,  new DynamicResourceExtension("LemoineSelectBg")));
            s.Setters.Add(new Setter(DatePicker.ForegroundProperty,  new DynamicResourceExtension("LemoineText")));
            s.Setters.Add(new Setter(DatePicker.BorderBrushProperty, new DynamicResourceExtension("LemoineBorderMid")));
            s.Setters.Add(new Setter(DatePicker.FontFamilyProperty,  new DynamicResourceExtension("LemoineMonoFont")));
            s.Setters.Add(new Setter(DatePicker.FontSizeProperty,    new DynamicResourceExtension("LemoineFS_MD")));
            return s;
        }

        // ── Button variant system ─────────────────────────────────────────────

        /// <summary>
        /// Visual role for a Lemoine button.
        /// Pass to <see cref="BuildButton"/> to get a correctly styled Button without
        /// manually wiring resource references at every call site.
        /// </summary>
        public enum LemoineButtonVariant
        {
            /// <summary>Transparent background, LemoineText foreground, LemoineBorder border.
            /// Use for toolbar icon buttons and secondary actions.</summary>
            Ghost,

            /// <summary>LemoineAccentDim background, LemoineAccent border + foreground.
            /// Use for the primary confirm / close action in a footer.</summary>
            Primary,

            /// <summary>Transparent background, LemoineRed border + foreground.
            /// Use for destructive actions only: Delete, Discard, Restore Defaults confirm.</summary>
            Danger,
        }

        /// <summary>
        /// Creates a fully configured Lemoine flat Button — template + color resource references.
        /// Replaces the three near-duplicate button factories that previously existed across
        /// LemoineSettingsWindow, GlobalSettingsWindow, and StepFlowWindow.
        /// </summary>
        public static Button BuildButton(
            string label,
            LemoineButtonVariant variant = LemoineButtonVariant.Ghost)
        {
            var b = new Button
            {
                Content         = label,
                Cursor          = Cursors.Hand,
                BorderThickness = new Thickness(1),
                Template        = BuildFlatButtonTemplate(),
            };
            b.SetResourceReference(Button.MinHeightProperty,  "LemoineH_BtnMin");
            b.SetResourceReference(Button.PaddingProperty,    "LemoineTh_BtnPad");
            b.SetResourceReference(Button.FontSizeProperty,   "LemoineFS_MD");
            b.SetResourceReference(Button.FontFamilyProperty, "LemoineUiFont");

            switch (variant)
            {
                case LemoineButtonVariant.Primary:
                    b.SetResourceReference(Button.BackgroundProperty,  "LemoineAccentDim");
                    b.SetResourceReference(Button.BorderBrushProperty, "LemoineAccent");
                    b.SetResourceReference(Button.ForegroundProperty,  "LemoineAccent");
                    break;
                case LemoineButtonVariant.Danger:
                    b.Background = Brushes.Transparent;
                    b.SetResourceReference(Button.BorderBrushProperty, "LemoineRed");
                    b.SetResourceReference(Button.ForegroundProperty,  "LemoineRed");
                    break;
                default: // Ghost
                    b.Background = Brushes.Transparent;
                    b.SetResourceReference(Button.BorderBrushProperty, "LemoineBorder");
                    b.SetResourceReference(Button.ForegroundProperty,  "LemoineText");
                    break;
            }
            return b;
        }

        /// <summary>
        /// Creates a compact Lemoine flat button — smaller height and tighter padding than
        /// <see cref="BuildButton"/>.  Uses <c>LemoineH_BtnSm</c> and <c>LemoineTh_BtnSmPad</c>
        /// tokens so the size scales with the active UI size preset.
        /// Default variant is <see cref="LemoineButtonVariant.Ghost"/>.
        /// </summary>
        public static Button BuildSmallButton(
            string label,
            LemoineButtonVariant variant = LemoineButtonVariant.Ghost)
        {
            var b = new Button
            {
                Content         = label,
                Cursor          = Cursors.Hand,
                BorderThickness = new Thickness(1),
                Template        = BuildFlatButtonTemplate(),
            };
            b.SetResourceReference(Button.HeightProperty,     "LemoineH_BtnSm");
            b.SetResourceReference(Button.PaddingProperty,    "LemoineTh_BtnSmPad");
            b.SetResourceReference(Button.FontSizeProperty,   "LemoineFS_SM");
            b.SetResourceReference(Button.FontFamilyProperty, "LemoineMonoFont");

            switch (variant)
            {
                case LemoineButtonVariant.Primary:
                    b.SetResourceReference(Button.BackgroundProperty,  "LemoineAccentDim");
                    b.SetResourceReference(Button.BorderBrushProperty, "LemoineAccent");
                    b.SetResourceReference(Button.ForegroundProperty,  "LemoineAccent");
                    break;
                case LemoineButtonVariant.Danger:
                    b.Background = Brushes.Transparent;
                    b.SetResourceReference(Button.BorderBrushProperty, "LemoineRed");
                    b.SetResourceReference(Button.ForegroundProperty,  "LemoineRed");
                    break;
                default: // Ghost
                    b.Background = Brushes.Transparent;
                    b.SetResourceReference(Button.BorderBrushProperty, "LemoineBorder");
                    b.SetResourceReference(Button.ForegroundProperty,  "LemoineText");
                    break;
            }
            return b;
        }

        // ── Flat button template — shared by StepFlowWindow and LemoineFileBrowser ──
        /// <summary>
        /// Shared ControlTemplate for flat Lemoine buttons.
        /// Binds Background, BorderBrush, and Padding from the Button; CornerRadius 3;
        /// 0.75 opacity on hover, 0.35 on disabled.
        /// </summary>
        public static ControlTemplate BuildFlatButtonTemplate()
        {
            var t = new ControlTemplate(typeof(Button));
            var b = new FrameworkElementFactory(typeof(Border));
            b.SetBinding(Border.BackgroundProperty,
                new System.Windows.Data.Binding { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent), Path = new PropertyPath(Button.BackgroundProperty) });
            b.SetBinding(Border.BorderBrushProperty,
                new System.Windows.Data.Binding { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent), Path = new PropertyPath(Button.BorderBrushProperty) });
            b.SetBinding(Border.PaddingProperty,
                new System.Windows.Data.Binding { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent), Path = new PropertyPath(Button.PaddingProperty) });
            b.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            b.SetResourceReference(Border.CornerRadiusProperty, "LemoineRadius_SM");
            // Explicit transparent fallback ensures hit testing fires across the full button area
            b.SetValue(Panel.BackgroundProperty, Brushes.Transparent);
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty,   VerticalAlignment.Center);
            b.AppendChild(cp);
            t.VisualTree = b;
            var hov = new Trigger { Property = Button.IsMouseOverProperty, Value = true };
            hov.Setters.Add(new Setter(UIElement.OpacityProperty, 0.75));
            var dis = new Trigger { Property = Button.IsEnabledProperty,   Value = false };
            dis.Setters.Add(new Setter(UIElement.OpacityProperty, 0.35));
            t.Triggers.Add(hov);
            t.Triggers.Add(dis);
            return t;
        }

        // ── DatePickerTextBox ─────────────────────────────────────────────────
        private const string DatePickerTextBoxXaml = @"
<Style xmlns:p=""clr-namespace:System.Windows.Controls.Primitives;assembly=PresentationFramework""
       TargetType=""{x:Type p:DatePickerTextBox}"">
  <Setter Property=""Background""              Value=""{DynamicResource LemoineSelectBg}""/>
  <Setter Property=""Foreground""              Value=""{DynamicResource LemoineText}""/>
  <Setter Property=""CaretBrush""              Value=""{DynamicResource LemoineText}""/>
  <Setter Property=""SelectionBrush""          Value=""{DynamicResource LemoineAccent}""/>
  <Setter Property=""BorderThickness""         Value=""0""/>
  <Setter Property=""Padding""                 Value=""2,0""/>
  <Setter Property=""VerticalContentAlignment"" Value=""Center""/>
  <Setter Property=""Template"">
    <Setter.Value>
      <ControlTemplate TargetType=""{x:Type p:DatePickerTextBox}"">
        <Grid>
          <Border Background=""{DynamicResource LemoineSelectBg}"" BorderThickness=""0"">
            <Grid>
              <ContentControl x:Name=""PART_Watermark""
                              Focusable=""False"" IsHitTestVisible=""False"" Opacity=""0.5"">
                <ContentControl.Template>
                  <ControlTemplate TargetType=""{x:Type ContentControl}"">
                    <TextBlock Text=""{TemplateBinding Content}""
                               VerticalAlignment=""Center""
                               Foreground=""{DynamicResource LemoineTextDim}""
                               FontFamily=""{DynamicResource LemoineMonoFont}""
                               FontSize=""{DynamicResource LemoineFS_MD}""
                               Padding=""2,0""/>
                  </ControlTemplate>
                </ContentControl.Template>
              </ContentControl>
              <ScrollViewer x:Name=""PART_ContentHost"" Background=""Transparent""/>
            </Grid>
          </Border>
        </Grid>
        <ControlTemplate.Triggers>
          <Trigger Property=""Text"" Value="""">
            <Setter TargetName=""PART_Watermark"" Property=""Visibility"" Value=""Visible""/>
          </Trigger>
          <Trigger Property=""Text"" Value=""{x:Null}"">
            <Setter TargetName=""PART_Watermark"" Property=""Visibility"" Value=""Visible""/>
          </Trigger>
          <MultiTrigger>
            <MultiTrigger.Conditions>
              <Condition Property=""Text"" Value=""""/>
              <Condition Property=""IsKeyboardFocused"" Value=""False""/>
            </MultiTrigger.Conditions>
            <Setter TargetName=""PART_Watermark"" Property=""Visibility"" Value=""Visible""/>
          </MultiTrigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>";

    // ── Scroll bubbling ───────────────────────────────────────────────────────
    /// <summary>
    /// Wires up scroll bubbling on <paramref name="inner"/>: when the inner
    /// ScrollViewer hits its top or bottom limit, the wheel event is re-raised
    /// on the parent element so the nearest ancestor ScrollViewer continues scrolling.
    /// </summary>
    public static void WireBubblingScroll(ScrollViewer inner)
    {
        inner.PreviewMouseWheel += (s, e) =>
        {
            // A scroller hosted in a Popup (dropdown, tag-picker) is self-contained — its
            // wheel must never escape into the parent window, or the page's scroll position
            // ends up governing the popup (the "only the parent window scrolls it" bug).
            if (IsInsidePopup(inner)) return;

            bool atTop    = inner.VerticalOffset <= 0;
            bool atBottom = inner.VerticalOffset >= inner.ScrollableHeight - 0.5;
            bool up   = e.Delta > 0;
            bool down = e.Delta < 0;

            if (!((atTop && up) || (atBottom && down))) return;

            e.Handled = true;
            var relay = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = UIElement.MouseWheelEvent,
                Source      = inner,
            };
            (inner.Parent as UIElement)?.RaiseEvent(relay);
        };
    }

    /// <summary>
    /// Stops a CLOSED ComboBox from eating the mouse wheel — by default a WPF ComboBox
    /// changes its selected item on wheel even when closed, which both mutates the value
    /// unexpectedly and traps page scrolling. This re-raises the wheel to the parent so the
    /// page/step scrolls instead. When the dropdown is open the wheel scrolls the list.
    /// </summary>
    public static void WireComboWheelBubbling(ComboBox combo)
    {
        if (combo == null) return;
        combo.PreviewMouseWheel += (s, e) =>
        {
            if (combo.IsDropDownOpen) return; // open list scrolls normally
            e.Handled = true;
            (VisualTreeHelper.GetParent(combo) as UIElement)?.RaiseEvent(
                new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
                {
                    RoutedEvent = UIElement.MouseWheelEvent,
                    Source      = combo,
                });
        };
    }

    // ── Global scroll bubbling (auto-wired, no per-call-site needed) ───────────
    private static bool _scrollBubblingRegistered;

    /// <summary>
    /// Marks a ScrollViewer as "self-contained": the wheel scrolls it directly and never
    /// escapes to an outer scroller, regardless of visual-tree topology. Set this on scrollers
    /// hosted in a Popup (dropdown, tag-picker) where popup-by-tree detection can be unreliable
    /// in Revit's WPF hosting — it is the authoritative signal that overrides ancestor checks.
    /// </summary>
    public static readonly DependencyProperty SelfContainedScrollProperty =
        DependencyProperty.RegisterAttached(
            "SelfContainedScroll", typeof(bool), typeof(LemoineControlStyles),
            new PropertyMetadata(false));

    public static void SetSelfContainedScroll(DependencyObject o, bool value) =>
        o.SetValue(SelfContainedScrollProperty, value);

    public static bool GetSelfContainedScroll(DependencyObject o) =>
        (bool)o.GetValue(SelfContainedScrollProperty);

    /// <summary>
    /// Registers AppDomain-wide class handlers (once) so that EVERY ScrollViewer and
    /// ComboBox bubbles the mouse wheel to its parent when it can't scroll further —
    /// without each call site having to call <see cref="WireBubblingScroll"/> /
    /// <see cref="WireComboWheelBubbling"/>. A WPF ScrollViewer (and a closed ComboBox)
    /// marks the wheel event handled even at its scroll limit, which traps the wheel and
    /// prevents the page behind it from scrolling (the "scroll down but not up" bug).
    /// Called from <see cref="InjectInto"/>, so it is live for every Lemoine window.
    /// </summary>
    internal static void EnsureGlobalScrollBubbling()
    {
        if (_scrollBubblingRegistered) return;
        _scrollBubblingRegistered = true;

        EventManager.RegisterClassHandler(typeof(ScrollViewer),
            UIElement.PreviewMouseWheelEvent,
            new MouseWheelEventHandler(OnScrollViewerWheel), handledEventsToo: false);

        EventManager.RegisterClassHandler(typeof(ComboBox),
            UIElement.PreviewMouseWheelEvent,
            new MouseWheelEventHandler(OnComboWheel), handledEventsToo: false);
    }

    private static void OnScrollViewerWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled) return;
        var sv = (ScrollViewer)sender;

        // A scroller that is explicitly tagged self-contained, or detected as living inside a
        // Popup (dropdown / tag-picker / open ComboBox list), scrolls itself and the wheel must
        // NOT escape into the parent window. The tag is authoritative — the tree-walk popup
        // check is unreliable under Revit's WPF hosting, which let the page's scroll position
        // govern the dropdown (the reported "checks the outside before the inside"). We drive the
        // offset directly: manual scrolling is symmetric by construction and consuming the event
        // keeps the page behind the popup from moving.
        if (GetSelfContainedScroll(sv) || IsInsidePopup(sv))
        {
            e.Handled = true;                          // swallow — never reaches the page
            if (sv.ScrollableHeight <= 0) return;      // nothing to scroll
            double step   = e.Delta / 120.0 * 48.0;    // 3 lines (~48px) per wheel notch
            double target = sv.VerticalOffset - step;
            sv.ScrollToVerticalOffset(Math.Max(0, Math.Min(sv.ScrollableHeight, target)));
            return;
        }

        // PreviewMouseWheel tunnels outer→inner, so this fires on every ScrollViewer
        // ancestor. Only the innermost ScrollViewer under the cursor should act — otherwise
        // an outer scroller at its own limit would steal the wheel before the inner one
        // gets to scroll. Let the deeper scroller handle it on its own tunnelling pass.
        if (FindAncestorScrollViewer(e.OriginalSource as DependencyObject) != sv) return;

        bool atTop    = sv.VerticalOffset <= 0;
        bool atBottom = sv.VerticalOffset >= sv.ScrollableHeight - 0.5;
        bool up   = e.Delta > 0;
        bool down = e.Delta < 0;

        // The inner scroller can still move in this direction → let it scroll normally.
        if (!((atTop && up) || (atBottom && down))) return;

        // At the limit → pass the wheel through to the parent scroller / page.
        e.Handled = true;
        (VisualTreeHelper.GetParent(sv) as UIElement)?.RaiseEvent(
            new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = UIElement.MouseWheelEvent,
                Source      = sv,
            });
    }

    private static void OnComboWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled) return;
        var combo = (ComboBox)sender;
        if (combo.IsDropDownOpen) return; // open list scrolls normally

        // A closed ComboBox eats the wheel (and changes its selection) by default —
        // pass it through to the parent so the page scrolls instead.
        e.Handled = true;
        (VisualTreeHelper.GetParent(combo) as UIElement)?.RaiseEvent(
            new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = UIElement.MouseWheelEvent,
                Source      = combo,
            });
    }

    /// <summary>Walks up the visual tree from <paramref name="start"/> to the nearest ScrollViewer.</summary>
    private static ScrollViewer? FindAncestorScrollViewer(DependencyObject? start)
    {
        for (var node = start; node != null; node = VisualTreeHelper.GetParent(node))
            if (node is ScrollViewer sv) return sv;
        return null;
    }

    /// <summary>
    /// True if <paramref name="element"/> lives inside a Popup. A Popup hosts its content in its
    /// own visual tree rooted at a PopupRoot. We check the hosting PresentationSource's RootVisual
    /// first (robust even when a visual-parent walk is broken mid-tree), then fall back to walking
    /// visual parents to the top. Primarily a backstop — popup scrollers we build also carry the
    /// authoritative <see cref="SelfContainedScrollProperty"/> tag.
    /// </summary>
    private static bool IsInsidePopup(DependencyObject element)
    {
        var root = PresentationSource.FromDependencyObject(element)?.RootVisual;
        if (root != null && root.GetType().Name == "PopupRoot") return true;

        DependencyObject node = element;
        DependencyObject parent;
        while ((parent = VisualTreeHelper.GetParent(node)) != null)
            node = parent;
        return node.GetType().Name == "PopupRoot";
    }

    // ── Floating action pill ────────────────────────────────────────────────
    /// <summary>
    /// Builds a fully-rounded "pill" button (Border-based so the corner radius is
    /// honoured) with a drop shadow, used for the floating bottom-right Create /
    /// Preview actions. <paramref name="primary"/> = accent-filled; otherwise a
    /// neutral surface pill.
    /// </summary>
    public static Border BuildActionPill(string label, bool primary, Action onClick)
    {
        var tb = new TextBlock
        {
            Text                = label,
            VerticalAlignment   = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            IsHitTestVisible    = false,
        };
        tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
        tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
        tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineAccent");

        var pill = new Border
        {
            Padding         = new Thickness(18, 9, 18, 9),
            BorderThickness = new Thickness(1),
            Cursor          = Cursors.Hand,
            Child           = tb,
            Effect          = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 14, Opacity = 0.32, ShadowDepth = 3, Direction = 270,
            },
        };
        pill.SetResourceReference(Border.CornerRadiusProperty, "LemoineRadius_Chip");
        pill.SetResourceReference(Border.BorderBrushProperty,  "LemoineAccent");
        pill.SetResourceReference(Border.BackgroundProperty,   primary ? "LemoineAccentDim" : "LemoineSurface");
        if (!primary)
        {
            pill.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
        }

        pill.MouseEnter += (s, e) =>
        {
            pill.SetResourceReference(Border.BackgroundProperty, "LemoineAccent");
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineKnobOn");
        };
        pill.MouseLeave += (s, e) =>
        {
            pill.SetResourceReference(Border.BackgroundProperty, primary ? "LemoineAccentDim" : "LemoineSurface");
            tb.SetResourceReference(TextBlock.ForegroundProperty, primary ? "LemoineAccent" : "LemoineText");
            if (!primary) pill.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
        };
        pill.MouseLeftButtonUp += (s, e) => { e.Handled = true; onClick(); };
        return pill;
    }

    // ── "Add" ghost pill (floats as last item in a list) ────────────────────
    /// <summary>
    /// Builds a rounded, dashed-feel "＋ Add …" affordance intended to sit as the
    /// final child of a scrolling list panel (trades / rules / legends), replacing
    /// the old sticky bottom bar.
    /// </summary>
    public static Border BuildAddPill(string label, Action onClick)
    {
        var lbl = new TextBlock
        {
            Text                = label,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
            IsHitTestVisible    = false,
        };
        lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
        lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
        lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");

        var border = new Border
        {
            Margin          = new Thickness(4, 6, 4, 6),
            Padding         = new Thickness(10, 7, 10, 7),
            BorderThickness = new Thickness(1),
            Cursor          = Cursors.Hand,
            Background      = Brushes.Transparent,
            Child           = lbl,
        };
        border.SetResourceReference(Border.CornerRadiusProperty, "LemoineRadius_Card");
        border.SetResourceReference(Border.BorderBrushProperty,  "LemoineBorder");

        border.MouseEnter += (s, e) =>
        {
            border.SetResourceReference(Border.BackgroundProperty,  "LemoineAccentDim");
            border.SetResourceReference(Border.BorderBrushProperty, "LemoineAccent");
            lbl.SetResourceReference(TextBlock.ForegroundProperty,  "LemoineText");
        };
        border.MouseLeave += (s, e) =>
        {
            border.Background = Brushes.Transparent;
            border.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
            lbl.SetResourceReference(TextBlock.ForegroundProperty,  "LemoineTextDim");
        };
        border.MouseLeftButtonUp += (s, e) => { e.Handled = true; onClick(); };
        return border;
    }

    // ── Floating status chip ────────────────────────────────────────────────
    /// <summary>
    /// Builds a rounded surface chip (collapsed by default) wrapping a green,
    /// italic status <see cref="TextBlock"/>. Used in the floating action area so
    /// status messages stay legible over content. Toggle the returned Border's
    /// Visibility and set <paramref name="text"/>.Text to show a message.
    /// </summary>
    public static Border BuildStatusChip(out TextBlock text)
    {
        text = new TextBlock
        {
            Text              = "",
            VerticalAlignment = VerticalAlignment.Center,
            FontStyle         = FontStyles.Italic,
            IsHitTestVisible  = false,
        };
        text.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
        text.SetResourceReference(TextBlock.ForegroundProperty, "LemoineGreen");
        text.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");

        var chip = new Border
        {
            Padding         = new Thickness(10, 5, 10, 5),
            BorderThickness = new Thickness(1),
            Visibility      = Visibility.Collapsed,
            Child           = text,
            Effect          = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 10, Opacity = 0.25, ShadowDepth = 2, Direction = 270,
            },
        };
        chip.SetResourceReference(Border.CornerRadiusProperty, "LemoineRadius_Chip");
        chip.SetResourceReference(Border.BackgroundProperty,   "LemoineSurface");
        chip.SetResourceReference(Border.BorderBrushProperty,  "LemoineBorder");
        return chip;
    }
}
}
