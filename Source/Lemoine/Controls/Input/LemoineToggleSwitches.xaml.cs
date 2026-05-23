using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace LemoineTools.Lemoine.Controls
{
    public class ToggleItem
    {
        public string Id    { get; set; } = null!;
        public string Label { get; set; } = null!;
        public string? Desc  { get; set; }
        public bool   DefaultOn { get; set; } = true;
    }

    public partial class LemoineToggleSwitches : UserControl
    {
        private readonly Dictionary<string, bool>   _state  = new Dictionary<string, bool>();
        private readonly Dictionary<string, ToggleRowBorder>  _rows   = new Dictionary<string, ToggleRowBorder>();
        private readonly Dictionary<string, Ellipse> _knobs  = new Dictionary<string, Ellipse>();
        private List<ToggleItem> _items = new List<ToggleItem>();

        public event Action<Dictionary<string, bool>>? StateChanged;

        /// <summary>Current state of all toggles.</summary>
        public Dictionary<string, bool> State => new Dictionary<string, bool>(_state);

        /// <summary>Accessible name announced by screen readers when the control receives focus.</summary>
        public string AccessibleName { set => AutomationProperties.SetName(this, value ?? string.Empty); }

        public LemoineToggleSwitches() => InitializeComponent();

        public void SetItems(IList<ToggleItem> items, Dictionary<string, bool>? initial = null)
        {
            _items = new List<ToggleItem>(items);
            _stack.Children.Clear();
            _state.Clear();
            _rows.Clear();
            _knobs.Clear();

            foreach (var item in _items)
            {
                bool on = initial != null && initial.TryGetValue(item.Id, out bool v) ? v : item.DefaultOn;
                _state[item.Id] = on;
                _stack.Children.Add(BuildRow(item, on));
            }
        }

        private ToggleRowBorder BuildRow(ToggleItem item, bool on)
        {
            var row = new ToggleRowBorder(on)
            {
                Margin          = new Thickness(0, 0, 0, 3),
                Cursor          = Cursors.Hand,
                BorderThickness = new Thickness(1),
                Focusable       = true,
                FocusVisualStyle = BuildFocusStyle(),
            };
            row.SetResourceReference(Border.CornerRadiusProperty, "LemoineRadius_SM");
            row.SetResourceReference(Border.PaddingProperty, "LemoineTh_CardPad");
            KeyboardNavigation.SetIsTabStop(row, true);
            row.SetResourceReference(Border.BackgroundProperty,   on ? "LemoineAccentDim" : "Transparent");
            row.SetResourceReference(Border.BorderBrushProperty,  on ? "LemoineAccent"    : "Transparent");

            // UIA: expose label, description, and on/off state to screen readers
            AutomationProperties.SetName(row, item.Label);
            AutomationProperties.SetHelpText(row, item.Desc ?? string.Empty);
            AutomationProperties.SetItemStatus(row, on ? "On" : "Off");

            // Toggle pill
            var trackBg = new Border();
            trackBg.SetResourceReference(Border.CornerRadiusProperty, "LemoineRadius_LG");
            trackBg.SetResourceReference(FrameworkElement.WidthProperty,  "LemoineH_Pill_W");
            trackBg.SetResourceReference(FrameworkElement.HeightProperty, "LemoineH_Pill_H");
            trackBg.SetResourceReference(Border.BackgroundProperty, on ? "LemoineAccent" : "LemoineBorder");

            var knob = new Ellipse();
            knob.SetResourceReference(Ellipse.FillProperty, on ? "LemoineKnobOn" : "LemoineKnobOff");
            double onPos = Math.Round(LemoineSettings.Instance.S(28) - LemoineSettings.Instance.S(11) - 2);
            knob.Margin = new Thickness(on ? onPos : 2, 2, 0, 2);
            knob.SetResourceReference(FrameworkElement.WidthProperty,  "LemoineH_Knob");
            knob.SetResourceReference(FrameworkElement.HeightProperty, "LemoineH_Knob");
            var trackCanvas = new Canvas { ClipToBounds = true };
            trackCanvas.SetResourceReference(FrameworkElement.WidthProperty,  "LemoineH_Pill_W");
            trackCanvas.SetResourceReference(FrameworkElement.HeightProperty, "LemoineH_Pill_H");
            trackCanvas.Children.Add(knob);
            trackBg.Child = trackCanvas;
            _knobs[item.Id] = knob;

            // Text
            var labelText = new TextBlock
            {
                Text       = item.Label,
                
                FontWeight = FontWeights.Medium,
                Margin     = new Thickness(0, 0, 0, item.Desc != null ? 2 : 0),
            };
            labelText.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
            labelText.SetResourceReference(TextBlock.ForegroundProperty, on ? "LemoineText" : "LemoineTextSub");
            labelText.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");

            var inner = new StackPanel();
            inner.Children.Add(labelText);
            if (!string.IsNullOrEmpty(item.Desc))
            {
                var descText = new TextBlock { Text = item.Desc };
                descText.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                descText.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                descText.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                inner.Children.Add(descText);
            }

            var dp = new DockPanel();
            DockPanel.SetDock(trackBg, Dock.Left);
            trackBg.Margin = new Thickness(0, 0, 10, 0);
            dp.Children.Add(trackBg);
            dp.Children.Add(inner);
            row.Child = dp;

            // Store row for re-theming
            _rows[item.Id] = row;

            // Click + keyboard handler
            var capturedId = item.Id;
            row.MouseLeftButtonDown += (s, e) => Toggle(capturedId);
            row.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Space || e.Key == Key.Return)
                {
                    Toggle(capturedId);
                    e.Handled = true;
                }
            };

            return row;
        }

        private static Style BuildFocusStyle()
        {
            var template = new ControlTemplate();
            var rect = new FrameworkElementFactory(typeof(System.Windows.Shapes.Rectangle));
            rect.SetValue(System.Windows.Shapes.Rectangle.StrokeThicknessProperty, 1.5);
            rect.SetValue(System.Windows.Shapes.Rectangle.RadiusXProperty, 3.0);
            rect.SetValue(System.Windows.Shapes.Rectangle.RadiusYProperty, 3.0);
            rect.SetValue(System.Windows.Shapes.Rectangle.SnapsToDevicePixelsProperty, true);
            rect.SetResourceReference(System.Windows.Shapes.Rectangle.StrokeProperty, "LemoineAccent");
            template.VisualTree = rect;
            var style = new Style();
            style.Setters.Add(new Setter(Control.TemplateProperty, template));
            return style;
        }

        private void Toggle(string id)
        {
            if (!_state.ContainsKey(id)) return;
            bool newOn = !_state[id];
            _state[id] = newOn;

            var row  = _rows[id];
            var knob = _knobs[id];

            // Update row colours and notify UIA of the state change
            row.SetIsOn(newOn);
            row.SetResourceReference(Border.BackgroundProperty,  newOn ? "LemoineAccentDim" : "Transparent");
            row.SetResourceReference(Border.BorderBrushProperty, newOn ? "LemoineAccent"    : "Transparent");

            // Update knob fill (theme-aware contrast)
            knob.SetResourceReference(Ellipse.FillProperty, newOn ? "LemoineKnobOn" : "LemoineKnobOff");

            // Animate knob position
            double onPos = Math.Round(LemoineSettings.Instance.S(28) - LemoineSettings.Instance.S(11) - 2);
            var anim = new ThicknessAnimation
            {
                To       = new Thickness(newOn ? onPos : 2, 2, 0, 2),
                Duration = TimeSpan.FromMilliseconds(LemoineSettings.Instance.AnimFast),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            knob.BeginAnimation(FrameworkElement.MarginProperty, anim);

            // Update track background
            if (knob.Parent is Canvas c && c.Parent is Border track)
                track.SetResourceReference(Border.BackgroundProperty, newOn ? "LemoineAccent" : "LemoineBorder");

            // Update UIA state so screen readers announce the new value
            AutomationProperties.SetItemStatus(row, newOn ? "On" : "Off");

            StateChanged?.Invoke(State);
        }
    }

    // ── Custom Border subclass that exposes a ToggleButton UIA role ──────────
    // Fixes audit finding R1: Border elements reported as "Pane" by screen readers.
    // Implementing IToggleProvider causes NVDA / JAWS / Narrator to announce
    // the row as a toggle control and read its on/off state correctly.

    internal sealed class ToggleRowBorder : Border, IToggleProvider
    {
        private bool _isOn;

        public ToggleRowBorder(bool initialOn) { _isOn = initialOn; }

        /// <summary>
        /// Call from LemoineToggleSwitches.Toggle() after the visual update so UIA
        /// listeners receive a property-changed event with the new toggle state.
        /// </summary>
        public void SetIsOn(bool value)
        {
            var oldState = _isOn ? ToggleState.On : ToggleState.Off;
            _isOn = value;
            var newState = _isOn ? ToggleState.On : ToggleState.Off;

            if (UIElementAutomationPeer.FromElement(this) is ToggleRowAutomationPeer peer)
                peer.RaiseToggleStateChanged(oldState, newState);
        }

        // IToggleProvider ────────────────────────────────────────────────────
        public ToggleState ToggleState => _isOn ? ToggleState.On : ToggleState.Off;

        /// <summary>
        /// UIA calls this when assistive technology activates the toggle (e.g. NVDA
        /// sends Space via UIA rather than keyboard routing). Keep in sync with the
        /// parent control's Toggle() method by raising MouseLeftButtonDown, which
        /// the parent already handles.
        /// </summary>
        public void Toggle() =>
            RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = Mouse.MouseDownEvent,
            });

        protected override AutomationPeer OnCreateAutomationPeer() =>
            new ToggleRowAutomationPeer(this);
    }

    internal sealed class ToggleRowAutomationPeer : FrameworkElementAutomationPeer
    {
        public ToggleRowAutomationPeer(ToggleRowBorder owner) : base(owner) { }

        private new ToggleRowBorder Owner => (ToggleRowBorder)base.Owner;

        protected override string GetClassNameCore()          => "ToggleSwitch";
        protected override AutomationControlType GetAutomationControlTypeCore()
            => AutomationControlType.CheckBox;   // closest standard UIA type for a toggle switch

        public override object GetPattern(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.Toggle
                ? Owner                          // ToggleRowBorder implements IToggleProvider
                : base.GetPattern(patternInterface);

        /// <summary>Raises UIA ToggleState property-changed event.</summary>
        public void RaiseToggleStateChanged(ToggleState oldState, ToggleState newState) =>
            RaisePropertyChangedEvent(
                TogglePatternIdentifiers.ToggleStateProperty,
                oldState,
                newState);
    }
}
