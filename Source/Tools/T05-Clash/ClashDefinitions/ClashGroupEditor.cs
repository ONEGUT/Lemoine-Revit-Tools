using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;
using LemoineTools.Lemoine.Controls;
using LemoineTools.Tools.AutoFilters;

namespace LemoineTools.Tools.Clash
{
    /// <summary>
    /// Reusable WPF builder for editing one <see cref="ClashGroupSpec"/> (Group 1 or
    /// Group 2 of a clash definition). Renders a mode selector (Filter Rules / Categories /
    /// Select Elements), a source-document picker, and the matching body, writing every
    /// change straight back into the supplied spec.
    ///
    /// The definitions library's group-selection UI; it operates on persist keys (not display
    /// keys) directly on the spec.
    /// </summary>
    public sealed class ClashGroupEditor
    {
        private readonly ClashGroupSpec          _spec;
        private readonly Action?                 _onChanged;
        private readonly ClashPickEventHandler?  _pickHandler;
        private readonly ExternalEvent?          _pickEvent;

        // ── Filter rule mappings (display ↔ persist) ──────────────────────────
        private readonly Dictionary<string, string>       _displayKeyToPersist = new Dictionary<string, string>();
        private readonly Dictionary<string, string>       _persistKeyToDisplay = new Dictionary<string, string>();
        private readonly Dictionary<string, List<string>> _filterGroups        = new Dictionary<string, List<string>>();

        // ── Category mappings (display ↔ OST) ─────────────────────────────────
        private readonly Dictionary<string, List<string>> _categoryGroups       = new Dictionary<string, List<string>>();
        private readonly Dictionary<string, string>       _categoryDisplayToOst = new Dictionary<string, string>();
        private readonly Dictionary<string, string>       _ostToCategoryDisplay = new Dictionary<string, string>();

        // ── Source documents ──────────────────────────────────────────────────
        private readonly List<string>             _docNames           = new List<string>();
        private readonly Dictionary<string, long> _docDisplayToLinkId = new Dictionary<string, long>();

        // ── Live working sets (display strings) ───────────────────────────────
        private readonly HashSet<string>           _ruleDisplays = new HashSet<string>();
        private readonly HashSet<string>           _catDisplays  = new HashSet<string>();
        private readonly List<(long lnk, long id)> _elemRefs     = new List<(long, long)>();

        private static readonly string[] ModeDisplayItems = { "Filter Rules", "Categories", "Select Elements" };
        private static string ModeToDisplay(string m) =>
            m == "Categories" ? "Categories" : m == "Elements" ? "Select Elements" : "Filter Rules";
        private static string DisplayToMode(string? d) =>
            d == "Categories" ? "Categories" : d == "Select Elements" ? "Elements" : "Rules";

        public ClashGroupEditor(
            ClashGroupSpec         spec,
            List<ClashDocInfo>     docs,
            ClashPickEventHandler? pickHandler,
            ExternalEvent?         pickEvent,
            Action?                onChanged)
        {
            _spec        = spec ?? new ClashGroupSpec();
            _pickHandler = pickHandler;
            _pickEvent   = pickEvent;
            _onChanged   = onChanged;

            foreach (var d in docs ?? new List<ClashDocInfo>())
            {
                if (_docDisplayToLinkId.ContainsKey(d.Name)) continue;
                _docNames.Add(d.Name);
                _docDisplayToLinkId[d.Name] = d.LinkInstId;
            }

            BuildFilterMappings();
            BuildCategoryMappings();
            RestoreFromSpec();
        }

        // ── Mapping builders (Revit-free — read from AutoFiltersSettings) ──────
        private void BuildFilterMappings()
        {
            var trades = AutoFiltersSettings.Instance.Trades;

            var nameCount = new Dictionary<string, int>();
            foreach (var trade in trades)
                foreach (var rule in trade.Rules)
                {
                    if (!nameCount.ContainsKey(rule.Name)) nameCount[rule.Name] = 0;
                    nameCount[rule.Name]++;
                }

            foreach (var trade in trades)
            {
                var groupItems = new List<string>();
                foreach (var rule in trade.Rules)
                {
                    string displayKey = nameCount.TryGetValue(rule.Name, out int cnt) && cnt > 1
                        ? $"{trade.Label} — {rule.Name}"
                        : rule.Name;
                    string persistKey = $"{trade.Id}::{rule.Id}";

                    _displayKeyToPersist[displayKey] = persistKey;
                    _persistKeyToDisplay[persistKey] = displayKey;
                    groupItems.Add(displayKey);
                }
                if (groupItems.Count > 0)
                    _filterGroups[trade.Label] = groupItems;
            }
        }

        private void BuildCategoryMappings()
        {
            foreach (var kv in AutoFiltersSettings.KnownCategoryMap)
            {
                string display = kv.Key;
                string ost     = kv.Value;
                string disc    = DisciplineOf(ost);

                if (!_categoryGroups.ContainsKey(disc)) _categoryGroups[disc] = new List<string>();
                _categoryGroups[disc].Add(display);
                _categoryDisplayToOst[display] = ost;
                _ostToCategoryDisplay[ost]     = display;
            }
            foreach (var k in _categoryGroups.Keys.ToList())
                _categoryGroups[k].Sort(StringComparer.OrdinalIgnoreCase);
        }

        private static string DisciplineOf(string ost)
        {
            bool C(params string[] needles) => needles.Any(n => ost.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0);
            if (C("Duct", "MechanicalEquipment", "FlexDuct"))                                      return "Mechanical";
            if (C("Pipe", "Plumbing", "Sprinkler", "FlexPipe", "FabricationPipe",
                  "FabricationHangers", "FabricationContainment"))                                  return "Piping";
            if (C("Cable", "Conduit", "Electrical", "Lighting", "Communication",
                  "FireAlarm", "Security", "Data", "Telephone", "NurseCall"))                       return "Electrical";
            if (C("Structural", "Rebar", "Reinforcement", "Fabric"))                                return "Structural";
            return "Architectural";
        }

        private void RestoreFromSpec()
        {
            foreach (var pk in _spec.RuleKeys ?? new List<string>())
            {
                if (_persistKeyToDisplay.TryGetValue(pk, out var dk) && dk != null)
                    _ruleDisplays.Add(dk);
                else
                    // Saved rule no longer exists in the catalog — it will be pruned when the group is
                    // re-saved. Record it so the drop isn't silent.
                    LemoineLog.Warn("ClashGroupEditor", $"Saved clash rule '{pk}' no longer exists — dropped from the group.");
            }

            foreach (var ost in _spec.Categories ?? new List<string>())
                if (_ostToCategoryDisplay.TryGetValue(ost, out var disp) && disp != null) _catDisplays.Add(disp);

            var elemIds   = _spec.ElemIds     ?? new List<long>();
            var elemLinks = _spec.ElemLinkIds ?? new List<long>();
            for (int i = 0; i < elemIds.Count; i++)
            {
                long lnk = (i < elemLinks.Count) ? elemLinks[i] : 0L;
                _elemRefs.Add((lnk, elemIds[i]));
            }
        }

        // ── Write working sets back into the spec ─────────────────────────────
        private void CommitRules()
        {
            _spec.RuleKeys = _ruleDisplays
                .Select(dk => _displayKeyToPersist.TryGetValue(dk, out var pk) ? pk : null)
                .Where(pk => pk != null)
                .Cast<string>()
                .ToList();
            Notify();
        }

        private void CommitCategories()
        {
            _spec.Categories = _catDisplays
                .Select(d => _categoryDisplayToOst.TryGetValue(d, out var ost) ? ost : null)
                .Where(ost => ost != null)
                .Cast<string>()
                .ToList();
            Notify();
        }

        private void CommitElements()
        {
            _spec.ElemIds     = _elemRefs.Select(r => r.id).ToList();
            _spec.ElemLinkIds = _elemRefs.Select(r => r.lnk).ToList();
            Notify();
        }

        private void Notify() => _onChanged?.Invoke();

        /// <summary>Short one-line summary of this group's current selection (for the editor header).</summary>
        public string Summary()
        {
            switch (_spec.Mode)
            {
                case "Categories": return _catDisplays.Count == 0 ? "—" : $"{_catDisplays.Count} category(ies)";
                case "Elements":   return _elemRefs.Count   == 0 ? "—" : $"{_elemRefs.Count} element(s)";
                default:           return _ruleDisplays.Count == 0 ? "—" : $"{_ruleDisplays.Count} rule(s)";
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Build
        // ═════════════════════════════════════════════════════════════════════
        public FrameworkElement Build()
        {
            var outer = new StackPanel();
            var body  = new StackPanel();

            Action rebuildBody = () =>
            {
                body.Children.Clear();
                switch (_spec.Mode)
                {
                    case "Categories": BuildCategoryBody(body); break;
                    case "Elements":   BuildElementsBody(body); break;
                    default:           BuildRulesBody(body);     break;
                }
                body.Dispatcher.BeginInvoke(new Action(() => Keyboard.ClearFocus()), DispatcherPriority.Input);
            };

            AddLabel(outer, "Group definition mode");
            var modeSelect = new LemoineSingleSelect
            {
                Items        = ModeDisplayItems,
                SelectedItem = ModeToDisplay(_spec.Mode),
            };
            modeSelect.SelectionChanged += val =>
            {
                _spec.Mode = DisplayToMode(val);
                rebuildBody();
                Notify();
            };
            outer.Children.Add(modeSelect);

            AddDivider(outer);

            AddLabel(outer, "Source documents (which models this group scans)");
            var docGroups   = new Dictionary<string, List<string>> { ["Documents"] = new List<string>(_docNames) };
            var sourceLinks = new HashSet<long>(_spec.SourceLinkIds ?? new List<long>());
            var initialDocs = (sourceLinks.Count == 0)
                ? new List<string>(_docNames)
                : _docNames.Where(n => sourceLinks.Contains(_docDisplayToLinkId[n])).ToList();
            var srcTabs = new LemoineMultiSelectTabs();
            srcTabs.SetGroups(docGroups, initialDocs);
            srcTabs.SelectionChanged += selected =>
            {
                var ids = new List<long>();
                foreach (var n in selected)
                    if (_docDisplayToLinkId.TryGetValue(n, out var lid)) ids.Add(lid);
                _spec.SourceLinkIds = ids;
                Notify();
            };
            outer.Children.Add(srcTabs);

            AddDivider(outer);
            outer.Children.Add(body);

            rebuildBody();
            return outer;
        }

        private void BuildRulesBody(StackPanel body)
        {
            if (_filterGroups.Count == 0)
            {
                AddDim(body, "No Auto Filters rules configured. Switch to Categories or Select Elements, or set up Auto Filters first.");
                return;
            }
            var tabs = new LemoineMultiSelectTabs();
            tabs.SetGroups(new Dictionary<string, List<string>>(_filterGroups), _ruleDisplays);
            tabs.SelectionChanged += selected =>
            {
                _ruleDisplays.Clear();
                foreach (var s in selected) _ruleDisplays.Add(s);
                CommitRules();
            };
            body.Children.Add(tabs);
        }

        private void BuildCategoryBody(StackPanel body)
        {
            if (_categoryGroups.Count == 0)
            {
                AddDim(body, "No categories available.");
                return;
            }
            var tabs = new LemoineMultiSelectTabs();
            tabs.SetGroups(new Dictionary<string, List<string>>(_categoryGroups), _catDisplays);
            tabs.SelectionChanged += selected =>
            {
                _catDisplays.Clear();
                foreach (var s in selected) _catDisplays.Add(s);
                CommitCategories();
            };
            body.Children.Add(tabs);
        }

        private void BuildElementsBody(StackPanel body)
        {
            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };

            var pickHost  = MakeButton("＋ Pick host elements");
            var pickLinks = MakeButton("＋ Pick linked elements");
            var clearBtn  = MakeButton("Clear");
            clearBtn.Margin = new Thickness(0);

            btnRow.Children.Add(pickHost);
            btnRow.Children.Add(pickLinks);
            btnRow.Children.Add(clearBtn);
            body.Children.Add(btnRow);

            var count = new TextBlock { TextWrapping = TextWrapping.Wrap };
            count.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            count.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            count.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            body.Children.Add(count);

            Action refresh = () => count.Text = _elemRefs.Count == 0
                ? "No elements picked yet. Use the buttons above to pick in the model."
                : $"{_elemRefs.Count} element(s) picked.";
            refresh();

            pickHost.Click  += (s, e) => StartPick(false, (Button)s!, refresh);
            pickLinks.Click += (s, e) => StartPick(true,  (Button)s!, refresh);
            clearBtn.Click  += (s, e) => { _elemRefs.Clear(); refresh(); CommitElements(); };
        }

        private void StartPick(bool inLinks, Button sourceBtn, Action refresh)
        {
            if (_pickHandler == null || _pickEvent == null) return;
            var disp = sourceBtn.Dispatcher;   // window's STA dispatcher

            _pickHandler.InLinks  = inLinks;
            _pickHandler.OnPicked = picks =>
            {
                // Runs on Revit's main thread — marshal back to the UI thread.
                disp.BeginInvoke(new Action(() =>
                {
                    foreach (var p in picks)
                        if (!_elemRefs.Any(e => e.lnk == p.linkId && e.id == p.elemId))
                            _elemRefs.Add((p.linkId, p.elemId));
                    refresh();
                    CommitElements();
                }));
            };
            _pickEvent.Raise();
        }

        // ── UI helpers ────────────────────────────────────────────────────────
        private static Button MakeButton(string label)
        {
            var b = new Button
            {
                Content         = label,
                Cursor          = Cursors.Hand,
                BorderThickness = new Thickness(1),
                Margin          = new Thickness(0, 0, 8, 0),
                Template        = LemoineControlStyles.BuildFlatButtonTemplate(),
            };
            b.SetResourceReference(Button.MinHeightProperty,  "LemoineH_BtnMin");
            b.SetResourceReference(Button.PaddingProperty,    "LemoineTh_BtnPad");
            b.SetResourceReference(Button.FontSizeProperty,   "LemoineFS_MD");
            b.SetResourceReference(Button.FontFamilyProperty, "LemoineUiFont");
            b.Background = Brushes.Transparent;
            b.SetResourceReference(Button.BorderBrushProperty, "LemoineBorder");
            b.SetResourceReference(Button.ForegroundProperty,  "LemoineText");
            return b;
        }

        private static void AddLabel(StackPanel parent, string text)
        {
            var lbl = new TextBlock { Text = text, Margin = new Thickness(0, 0, 0, 4) };
            lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            parent.Children.Add(lbl);
        }

        private static void AddDim(StackPanel parent, string text)
        {
            var tb = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 4) };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            parent.Children.Add(tb);
        }

        private static void AddDivider(StackPanel parent)
        {
            var sep = new System.Windows.Shapes.Rectangle { Height = 1, Margin = new Thickness(0, 8, 0, 8) };
            sep.SetResourceReference(System.Windows.Shapes.Rectangle.FillProperty, "LemoineBorder");
            parent.Children.Add(sep);
        }
    }
}
