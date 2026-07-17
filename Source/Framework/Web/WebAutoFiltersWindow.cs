using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LemoineTools.Tools.AutoFilters;

namespace LemoineTools.Framework.Web
{
    /// <summary>
    /// HTML analogue of <see cref="FiltersSettingsWindow"/> — the Auto Filters editor. Drives its
    /// UI from <see cref="WebAutoFilters"/> and reuses the SAME ExternalEvents as the WPF window:
    /// AutoFiltersHandler (apply/create), DeleteFiltersHandler (remove from view),
    /// OpenDiscoverEvent, OpenDeleteFromProjectEvent — plus the same close-time auto-create pass
    /// over changed filters. Opened behind the Web UI flag (rule R25).
    /// </summary>
    public sealed class WebAutoFiltersWindow : WebWindowBase
    {
        private readonly WebAutoFilters _model;

        public WebAutoFiltersWindow(List<string> fillPatterns, List<string> linePatterns)
            : base(AppStrings.T("autofilters.filtersWindow.window.toolbar.title"), 1500, 900, 1100, 640)
        {
            _model = new WebAutoFilters(fillPatterns, linePatterns);
            // Reload discovered rules when focus returns from the Discover window (same
            // contract as the WPF window's Activated hook).
            Activated += OnActivatedReload;
        }

        protected override string PageFileName => "autofilters.html";

        protected override Dictionary<string, object?> BuildInitPayload() => _model.BuildPayload();

        private static string TW(string key, params object[] args) =>
            AppStrings.T("autofilters.filtersWindow." + key, args);
        private static string TF(string key, params object[] args) =>
            AppStrings.T("globalSettings.filters." + key, args);

        private void OnActivatedReload(object? sender, EventArgs e)
        {
            try { if (_model.ReloadIfChangedExternally()) SendInit(); }
            catch (Exception ex) { DiagnosticsLog.Swallowed("WebAutoFiltersWindow: reload on activate", ex); }
        }

        protected override bool HandleAction(string action, IReadOnlyDictionary<string, object?> p)
        {
            switch (action)
            {
                // ── Trades ────────────────────────────────────────────────────
                case "selectTrade":  _model.SelectTrade(Str(p, "id")); SendInit(); return true;
                case "checkTrade":   _model.SetTradeChecked(Str(p, "id"), GetBool(p, "value")); return true;
                case "addTrade":     _model.AddTrade(Str(p, "label"), Str(p, "color")); SendInit(); return true;
                case "applyTradeEdit": _model.ApplyTradeEdit(Str(p, "id"), Str(p, "label"), Str(p, "color")); SendInit(); return true;
                case "duplicateTrade": _model.DuplicateTrade(Str(p, "id")); SendInit(); return true;
                case "deleteTrade":  _model.DeleteTrade(Str(p, "id")); SendInit(); return true;

                // ── Rules ─────────────────────────────────────────────────────
                case "selectRule":   _model.SelectRule(Str(p, "id"), GetBool(p, "shift"), GetBool(p, "ctrl")); SendInit(); return true;
                case "addRule":      _model.AddRule(); SendInit(); return true;
                case "reorderRule":  _model.ReorderRule(GetInt(p, "from"), GetInt(p, "to")); SendInit(); return true;
                case "applyRuleEdit": _model.ApplyRuleEdit(Str(p, "id"), Str(p, "name")); SendInit(); return true;
                case "duplicateRule": _model.DuplicateRule(Str(p, "id")); SendInit(); return true;
                case "deleteRule":   _model.DeleteRule(Str(p, "id")); SendInit(); return true;

                // ── Rule editor ───────────────────────────────────────────────
                case "addCategory":    _model.AddCategory(Str(p, "value")); SendInit(); return true;
                case "removeCategory": _model.RemoveCategory(Str(p, "value")); SendInit(); return true;
                case "setMatchAll":    _model.SetMatchAll(GetBool(p, "value")); SendInit(); return true;
                case "setMatchType":   _model.SetMatchType(Str(p, "value")); return true;
                case "setParameter":   _model.SetParameter(Str(p, "value")); SendInit(); return true;
                case "clearParameter": _model.SetParameter(""); SendInit(); return true;
                case "addKeyword":     _model.AddKeyword(Str(p, "value")); SendInit(); return true;
                case "removeKeyword":  _model.RemoveKeyword(Str(p, "value")); SendInit(); return true;
                case "setLayer":       _model.SetLayer(Str(p, "layer"), Str(p, "value")); SendInit(); return true;
                case "setOverrideOn":  _model.SetOverrideOn(Str(p, "layer"), GetBool(p, "value")); SendInit(); return true;
                case "setColor":       _model.SetColor(Str(p, "layer"), Str(p, "value")); SendInit(); return true;
                case "setPattern":     _model.SetPattern(Str(p, "layer"), Str(p, "value")); return true;
                case "setLineWeight":  _model.SetLineWeight(GetInt(p, "value")); return true;
                case "setTransparency": _model.SetTransparency(GetInt(p, "value")); return true;
                case "setFlag":        _model.SetFlag(Str(p, "flag"), GetBool(p, "value")); SendInit(); return true;

                case "applyMerge":     _model.ApplyMerge(GetBool(p, "destructive")); SendInit(); return true;

                // ── History ───────────────────────────────────────────────────
                case "undo":        _model.Undo(); SendInit(); return true;
                case "redo":        _model.Redo(); SendInit(); return true;
                case "historyJump": _model.HistoryJump(GetInt(p, "index")); SendInit(); return true;

                // ── Templates ─────────────────────────────────────────────────
                case "templateLoad":
                    _model.SetStatus(_model.TemplateLoad(Str(p, "name"), out var loadErr)
                        ? TF("status.loaded", Str(p, "name")) : loadErr ?? TF("dialogs.loadFailed"));
                    SendInit(); return true;
                case "templateSave":
                    _model.SetStatus(_model.TemplateSave(Str(p, "name"), out var saveErr)
                        ? TF("status.savedTemplate", Str(p, "name")) : saveErr ?? TF("dialogs.saveFailed"));
                    SendInit(); return true;
                case "templateDelete":
                    if (!_model.TemplateDelete(Str(p, "name"), out var delErr))
                        DiagnosticsLog.Warn("WebAutoFiltersWindow: template delete", delErr ?? "unknown");
                    SendInit(); return true;
                case "restoreDefaults":
                    _model.RestoreDefaults();
                    _model.SetStatus(TF("status.restoredDefaults"));
                    SendInit(); return true;
                case "importFile": ImportFromFile(); return true;
                case "exportFile": ExportToFile();   return true;

                // ── Revit actions (same events as the WPF window) ─────────────
                case "applyToView":      ApplyTradesToView(_model.CheckedTrades()); return true;
                case "applyTradeToView":
                    var active = _model.ActiveTrade();
                    if (active == null) { Flash(TW("window.status.noTradeSelected")); return true; }
                    ApplyTradesToView(new List<FilterTradeConfig> { active });
                    return true;
                case "removeFromView":   RemoveTradesFromView(_model.CheckedTrades()); return true;
                case "discover":         LaunchDiscover(); return true;
                case "deleteFromProject": OpenDeleteFromProject(); return true;

                default: return false;
            }
        }

        private void Flash(string msg) { _model.SetStatus(msg); SendInit(); }

        // ── Apply / Remove / Discover / Delete-from-Project ───────────────────
        private void ApplyTradesToView(List<FilterTradeConfig> trades)
        {
            if (trades.Count == 0) { Flash(TW("window.status.noTradesSelected")); return; }

            var handler = App.AutoFiltersHandler;
            var evt     = App.AutoFiltersEvent;
            if (handler == null || evt == null) { Flash(TW("window.status.applyUnavailable")); return; }

            _model.Persist();   // handler reads AutoFiltersSettings.Instance

            handler.CreateOnly                       = false;
            handler.OverwriteFilterDefinition        = true;
            handler.KeepExistingOverrides            = false;
            handler.IncludeSelectedExternallyManaged = true;
            handler.ShowFailureDialog                = true;
            handler.ChangedFilterNames               = null;
            handler.SelectedDisciplines              = trades.Select(t => t.Label).ToList();
            handler.SelectedLinkTitles               = new List<string>();
            handler.PushLog                          = null;
            handler.OnProgress                       = null;
            handler.OnComplete                       = null;

            evt.Raise();
            Flash(trades.Count == 1
                ? TW("window.status.applyingOne", trades[0].Label)
                : TW("window.status.applyingMany", trades.Count));
        }

        private void RemoveTradesFromView(List<FilterTradeConfig> trades)
        {
            if (trades.Count == 0) { Flash(TW("window.status.noTradesSelected")); return; }

            var handler = App.DeleteFiltersHandler;
            var evt     = App.DeleteFiltersEvent;
            if (handler == null || evt == null) { Flash(TW("window.status.removeUnavailable")); return; }

            var names = new List<string>();
            foreach (var t in trades)
                foreach (var r in t.Rules)
                {
                    if (!r.Enabled) continue;
                    if (!t.ExternallyManaged && !AutoFiltersSettings.RuleProducesFilter(r)) continue;
                    names.Add(AutoFiltersSettings.MakeFilterName(t.Id, r.Name));
                }
            if (names.Count == 0) { Flash(TW("window.status.noFiltersToRemove")); return; }

            handler.SelectedFilterNames = names;
            handler.PushLog    = null;
            handler.OnProgress = null;
            handler.OnComplete = null;
            evt.Raise();
            Flash(trades.Count == 1
                ? TW("window.status.removingOne", trades[0].Label)
                : TW("window.status.removingMany", trades.Count));
        }

        private void LaunchDiscover()
        {
            // Persist a deep copy first so Discover reads the latest rules and its additions
            // read back as an external change on re-activation (same as the WPF window).
            _model.Persist();

            var evt = App.OpenDiscoverEvent;
            if (evt == null) { Flash(TW("window.status.discoverUnavailable")); return; }
            evt.Raise();
            Flash(TW("window.status.openingDiscover"));
        }

        private void OpenDeleteFromProject()
        {
            var evt = App.OpenDeleteFromProjectEvent;
            if (evt == null) { Flash(TW("window.status.deleteFromProjectUnavailable")); return; }
            evt.Raise();
            Flash(TW("window.status.openingDeleteFromProject"));
        }

        // ── File import / export (native dialogs on this STA thread) ──────────
        private void ImportFromFile()
        {
            try
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title  = TF("dialogs.importTitle"),
                    Filter = TF("dialogs.xmlFilter"),
                };
                if (dlg.ShowDialog(this) != true) return;
                _model.SetStatus(_model.ImportFrom(dlg.FileName)
                    ? TF("status.imported") : TF("dialogs.importFailed"));
                SendInit();
            }
            catch (Exception ex) { DiagnosticsLog.Error("WebAutoFiltersWindow: import", ex); }
        }

        private void ExportToFile()
        {
            try
            {
                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Title    = TF("dialogs.exportTitle"),
                    Filter   = TF("dialogs.xmlFilter"),
                    FileName = "LemoineAutoFilters.xml",
                };
                if (dlg.ShowDialog(this) != true) return;
                _model.SetStatus(_model.ExportTo(dlg.FileName)
                    ? TF("status.exported") : TF("dialogs.exportFailed"));
                SendInit();
            }
            catch (Exception ex) { DiagnosticsLog.Error("WebAutoFiltersWindow: export", ex); }
        }

        // ── Close: save-if-dirty + auto-create pass (mirrors the WPF OnClosed) ─
        protected override void OnWindowClosed()
        {
            Activated -= OnActivatedReload;
            try
            {
                var (shouldRaise, changed, changedOverride) = _model.FlushOnClose();
                if (!shouldRaise) return;

                var handler = App.AutoFiltersHandler;
                var evt     = App.AutoFiltersEvent;
                if (handler == null || evt == null)
                {
                    DiagnosticsLog.Warn("WebAutoFiltersWindow", "Auto-create skipped: event handler unavailable.");
                    return;
                }

                handler.CreateOnly               = true;
                handler.ShowFailureDialog        = true;
                handler.ChangedFilterNames       = changed;
                handler.ApplyOverrideFilterNames = changedOverride;
                handler.SelectedDisciplines      = new List<string>();
                handler.SelectedLinkTitles       = new List<string>();
                handler.PushLog                  = null;
                handler.OnProgress               = null;
                handler.OnComplete               = null;
                evt.Raise();
            }
            catch (Exception ex) { DiagnosticsLog.Error("WebAutoFiltersWindow: close flush", ex); }
        }

        // ── payload helpers ────────────────────────────────────────────────────
        private static bool GetBool(IReadOnlyDictionary<string, object?> p, string key) =>
            p.TryGetValue(key, out var v) && (v is bool b ? b : bool.TryParse(v?.ToString(), out var r) && r);

        private static int GetInt(IReadOnlyDictionary<string, object?> p, string key)
        {
            if (!p.TryGetValue(key, out var v) || v == null) return 0;
            if (v is int i) return i;
            if (v is long l) return (int)l;
            if (v is double d) return (int)d;
            return int.TryParse(v.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) ? r : 0;
        }
    }
}
