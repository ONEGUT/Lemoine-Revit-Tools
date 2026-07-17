using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LemoineTools.Tools.AutoFilters;

namespace LemoineTools.Framework.Web
{
    /// <summary>
    /// Revit-free view model for the web Auto Filters window (HTML analogue of
    /// <see cref="FiltersSettingsWindow"/>). Holds the same working buffer discipline as the
    /// WPF window: a deep copy of <see cref="AutoFiltersSettings.Instance"/>.Trades, a
    /// serialized load-time snapshot for the close-time dirty check, and an in-window
    /// undo/redo history of serialized snapshots. <see cref="WebAutoFiltersWindow"/> owns the
    /// ExternalEvents (apply/remove/discover/delete-from-project) and the close-time
    /// auto-create pass.
    /// </summary>
    public sealed class WebAutoFilters
    {
        private List<FilterTradeConfig> _trades;
        private string  _snapshot;
        private string? _activeTradeId;
        private string? _activeRuleId;
        private string? _shiftAnchorRuleId;
        // Multi-select over rule rows (includes the active rule while >= 2 are selected).
        private readonly HashSet<string> _selectedRuleIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _applyExcluded = new HashSet<string>(StringComparer.Ordinal);
        private string _surfLayer = "FG";
        private string _cutLayer  = "FG";
        private string _status = "";

        private readonly List<string> _fillPatterns;
        private readonly List<string> _linePatterns;

        // Undo/redo — a stack of (label, serialized buffer) like the WPF window's history.
        private readonly List<(string Label, string Snapshot)> _history = new List<(string, string)>();
        private int _historyIndex;

        private static string TW(string key, params object[] args) =>
            AppStrings.T("autofilters.filtersWindow." + key, args);
        private static string TF(string key, params object[] args) =>
            AppStrings.T("globalSettings.filters." + key, args);

        public WebAutoFilters(List<string> fillPatterns, List<string> linePatterns)
        {
            _fillPatterns = fillPatterns ?? new List<string>();
            _linePatterns = linePatterns ?? new List<string> { "Solid" };
            _trades   = AutoFiltersSettings.DeepCopy(AutoFiltersSettings.Instance.Trades);
            _snapshot = Serialize(_trades);
            _history.Add((TW("window.history.opened"), _snapshot));
            _historyIndex = 0;
            EnsureActive();
        }

        public void SetStatus(string status) => _status = status ?? "";

        private void EnsureActive()
        {
            if (_activeTradeId == null || _trades.All(t => t.Id != _activeTradeId))
                _activeTradeId = _trades.FirstOrDefault()?.Id;
            var trade = ActiveTrade();
            if (_activeRuleId == null || trade == null || trade.Rules.All(r => r.Id != _activeRuleId))
                _activeRuleId = trade?.Rules.FirstOrDefault()?.Id;
            _selectedRuleIds.RemoveWhere(id => trade == null || trade.Rules.All(r => r.Id != id));
            if (_selectedRuleIds.Count < 2) _selectedRuleIds.Clear();
        }

        public FilterTradeConfig? ActiveTrade() => _trades.FirstOrDefault(t => t.Id == _activeTradeId);
        public FilterRuleConfig?  ActiveRule()  => ActiveTrade()?.Rules.FirstOrDefault(r => r.Id == _activeRuleId);

        // ── Window-facing buffer access ───────────────────────────────────────
        public List<FilterTradeConfig> Trades => _trades;

        /// <summary>Trades currently checked for the sidebar Apply/Remove footer
        /// (selection is an exclusion set — new trades are included by default).</summary>
        public List<FilterTradeConfig> CheckedTrades() =>
            _trades.Where(t => !_applyExcluded.Contains(t.Id)).ToList();

        /// <summary>Persist the buffer to the settings singleton and refresh the dirty snapshot.</summary>
        public void Persist()
        {
            AutoFiltersSettings.Instance.Trades = AutoFiltersSettings.DeepCopy(_trades);
            AutoFiltersSettings.Instance.Save();
            _snapshot = Serialize(_trades);
        }

        /// <summary>Reload from the singleton when it changed externally (Discover committed new
        /// trades while this window was open). Returns true when a reload happened.</summary>
        public bool ReloadIfChangedExternally()
        {
            string current = Serialize(_trades);
            if (current != _snapshot) return false;   // in-window edits win; no clobber
            string latest = Serialize(AutoFiltersSettings.Instance.Trades);
            if (latest == _snapshot) return false;
            _trades   = AutoFiltersSettings.DeepCopy(AutoFiltersSettings.Instance.Trades);
            _snapshot = Serialize(_trades);
            EnsureActive();
            return true;
        }

        /// <summary>Close-time flush: saves if dirty and computes which filters the auto-create
        /// pass should refresh. Mirrors FiltersSettingsWindow.OnClosed exactly.</summary>
        public (bool ShouldRaise, HashSet<string>? Changed, HashSet<string>? ChangedOverride) FlushOnClose()
        {
            bool dirty = Serialize(_trades) != _snapshot;
            var snapshotTrades = Deserialize(_snapshot);
            if (dirty)
            {
                AutoFiltersSettings.Instance.Trades = _trades;
                AutoFiltersSettings.Instance.Save();
            }

            var expected = AutoFiltersSettings.ComputeExpectedFilterNames(_trades);
            bool manifestStale = !expected.SetEquals(
                AutoFiltersSettings.Instance.CreatedFilterNames ?? new List<string>());

            if ((dirty || manifestStale) && expected.Count > 0)
            {
                var changed         = AutoFiltersSettings.ComputeChangedFilterNames(snapshotTrades, _trades);
                var changedOverride = AutoFiltersSettings.ComputeChangedOverrideFilterNames(snapshotTrades, _trades);
                return (true, changed, changedOverride);
            }
            return (false, null, null);
        }

        // ── History ───────────────────────────────────────────────────────────
        private void Capture(string label)
        {
            string cur = Serialize(_trades);
            if (_historyIndex >= 0 && _historyIndex < _history.Count && _history[_historyIndex].Snapshot == cur)
                return;
            if (_historyIndex < _history.Count - 1)
                _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
            _history.Add((label, cur));
            _historyIndex = _history.Count - 1;
        }

        public bool CanUndo => _historyIndex > 0;
        public bool CanRedo => _historyIndex < _history.Count - 1;

        public void Undo() { if (CanUndo) Restore(_historyIndex - 1); }
        public void Redo() { if (CanRedo) Restore(_historyIndex + 1); }
        public void HistoryJump(int index) { if (index >= 0 && index < _history.Count) Restore(index); }

        private void Restore(int index)
        {
            _historyIndex = index;
            _trades = Deserialize(_history[index].Snapshot);
            _selectedRuleIds.Clear();
            EnsureActive();
        }

        // ── Trade mutations ───────────────────────────────────────────────────
        public void SelectTrade(string id)
        {
            _activeTradeId = id;
            _activeRuleId  = ActiveTrade()?.Rules.FirstOrDefault()?.Id;
            _selectedRuleIds.Clear();
            _shiftAnchorRuleId = null;
        }

        public void SetTradeChecked(string id, bool isChecked)
        {
            if (isChecked) _applyExcluded.Remove(id); else _applyExcluded.Add(id);
        }

        public void AddTrade(string label, string color)
        {
            var t = FilterTradeConfig.NewBlank();
            if (!string.IsNullOrWhiteSpace(label)) t.Label = label.Trim();
            if (!string.IsNullOrWhiteSpace(color)) t.Color = color;
            _trades.Add(t);
            _activeTradeId = t.Id;
            _activeRuleId  = null;
            Capture(TW("window.history.addTrade", t.Label));
        }

        public void ApplyTradeEdit(string id, string label, string color)
        {
            var t = _trades.FirstOrDefault(x => x.Id == id);
            if (t == null) return;
            if (!string.IsNullOrWhiteSpace(label)) t.Label = label.Trim();
            if (!string.IsNullOrWhiteSpace(color)) t.Color = color;
            Capture(TW("window.history.edit", t.Label));
        }

        public void DuplicateTrade(string id)
        {
            var t = _trades.FirstOrDefault(x => x.Id == id);
            if (t == null) return;
            var copy = AutoFiltersSettings.DeepCopy(new List<FilterTradeConfig> { t })[0];
            copy.Id     = FilterTradeConfig.NewBlank().Id;
            copy.Label += TF("suffixes.copy");
            foreach (var r in copy.Rules) r.Id = Guid.NewGuid().ToString("N").Substring(0, 8);
            _trades.Insert(_trades.IndexOf(t) + 1, copy);
            _activeTradeId = copy.Id;
            EnsureActive();
            Capture(TW("window.history.addTrade", copy.Label));
        }

        public void DeleteTrade(string id)
        {
            var t = _trades.FirstOrDefault(x => x.Id == id);
            if (t == null) return;
            _trades.Remove(t);
            EnsureActive();
            Capture(TW("window.history.deleteTrade", t.Label));
        }

        // ── Rule mutations ────────────────────────────────────────────────────
        /// <summary>Row click with the WPF modifier contract: Shift = contiguous range from
        /// the anchor (editor sources from the anchor), Ctrl = toggle in/out of the set
        /// (seeding it with the active rule), plain = single select + new anchor.</summary>
        public void SelectRule(string id, bool shift = false, bool ctrl = false)
        {
            var trade = ActiveTrade(); if (trade == null) return;
            if (shift)
            {
                string anchorId = _shiftAnchorRuleId ?? _activeRuleId ?? id;
                int anchorIdx  = trade.Rules.FindIndex(r => r.Id == anchorId);
                int clickedIdx = trade.Rules.FindIndex(r => r.Id == id);
                if (clickedIdx < 0) return;
                if (anchorIdx < 0) anchorIdx = clickedIdx;
                _selectedRuleIds.Clear();
                for (int i = Math.Min(anchorIdx, clickedIdx); i <= Math.Max(anchorIdx, clickedIdx); i++)
                    _selectedRuleIds.Add(trade.Rules[i].Id);
                _activeRuleId = anchorId;   // editor sources from the anchor
            }
            else if (ctrl)
            {
                if (_selectedRuleIds.Contains(id)) _selectedRuleIds.Remove(id);
                else
                {
                    if (_activeRuleId != null) _selectedRuleIds.Add(_activeRuleId);
                    _selectedRuleIds.Add(id);
                }
                if (_selectedRuleIds.Count < 2) _selectedRuleIds.Clear();
            }
            else
            {
                _shiftAnchorRuleId = id;
                _selectedRuleIds.Clear();
                _activeRuleId = id;
            }
        }

        public void AddRule()
        {
            var t = ActiveTrade(); if (t == null) return;
            var r = FilterRuleConfig.NewBlank();
            t.Rules.Add(r);
            _activeRuleId = r.Id;
            Capture(TW("window.history.addRule", r.Name));
        }

        public void ReorderRule(int from, int to)
        {
            var t = ActiveTrade(); if (t == null) return;
            if (from < 0 || from >= t.Rules.Count || to < 0 || to >= t.Rules.Count || from == to) return;
            var r = t.Rules[from];
            t.Rules.RemoveAt(from);
            t.Rules.Insert(to, r);
            Capture(TW("window.history.moveRules"));
        }

        public void ApplyRuleEdit(string id, string name)
        {
            var t = ActiveTrade();
            var r = t?.Rules.FirstOrDefault(x => x.Id == id);
            if (r == null || string.IsNullOrWhiteSpace(name)) return;
            r.Name = name.Trim();
            Capture(TW("window.history.rename", r.Name));
        }

        public void DuplicateRule(string id)
        {
            var t = ActiveTrade();
            var r = t?.Rules.FirstOrDefault(x => x.Id == id);
            if (t == null || r == null) return;
            var copy = AutoFiltersSettings.DeepCopy(new List<FilterTradeConfig>
            { new FilterTradeConfig { Rules = new List<FilterRuleConfig> { r } } })[0].Rules[0];
            copy.Id    = Guid.NewGuid().ToString("N").Substring(0, 8);
            copy.Name += TF("suffixes.copy");
            t.Rules.Insert(t.Rules.IndexOf(r) + 1, copy);
            _activeRuleId = copy.Id;
            Capture(TW("window.history.addRule", copy.Name));
        }

        public void DeleteRule(string id)
        {
            var t = ActiveTrade();
            var r = t?.Rules.FirstOrDefault(x => x.Id == id);
            if (t == null || r == null) return;
            t.Rules.Remove(r);
            EnsureActive();
            Capture(TW("window.history.deleteRule", r.Name));
        }

        // ── Batch propagation (multi-select) ──────────────────────────────────
        // While >= 2 rules are selected, an edit to the anchor rule propagates that ONE
        // field to every other selected rule — the exact key set the WPF ApplyBatchField
        // supports. BG-layer colour/pattern edits are anchor-only there too, so callers
        // pass null for those.
        private void Batch(string? key)
        {
            if (key == null || _selectedRuleIds.Count < 2) return;
            var trade  = ActiveTrade();
            var source = ActiveRule();
            if (trade == null || source == null) return;

            foreach (var ruleId in _selectedRuleIds.Where(rid => rid != source.Id).ToList())
            {
                var target = trade.Rules.FirstOrDefault(r => r.Id == ruleId);
                if (target == null) continue;
                switch (key)
                {
                    case "logic.categories": target.BuiltInCategories = source.BuiltInCategories.ToList(); break;
                    case "logic.parameter":  target.Parameter = source.Parameter; break;
                    case "logic.match":      target.Match = source.Match.ToList(); break;
                    case "logic.matchtype":  target.MatchType = source.MatchType; break;

                    case "style.surf.enabled": target.OverrideSurf = source.OverrideSurf; break;
                    case "style.surf.color":   target.SurfColor    = source.SurfColor;    break;
                    case "style.surf.pattern": target.SurfPattern  = source.SurfPattern;  break;

                    case "style.cut.enabled":  target.OverrideCut  = source.OverrideCut;  break;
                    case "style.cut.color":    target.CutColor     = source.CutColor;     break;
                    case "style.cut.pattern":  target.CutPattern   = source.CutPattern;   break;

                    case "style.line.enabled": target.OverrideLine = source.OverrideLine; break;
                    case "style.line.color":   target.LineColor    = source.LineColor;    break;
                    case "style.line.weight":  target.LineWeight   = source.LineWeight;   break;

                    case "appearance.halftone":     target.Halftone     = source.Halftone;     break;
                    case "appearance.transparency": target.Transparency = source.Transparency; break;
                    case "appearance.visible":      target.Visible      = source.Visible;      break;
                    case "appearance.apply":        target.Enabled      = source.Enabled;
                                                    target.FilterOn     = source.FilterOn;     break;
                }
            }
        }

        // ── Rule editor setters (active rule) ─────────────────────────────────
        public void AddCategory(string displayName)
        {
            var r = ActiveRule(); if (r == null) return;
            if (!AutoFiltersSettings.TryResolveCategoryOst(displayName, out var ost)) return;
            if (!r.BuiltInCategories.Contains(ost)) r.BuiltInCategories.Add(ost);
            Batch("logic.categories");
            Capture(TW("window.history.edit", r.Name));
        }

        public void RemoveCategory(string displayName)
        {
            var r = ActiveRule(); if (r == null) return;
            if (AutoFiltersSettings.TryResolveCategoryOst(displayName, out var ost))
                r.BuiltInCategories.Remove(ost);
            else
                r.BuiltInCategories.Remove(displayName);
            Batch("logic.categories");
            Capture(TW("window.history.edit", r.Name));
        }

        public void SetMatchAll(bool on)
        {
            var r = ActiveRule(); if (r == null) return;
            r.MatchType = on ? "all" : "contains";
            Batch("logic.matchtype");
            Capture(TW("window.history.edit", r.Name));
        }

        public void SetMatchType(string matchType)
        {
            var r = ActiveRule(); if (r == null) return;
            r.MatchType = matchType == "equals" ? "equals" : "contains";
            Batch("logic.matchtype");
            Capture(TW("window.history.edit", r.Name));
        }

        public void SetParameter(string name)
        {
            var r = ActiveRule(); if (r == null) return;
            r.Parameter = name ?? "";
            Batch("logic.parameter");
            Capture(TW("window.history.edit", r.Name));
        }

        public void AddKeyword(string keyword)
        {
            var r = ActiveRule(); if (r == null || string.IsNullOrWhiteSpace(keyword)) return;
            keyword = keyword.Trim();
            if (!r.Match.Contains(keyword)) r.Match.Add(keyword);
            Batch("logic.match");
            Capture(TW("window.history.edit", r.Name));
        }

        public void RemoveKeyword(string keyword)
        {
            var r = ActiveRule(); if (r == null) return;
            r.Match.Remove(keyword);
            Batch("logic.match");
            Capture(TW("window.history.edit", r.Name));
        }

        public void SetLayer(string row, string layer)
        {
            if (row == "surface") _surfLayer = layer == "BG" ? "BG" : "FG";
            else if (row == "cut") _cutLayer = layer == "BG" ? "BG" : "FG";
        }

        public void SetOverrideOn(string row, bool on)
        {
            var r = ActiveRule(); if (r == null) return;
            switch (row)
            {
                case "surface": if (_surfLayer == "BG") r.OverrideSurfBg = on; else r.OverrideSurf = on; break;
                case "cut":     if (_cutLayer  == "BG") r.OverrideCutBg  = on; else r.OverrideCut  = on; break;
                case "lines":   r.OverrideLine = on; break;
            }
            Batch(row == "surface" ? (_surfLayer == "FG" ? "style.surf.enabled" : null)
                : row == "cut"     ? (_cutLayer  == "FG" ? "style.cut.enabled"  : null)
                : "style.line.enabled");
            Capture(TW("window.history.edit", r.Name));
        }

        public void SetColor(string row, string hex)
        {
            var r = ActiveRule(); if (r == null || string.IsNullOrWhiteSpace(hex)) return;
            switch (row)
            {
                case "surface": if (_surfLayer == "BG") r.SurfBgColor = hex; else r.SurfColor = hex; break;
                case "cut":     if (_cutLayer  == "BG") r.CutBgColor  = hex; else r.CutColor  = hex; break;
                case "lines":   r.LineColor = hex; break;
            }
            Batch(row == "surface" ? (_surfLayer == "FG" ? "style.surf.color" : null)
                : row == "cut"     ? (_cutLayer  == "FG" ? "style.cut.color"  : null)
                : "style.line.color");
            Capture(TW("window.history.edit", r.Name));
        }

        public void SetPattern(string row, string pattern)
        {
            var r = ActiveRule(); if (r == null) return;
            switch (row)
            {
                case "surface": if (_surfLayer == "BG") r.SurfBgPattern = pattern; else r.SurfPattern = pattern; break;
                case "cut":     if (_cutLayer  == "BG") r.CutBgPattern  = pattern; else r.CutPattern  = pattern; break;
            }
            Batch(row == "surface" ? (_surfLayer == "FG" ? "style.surf.pattern" : null)
                : _cutLayer == "FG" ? "style.cut.pattern" : null);
            Capture(TW("window.history.edit", r.Name));
        }

        public void SetLineWeight(int weight)
        {
            var r = ActiveRule(); if (r == null) return;
            r.LineWeight = Math.Max(1, Math.Min(14, weight));
            Batch("style.line.weight");
            Capture(TW("window.history.edit", r.Name));
        }

        public void SetTransparency(int value)
        {
            var r = ActiveRule(); if (r == null) return;
            r.Transparency = Math.Max(0, Math.Min(100, value));
            Batch("appearance.transparency");
            Capture(TW("window.history.edit", r.Name));
        }

        public void SetFlag(string flag, bool on)
        {
            var r = ActiveRule(); if (r == null) return;
            switch (flag)
            {
                case "halftone": r.Halftone = on; break;
                case "visible":  r.Visible  = on; break;
                // "Apply" drives Enabled and keeps FilterOn in sync (same as the WPF toggle).
                case "filterOn": r.Enabled = on; r.FilterOn = on; break;
            }
            Batch(flag == "halftone" ? "appearance.halftone"
                : flag == "visible"  ? "appearance.visible" : "appearance.apply");
            Capture(TW("window.history.edit", r.Name));
        }

        // ── Merge selected rules (port of the WPF MergePlan/ApplyMerge) ───────
        // Merge into one: the anchor absorbs the union and the others are deleted.
        // Create combined: a NEW rule with the unioned definition is appended; originals kept.
        // Both require one shared parameter and keyword-based rules throughout.
        private sealed class MergePlan
        {
            public bool         Ok;
            public string?      Reason;
            public string       Parameter  = "";
            public string       MatchType  = "";
            public List<string> Keywords   = new List<string>();
            public List<string> Categories = new List<string>();
            public static MergePlan Blocked(string reason) => new MergePlan { Ok = false, Reason = reason };
        }

        private static readonly HashSet<string> PositiveKeywordMatchTypes =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "contains", "equals", "begins with", "ends with" };

        private MergePlan ComputeMergePlan()
        {
            var trade  = ActiveTrade();
            var anchorRule = ActiveRule();
            if (trade == null || anchorRule == null)
                return MergePlan.Blocked(TF("merge.blockedNeedTwo"));

            var selected = new List<FilterRuleConfig> { anchorRule };
            selected.AddRange(trade.Rules.Where(r =>
                r.Id != anchorRule.Id && _selectedRuleIds.Contains(r.Id)));

            if (selected.Count < 2) return MergePlan.Blocked(TF("merge.blockedNeedTwo"));

            string param = anchorRule.Parameter ?? "";
            if (selected.Any(r => !string.Equals(r.Parameter ?? "", param, StringComparison.Ordinal)))
                return MergePlan.Blocked(TF("merge.blockedSameParameter"));

            foreach (var r in selected)
            {
                string mt = (r.MatchType ?? "contains").ToLowerInvariant();
                if (mt == "all" || mt == "has a value" || mt == "has no value")
                    return MergePlan.Blocked(TF("merge.blockedKeywordOnly"));
            }

            var matchTypes = selected
                .Select(r => (r.MatchType ?? "contains").ToLowerInvariant())
                .Distinct().ToList();
            string resultType;
            if (matchTypes.Count == 1) resultType = matchTypes[0];
            else if (matchTypes.All(mt => PositiveKeywordMatchTypes.Contains(mt))) resultType = "contains";
            else return MergePlan.Blocked(TF("merge.blockedMixedMatch"));

            return new MergePlan
            {
                Ok         = true,
                Parameter  = param,
                MatchType  = resultType,
                Keywords   = UnionStrings(selected.Select(r => r.Match ?? new List<string>())),
                Categories = UnionStrings(selected.Select(r => r.BuiltInCategories ?? new List<string>())),
            };
        }

        private static List<string> UnionStrings(IEnumerable<IEnumerable<string>> lists)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var outl = new List<string>();
            foreach (var l in lists)
                foreach (var x in l)
                    if (!string.IsNullOrEmpty(x) && seen.Add(x)) outl.Add(x);
            return outl;
        }

        public void ApplyMerge(bool destructive)
        {
            var trade  = ActiveTrade();
            var anchorRule = ActiveRule();
            var plan   = ComputeMergePlan();
            if (trade == null || anchorRule == null || !plan.Ok) return;

            if (destructive)
            {
                anchorRule.Parameter         = plan.Parameter;
                anchorRule.MatchType         = plan.MatchType;
                anchorRule.Match             = plan.Keywords;
                anchorRule.BuiltInCategories = plan.Categories;

                var others = _selectedRuleIds.Where(id => id != anchorRule.Id).ToList();
                _selectedRuleIds.Clear();
                foreach (var id in others)
                    trade.Rules.RemoveAll(r => r.Id == id);
                _activeRuleId = anchorRule.Id;
            }
            else
            {
                var combined = FilterRuleConfig.NewBlank();
                combined.Name              = anchorRule.Name + TF("suffixes.combined");
                combined.Parameter         = plan.Parameter;
                combined.MatchType         = plan.MatchType;
                combined.Match             = plan.Keywords;
                combined.BuiltInCategories = plan.Categories;
                combined.Enabled           = anchorRule.Enabled;
                // Graphics come from the anchor so the combined rule looks like it.
                combined.CutColor     = anchorRule.CutColor;     combined.CutPattern   = anchorRule.CutPattern;
                combined.OverrideCut  = anchorRule.OverrideCut;
                combined.SurfColor    = anchorRule.SurfColor;    combined.SurfPattern  = anchorRule.SurfPattern;
                combined.OverrideSurf = anchorRule.OverrideSurf;
                combined.LineColor    = anchorRule.LineColor;    combined.LinePattern  = anchorRule.LinePattern;
                combined.LineWeight   = anchorRule.LineWeight;   combined.OverrideLine = anchorRule.OverrideLine;
                combined.Halftone     = anchorRule.Halftone;     combined.Transparency = anchorRule.Transparency;
                combined.Visible      = anchorRule.Visible;      combined.FilterOn     = anchorRule.FilterOn;

                trade.Rules.Add(combined);
                _selectedRuleIds.Clear();
                _activeRuleId = combined.Id;
            }
            Capture(TW("window.history.editGeneric"));
        }

        // ── Templates ─────────────────────────────────────────────────────────
        public List<string> TemplateNames() =>
            AutoFiltersSettings.Templates.List().Select(t => t.Name).ToList();

        public bool TemplateLoad(string name, out string? error)
        {
            error = null;
            var info = AutoFiltersSettings.Templates.List().FirstOrDefault(t => t.Name == name);
            if (info == null) { error = TF("dialogs.loadFailed"); return false; }
            if (!AutoFiltersSettings.Templates.Load(info, out var data, out error) || data == null) return false;
            _trades = data;
            EnsureActive();
            Capture(TF("status.loaded", name));
            return true;
        }

        public bool TemplateSave(string name, out string? error) =>
            AutoFiltersSettings.Templates.Save(name, _trades, out error);

        public bool TemplateDelete(string name, out string? error)
        {
            error = null;
            var info = AutoFiltersSettings.Templates.List().FirstOrDefault(t => t.Name == name);
            return info != null && AutoFiltersSettings.Templates.Delete(info, out error);
        }

        public void RestoreDefaults()
        {
            _trades = AutoFiltersSettings.BuildDefaultTrades();
            EnsureActive();
            Capture(TF("status.restoredDefaults"));
        }

        public bool ImportFrom(string path)
        {
            try
            {
                var xs = new System.Xml.Serialization.XmlSerializer(typeof(AutoFiltersSettings));
                using (var r = new System.IO.StreamReader(path))
                {
                    if (xs.Deserialize(r) is AutoFiltersSettings data && data.Trades.Count > 0)
                    {
                        _trades = data.Trades;
                        EnsureActive();
                        Capture(TF("status.imported"));
                        return true;
                    }
                }
            }
            catch (Exception ex) { DiagnosticsLog.Error("WebAutoFilters: import", ex); }
            return false;
        }

        public bool ExportTo(string path)
        {
            try
            {
                var xs = new System.Xml.Serialization.XmlSerializer(typeof(AutoFiltersSettings));
                using (var w = new System.IO.StreamWriter(path))
                    xs.Serialize(w, new AutoFiltersSettings { Trades = _trades });
                return true;
            }
            catch (Exception ex) { DiagnosticsLog.Error("WebAutoFilters: export", ex); return false; }
        }

        // ── Payload ───────────────────────────────────────────────────────────
        public Dictionary<string, object?> BuildPayload()
        {
            var activeTrade = ActiveTrade();
            return new Dictionary<string, object?>
            {
                ["title"]   = TW("window.toolbar.title"),
                ["labels"]  = Labels(),
                ["status"]  = _status,
                ["canUndo"] = CanUndo,
                ["canRedo"] = CanRedo,
                ["trades"]  = _trades.Select(t => (object?)new Dictionary<string, object?>
                {
                    ["id"] = t.Id, ["label"] = t.Label, ["color"] = t.Color,
                    ["checked"] = !_applyExcluded.Contains(t.Id),
                    ["active"]  = t.Id == _activeTradeId,
                    ["external"] = t.ExternallyManaged,
                }).ToList(),
                ["rules"]   = (activeTrade?.Rules ?? new List<FilterRuleConfig>())
                    .Select(r => (object?)new Dictionary<string, object?>
                    {
                        ["id"] = r.Id, ["name"] = r.Name, ["color"] = r.SurfColor,
                        ["catLabel"] = CategorySummary(r),
                        ["active"]  = r.Id == _activeRuleId,
                        ["selected"] = _selectedRuleIds.Contains(r.Id),
                        ["enabled"] = r.Enabled,
                    }).ToList(),
                ["editor"]  = BuildEditor(),
                ["batch"]   = BuildBatch(),
                ["history"] = _history.Select((h, i) => (object?)new Dictionary<string, object?>
                { ["label"] = h.Label, ["index"] = i, ["current"] = i == _historyIndex }).ToList(),
                ["templates"] = TemplateNames().Cast<object?>().ToList(),
            };
        }

        private string CategorySummary(FilterRuleConfig r)
        {
            if (r.BuiltInCategories.Count == 0) return TF("ruleList.allCategories");
            string names = string.Join(", ",
                r.BuiltInCategories.Select(AutoFiltersSettings.DisplayNameForOst));
            return r.MatchType == "all" ? names + TF("ruleList.wholeCategorySuffix") : names;
        }

        // Batch-mode payload: present only while >= 2 rules are selected. Carries the
        // pre-formatted batch header and the merge section (plan summary or blocked reason
        // plus the confirm texts) so the page renders strings verbatim.
        private Dictionary<string, object?>? BuildBatch()
        {
            var anchorRule = ActiveRule();
            if (_selectedRuleIds.Count < 2 || anchorRule == null) return null;

            var plan = ComputeMergePlan();
            var d = new Dictionary<string, object?>
            {
                ["count"]       = _selectedRuleIds.Count,
                ["header"]      = TF("sections.batchEdit"),
                ["desc"]        = TF("batchEdit.editingDesc", _selectedRuleIds.Count),
                ["mergeHeader"] = TF("sections.merge"),
                ["ok"]          = plan.Ok,
            };
            if (!plan.Ok) { d["reason"] = plan.Reason; return d; }

            string catWord = plan.Categories.Count == 1
                ? TF("merge.categoryWordSingular") : TF("merge.categoryWordPlural");
            string resultNameCombined = anchorRule.Name + TF("suffixes.combined");
            d["summary"]       = TF("merge.summary", _selectedRuleIds.Count, plan.Keywords.Count, plan.Categories.Count, catWord, plan.MatchType);
            d["mergeLabel"]    = TF("merge.mergeIntoOne");
            d["combineLabel"]  = TF("merge.createCombined");
            d["confirmMerge"]  = TF("merge.confirmMerge", anchorRule.Name, _selectedRuleIds.Count - 1);
            d["confirmCreate"] = TF("merge.confirmCreate", resultNameCombined);
            d["keywordsLine"]  = TF("merge.keywordsLine", plan.Keywords.Count > 0 ? string.Join(", ", plan.Keywords) : TF("merge.none"));
            d["categoriesLine"] = TF("merge.categoriesLine", plan.Categories.Count > 0
                ? string.Join(", ", plan.Categories.Select(AutoFiltersSettings.DisplayNameForOst))
                : TF("merge.all"));
            d["mergeBtn"]  = TF("merge.mergeBtn");
            d["createBtn"] = TF("merge.createBtn");
            return d;
        }

        private Dictionary<string, object?>? BuildEditor()
        {
            var r = ActiveRule();
            if (r == null) return null;

            string paramOptionsFor = r.BuiltInCategories.FirstOrDefault() ?? "";
            return new Dictionary<string, object?>
            {
                ["id"]   = r.Id,
                ["name"] = r.Name,
                ["categories"] = r.BuiltInCategories.Select(ost => (object?)new Dictionary<string, object?>
                {
                    ["ost"] = ost, ["label"] = AutoFiltersSettings.DisplayNameForOst(ost),
                }).ToList(),
                ["categoryOptions"]  = AutoFiltersSettings.KnownCategoryDisplayNames.Cast<object?>().ToList(),
                ["matchType"]        = r.MatchType,
                ["parameter"]        = r.Parameter ?? "",
                ["parameterBuiltIn"] = AutoFiltersSettings.IsLinkSafeParameter(r.Parameter),
                ["parameterOptions"] = AutoFiltersSettings.GetParametersFor(paramOptionsFor).Cast<object?>().ToList(),
                ["keywords"]         = r.Match.Cast<object?>().ToList(),
                ["enabled"]          = r.Enabled,
                ["surface"] = new Dictionary<string, object?>
                {
                    ["on"]      = _surfLayer == "BG" ? r.OverrideSurfBg : r.OverrideSurf,
                    ["layer"]   = _surfLayer,
                    ["color"]   = _surfLayer == "BG" ? r.SurfBgColor : r.SurfColor,
                    ["pattern"] = _surfLayer == "BG" ? r.SurfBgPattern : r.SurfPattern,
                },
                ["cut"] = new Dictionary<string, object?>
                {
                    ["on"]      = _cutLayer == "BG" ? r.OverrideCutBg : r.OverrideCut,
                    ["layer"]   = _cutLayer,
                    ["color"]   = _cutLayer == "BG" ? r.CutBgColor : r.CutColor,
                    ["pattern"] = _cutLayer == "BG" ? r.CutBgPattern : r.CutPattern,
                },
                ["lines"] = new Dictionary<string, object?>
                {
                    ["on"] = r.OverrideLine, ["color"] = r.LineColor, ["weight"] = r.LineWeight,
                },
                ["transparency"]   = r.Transparency,
                ["halftone"]       = r.Halftone,
                ["visible"]        = r.Visible,
                ["filterOn"]       = r.Enabled,
                ["patternOptions"] = _fillPatterns.Cast<object?>().ToList(),
            };
        }

        // ── Serialize helpers (same discipline as the WPF window) ────────────
        private static string Serialize(List<FilterTradeConfig>? trades)
        {
            if (trades == null) return "";
            try
            {
                var xs = new System.Xml.Serialization.XmlSerializer(typeof(List<FilterTradeConfig>));
                using (var sw = new System.IO.StringWriter(CultureInfo.InvariantCulture))
                {
                    xs.Serialize(sw, trades);
                    return sw.ToString();
                }
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed("WebAutoFilters.Serialize", ex);
                return Guid.NewGuid().ToString();   // treat as dirty — never lose edits
            }
        }

        private static List<FilterTradeConfig> Deserialize(string? xml)
        {
            if (string.IsNullOrEmpty(xml)) return new List<FilterTradeConfig>();
            try
            {
                var xs = new System.Xml.Serialization.XmlSerializer(typeof(List<FilterTradeConfig>));
                using (var sr = new System.IO.StringReader(xml))
                    return (List<FilterTradeConfig>)xs.Deserialize(sr) ?? new List<FilterTradeConfig>();
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed("WebAutoFilters.Deserialize", ex);
                return new List<FilterTradeConfig>();
            }
        }

        private Dictionary<string, object?> Labels() => new Dictionary<string, object?>
        {
            ["undoTip"]        = TW("window.toolbar.undoTooltip"),
            ["redoTip"]        = TW("window.toolbar.redoTooltip"),
            ["history"]        = TW("window.toolbar.historyButton"),
            ["historyHeader"]  = TW("window.history.popupHeader"),
            ["historyNow"]     = TW("window.history.tagNow"),
            ["deleteFromProject"] = TW("window.toolbar.deleteFromProject"),
            ["templates"]      = TF("sidebar.templatesButton"),
            ["discover"]       = TF("sidebar.discoverPill"),
            ["addTrade"]       = TF("sidebar.addTradePill"),
            ["editTradeTip"]   = TF("sidebar.editTradeTooltip"),
            ["applyToView"]    = TF("sidebar.applyToViewLabel"),
            ["removeFromView"] = TF("sidebar.removeFromViewLabel"),
            ["noTrades"]       = TF("sidebar.noTrades"),
            ["addRule"]        = TF("ruleList.addRulePill"),
            ["editRuleTip"]    = TF("ruleList.editRuleTooltip"),
            ["noRules"]        = TF("ruleList.noRulesYet"),
            ["applyTradeToView"] = TF("ruleList.applyTradeFooterLabel"),
            ["noRule"]         = TF("editor.selectRulePrompt"),
            ["filterLogic"]    = TF("sections.filterLogic"),
            ["overrideStyle"]  = TF("sections.overrideStyle"),
            ["appearance"]     = TF("sections.appearanceVisibility"),
            ["colors"]         = TF("sections.colors"),
            ["category"]       = TF("filterLogic.categoryLabel"),
            ["parameter"]      = TF("filterLogic.parameterLabel"),
            ["searchString"]   = TF("filterLogic.searchStringLabel"),
            ["all"]            = TF("filterLogic.wholeCategoryToggleLabel"),
            ["allDesc"]        = TF("filterLogic.wholeCategoryDesc"),
            ["builtInParam"]   = TF("filterLogic.hintLinkSafe"),
            ["notLinkSafe"]    = TF("filterLogic.hintNotLinkSafe"),
            ["addCategoryTip"] = TF("filterLogic.categoryPlaceholder"),
            ["pickParameterTip"] = TF("filterLogic.parameterPlaceholder"),
            ["addKeywordTip"]  = TF("filterLogic.keywordPlaceholder"),
            ["removeTip"]      = TW("window.common.cancel"),
            ["matchContains"]  = "contains",
            ["matchEquals"]    = "equals",
            ["surface"]        = TF("overrideStyle.surface"),
            ["cut"]            = TF("overrideStyle.cut"),
            ["lines"]          = TF("overrideStyle.lines"),
            ["solidFill"]      = TF("overrideStyle.solidFill"),
            ["pickColorTip"]   = TF("addTradeForm.colorLabel"),
            ["transparency"]   = TF("transparency.label"),
            ["halftone"]       = TF("appearance.halftone"),
            ["halftoneDesc"]   = TF("appearance.halftone"),
            ["visible"]        = TF("appearance.visibleLabel"),
            ["visibleDesc"]    = TF("appearance.visibleTooltip"),
            ["filterOn"]       = TF("appearance.applyLabel"),
            ["filterOnDesc"]   = TF("appearance.applyTooltip"),
            ["cancel"]         = TF("common.cancel"),
            ["tradeIdLabel"]   = TF("common.tradeIdLabel"),
            ["tradeNameLabel"] = TF("tradeEditPopup.nameLabel"),
            ["tradeDuplicate"] = TF("tradeEditPopup.duplicate"),
            ["tradeDelete"]    = TF("tradeEditPopup.deleteTrade"),
            ["addTradeNameLabel"] = TF("addTradeForm.labelLabel"),
            ["addTradeColorLabel"] = TF("addTradeForm.colorLabel"),
            ["addTradeApply"]  = TF("addTradeForm.addTrade"),
            ["ruleNameLabel"]  = TF("ruleEditPopup.nameLabel"),
            ["ruleDelete"]     = TF("ruleEditPopup.deleteRule"),
            ["ruleCopy"]       = TF("ruleEditPopup.copy"),
            ["save"]           = TF("templatesPopup.save"),
            ["tmplSavedHeader"] = TF("templatesPopup.savedTemplatesHeader"),
            ["tmplNone"]       = TF("templatesPopup.noSavedTemplates"),
            ["tmplDeleteTip"]  = TF("templatesPopup.deleteTemplate"),
            ["tmplSaveAs"]     = TF("templatesPopup.saveCurrentAsTemplate"),
            ["tmplFileHeader"] = TF("templatesPopup.fileHeader"),
            ["tmplImport"]     = TF("templatesPopup.importFromFile"),
            ["tmplExport"]     = TF("templatesPopup.exportToFile"),
            ["tmplRestore"]    = TF("templatesPopup.restoreDefaults"),
            ["tmplRestoreConfirm"] = TF("templatesPopup.restoreConfirm"),
            ["tmplRestoreYes"] = TF("templatesPopup.yesRestore"),
        };
    }
}
