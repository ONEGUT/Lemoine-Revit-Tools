using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Tools.AutoFilters;

namespace LemoineTools.Tools.Testing.LegendCreator
{
    /// <summary>
    /// Creates a legend view in the active Revit document from the current
    /// <see cref="LegendCreatorSettings"/> (Layout + Rows → Groups → Blocks).
    ///
    /// Multi-row layout: groups in each row are placed left-to-right; rows
    /// stack downward. All five shape kinds (square/circle/tri/line/dash) and
    /// three fill types (solid/hatch/dots) are supported.
    /// </summary>
    public sealed class LegendCreatorEventHandler : IExternalEventHandler
    {
        private const double GroupGap   = 0.60;   // horizontal gap between columns
        private const double RowGap     = 0.80;   // vertical gap between rows
        private const double LabelWidth = 4.00;   // proven-safe TextNote width ceiling (feet)

        // ── Callbacks ───────────────────────────────────────────────────────
        public Action<string, string>?    PushLog    { get; set; }
        public Action<int, int, int, int>? OnProgress { get; set; }
        public Action<int, int, int>?      OnComplete { get; set; }

        // Set by the step-flow launcher to pin which legend view is duplicated.
        // Null → fall back to the first legend view found in the project.
        public ElementId? TemplateLegendId { get; set; }

        // Set by LegendCreatorUpdateCommand after the user picks a legend view.
        // Null → fall back to matching by Layout.Title name.
        public ElementId? TargetLegendId { get; set; }

        /// <summary>
        /// False (default) → duplicate a template legend and create a new view.
        /// True            → update the view specified by TargetLegendId (or the first view
        ///                   whose name matches Layout.Title), clearing and redrawing in place.
        /// </summary>
        public bool UpdateMode { get; set; }

        // Per-role TextNoteType element IDs set by the step-flow launcher.
        // Null → fall back to the first TextNoteType found in the document.
        public ElementId? TitleTypeId       { get; set; }
        public ElementId? SubtitleTypeId    { get; set; }
        public ElementId? GroupHeaderTypeId { get; set; }
        public ElementId? LabelTypeId       { get; set; }

        public string GetName() => "LemoineTools.Tools.Testing.LegendCreator.LegendCreatorEventHandler";

        public void Execute(UIApplication app)
        {
            var uidoc = app.ActiveUIDocument;
            if (uidoc == null)
            {
                Log("No active document.", "fail");
                Complete(0, 1, 0);
                return;
            }
            int pass = 0, fail = 0, skip = 0;
            try { CreateLegend(uidoc.Document, ref pass, ref fail, ref skip); }
            catch (Exception ex) { Log($"Fatal: {ex.Message}", "fail"); fail++; }
            Progress(100, pass, fail, skip);
            Complete(pass, fail, skip);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Core legend creation
        // ─────────────────────────────────────────────────────────────────────
        private void CreateLegend(Document doc, ref int pass, ref int fail, ref int skip)
        {
            var settings = LegendCreatorSettings.Instance;
            var layout   = settings.Layout ?? new LegendLayoutConfig();
            var rows     = settings.Rows   ?? new List<LegendRowConfig>();

            // Paper-inch → model-foot: feet = paper_inches × scale ÷ 12
            double scale   = layout.ViewScale > 0 ? (double)layout.ViewScale : 48.0;
            double swatchW = layout.SwatchW / 12.0 * scale;
            double swatchH = layout.SwatchH / 12.0 * scale;
            double gapFt   = layout.Gap     / 12.0 * scale;
            double colW    = swatchW + gapFt + LabelWidth + GroupGap; // horizontal column stride

            // ── Find template legend (Create mode only) ───────────────────────
            View? existingLegend = null;
            if (!UpdateMode)
            {
                if (TemplateLegendId != null && TemplateLegendId != ElementId.InvalidElementId)
                    existingLegend = doc.GetElement(TemplateLegendId) as View;
                if (existingLegend?.ViewType != ViewType.Legend)
                    existingLegend = new FilteredElementCollector(doc)
                        .OfCategory(BuiltInCategory.OST_Views).Cast<View>()
                        .FirstOrDefault(v => v.ViewType == ViewType.Legend);
                if (existingLegend == null)
                {
                    Log("No Legend view found in project. Create one first via View → New Legend.", "fail");
                    fail++;
                    return;
                }
            }

            // ── Required project types ────────────────────────────────────────
            FilledRegionType? baseFRT = new FilteredElementCollector(doc)
                .OfClass(typeof(FilledRegionType)).Cast<FilledRegionType>().FirstOrDefault();

            ElementId textTypeId = new FilteredElementCollector(doc)
                .OfClass(typeof(TextNoteType)).FirstElementId();

            if (baseFRT == null || textTypeId == ElementId.InvalidElementId)
            {
                Log("Missing required project type (FilledRegionType or TextNoteType).", "fail");
                fail++;
                return;
            }

            // ── Fill pattern elements ─────────────────────────────────────────
            var fpElems = new FilteredElementCollector(doc)
                .OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>().ToList();

            ElementId solidFillId = fpElems
                .FirstOrDefault(fp => fp.GetFillPattern().IsSolidFill)?.Id
                ?? ElementId.InvalidElementId;

            ElementId hatchFillId = fpElems
                .FirstOrDefault(fp => !fp.GetFillPattern().IsSolidFill &&
                    (fp.Name ?? "").IndexOf("diagonal", StringComparison.OrdinalIgnoreCase) >= 0)?.Id
                ?? solidFillId;

            ElementId dotsFillId = fpElems
                .FirstOrDefault(fp => !fp.GetFillPattern().IsSolidFill &&
                    (fp.Name ?? "").IndexOf("dot", StringComparison.OrdinalIgnoreCase) >= 0)?.Id
                ?? solidFillId;

            // ── Rule map for color resolution ─────────────────────────────────
            var ruleMap = new Dictionary<string, FilterRuleConfig>(StringComparer.Ordinal);
            try
            {
                foreach (var trade in AutoFiltersSettings.Instance.Trades)
                    foreach (var rule in trade.Rules)
                        if (!ruleMap.ContainsKey(rule.Id)) ruleMap[rule.Id] = rule;
            }
            catch { }

            // ── Collect needed (colorHex, fill) pairs for FRT pre-creation ───
            var neededFrts = new HashSet<(string ColorHex, string Fill)>();
            foreach (var row in rows)
                foreach (var grp in row.Groups ?? new List<LegendGroupConfig>())
                    foreach (var blk in grp.Blocks ?? new List<LegendBlockConfig>())
                    {
                        if (!blk.Visible) continue;
                        var rgb = ResolveColor(blk, ruleMap);
                        if (rgb.HasValue)
                            neededFrts.Add((ToHex(rgb.Value), blk.Fill ?? "solid"));
                    }

            // ── Generate unique legend view name (Create mode only) ───────────
            string baseTitle  = string.IsNullOrWhiteSpace(layout.Title) ? "Legend" : layout.Title.Trim();
            string legendName = baseTitle;
            if (!UpdateMode)
            {
                var existingNames = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Views).Cast<View>()
                    .Where(v => v.ViewType == ViewType.Legend)
                    .Select(v => v.Name).ToHashSet();
                int nameIdx = 1;
                while (existingNames.Contains(legendName))
                    legendName = $"{baseTitle} ({++nameIdx})";
            }

            Progress(40, pass, fail, skip);

            // Pre-load existing FRT names to avoid re-duplicating
            var frtByKey = new FilteredElementCollector(doc)
                .OfClass(typeof(FilledRegionType)).Cast<FilledRegionType>()
                .GroupBy(frt => SafeName(frt) ?? string.Empty)
                .ToDictionary(g => g.Key, g => g.First().Id);

            var logMsgs = new List<string>();

            using (var tx = new Transaction(doc,
                UpdateMode ? "Legend Creator — Update Legend" : "Legend Creator — Create Legend"))
            {
                var fho = tx.GetFailureHandlingOptions();
                fho.SetClearAfterRollback(true);
                fho.SetDelayedMiniWarnings(true);
                tx.SetFailureHandlingOptions(fho);
                tx.Start();

                // ── Build FRT map: (colorHex, fill) → ElementId ──────────────
                var frtMap = new Dictionary<(string, string), ElementId>();
                foreach (var (colorHex, fill) in neededFrts)
                {
                    var rgb = TryParseHex(colorHex);
                    if (!rgb.HasValue) continue;

                    ElementId patId = FillPatternId(fill, solidFillId, hatchFillId, dotsFillId);
                    string tname   = $"LegendCreator_{rgb.Value.R}_{rgb.Value.G}_{rgb.Value.B}_{fill}";

                    if (frtByKey.TryGetValue(tname, out ElementId existing))
                    {
                        frtMap[(colorHex, fill)] = existing;
                        continue;
                    }
                    try
                    {
                        var newFRT = baseFRT.Duplicate(tname) as FilledRegionType;
                        if (newFRT == null) continue;
                        if (patId != ElementId.InvalidElementId) newFRT.ForegroundPatternId = patId;
                        newFRT.ForegroundPatternColor = new Color(
                            (byte)rgb.Value.R, (byte)rgb.Value.G, (byte)rgb.Value.B);
                        newFRT.BackgroundPatternId = ElementId.InvalidElementId;
                        try { newFRT.LineWeight = 1; } catch { }
                        frtMap[(colorHex, fill)] = newFRT.Id;
                        frtByKey[tname]          = newFRT.Id;
                    }
                    catch (Exception ex) { logMsgs.Add($"Swatch type: {ex.Message}"); }
                }

                Progress(60, pass, fail, skip);

                // ── Find/create the target legend view ───────────────────────
                View? dv;
                if (UpdateMode)
                {
                    if (TargetLegendId != null && TargetLegendId != ElementId.InvalidElementId)
                        dv = doc.GetElement(TargetLegendId) as View;
                    else
                        dv = new FilteredElementCollector(doc)
                            .OfCategory(BuiltInCategory.OST_Views).Cast<View>()
                            .FirstOrDefault(v => v.ViewType == ViewType.Legend && v.Name == baseTitle);
                    if (dv == null || dv.ViewType != ViewType.Legend)
                    {
                        Log("Target legend view not found. Use 'Create Legend' to create a new one.", "fail");
                        fail++;
                        tx.Commit();
                        return;
                    }
                    var toDelete = new FilteredElementCollector(doc, dv.Id)
                        .WherePasses(new LogicalOrFilter(
                            new ElementCategoryFilter(BuiltInCategory.OST_FilledRegion),
                            new ElementClassFilter(typeof(TextNote))))
                        .ToElementIds().ToList();
                    foreach (var id in toDelete)
                        try { doc.Delete(id); } catch { }
                    try { dv.Scale = layout.ViewScale > 0 ? layout.ViewScale : 48; } catch { }
                }
                else
                {
                    ElementId newLegendId = existingLegend!.Duplicate(ViewDuplicateOption.Duplicate);
                    dv = doc.GetElement(newLegendId) as View;
                    if (dv == null) { fail++; tx.Commit(); return; }
                    dv.Name = legendName;
                    try { dv.Scale = layout.ViewScale > 0 ? layout.ViewScale : 48; } catch { }
                    // Clear any content carried over from the template legend before drawing.
                    var templateElems = new FilteredElementCollector(doc, dv.Id)
                        .WherePasses(new LogicalOrFilter(
                            new ElementCategoryFilter(BuiltInCategory.OST_FilledRegion),
                            new ElementClassFilter(typeof(TextNote))))
                        .ToElementIds().ToList();
                    foreach (var id in templateElems) try { doc.Delete(id); } catch { }
                }

                // ── Resolve per-role TextNoteType IDs ────────────────────────
                ElementId titleTid  = ResolveTypeId(TitleTypeId,       textTypeId);
                ElementId subTid    = ResolveTypeId(SubtitleTypeId,    textTypeId);
                ElementId headerTid = ResolveTypeId(GroupHeaderTypeId, textTypeId);
                ElementId labelTid  = ResolveTypeId(LabelTypeId,       textTypeId);

                // Model-space font heights from each TextNoteType's registered size.
                // TEXT_SIZE is stored in Revit internal units (feet, paper-space).
                // Multiply by view scale to get model-space height.
                double titleFontH  = ModelFontH(doc, titleTid,  scale, layout.FontPt);
                double subFontH    = ModelFontH(doc, subTid,    scale, layout.FontPt);
                double headerFontH = ModelFontH(doc, headerTid, scale, layout.FontPt);
                double labelFontH  = ModelFontH(doc, labelTid,  scale, layout.FontPt);

                // entryH: vertical step per block — tall enough for both swatch and label.
                double entryH = Math.Max(swatchH, labelFontH) + 0.05;
                // hdrPad: space from group header top to first block center.
                double hdrPad = headerFontH + 0.08;

                // NoteWidth: estimate bounding-box width from character count.
                // The aspect ratio 0.55 is conservative for most Revit fonts.
                // LabelWidth is the proven-safe upper bound (never causes API error).
                double NoteWidth(string text, double fontH)
                {
                    if (string.IsNullOrEmpty(text)) return LabelWidth;
                    double w = Math.Max(text.Length * fontH * 0.55, fontH * 2.0);
                    return Math.Min(w, LabelWidth);
                }

                // PlaceNote: create with computed width; fall back to LabelWidth on error.
                void PlaceNote(ElementId viewId, XYZ origin, string text, ElementId typeId, double fontH)
                {
                    if (string.IsNullOrEmpty(text)) return;
                    var o = new TextNoteOptions { TypeId = typeId };
                    try { o.HorizontalAlignment = HorizontalTextAlignment.Left; } catch { }
                    double w = NoteWidth(text, fontH);
                    try   { TextNote.Create(doc, viewId, origin, w,          text, o); return; }
                    catch { }
                    try   { TextNote.Create(doc, viewId, origin, LabelWidth, text, o); }
                    catch (Exception ex) { logMsgs.Add($"TextNote '{text}': {ex.Message}"); }
                }

                double cy = 0.0;

                // ── Title / Subtitle above first row ──────────────────────────
                // TextNote origin is the top-left corner; place so text sits above grid.
                if (!string.IsNullOrWhiteSpace(layout.Title))
                {
                    PlaceNote(dv.Id, new XYZ(0, cy, 0), layout.Title.Trim(), titleTid, titleFontH);
                    cy -= titleFontH + 0.08;
                }
                if (!string.IsNullOrWhiteSpace(layout.Subtitle))
                {
                    PlaceNote(dv.Id, new XYZ(0, cy, 0), layout.Subtitle.Trim(), subTid, subFontH);
                    cy -= subFontH + 0.12;
                }

                int totalBlocks = rows.Sum(r =>
                    r.Groups?.Sum(g => g.Blocks?.Count(b => b.Visible) ?? 0) ?? 0);
                int blocksDone = 0;

                // ── Row loop ──────────────────────────────────────────────────
                foreach (var row in rows)
                {
                    double rowStartY   = cy;
                    double rowMaxDepth = 0;
                    double cx          = 0.0;

                    foreach (var grp in row.Groups ?? new List<LegendGroupConfig>())
                    {
                        // Group header: origin at top-left (cy = top of header text).
                        string header = string.IsNullOrWhiteSpace(grp.Title)
                            ? "—" : grp.Title.ToUpperInvariant();
                        PlaceNote(dv.Id, new XYZ(cx, cy, 0), header, headerTid, headerFontH);

                        // First block center is below the header by hdrPad.
                        double blockY   = cy - hdrPad;
                        int    visCount = 0;

                        foreach (var blk in grp.Blocks ?? new List<LegendBlockConfig>())
                        {
                            if (!blk.Visible) { skip++; continue; }

                            var rgb     = ResolveColor(blk, ruleMap);
                            string hex  = rgb.HasValue ? ToHex(rgb.Value) : "#888888";
                            string fill = blk.Fill ?? "solid";

                            // Swatch: centered at blockY (y0=top, y1=bottom).
                            if (rgb.HasValue && frtMap.TryGetValue((hex, fill), out ElementId frtId))
                            {
                                double x0 = cx,           y0 = blockY + swatchH / 2;
                                double x1 = cx + swatchW, y1 = blockY - swatchH / 2;
                                try
                                {
                                    CurveLoop loop = MakeShapeLoop(blk.Kind ?? "square", x0, y0, x1, y1);
                                    FilledRegion.Create(doc, frtId, dv.Id, new List<CurveLoop> { loop });
                                }
                                catch (Exception ex) { logMsgs.Add($"Swatch '{blk.Name}': {ex.Message}"); }
                            }

                            // Label: origin shifted up so text is centered with the swatch.
                            // TextNote origin = top-left, so Y = blockY + labelFontH/2.
                            string label = string.IsNullOrEmpty(blk.Name) ? blk.Id : blk.Name;
                            PlaceNote(dv.Id,
                                new XYZ(cx + swatchW + gapFt, blockY + labelFontH / 2, 0),
                                label, labelTid, labelFontH);

                            blockY -= entryH;
                            visCount++;
                            blocksDone++;
                            Progress(60 + (int)(blocksDone * 35.0 / Math.Max(1, totalBlocks)),
                                pass, fail, skip);
                        }

                        double groupDepth = visCount * entryH + hdrPad;
                        if (groupDepth > rowMaxDepth) rowMaxDepth = groupDepth;
                        cx += colW;
                    }

                    cy = rowStartY - rowMaxDepth - RowGap;
                }

                tx.Commit();
                pass++;
            }

            foreach (var l in logMsgs) Log(l, "info");
            Log(UpdateMode
                ? $"Updated legend view '{baseTitle}'."
                : $"Created legend view '{legendName}'.", "pass");
        }

        // ── Shape helpers ─────────────────────────────────────────────────────

        private static CurveLoop MakeShapeLoop(string kind, double x0, double y0, double x1, double y1)
        {
            switch (kind)
            {
                case "circle": return MakeCircleLoop(x0, y0, x1, y1);
                case "tri":    return MakeTriLoop(x0, y0, x1, y1);
                case "line":   return MakeLineLoop(x0, y0, x1, y1);
                case "dash":   return MakeDashLoop(x0, y0, x1, y1);
                default:       return MakeRectLoop(x0, y0, x1, y1); // "square"
            }
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

        private static CurveLoop MakeCircleLoop(double x0, double y0, double x1, double y1)
        {
            double cx = (x0 + x1) / 2.0;
            double cy = (y0 + y1) / 2.0;
            double r  = Math.Min(x1 - x0, y0 - y1) / 2.0 * 0.9;
            int segs  = 8;
            var loop  = new CurveLoop();
            var pts   = new XYZ[segs];
            for (int i = 0; i < segs; i++)
            {
                double angle = 2.0 * Math.PI * i / segs;
                pts[i] = new XYZ(cx + r * Math.Cos(angle), cy + r * Math.Sin(angle), 0);
            }
            for (int i = 0; i < segs; i++)
                loop.Append(Line.CreateBound(pts[i], pts[(i + 1) % segs]));
            return loop;
        }

        private static CurveLoop MakeTriLoop(double x0, double y0, double x1, double y1)
        {
            double midX = (x0 + x1) / 2.0;
            var loop    = new CurveLoop();
            // Clockwise: bottom-left → apex → bottom-right
            loop.Append(Line.CreateBound(new XYZ(x0,  y1, 0), new XYZ(midX, y0, 0)));
            loop.Append(Line.CreateBound(new XYZ(midX, y0, 0), new XYZ(x1,  y1, 0)));
            loop.Append(Line.CreateBound(new XYZ(x1,  y1, 0), new XYZ(x0,  y1, 0)));
            return loop;
        }

        private static CurveLoop MakeLineLoop(double x0, double y0, double x1, double y1)
        {
            // Thin horizontal bar: full width, ~15% swatch height, vertically centered
            double midY  = (y0 + y1) / 2.0;
            double halfH = (y0 - y1) * 0.15 / 2.0;
            return MakeRectLoop(x0, midY + halfH, x1, midY - halfH);
        }

        private static CurveLoop MakeDashLoop(double x0, double y0, double x1, double y1)
        {
            // Centered short bar (60% width) — visually reads as a dash
            double w    = x1 - x0;
            double midX = (x0 + x1) / 2.0;
            double midY = (y0 + y1) / 2.0;
            double halfW = w * 0.60 / 2.0;
            double halfH = (y0 - y1) * 0.15 / 2.0;
            return MakeRectLoop(midX - halfW, midY + halfH, midX + halfW, midY - halfH);
        }

        // ── Color + fill helpers ──────────────────────────────────────────────

        private static (int R, int G, int B)? ResolveColor(
            LegendBlockConfig blk, Dictionary<string, FilterRuleConfig> ruleMap)
        {
            if (blk.ColorOverride)
                return TryParseHex(blk.Color);
            if (!string.IsNullOrEmpty(blk.SourceRuleId)
                && ruleMap.TryGetValue(blk.SourceRuleId, out var rule))
                return TryParseHex(rule.SurfColor);
            return TryParseHex(blk.Color);
        }

        private static ElementId FillPatternId(string fill, ElementId solid, ElementId hatch, ElementId dots)
        {
            switch (fill)
            {
                case "hatch": return hatch;
                case "dots":  return dots;
                default:      return solid;
            }
        }

        private static (int R, int G, int B)? TryParseHex(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return null;
            hex = hex.TrimStart('#');
            if (hex.Length != 6) return null;
            try
            {
                return (Convert.ToInt32(hex.Substring(0, 2), 16),
                        Convert.ToInt32(hex.Substring(2, 2), 16),
                        Convert.ToInt32(hex.Substring(4, 2), 16));
            }
            catch { return null; }
        }

        private static string ToHex((int R, int G, int B) rgb)
            => $"#{rgb.R:X2}{rgb.G:X2}{rgb.B:X2}";

        private static string? SafeName(Element el)
        {
            try { return el.Name; } catch { }
            try { return el.get_Parameter(BuiltInParameter.SYMBOL_NAME_PARAM)?.AsString(); } catch { }
            return null;
        }

        // Returns the candidate if it is a valid non-null ElementId, else the fallback.
        private static ElementId ResolveTypeId(ElementId? candidate, ElementId fallback)
            => (candidate != null && candidate != ElementId.InvalidElementId) ? candidate : fallback;

        // Returns the model-space text height for a TextNoteType (feet).
        // TEXT_SIZE is in Revit internal units (paper-space feet); multiply by view scale.
        private static double ModelFontH(Document doc, ElementId typeId, double scale, int fallbackPt)
        {
            try
            {
                var tnt = doc.GetElement(typeId) as TextNoteType;
                double h = tnt?.get_Parameter(BuiltInParameter.TEXT_SIZE)?.AsDouble() ?? 0;
                if (h > 0) return h * scale;
            }
            catch { }
            return fallbackPt / 72.0 / 12.0 * scale;
        }

        private void Log(string t, string s)      => PushLog?.Invoke(t, s);
        private void Progress(int p, int a, int f, int sk) => OnProgress?.Invoke(p, a, f, sk);
        private void Complete(int p, int f, int s) => OnComplete?.Invoke(p, f, s);
    }
}
