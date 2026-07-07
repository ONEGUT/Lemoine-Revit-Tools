using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace LemoineTools.Framework.Controls
{
    /// <summary>
    /// Mutable state behind a <see cref="NamingSlots"/> control: three ordered
    /// naming slots (Front / Center / End), each holding a token from the caller's
    /// vocabulary plus a free-text value used when the token is "Custom".
    ///
    /// Token strings are logic identifiers compared with == by callers (same contract
    /// as the original Bulk Views naming step) — they are not externalized.
    /// </summary>
    public sealed class NamingSlotsState
    {
        public string Front        = "None";
        public string FrontCustom  = "";
        public string Center       = "None";
        public string CenterCustom = "";
        public string End          = "None";
        public string EndCustom    = "";

        /// <summary>True when at least one slot is set to something other than "None".</summary>
        public bool AnySet => Front != "None" || Center != "None" || End != "None";

        /// <summary>
        /// Resolves the three slots to name parts using the caller's token resolver and
        /// drops empties. "None" resolves to nothing; "Custom" resolves to the slot's
        /// custom text (trimmed; empty when blank). Every other token goes through
        /// <paramref name="tokenResolver"/>.
        /// </summary>
        public List<string> ResolveParts(Func<string, string> tokenResolver)
        {
            string One(string slot, string custom)
            {
                if (slot == "None")   return "";
                if (slot == "Custom") return string.IsNullOrWhiteSpace(custom) ? "" : custom.Trim();
                return tokenResolver(slot) ?? "";
            }

            return new[]
                {
                    One(Front,  FrontCustom),
                    One(Center, CenterCustom),
                    One(End,    EndCustom),
                }
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();
        }
    }

    /// <summary>
    /// Reusable Front / Center / End naming-slot rows — the house pattern for
    /// assembling generated element names from token pieces. Each row is a
    /// <see cref="SingleSelect"/> over the caller's token vocabulary plus a
    /// custom-text box that appears only when that slot is set to "Custom".
    ///
    /// Extracted from the Bulk Views by Level naming step so the Scope Box Creator,
    /// Scope Box Manager rename, and view tools share one implementation.
    ///
    /// Usage:
    ///   var slots = new NamingSlots(tokens, state);   // mutates state in place
    ///   slots.Changed += () => UpdatePreview();
    ///   panel.Children.Add(slots);
    ///
    /// The caller owns preview rendering and token resolution
    /// (<see cref="NamingSlotsState.ResolveParts"/>).
    /// </summary>
    public sealed class NamingSlots : StackPanel
    {
        private readonly NamingSlotsState _state;

        /// <summary>Raised after any slot token or custom text changes (state already updated).</summary>
        public event Action? Changed;

        /// <param name="tokenOptions">
        ///   Full token vocabulary shown in each slot dropdown. Must include "None";
        ///   include "Custom" to offer free text. Order is preserved.
        /// </param>
        /// <param name="state">Backing state — mutated in place as the user edits.</param>
        public NamingSlots(IList<string> tokenOptions, NamingSlotsState state)
        {
            _state = state;

            AddSlotRow(AppStrings.T("controls.inputs.namingSlots.front"), tokenOptions,
                       state.Front,  v => _state.Front = v,
                       state.FrontCustom,  v => _state.FrontCustom = v);
            AddSlotRow(AppStrings.T("controls.inputs.namingSlots.center"), tokenOptions,
                       state.Center, v => _state.Center = v,
                       state.CenterCustom, v => _state.CenterCustom = v);
            AddSlotRow(AppStrings.T("controls.inputs.namingSlots.end"), tokenOptions,
                       state.End,    v => _state.End = v,
                       state.EndCustom,    v => _state.EndCustom = v);
        }

        // One slot row: [label] [token combo] [custom textbox, visible only for "Custom"].
        private void AddSlotRow(string label, IList<string> tokenOptions,
                                string curVal, Action<string> setVal,
                                string curCustom, Action<string> setCustom)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin      = new Thickness(0, 0, 0, 8),
            };

            var lbl = new TextBlock
            {
                Text              = label,
                Width             = 60,
                VerticalAlignment = VerticalAlignment.Center,
            };
            lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            row.Children.Add(lbl);

            var cb = new SingleSelect
            {
                Width        = 130,
                Items        = tokenOptions,
                SelectedItem = curVal,
            };
            row.Children.Add(cb);

            var tb = new TextBox
            {
                Text       = curCustom,
                Width      = 140,
                Margin     = new Thickness(6, 0, 0, 0),
                Visibility = curVal == "Custom" ? Visibility.Visible : Visibility.Collapsed,
            };
            tb.SetResourceReference(FrameworkElement.HeightProperty, "LemoineH_Input");
            tb.SetResourceReference(Control.PaddingProperty,         "LemoineTh_InputPad");
            tb.SetResourceReference(TextBox.ForegroundProperty,  "LemoineText");
            tb.SetResourceReference(TextBox.BackgroundProperty,  "LemoineSelectBg");
            tb.SetResourceReference(TextBox.FontSizeProperty,    "LemoineFS_SM");
            tb.SetResourceReference(TextBox.FontFamilyProperty,  "LemoineMonoFont");
            tb.SetResourceReference(TextBox.BorderBrushProperty, "LemoineBorder");
            row.Children.Add(tb);

            cb.SelectionChanged += v =>
            {
                if (v == null) return;
                setVal(v);
                tb.Visibility = v == "Custom" ? Visibility.Visible : Visibility.Collapsed;
                Changed?.Invoke();
            };
            tb.TextChanged += (s, e) => { setCustom(tb.Text); Changed?.Invoke(); };

            Children.Add(row);
        }
    }
}
