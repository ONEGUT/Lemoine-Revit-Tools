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
            EnsureActive();
        }

        // ── Trade mutations ───────────────────────────────────────────────────
        public void SelectTrade(string id)
        {
            _activeTradeId = id;
            _activeRuleId  = ActiveTrade()?.Rules.FirstOrDefault()?.Id;
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
        public void SelectRule(string id) => _activeRuleId = id;

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

        // ── Rule editor setters (active rule) ─────────────────────────────────
        public void AddCategory(string displayName)
        {
            var r = ActiveRule(); if (r == null) return;
            if (!AutoFiltersSettings.TryResolveCategoryOst(displayName, out var ost)) return;
            if (!r.BuiltInCategories.Contains(ost)) r.BuiltInCategories.Add(ost);
            Capture(TW("window.history.edit", r.Name));
        }

        public void RemoveCategory(string displayName)
        {
            var r = ActiveRule(); if (r == null) return;
            if (AutoFiltersSettings.TryResolveCategoryOst(displayName, out var ost))
                r.BuiltInCategories.Remove(ost);
            else
                r.BuiltInCategories.Remove(displayName);
            Capture(TW("window.history.edit", r.Name));
        }

        public void SetMatchAll(bool on)
        {
            var r = ActiveRule(); if (r == null) return;
            r.MatchType = on ? "all" : "contains";
            Capture(TW("window.history.edit", r.Name));
        }

        public void SetMatchType(string matchType)
        {
            var r = ActiveRule(); if (r == null) return;
            r.MatchType = matchType == "equals" ? "equals" : "contains";
            Capture(TW("window.history.edit", r.Name));
        }

        public void SetParameter(string name)
        {
            var r = ActiveRule(); if (r == null) return;
            r.Parameter = name ?? "";
            Capture(TW("window.history.edit", r.Name));
        }

        public void AddKeyword(string keyword)
        {
            var r = ActiveRule(); if (r == null || string.IsNullOrWhiteSpace(keyword)) return;
            keyword = keyword.Trim();
            if (!r.Match.Contains(keyword)) r.Match.Add(keyword);
            Capture(TW("window.history.edit", r.Name));
        }

        public void RemoveKeyword(string keyword)
        {
            var r = ActiveRule(); if (r == null) return;
            r.Match.Remove(keyword);
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
            Capture(TW("window.history.edit", r.Name));
        }

        public void SetLineWeight(int weight)
        {
            var r = ActiveRule(); if (r == null) return;
            r.LineWeight = Math.Max(1, Math.Min(14, weight));
            Capture(TW("window.history.edit", r.Name));
        }

        public void SetTransparency(int value)
        {
            var r = ActiveRule(); if (r == null) return;
            r.Transparency = Math.Max(0, Math.Min(100, value));
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
            Capture(TW("window.history.edit", r.Name));
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
                        ["enabled"] = r.Enabled,
                    }).ToList(),
                ["editor"]  = BuildEditor(),
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
