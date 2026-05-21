using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Helpers;
using RevitColor = Autodesk.Revit.DB.Color;

namespace LemoineTools.Tools.AutoFilters
{
    /// <summary>
    /// Applies selected ParameterFilterElements to one or more views,
    /// optionally setting color overrides from the MEP color map.
    /// </summary>
    public class ApplyFiltersToViewsEventHandler : IExternalEventHandler
    {
        // ── Set by ViewModel before Raise() ───────────────────────────────────
        public IList<string> SelectedFilterNames { get; set; } = new List<string>();
        public IList<string> SelectedViewNames   { get; set; } = new List<string>();
        public bool          OverwriteExisting   { get; set; } = false;
        public bool          ApplyColorOverrides { get; set; } = true;

        // ── Callbacks ─────────────────────────────────────────────────────────
        public Action<string, string>?     PushLog    { get; set; }
        public Action<int, int, int, int>? OnProgress { get; set; }
        public Action<int, int, int>?      OnComplete { get; set; }

        public string GetName() => "LemoineTools.ApplyFiltersToViewsEventHandler";

        public void Execute(UIApplication app)
        {
            var doc = app.ActiveUIDocument.Document;
            int pass = 0, fail = 0, skip = 0;

            try
            {
                Apply(doc, ref pass, ref fail, ref skip);
            }
            catch (Exception ex)
            {
                Log($"Fatal: {ex.Message}", "fail");
                fail++;
            }

            Progress(100, pass, fail, skip);
            Complete(pass, fail, skip);
        }

        // ── Core logic ────────────────────────────────────────────────────────
        private void Apply(Document doc, ref int pass, ref int fail, ref int skip)
        {
            if (SelectedFilterNames.Count == 0 || SelectedViewNames.Count == 0)
            {
                Log("No filters or views selected.", "fail");
                fail++; return;
            }

            // Build filter name → ElementId map
            var filterMap = new FilteredElementCollector(doc)
                .OfClass(typeof(ParameterFilterElement))
                .Cast<ParameterFilterElement>()
                .Where(f => SelectedFilterNames.Contains(f.Name))
                .ToDictionary(f => f.Name, f => f.Id);

            foreach (var m in SelectedFilterNames.Except(filterMap.Keys))
                Log($"Filter not found in project: '{m}'", "info");

            if (filterMap.Count == 0)
            {
                Log("None of the selected filters exist in this project.", "fail");
                fail++; return;
            }

            // Build view name → View map
            var selectedViewSet = new HashSet<string>(SelectedViewNames, StringComparer.Ordinal);
            var viewList = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => !v.IsTemplate && selectedViewSet.Contains(v.Name) &&
                            v.AreGraphicsOverridesAllowed())
                .ToList();

            if (viewList.Count == 0)
            {
                Log("None of the selected views support graphic overrides.", "fail");
                fail++; return;
            }

            Log($"{filterMap.Count} filter(s), {viewList.Count} view(s) — beginning apply…", "info");

            ElementId solidFillId = GetSolidFillId(doc);
            ElementId solidLineId = GetSolidLineId();

            int totalOps = filterMap.Count * viewList.Count;
            int done     = 0;

            using (var tx = new Transaction(doc, "Apply Filters to Views"))
            {
                ConfigureFailures(tx);
                tx.Start();

                foreach (var view in viewList)
                {
                    var existingIds = new HashSet<long>(
                        view.GetFilters().Select(id => id.Value));

                    foreach (var pair in filterMap)
                    {
                        string    filterName = pair.Key;
                        ElementId filterId   = pair.Value;
                        bool alreadyPresent  = existingIds.Contains(filterId.Value);

                        if (alreadyPresent && !OverwriteExisting)
                        {
                            skip++; done++;
                            Progress((int)(done * 90.0 / totalOps), pass, fail, skip);
                            continue;
                        }

                        try
                        {
                            if (!alreadyPresent)
                            {
                                view.AddFilter(filterId);
                                existingIds.Add(filterId.Value);
                            }
                            view.SetFilterVisibility(filterId, true);

                            if (ApplyColorOverrides)
                            {
                                RevitColor? color = MatchColor(filterName, out string? label);
                                if (color != null)
                                {
                                    var ogs = new OverrideGraphicSettings();
                                    if (solidFillId != ElementId.InvalidElementId)
                                    {
                                        ogs.SetSurfaceForegroundPatternId(solidFillId);
                                        ogs.SetSurfaceForegroundPatternColor(color);
                                        ogs.SetSurfaceForegroundPatternVisible(true);
                                    }
                                    var black = new RevitColor(0, 0, 0);
                                    ogs.SetProjectionLineColor(black);
                                    ogs.SetProjectionLineWeight(2);
                                    ogs.SetCutLineColor(black);
                                    ogs.SetCutLineWeight(2);
                                    if (solidLineId != ElementId.InvalidElementId)
                                    {
                                        ogs.SetProjectionLinePatternId(solidLineId);
                                        ogs.SetCutLinePatternId(solidLineId);
                                    }
                                    if (label == "Insulation") ogs.SetSurfaceTransparency(75);
                                    view.SetFilterOverrides(filterId, ogs);
                                }
                            }

                            pass++;
                        }
                        catch (Exception ex)
                        {
                            Log($"'{filterName}' on '{view.Name}': {ex.Message}", "fail");
                            fail++;
                        }

                        done++;
                        Progress((int)(done * 90.0 / totalOps), pass, fail, skip);
                    }
                }

                tx.Commit();
            }

            Log($"Complete — {pass} applied, {skip} skipped (already present), {fail} failed.", "pass");
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static RevitColor? MatchColor(string filterName, out string? label)
        {
            // V3: look up the rule in Trade→Rule by matching the filter name pattern.
            // Filter name pattern is {TradeId}_{RuleName.Replace(" ","_").ToUpper()}.
            // Falls back to MepColorMap for filters created before V2.
            foreach (var trade in AutoFiltersSettings.Instance.Trades ?? new System.Collections.Generic.List<FilterTradeConfig>())
            {
                foreach (var rule in trade.Rules)
                {
                    string expected = trade.Id + "_"
                        + rule.Name.Trim().Replace(" ", "_").ToUpperInvariant();
                    if (string.Equals(filterName, expected, StringComparison.OrdinalIgnoreCase))
                    {
                        label = rule.Name;
                        return HexToRevitColor(rule.CutColor);
                    }
                }
            }
            // Fallback for V1-era filter names
            return MepColorMap.Match(filterName, out label);
        }

        private static RevitColor? HexToRevitColor(string hex)
        {
            try
            {
                hex = (hex ?? "").TrimStart('#');
                if (hex.Length == 6 && int.TryParse(hex,
                    System.Globalization.NumberStyles.HexNumber, null, out int v))
                    return new RevitColor((byte)((v >> 16) & 0xFF), (byte)((v >> 8) & 0xFF), (byte)(v & 0xFF));
            }
            catch { }
            return null;
        }

        private static void ConfigureFailures(Transaction tx)
        {
            var opts = tx.GetFailureHandlingOptions();
            opts.SetClearAfterRollback(true);
            opts.SetDelayedMiniWarnings(true);
            tx.SetFailureHandlingOptions(opts);
        }

        private static ElementId GetSolidFillId(Document doc)
        {
            foreach (FillPatternElement fp in
                new FilteredElementCollector(doc)
                .OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>())
            {
                if (fp.GetFillPattern().IsSolidFill) return fp.Id;
            }
            return ElementId.InvalidElementId;
        }

        private static ElementId GetSolidLineId()
        {
            try { return LinePatternElement.GetSolidPatternId(); }
            catch { return ElementId.InvalidElementId; }
        }

        private void Log(string text, string status) => PushLog?.Invoke(text, status);
        private void Progress(int pct, int p, int f, int s) => OnProgress?.Invoke(pct, p, f, s);
        private void Complete(int p, int f, int s) => OnComplete?.Invoke(p, f, s);
    }
}
