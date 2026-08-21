using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Framework.Zones;

namespace LemoineTools.Tools.Zones.Windows
{
    // =========================================================================
    // The three generation flows, in the toolbar.
    //
    // Each one saves the library, raises an ExternalEvent, and opens the
    // EXISTING step flow unchanged — this rework does not touch their designs.
    // The ↗ on each button is what says so: this opens its own window.
    //
    // All three are disabled while one is open, so a user cannot start Build
    // Sheets on top of a half-finished Discover and have the second flow read a
    // library the first is still writing.
    // =========================================================================
    public partial class ZoneManagerWindow
    {
        /// <summary>A toolbar flow button. Disabled buttons keep their border and dim their text.</summary>
        private UIElement MakeFlowButton(string label, Action onClick, bool accent, bool enabled)
        {
            var st = AppSettings.Instance;

            var t = new TextBlock
            {
                // Trailing north-east arrow: this opens a window of its own.
                Text = label + " " + char.ConvertFromUtf32(0x2197),
                VerticalAlignment = VerticalAlignment.Center,
                Background = Brushes.Transparent,
            };
            t.FontFamily = st.ActiveTheme.MonoFont;
            t.SetResourceReference(TextBlock.FontSizeProperty, "LemoineFS_SM");

            // Never a ternary inside SetResourceReference — it would resolve the string itself.
            if (!enabled)    { t.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim"); t.Opacity = 0.55; }
            else if (accent)   t.SetResourceReference(TextBlock.ForegroundProperty, "LemoineAccent");
            else               t.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");

            var b = new Border
            {
                Height  = st.S(26),
                Padding = st.Th(8, 0, 8, 0),
                Margin  = st.Th(4, 0, 0, 0),
                BorderThickness = new Thickness(1),
                Child = t,
                Background = Brushes.Transparent,
                Cursor = enabled ? Cursors.Hand : Cursors.Arrow,
            };
            b.SetResourceReference(Border.CornerRadiusProperty, "LemoineRadius_SM");

            if (accent && enabled)
            {
                b.SetResourceReference(Border.BorderBrushProperty, "LemoineAccent");
                b.SetResourceReference(Border.BackgroundProperty,  "LemoineAccentDim");
            }
            else b.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            // _toolbarBorder drags the window on any UNHANDLED MouseLeftButtonDown that
            // bubbles up to it. Every button here lives inside that subtree, so without this,
            // a click's Down bubbles past the button (which only listens for Up) and starts
            // Window.DragMove() — a blocking native move/drag that swallows the matching Up
            // and eats the click. Marking Down handled here, before it bubbles, is what makes
            // toolbar buttons register on the first click instead of needing a lucky fast one.
            b.PreviewMouseLeftButtonDown += (s, e) => e.Handled = true;

            if (!enabled)
            {
                b.ToolTip = AppStrings.T("zones.manager.flows.needExtents");
                return b;
            }

            b.MouseLeftButtonUp += (s, e) =>
            {
                try { onClick(); }
                catch (Exception ex) { DiagnosticsLog.Error($"ZoneManagerWindow: flow '{label}'", ex); }
                e.Handled = true;
            };
            return b;
        }

        private UIElement MakeCloseButton()
        {
            var st = AppSettings.Instance;

            var t = new TextBlock
            {
                Text = char.ConvertFromUtf32(0x2715),   // multiplication x
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
                Background = Brushes.Transparent,
            };
            t.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            t.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_XL");

            var b = new Border
            {
                Width = st.S(22), Height = st.S(22),
                Cursor = Cursors.Hand,
                Child = t,
                ToolTip = AppStrings.T("zones.manager.close"),
                // Without this the button responds only where the glyph's ink is.
                Background = Brushes.Transparent,
            };
            // Same DragMove-eats-the-click guard as MakeFlowButton — see the comment there.
            b.PreviewMouseLeftButtonDown += (s, e) => e.Handled = true;
            b.MouseLeftButtonUp += (s, e) => { Close(); e.Handled = true; };
            return b;
        }

        // ── Launchers ────────────────────────────────────────────────────────

        private void OnCreateViews()
            => LaunchFlow(App.ZoneOpenViewsEvent, "Create Views",
                          AppStrings.T("zones.manager.status.openingViews"));

        private void OnBuildSheets()
            => LaunchFlow(App.ZoneOpenSheetsEvent, "Build Sheets",
                          AppStrings.T("zones.manager.status.openingSheets"));

        private void OnKeyPlans()
            => LaunchFlow(App.ZoneOpenKeyPlanEvent, "Key Plans",
                          AppStrings.T("zones.manager.status.openingKeyPlans"));

        /// <summary>
        /// Saves, then hands off to Revit's main thread.
        ///
        /// The save comes FIRST so the flow reads what is on screen rather than what was last
        /// persisted, and a failed save aborts the launch — opening a flow against a stale
        /// library would silently generate the wrong thing.
        /// </summary>
        private void LaunchFlow(ExternalEvent? evt, string what, string openingMessage)
        {
            try { ZoneSettings.Save(); }
            catch (Exception ex)
            {
                DiagnosticsLog.Error($"ZoneManagerWindow: save before {what}", ex);
                FlashStatus(AppStrings.T("zones.manager.status.saveFailed"));
                return;
            }

            if (evt == null)
            {
                DiagnosticsLog.Warn("ZoneManagerWindow", $"{what} unavailable: event handler not registered.");
                FlashStatus(AppStrings.T("zones.manager.status.flowUnavailable"));
                return;
            }

            // Marks the window busy AND arms the reload: coming back from any flow re-reads the
            // library, exactly as returning from Discover already did.
            _flowBusy = true;
            _discoverLaunched = true;
            BuildToolbarActions();

            evt.Raise();
            FlashStatus(openingMessage);
        }
    }
}
