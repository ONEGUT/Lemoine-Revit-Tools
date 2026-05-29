using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Tools.AutoFilters;
using LemoineTools.Lemoine;

namespace LemoineTools.Tools.Testing.CoordSet
{
    /// <summary>
    /// Creates a coordination set legend view.
    /// Copied from AutoFiltersLegendEventHandler and extended to accept
    /// LegendGroupConfig ordering and per-entry shape selection (Rect/Circle).
    /// Does NOT modify T01.
    /// </summary>
    public sealed class CoordSetLegendEventHandler : IExternalEventHandler
    {
        private const double RowHeight   = 0.75;
        private const double SwatchW     = 0.75;
        private const double SwatchH     = 0.30;
        private const double SwatchNudge = 0.06;
        private const double LabelGap    = 0.20;
        private const double GroupGap    = 1.00;
        private const double HdrPad      = 0.40;
        private const double LabelWidth  = 4.00;

        public Action<string, string>?     PushLog    { get; set; }
        public Action<int, int, int, int>? OnProgress { get; set; }
        public Action<int, int, int>?      OnComplete { get; set; }

        // Optional: when set, legend uses these groups + shapes instead of auto-discovery
        public List<LegendGroupConfig>?  LegendGroups { get; set; }
        public AutoFiltersSettings?      FilterSettings { get; set; }

        public string GetName() => "LemoineTools.Tools.Testing.CoordSetLegendEventHandler";

        public void Execute(UIApplication app)
        {
            var doc  = app.ActiveUIDocument.Document;
            var view = doc.ActiveView;
            int pass = 0, fail = 0, skip = 0;
            try { CreateLegend(doc, view, ref pass, ref fail, ref skip); }
            catch (Exception ex) { LemoineLog.Error("CoordSet legend: run aborted", ex); Log($"Error: {ex.Message}", "fail"); fail++; }
            Progress(100, pass, fail, skip);
            Complete(pass, fail, skip);
        }

        /// <summary>
        /// Callable directly from CoordSetRunHandler (same thread, no ExternalEvent needed).
        /// </summary>
        internal static void RunInline(Document doc, View view,
            List<LegendGroupConfig>? legendGroups, AutoFiltersSettings? filterSettings,
            Action<string, string> pushLog, ref int pass, ref int fail, ref int skip)
        {
            var h = new CoordSetLegendEventHandler
            {
                PushLog        = pushLog,
                LegendGroups   = legendGroups,
                FilterSettings = filterSettings,
            };
            h.CreateLegend(doc, view, ref pass, ref fail, ref skip);
        }

        private void CreateLegend(Document doc, View view,
            ref int pass, ref int fail, ref int skip)
        {
            // Build row list — either from LegendGroups config or from active view filters
            var rows = new List<(string Disc, string Val, (int R, int G, int B)? Rgb, string Shape)>();

            if (LegendGroups != null && LegendGroups.Count > 0 && FilterSettings != null)
            {
                // Config-driven: respect user-defined group order and per-entry shape
                var ruleMap = new Dictionary<string, FilterRuleConfig>(StringComparer.Ordinal);
                foreach (var trade in FilterSettings.Trades)
                    foreach (var rule in trade.Rules)
                        if (!ruleMap.ContainsKey(rule.Id)) ruleMap[rule.Id] = rule;

                foreach (var group in LegendGroups)
                {
                    foreach (var entry in group.Entries)
                    {
                        if (!ruleMap.TryGetValue(entry.FilterRuleId, out var rule)) continue;
                        (int R, int G, int B)? rgb = TryParseHex(rule.CutColor);
                        rows.Add((group.Label, rule.Name, rgb, entry.Shape ?? "Rect"));
                    }
                }
            }
            else
            {
                // Fallback: auto-discover from filters on active view
                if (!view.AreGraphicsOverridesAllowed())
                {
                    Log($"Active view '{view.Name}' does not support graphic overrides.", "fail");
                    fail++;
                    return;
                }

                var filterIds = view.GetFilters().ToList();
                if (filterIds.Count == 0)
                {
                    Log($"No filters on '{view.Name}'. Run Auto Filters first.", "fail");
                    fail++;
                    return;
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
                    catch (Exception __lex) { LemoineLog.Swallowed("CoordSet legend: read surface foreground colour", __lex); }
                    if (rgb == null)
                    {
                        try
                        {
                            Color c = ogs.ProjectionLineColor;
                            if (c.IsValid && !(c.Red == 0 && c.Green == 0 && c.Blue == 0))
                                rgb = (c.Red, c.Green, c.Blue);
                        }
                        catch (Exception __lex) { LemoineLog.Swallowed("CoordSet legend: read projection line colour", __lex); }
                    }

                    string name = pfe.Name;
                    int sep = name.IndexOf(" - ");
                    string disc = sep >= 0 ? name.Substring(0, sep).Trim() : "Other";
                    string val  = sep >= 0 ? name.Substring(sep + 3).Trim() : name;
                    rows.Add((disc, val, rgb, "Rect"));
                }
            }

            if (rows.Count == 0)
            {
                Log("No legend rows to create.", "fail");
                fail++;
                return;
            }

            // Group rows by Disc (preserving encounter order for config-driven)
            var groups = new List<(string Key, List<(string Disc, string Val, (int R, int G, int B)? Rgb, string Shape)> Entries)>();
            var seen   = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                if (!seen.TryGetValue(row.Disc, out int gi))
                {
                    gi = groups.Count;
                    seen[row.Disc] = gi;
                    groups.Add((row.Disc, new List<(string, string, (int, int, int)?, string)>()));
                }
                groups[gi].Entries.Add(row);
            }

            Progress(25, pass, fail, skip);

            View existingLegend = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Views).Cast<View>()
                .FirstOrDefault(v => v.ViewType == ViewType.Legend);

            if (existingLegend == null)
            {
                Log("No Legend view found in project. Create one first (View → New Legend).", "fail");
                fail++;
                return;
            }

            ElementId solidFillId = ElementId.InvalidElementId;
            foreach (FillPatternElement fp in
                new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>())
            {
                if (fp.GetFillPattern().IsSolidFill) { solidFillId = fp.Id; break; }
            }

            FilledRegionType baseFRT = new FilteredElementCollector(doc)
                .OfClass(typeof(FilledRegionType)).Cast<FilledRegionType>().FirstOrDefault();

            ElementId textTypeId = new FilteredElementCollector(doc)
                .OfClass(typeof(TextNoteType)).FirstElementId();

            if (baseFRT == null || textTypeId == ElementId.InvalidElementId)
            {
                Log("Missing required project element (FilledRegionType or TextNoteType).", "fail");
                fail++;
                return;
            }

            var allLegendNames = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Views).Cast<View>()
                .Where(v => v.ViewType == ViewType.Legend)
                .Select(v => v.Name).ToHashSet();

            string legendName = "Coordination Legend";
            int n = 1;
            while (allLegendNames.Contains(legendName))
                legendName = $"Coordination Legend ({++n})";

            Progress(40, pass, fail, skip);

            var needed = rows.Where(r => r.Rgb.HasValue)
                             .Select(r => r.Rgb!.Value).ToHashSet();

            var frtByName = new FilteredElementCollector(doc)
                .OfClass(typeof(FilledRegionType)).Cast<FilledRegionType>()
                .ToDictionary(frt => SafeName(frt) ?? string.Empty, frt => frt.Id);

            var log = new List<string>();

            using (var tx = new Transaction(doc, "Coord Set — Create Legend"))
            {
                var fho = tx.GetFailureHandlingOptions();
                fho.SetClearAfterRollback(true);
                fho.SetDelayedMiniWarnings(true);
                tx.SetFailureHandlingOptions(fho);
                tx.Start();

                var colorTypeMap = new Dictionary<(int, int, int), ElementId>();
                foreach (var rgb in needed)
                {
                    string tname = $"CoordLegend_{rgb.R}_{rgb.G}_{rgb.B}";
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
                        try { newFRT.LineWeight = 1; } catch (Exception __lex) { LemoineLog.Swallowed("CoordSet legend: set filled-region line weight", __lex); }
                        colorTypeMap[rgb] = newFRT.Id;
                        frtByName[tname]  = newFRT.Id;
                    }
                    catch (Exception ex) { log.Add($"Swatch type: {ex.Message}"); }
                }

                Progress(60, pass, fail, skip);

                ElementId newLegendId = existingLegend.Duplicate(ViewDuplicateOption.Duplicate);
                View? dv = doc.GetElement(newLegendId) as View;
                if (dv == null) { fail++; return; }
                dv.Name = legendName;

                var opts = new TextNoteOptions { TypeId = textTypeId };
                try { opts.HorizontalAlignment = HorizontalTextAlignment.Left; } catch (Exception __lex) { LemoineLog.Swallowed("CoordSet legend: set text-note alignment", __lex); }

                double cy = 0.0;
                int totalRows = rows.Count;
                int rowsDone  = 0;

                foreach (var (disc, entries) in groups)
                {
                    TextNote.Create(doc, dv.Id, new XYZ(0, cy, 0), LabelWidth,
                        disc.ToUpperInvariant(), opts);
                    cy -= RowHeight + HdrPad;

                    foreach (var (_, val, rgb, shape) in entries)
                    {
                        if (rgb.HasValue && colorTypeMap.TryGetValue(rgb.Value, out ElementId ftId))
                        {
                            double x0 = 0, y0 = cy - SwatchNudge;
                            double x1 = SwatchW, y1 = cy - SwatchNudge - SwatchH;
                            try
                            {
                                CurveLoop loop = shape == "Circle"
                                    ? MakeCircleLoop(
                                        (x0 + x1) / 2.0, (y0 + y1) / 2.0,
                                        Math.Min(SwatchW, SwatchH) / 2.0 * 0.9)
                                    : MakeRectLoop(x0, y0, x1, y1);
                                FilledRegion.Create(doc, ftId, dv.Id,
                                    new List<CurveLoop> { loop });
                            }
                            catch (Exception ex) { log.Add($"Swatch '{val}': {ex.Message}"); }
                        }

                        TextNote.Create(doc, dv.Id,
                            new XYZ(SwatchW + LabelGap, cy, 0), LabelWidth, val, opts);
                        cy -= RowHeight;
                        rowsDone++;
                        Progress(60 + (int)(rowsDone * 35.0 / totalRows), pass, fail, skip);
                    }

                    cy -= GroupGap;
                }

                tx.Commit();
                pass++;
            }

            foreach (var l in log) Log(l, "info");
            Log($"Created legend '{legendName}'  ({groups.Count} groups, {rows.Count} rows).", "pass");
        }

        private static CurveLoop MakeRectLoop(double x0, double y0, double x1, double y1)
        {
            var loop = new CurveLoop();
            loop.Append(Line.CreateBound(new XYZ(x0, y0, 0), new XYZ(x1, y0, 0)));
            loop.Append(Line.CreateBound(new XYZ(x1, y0, 0), new XYZ(x1, y1, 0)));
            loop.Append(Line.CreateBound(new XYZ(x1, y1, 0), new XYZ(x0, y1, 0)));
            loop.Append(Line.CreateBound(new XYZ(x0, y1, 0), new XYZ(x0, y0, 0)));
            return loop;
        }

        private static CurveLoop MakeCircleLoop(double cx, double cy, double r)
        {
            // 8-segment polyline approximating a circle
            int segs = 8;
            var loop = new CurveLoop();
            var pts  = new XYZ[segs];
            for (int i = 0; i < segs; i++)
            {
                double angle = 2.0 * Math.PI * i / segs;
                pts[i] = new XYZ(cx + r * Math.Cos(angle), cy + r * Math.Sin(angle), 0);
            }
            for (int i = 0; i < segs; i++)
                loop.Append(Line.CreateBound(pts[i], pts[(i + 1) % segs]));
            return loop;
        }

        private static (int R, int G, int B)? TryParseHex(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return null;
            hex = hex.TrimStart('#');
            if (hex.Length != 6) return null;
            try
            {
                int r = Convert.ToInt32(hex.Substring(0, 2), 16);
                int g = Convert.ToInt32(hex.Substring(2, 2), 16);
                int b = Convert.ToInt32(hex.Substring(4, 2), 16);
                return (r, g, b);
            }
            catch { return null; }
        }

        private static string? SafeName(Element el)
        {
            try { return el.Name; } catch (Exception __lex) { LemoineLog.Swallowed("CoordSet legend: read element name", __lex); }
            try
            {
                var p = el.get_Parameter(BuiltInParameter.SYMBOL_NAME_PARAM);
                if (p != null) return p.AsString();
            }
            catch (Exception __lex) { LemoineLog.Swallowed("CoordSet legend: read symbol name parameter", __lex); }
            return null;
        }

        private void Log(string text, string status) => PushLog?.Invoke(text, status);
        private void Progress(int pct, int p, int f, int s) => OnProgress?.Invoke(pct, p, f, s);
        private void Complete(int p, int f, int s) => OnComplete?.Invoke(p, f, s);
    }
}
