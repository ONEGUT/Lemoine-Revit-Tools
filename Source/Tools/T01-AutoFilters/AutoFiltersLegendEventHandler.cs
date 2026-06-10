using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.AutoFilters
{
    /// <summary>
    /// Executes Filter Legend creation on the Revit main thread.
    /// Ports the logic from <c>AutoFiltersLegendCommand</c> into the EventHandler pattern.
    /// </summary>
    public class AutoFiltersLegendEventHandler : IExternalEventHandler
    {
        // Layout constants (Revit internal units = feet)
        private const double RowHeight   = 0.75;
        private const double SwatchW     = 0.75;
        private const double SwatchH     = 0.30;
        private const double SwatchNudge = 0.06;
        private const double LabelGap    = 0.20;
        private const double GroupGap    = 1.00;
        private const double HdrPad      = 0.40;
        private const double LabelWidth  = 4.00;

        // ── Callbacks ──────────────────────────────────────────────────────────
        public Action<string, string>?     PushLog    { get; set; }
        public Action<int, int, int, int>? OnProgress { get; set; }
        public Action<int, int, int>?      OnComplete { get; set; }

        // ── IExternalEventHandler ───────────────────────────────────────────────
        public string GetName() => "LemoineTools.Tools.AutoFilters.AutoFiltersLegendEventHandler";

        public void Execute(UIApplication app)
        {
            var doc  = app.ActiveUIDocument.Document;
            var view = doc.ActiveView;
            int pass = 0, fail = 0, skip = 0;

            try
            {
                CreateLegend(doc, view, ref pass, ref fail, ref skip);
            }
            catch (Exception ex)
            {
                LemoineLog.Error("AutoFilters legend: run aborted", ex); Log($"Error: {ex.Message}", "fail");
                fail++;
            }

            Progress(100, pass, fail, skip);
            Complete(pass, fail, skip);
        }

        // ── Core logic ──────────────────────────────────────────────────────────
        private void CreateLegend(Document doc, View view,
            ref int pass, ref int fail, ref int skip)
        {
            if (!view.AreGraphicsOverridesAllowed())
            {
                Log($"Active view '{view.Name}' does not support graphic overrides.", "fail");
                fail++;
                return;
            }

            var filterIds = view.GetFilters().ToList();
            if (filterIds.Count == 0)
            {
                Log($"No filters applied to '{view.Name}'. Run Auto Filters first.", "fail");
                fail++;
                return;
            }

            Progress(10, pass, fail, skip);

            // ── Build legend rows ─────────────────────────────────────────────
            var rows = new List<(string Disc, string Val, (int R, int G, int B)? Rgb)>();

            // Filter names are "{TRADEID}_{RULE_NAME}" (see MakeFilterName), so the old
            // " - " split always fell into "Other". Map each expected filter name back to
            // its owning trade label (group) and rule display name (legend value).
            var nameToTrade = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var nameToRule  = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var trade in AutoFiltersSettings.Instance.Trades ?? new List<FilterTradeConfig>())
                foreach (var rule in trade.Rules ?? new List<FilterRuleConfig>())
                {
                    string fn = AutoFiltersSettings.MakeFilterName(trade.Id, rule.Name);
                    nameToTrade[fn] = trade.Label;
                    nameToRule[fn]  = rule.Name;
                }

            foreach (var fid in filterIds)
            {
                var pfe = doc.GetElement(fid) as ParameterFilterElement;
                if (pfe == null) continue;

                var ogs = view.GetFilterOverrides(fid);
                (int R, int G, int B)? rgb = null;

                try
                {
                    Color c = ogs.SurfaceForegroundPatternColor;
                    if (c.IsValid && !(c.Red == 0 && c.Green == 0 && c.Blue == 0))
                        rgb = (c.Red, c.Green, c.Blue);
                }
                catch (Exception __lex) { LemoineLog.Swallowed("AutoFilters legend: read surface foreground colour", __lex); }

                if (rgb == null)
                {
                    try
                    {
                        Color c = ogs.ProjectionLineColor;
                        if (c.IsValid && !(c.Red == 0 && c.Green == 0 && c.Blue == 0))
                            rgb = (c.Red, c.Green, c.Blue);
                    }
                    catch (Exception __lex) { LemoineLog.Swallowed("AutoFilters legend: read projection line colour", __lex); }
                }

                string name = pfe.Name;
                string disc = nameToTrade.TryGetValue(name, out var lbl) && !string.IsNullOrEmpty(lbl)
                    ? lbl : "Other";
                string val  = nameToRule.TryGetValue(name, out var rn) && !string.IsNullOrEmpty(rn)
                    ? rn : name;

                rows.Add((disc, val, rgb));
            }

            if (rows.Count == 0)
            {
                Log("No usable filter entries on active view.", "fail");
                fail++;
                return;
            }

            var groups = rows
                .GroupBy(r => r.Disc)
                .Select(g => (g.Key, g.OrderBy(r => r.Val).ToList()))
                .OrderBy(g => g.Key)
                .ToList();

            Progress(25, pass, fail, skip);

            // ── Find an existing Legend view to duplicate ─────────────────────
            View? existingLegend = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Views)
                .Cast<View>()
                .FirstOrDefault(v => v.ViewType == ViewType.Legend);

            if (existingLegend == null)
            {
                Log("No Legend view found in project. Create one in Revit (View → New Legend) then re-run.", "fail");
                fail++;
                return;
            }

            // ── Collect required project elements ─────────────────────────────
            ElementId solidFillId = ElementId.InvalidElementId;
            foreach (FillPatternElement fp in
                new FilteredElementCollector(doc)
                .OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>())
            {
                if (fp.GetFillPattern().IsSolidFill) { solidFillId = fp.Id; break; }
            }

            FilledRegionType? baseFRT = new FilteredElementCollector(doc)
                .OfClass(typeof(FilledRegionType))
                .Cast<FilledRegionType>().FirstOrDefault();

            ElementId textTypeId = new FilteredElementCollector(doc)
                .OfClass(typeof(TextNoteType)).FirstElementId();

            if (baseFRT == null || textTypeId == ElementId.InvalidElementId)
            {
                Log("Missing required project element (FilledRegionType or TextNoteType).", "fail");
                fail++;
                return;
            }

            // ── Choose unique legend view name ────────────────────────────────
            var allLegendNames = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Views).Cast<View>()
                .Where(v => v.ViewType == ViewType.Legend)
                .Select(v => v.Name).ToHashSet();

            string legendName = "Filter Color Legend";
            int n = 1;
            while (allLegendNames.Contains(legendName))
                legendName = $"Filter Color Legend ({++n})";

            Progress(40, pass, fail, skip);

            // ── Build swatch type map ─────────────────────────────────────────
            var needed = rows.Where(r => r.Rgb.HasValue).Select(r => r.Rgb.Value).ToHashSet();

            var frtByName = new FilteredElementCollector(doc)
                .OfClass(typeof(FilledRegionType)).Cast<FilledRegionType>()
                .ToDictionary(frt => SafeName(frt) ?? string.Empty, frt => frt.Id);

            var log = new List<string>();

            using (var tx = new Transaction(doc, "Auto Filters — Create Legend"))
            {
                var fho = tx.GetFailureHandlingOptions();
                fho.SetClearAfterRollback(true);
                fho.SetDelayedMiniWarnings(true);
                tx.SetFailureHandlingOptions(fho);
                tx.Start();

                // Create/reuse one FilledRegionType per unique color
                var colorTypeMap = new Dictionary<(int, int, int), ElementId>();
                foreach (var rgb in needed)
                {
                    string tname = $"LegendSwatch_{rgb.R}_{rgb.G}_{rgb.B}";
                    if (frtByName.TryGetValue(tname, out ElementId existing))
                    {
                        colorTypeMap[rgb] = existing;
                        continue;
                    }
                    try
                    {
                        FilledRegionType? newFRT = baseFRT.Duplicate(tname) as FilledRegionType;
                        if (newFRT == null) continue;
                        if (solidFillId != ElementId.InvalidElementId)
                            newFRT.ForegroundPatternId = solidFillId;
                        newFRT.ForegroundPatternColor = new Color(
                            (byte)rgb.R, (byte)rgb.G, (byte)rgb.B);
                        newFRT.BackgroundPatternId = ElementId.InvalidElementId;
                        try { newFRT.LineWeight = 1; } catch (Exception __lex) { LemoineLog.Swallowed("AutoFilters legend: set filled-region line weight", __lex); }
                        colorTypeMap[rgb] = newFRT.Id;
                        frtByName[tname]  = newFRT.Id;
                    }
                    catch (Exception ex)
                    {
                        log.Add($"Swatch type failed: {ex.Message}");
                    }
                }

                Progress(60, pass, fail, skip);

                // Duplicate the existing legend view
                ElementId newLegendId = existingLegend.Duplicate(ViewDuplicateOption.Duplicate);
                View? dv = doc.GetElement(newLegendId) as View;
                if (dv == null)
                {
                    Log("Failed to duplicate the template legend view.", "fail");
                    fail++;
                    tx.Commit();
                    return;
                }
                dv.Name = legendName;

                var opts = new TextNoteOptions { TypeId = textTypeId };
                try { opts.HorizontalAlignment = HorizontalTextAlignment.Left; } catch (Exception __lex) { LemoineLog.Swallowed("AutoFilters legend: set text-note alignment", __lex); }

                double cy = 0.0;
                int totalRows = rows.Count;
                int rowsDone  = 0;

                foreach (var (disc, entries) in groups)
                {
                    TextNote.Create(doc, dv.Id, new XYZ(0, cy, 0), LabelWidth,
                        disc.ToUpperInvariant(), opts);
                    cy -= RowHeight + HdrPad;

                    foreach (var (_, val, rgb) in entries)
                    {
                        if (rgb.HasValue && colorTypeMap.TryGetValue(rgb.Value, out ElementId ftId))
                        {
                            double x0 = 0, y0 = cy - SwatchNudge;
                            double x1 = SwatchW, y1 = cy - SwatchNudge - SwatchH;
                            try
                            {
                                var loop = new CurveLoop();
                                loop.Append(Line.CreateBound(new XYZ(x0,y0,0), new XYZ(x1,y0,0)));
                                loop.Append(Line.CreateBound(new XYZ(x1,y0,0), new XYZ(x1,y1,0)));
                                loop.Append(Line.CreateBound(new XYZ(x1,y1,0), new XYZ(x0,y1,0)));
                                loop.Append(Line.CreateBound(new XYZ(x0,y1,0), new XYZ(x0,y0,0)));
                                FilledRegion.Create(doc, ftId, dv.Id,
                                    new List<CurveLoop> { loop });
                            }
                            catch (Exception ex)
                            {
                                log.Add($"Swatch '{val}': {ex.Message}");
                            }
                        }

                        TextNote.Create(doc, dv.Id,
                            new XYZ(SwatchW + LabelGap, cy, 0), LabelWidth, val, opts);

                        cy -= RowHeight;
                        rowsDone++;
                        Progress(60 + (int)(rowsDone * 35.0 / totalRows),
                            pass, fail, skip);
                    }

                    cy -= GroupGap;
                }

                tx.Commit();
                pass++;
            }

            foreach (var l in log) Log(l, "info");
            Log($"Created legend view '{legendName}'  ({groups.Count} groups, {rows.Count} rows).", "pass");
        }

        // ── Helpers ─────────────────────────────────────────────────────────────
        private static string? SafeName(Element el)
        {
            try { return el.Name; } catch (Exception __lex) { LemoineLog.Swallowed("AutoFilters legend: read element name", __lex); }
            try
            {
                var p = el.get_Parameter(BuiltInParameter.SYMBOL_NAME_PARAM);
                if (p != null) return p.AsString();
            }
            catch (Exception __lex) { LemoineLog.Swallowed("AutoFilters legend: read symbol name parameter", __lex); }
            return null;
        }

        // ── Callback wrappers ────────────────────────────────────────────────────
        private void Log(string text, string status) => PushLog?.Invoke(text, status);
        private void Progress(int pct, int p, int f, int s) => OnProgress?.Invoke(pct, p, f, s);
        private void Complete(int p, int f, int s) => OnComplete?.Invoke(p, f, s);
    }
}
