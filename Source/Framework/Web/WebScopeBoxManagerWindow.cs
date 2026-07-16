using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using LemoineTools.Tools.ScopeBoxes;

namespace LemoineTools.Framework.Web
{
    /// <summary>
    /// HTML analogue of <see cref="ScopeBoxManagerWindow"/> — the scope-box management surface
    /// (sidebar of boxes with usage, a per-box editor, and bulk rename/delete). Drives its UI from
    /// <see cref="WebScopeBoxManager"/> and runs every mutation on Revit's main thread through the
    /// SAME <see cref="ScopeBoxManagerScanHandler"/> / <see cref="ScopeBoxManagerRunHandler"/> +
    /// ExternalEvents the WPF window uses. Refreshes from a fresh usage scan after each action.
    /// Opened behind the Web UI flag; the WPF window stays as the fallback (rule R25).
    /// </summary>
    public sealed class WebScopeBoxManagerWindow : WebWindowBase
    {
        private readonly WebScopeBoxManager _model = new WebScopeBoxManager();
        private bool _busy;

        public WebScopeBoxManagerWindow()
            : base(AppStrings.T("scopeBoxes.manager.toolbar.title"), 1100, 700, 860, 500)
        {
            RaiseScan();   // model holds the result; SendInit re-fires once the page is ready
        }

        protected override string PageFileName => "scopeboxmanager.html";

        protected override Dictionary<string, object?> BuildInitPayload() => _model.BuildPayload();

        private static string T(string key, params object[] args) =>
            AppStrings.T("scopeBoxes.manager." + key, args);

        protected override bool HandleAction(string action, IReadOnlyDictionary<string, object?> p)
        {
            switch (action)
            {
                case "refresh":     RaiseScan(); return true;
                case "setFilter":   _model.SetFilter(Str(p, "value")); SendInit(); return true;
                case "selectBox":   _model.SelectBox(GetLong(p, "id")); SendInit(); return true;

                // Row check toggles — the page already reflects them; no re-render needed.
                case "checkBox":    _model.ToggleBox(GetLong(p, "id"),   GetBool(p, "value")); return true;
                case "checkView":   _model.ToggleView(GetLong(p, "id"),  GetBool(p, "value")); return true;
                case "checkDatum":  _model.ToggleDatum(GetLong(p, "id"), GetBool(p, "value")); return true;

                case "renameInline":
                    long rid = GetLong(p, "id");
                    string rnew = Str(p, "value");
                    RunAction(h => { h.Action = "Rename"; h.RenamePairs = new List<(ElementId, string)> { (Eid(rid), rnew) }; });
                    return true;

                case "duplicate":
                    RunAction(h => { h.Action = "Duplicate"; h.BoxId = Eid(GetLong(p, "id")); });
                    return true;

                case "delete":
                    RunAction(h => { h.Action = "Delete"; h.BoxIds = IdList(p, "ids"); });
                    return true;

                case "setViews":
                    RunAction(h => { h.Action = "SetViews"; h.BoxId = Eid(GetLong(p, "boxId")); h.ViewIds = IdList(p, "ids"); });
                    return true;

                case "setDatums":
                    RunAction(h => { h.Action = "SetDatums"; h.BoxId = Eid(GetLong(p, "boxId")); h.DatumIds = IdList(p, "ids"); });
                    return true;

                case "bindSides":
                    RunAction(h =>
                    {
                        h.Action      = "BindSides";
                        h.BoxId       = Eid(GetLong(p, "boxId"));
                        h.NorthGridId = Eid(GetLong(p, "north"));
                        h.SouthGridId = Eid(GetLong(p, "south"));
                        h.EastGridId  = Eid(GetLong(p, "east"));
                        h.WestGridId  = Eid(GetLong(p, "west"));
                    });
                    return true;

                case "split":
                    RunAction(h =>
                    {
                        h.Action              = "Split";
                        h.BoxId               = Eid(GetLong(p, "boxId"));
                        h.SplitMode           = Str(p, "mode") == "Middle" ? "Middle" : "Gridline";
                        h.SplitGridId         = Eid(GetLong(p, "gridId"));
                        h.SplitAxis           = Str(p, "axis") == "EW" ? "EW" : "NS";
                        h.SplitOverlapFt      = GetDouble(p, "overlap");
                        h.SplitDeleteOriginal = GetBool(p, "deleteOriginal");
                    });
                    return true;

                case "setRenamePattern":
                    _model.SetRenamePattern(Str(p, "value"));
                    return true;

                case "applyRename":
                    string pattern = Str(p, "value");
                    _model.SetRenamePattern(pattern);
                    var pairs = _model.ResolveRenameNames(pattern)
                        .Select(x => (Eid(x.BoxId), x.NewName)).ToList();
                    RunAction(h => { h.Action = "Rename"; h.RenamePairs = pairs; });
                    return true;

                default: return false;
            }
        }

        // ── Scan / run round-trips ────────────────────────────────────────────
        private void RaiseScan()
        {
            var handler = App.ScopeBoxManagerScanHandler;
            var evt     = App.ScopeBoxManagerScanEvent;
            if (handler == null || evt == null) { _model.SetStatus(T("status.noHandler")); SendInit(); return; }

            handler.OnScanComplete = result => RunOnUiThread(() => { _model.SetScan(result); SendInit(); });
            handler.OnError        = msg    => RunOnUiThread(() => { _model.SetStatus(T("status.scanFailed", msg)); SendInit(); });
            evt.Raise();
        }

        private void RunAction(Action<ScopeBoxManagerRunHandler> configure)
        {
            if (_busy) { _model.SetStatus(T("status.busy")); SendInit(); return; }

            var handler = App.ScopeBoxManagerRunHandler;
            var evt     = App.ScopeBoxManagerRunEvent;
            if (handler == null || evt == null) { _model.SetStatus(T("status.noHandler")); SendInit(); return; }

            _busy = true;
            configure(handler);
            handler.OnActionComplete = (ok, failed, summary, refreshed) => RunOnUiThread(() =>
            {
                _busy = false;
                _model.SetStatus(summary);
                if (refreshed != null) _model.SetScan(refreshed);
                SendInit();
            });
            evt.Raise();
        }

        protected override void OnWindowClosed()
        {
            // Session-long static handlers — sever the callbacks so this closed window (and its
            // page state) isn't retained, and a late scan/action can't marshal into a dead dispatcher.
            var scan = App.ScopeBoxManagerScanHandler;
            if (scan != null) { scan.OnScanComplete = null; scan.OnError = null; }
            var run = App.ScopeBoxManagerRunHandler;
            if (run != null) run.OnActionComplete = null;
        }

        // ── payload helpers ──────────────────────────────────────────────────
        private static ElementId Eid(long v) => v > 0 ? new ElementId(v) : ElementId.InvalidElementId;

        private static long GetLong(IReadOnlyDictionary<string, object?> p, string key)
        {
            if (!p.TryGetValue(key, out var v) || v == null) return 0;
            if (v is long l) return l;
            if (v is int i) return i;
            if (v is double d) return (long)d;
            return long.TryParse(v.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) ? r : 0;
        }

        private static double GetDouble(IReadOnlyDictionary<string, object?> p, string key)
        {
            if (!p.TryGetValue(key, out var v) || v == null) return 0;
            if (v is double d) return d;
            if (v is long l) return l;
            if (v is int i) return i;
            return double.TryParse(v.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var r) ? r : 0;
        }

        private static bool GetBool(IReadOnlyDictionary<string, object?> p, string key) =>
            p.TryGetValue(key, out var v) && (v is bool b ? b : bool.TryParse(v?.ToString(), out var r) && r);

        private static List<ElementId> IdList(IReadOnlyDictionary<string, object?> p, string key)
        {
            var ids = new List<ElementId>();
            if (p.TryGetValue(key, out var v) && v is List<object?> list)
                foreach (var o in list)
                {
                    if (o == null) continue;
                    long id = o is long l ? l : o is int i ? i : o is double d ? (long)d
                        : long.TryParse(o.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) ? r : 0;
                    if (id > 0) ids.Add(new ElementId(id));
                }
            return ids;
        }
    }
}
