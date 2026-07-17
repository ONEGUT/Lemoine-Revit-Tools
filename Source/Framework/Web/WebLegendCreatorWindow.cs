using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using LemoineTools.Tools.FiltersLegends.LegendCreator;

namespace LemoineTools.Framework.Web
{
    /// <summary>
    /// HTML analogue of <see cref="LegendSettingsWindow"/> — the Legend Creator. Drives its UI
    /// from <see cref="WebLegendCreator"/> (which edits <see cref="LegendCreatorSettings"/>
    /// directly) and runs Create/Update through the SAME <see cref="LegendCreatorEventHandler"/>
    /// + ExternalEvent as the WPF window. Opened behind the Web UI flag (rule R25).
    /// </summary>
    public sealed class WebLegendCreatorWindow : WebWindowBase
    {
        private readonly WebLegendCreator _model;
        private bool _createBusy;

        public WebLegendCreatorWindow(List<(long Id, string Name)> textTypes)
            : base(AppStrings.T("testing.legendCreator.builder.window.titleBar.title"), 1400, 860, 1000, 600)
        {
            _model = new WebLegendCreator(textTypes);
        }

        protected override string PageFileName => "legendcreator.html";

        protected override Dictionary<string, object?> BuildInitPayload() => _model.BuildPayload();

        private static string T(string key, params object[] args) =>
            AppStrings.T("testing.legendCreator.builder." + key, args);

        protected override bool HandleAction(string action, IReadOnlyDictionary<string, object?> p)
        {
            switch (action)
            {
                case "selectLegend":  _model.SelectLegend(Str(p, "id")); SendInit(); return true;
                case "addLegend":     _model.AddLegend(); SendInit(); return true;
                case "applyLegendEdit": _model.ApplyLegendEdit(Str(p, "id"), Str(p, "title"), Str(p, "subtitle")); SendInit(); return true;
                case "duplicateLegend": _model.DuplicateLegend(Str(p, "id")); SendInit(); return true;
                case "deleteLegend":  _model.DeleteLegend(Str(p, "id")); SendInit(); return true;

                case "addGroup":      _model.AddGroup(); SendInit(); return true;
                case "deleteGroup":   _model.DeleteGroup(Str(p, "id")); SendInit(); return true;
                case "toggleGroup":   _model.ToggleGroupCollapsed(Str(p, "id"), GetBool(p, "value")); SendInit(); return true;
                case "renameGroup":   _model.RenameGroup(Str(p, "id"), Str(p, "value")); SendInit(); return true;
                case "toggleGroupVisibility": _model.ToggleGroupVisibility(Str(p, "id")); SendInit(); return true;
                case "addBlock":      _model.AddCustomBlock(Str(p, "groupId")); SendInit(); return true;
                case "deleteBlock":   _model.DeleteBlock(Str(p, "groupId"), Str(p, "id")); SendInit(); return true;
                case "toggleBlockVisibility": _model.ToggleBlockVisibility(Str(p, "groupId"), Str(p, "id")); SendInit(); return true;
                case "renameBlock":   _model.RenameBlock(Str(p, "groupId"), Str(p, "id"), Str(p, "value")); SendInit(); return true;
                case "dropFilter":    _model.DropFilter(Str(p, "groupId"), Str(p, "key")); SendInit(); return true;
                case "moveGroup":     _model.MoveGroup(Str(p, "id"), GetInt(p, "rowIndex"), GetInt(p, "colIndex"), GetBool(p, "newRow")); SendInit(); return true;

                case "setSizing":     _model.SetSizing(Str(p, "field"), Get(p, "value")); return true;
                case "setTextStyle":  _model.SetTextStyle(Str(p, "role"), Str(p, "value")); return true;
                case "setPaletteTrade": _model.SetPaletteTrade(Str(p, "value")); SendInit(); return true;

                case "templateLoad":
                    if (!_model.TemplateLoad(Str(p, "name"), out var loadErr))
                    {
                        DiagnosticsLog.Warn("WebLegendCreatorWindow: template load", loadErr ?? "unknown");
                        _model.SetStatus(loadErr ?? T("builder.templatesPopup.loadFailedMessage"));
                    }
                    SendInit(); return true;
                case "templateSave":
                    if (!_model.TemplateSave(Str(p, "name"), out var saveErr))
                    {
                        DiagnosticsLog.Warn("WebLegendCreatorWindow: template save", saveErr ?? "unknown");
                        _model.SetStatus(saveErr ?? T("builder.templatesPopup.saveFailedMessage"));
                    }
                    SendInit(); return true;
                case "templateDelete":
                    if (!_model.TemplateDelete(Str(p, "name"), out var delErr))
                        DiagnosticsLog.Warn("WebLegendCreatorWindow: template delete", delErr ?? "unknown");
                    SendInit(); return true;
                case "importFile": ImportFromFile(); return true;
                case "exportFile": ExportToFile();   return true;

                case "createOrUpdate": RunCreateOrUpdate(); return true;
                default: return false;
            }
        }

        // ── Create / Update run (same handler + payload as the WPF window) ────
        private void RunCreateOrUpdate()
        {
            if (_createBusy) return;
            var entry = _model.ActiveEntry();
            if (entry == null) return;

            var handler = App.LegendCreatorHandler;
            var evt     = App.LegendCreatorEvent;
            if (handler == null || evt == null)
            {
                _model.SetStatus(T("window.status.notInitialized"));
                SendInit();
                return;
            }

            bool isUpdate = entry.RevitViewId != -1;
            if (entry.Layout == null) entry.Layout = new LegendLayoutConfig();
            LegendCreatorSettings.Instance.Save();

            handler.Layout            = entry.Layout;
            handler.Rows              = entry.Rows;
            handler.UpdateMode        = isUpdate;
            handler.TargetLegendId    = isUpdate ? new ElementId(entry.RevitViewId) : (ElementId?)null;
            handler.TemplateLegendId  = null;
            handler.TitleTypeId       = entry.TitleTypeId       != -1 ? new ElementId(entry.TitleTypeId)       : (ElementId?)null;
            handler.SubtitleTypeId    = entry.SubtitleTypeId    != -1 ? new ElementId(entry.SubtitleTypeId)    : (ElementId?)null;
            handler.GroupHeaderTypeId = entry.GroupHeaderTypeId != -1 ? new ElementId(entry.GroupHeaderTypeId) : (ElementId?)null;
            handler.LabelTypeId       = entry.LabelTypeId       != -1 ? new ElementId(entry.LabelTypeId)       : (ElementId?)null;
            handler.PushLog           = null;
            handler.OnProgress        = null;

            string entryId = entry.Id;
            handler.OnLegendCreated = viewId => RunOnUiThread(() =>
            {
                // Rebind THIS entry (not whatever is active by then) to the new Revit view.
                var e = LegendCreatorSettings.Instance.Legends.Find(l => l.Id == entryId);
                if (e == null) return;
                e.RevitViewId = viewId.Value;
                LegendCreatorSettings.Instance.Save();
            });
            handler.OnComplete = (pass, fail, skip) => RunOnUiThread(() =>
            {
                _createBusy = false;
                _model.SetStatus(fail > 0
                    ? T("window.status.completedWithErrors", fail)
                    : isUpdate ? T("window.status.updatedMessage") : T("window.status.createdMessage"));
                SendInit();
            });

            _createBusy = true;
            _model.SetStatus(isUpdate ? T("window.status.updating") : T("window.status.creating"));
            SendInit();
            evt.Raise();
        }

        // ── File import / export (native dialogs on this STA thread) ──────────
        private void ImportFromFile()
        {
            try
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title  = T("builder.templatesPopup.importDialogTitle"),
                    Filter = T("builder.templatesPopup.xmlFileFilter"),
                };
                if (dlg.ShowDialog(this) != true) return;
                if (!_model.ImportFrom(dlg.FileName))
                    _model.SetStatus(T("builder.templatesPopup.importFailedMessage"));
                SendInit();
            }
            catch (Exception ex) { DiagnosticsLog.Error("WebLegendCreatorWindow: import", ex); }
        }

        private void ExportToFile()
        {
            try
            {
                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Title  = T("builder.templatesPopup.exportDialogTitle"),
                    Filter = T("builder.templatesPopup.xmlFileFilter"),
                    FileName = "LegendCreatorSettings.xml",
                };
                if (dlg.ShowDialog(this) != true) return;
                _model.ExportTo(dlg.FileName);
                SendInit();
            }
            catch (Exception ex) { DiagnosticsLog.Error("WebLegendCreatorWindow: export", ex); }
        }

        protected override void OnWindowClosed()
        {
            // Sever run callbacks so a late completion can't marshal into a dead dispatcher.
            var h = App.LegendCreatorHandler;
            if (h != null) { h.OnComplete = null; h.OnLegendCreated = null; h.PushLog = null; h.OnProgress = null; }
            try { LegendCreatorSettings.Instance.Save(); }
            catch (Exception ex) { DiagnosticsLog.Error("WebLegendCreatorWindow: save on close", ex); }
        }

        private static object? Get(IReadOnlyDictionary<string, object?> p, string key) =>
            p.TryGetValue(key, out var v) ? v : null;

        private static bool GetBool(IReadOnlyDictionary<string, object?> p, string key) =>
            p.TryGetValue(key, out var v) && (v is bool b ? b : bool.TryParse(v?.ToString(), out var r) && r);

        private static int GetInt(IReadOnlyDictionary<string, object?> p, string key)
        {
            if (!p.TryGetValue(key, out var v) || v == null) return 0;
            if (v is int i) return i;
            if (v is long l) return (int)l;
            if (v is double d) return (int)d;
            return int.TryParse(v.ToString(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var r) ? r : 0;
        }
    }
}
