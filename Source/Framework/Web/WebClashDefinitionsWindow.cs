using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.UI;
using LemoineTools.Tools.Dimensioning;

namespace LemoineTools.Framework.Web
{
    /// <summary>
    /// HTML analogue of <see cref="ClashDefinitionsWindow"/> — the clash-definition library. Sidebar
    /// of definitions + a per-definition editor (name, Group 1 / Group 2 group editors, marking
    /// settings) driven by <see cref="WebClashDefinitions"/>. Element picking bridges to the same
    /// <see cref="ClashPickEventHandler"/> + ExternalEvent the WPF window uses. Auto-saves on close.
    /// </summary>
    public sealed class WebClashDefinitionsWindow : WebWindowBase
    {
        private readonly WebClashDefinitions   _model;
        private readonly ClashPickEventHandler? _pickHandler;
        private readonly ExternalEvent?         _pickEvent;

        public WebClashDefinitionsWindow(
            List<string> lineStyleNames, List<ClashDocInfo> docs, List<string> hostPhaseNames,
            ClashPickEventHandler? pickHandler, ExternalEvent? pickEvent)
            : base(AppStrings.T("clashDefinitions.window.title"), 900, 660, 640, 460)
        {
            _model       = new WebClashDefinitions(lineStyleNames, docs, hostPhaseNames);
            _pickHandler = pickHandler;
            _pickEvent   = pickEvent;
        }

        protected override string PageFileName => "clashdefs.html";

        protected override Dictionary<string, object?> BuildInitPayload() => _model.BuildPayload();

        protected override bool HandleAction(string action, IReadOnlyDictionary<string, object?> p)
        {
            int  group = (int)ToNum(Get(p, "group"), 1);
            switch (action)
            {
                case "addDef":       _model.AddDefinition();                         SendInit(); return true;
                case "dupDef":       _model.DuplicateDefinition(Str(p, "id"));        SendInit(); return true;
                case "delDef":       _model.DeleteDefinition(Str(p, "id"));           SendInit(); return true;
                case "selectDef":    _model.SelectDefinition(Str(p, "id"));           SendInit(); return true;
                case "setName":      _model.SetName(Str(p, "value"));                 SendInit(); return true;
                case "setMode":      _model.SetGroupMode(group, Str(p, "value"));     SendInit(); return true;
                case "toggleDoc":    _model.ToggleDoc(group, (long)ToNum(Get(p, "linkId"), 0), ToBool(Get(p, "value"))); SendInit(); return true;
                case "toggleWorkset":_model.ToggleWorkset(group, (long)ToNum(Get(p, "linkId"), 0), (int)ToNum(Get(p, "wsId"), 0), ToBool(Get(p, "value"))); SendInit(); return true;
                case "clearElements":_model.ClearElements(group);                     SendInit(); return true;
                case "setRules":     _model.SetGroupRules(group, StrList(p, "values"));      return true; // tab manages itself, no re-render
                case "setCategories":_model.SetGroupCategories(group, StrList(p, "values")); return true;
                case "setMarking":
                    string field = Str(p, "field");
                    _model.SetMarking(field, Get(p, "value"));
                    if (field == "phaseMode") SendInit(); // toggles the specific-phase section
                    return true;
                case "pickElements": StartPick(group, ToBool(Get(p, "inLinks"))); return true;
                default: return false;
            }
        }

        private void StartPick(int group, bool inLinks)
        {
            if (_pickHandler == null || _pickEvent == null) return;
            _pickHandler.InLinks  = inLinks;
            _pickHandler.OnPicked = picks =>
                RunOnUiThread(() =>
                {
                    _model.AddPickedElements(group, picks);
                    SendInit();
                });
            _pickEvent.Raise();
        }

        protected override void OnWindowClosed()
        {
            // Sever the pick callback so a late pick can't marshal into this terminated dispatcher.
            if (_pickHandler != null) _pickHandler.OnPicked = null;
            try { _model.Save(); }
            catch (Exception ex) { DiagnosticsLog.Error("WebClashDefinitionsWindow: save on close", ex); }
        }

        // ── payload helpers ──────────────────────────────────────────────────────
        private static object? Get(IReadOnlyDictionary<string, object?> p, string key) =>
            p.TryGetValue(key, out var v) ? v : null;

        private static bool ToBool(object? v) => v is bool b ? b : v is string s && bool.TryParse(s, out var r) && r;
        private static double ToNum(object? v, double dflt) =>
            v is double d ? d : v is long l ? l : v is int i ? i :
            v is string s && double.TryParse(s, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var r) ? r : dflt;

        private static List<string> StrList(IReadOnlyDictionary<string, object?> p, string key)
        {
            if (!(Get(p, key) is List<object?> list)) return new List<string>();
            return list.Select(x => x as string).Where(x => x != null).Cast<string>().ToList();
        }
    }
}
