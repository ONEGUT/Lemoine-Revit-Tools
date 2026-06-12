using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Tools.AutoFilters;
using LemoineTools.Lemoine;

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
        // Spacing is now driven by LegendLayout (paper inches) + the per-legend gap settings,
        // so the preview and this output share one layout model. No fixed feet constants.

        // ── Callbacks ───────────────────────────────────────────────────────
        public Action<string, string>?    PushLog         { get; set; }
        public Action<int, int, int, int>? OnProgress     { get; set; }
        public Action<int, int, int>?      OnComplete     { get; set; }
        /// <summary>Fired after a successful Create (not Update) with the new view's ElementId.</summary>
        public Action<ElementId>?          OnLegendCreated { get; set; }

        // Layout and rows to render — set by the caller before raising the event.
        public LegendLayoutConfig?    Layout { get; set; }
        public List<LegendRowConfig>? Rows   { get; set; }

        // Null → fall back to the first legend view found in the project.
        public ElementId? TemplateLegendId { get; set; }

        // Null → fall back to matching by Layout.Title name.
        public ElementId? TargetLegendId { get; set; }

        /// <summary>
        /// False (default) → duplicate a template legend and create a new view.
        /// True            → update the view specified by TargetLegendId.
        /// </summary>
        public bool UpdateMode { get; set; }

        // Per-role TextNoteType element IDs. Null → fall back to first in document.
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
            catch (Exception ex) { LemoineLog.Error("LegendCreator: run aborted", ex); Log($"Error: {ex.Message}", "fail"); fail++; }
            finally
            {
                // Session-long static handler — drop the run's payload.
                Layout = null;
                Rows   = null;
            }
            Progress(100, pass, fail, skip);
            Complete(pass, fail, skip);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Core legend creation
        // ─────────────────────────────────────────────────────────────────────
        private void CreateLegend(Document doc, ref int pass, ref int fail, ref int skip)
        {
            var layout = this.Layout ?? new LegendLayoutConfig();
            var rows   = this.Rows   ?? new List<LegendRowConfig>();

            // Model-space conversions are computed INSIDE the transaction from the view's
            // realized Scale (see below), so a scale the view refuses can never skew the
            // text-vs-swatch proportions away from what the preview showed.
            int requestedScale = layout.ViewScale > 0 ? layout.ViewScale : 48;

            // ── Find template legend ───────────────────────────────────────────
            // Resolved in BOTH modes: create duplicates it, and update falls back to it
            // when the bound target view no longer exists (deleted, or another project).
            View? templateLegend = null;
            if (TemplateLegendId != null && TemplateLegendId != ElementId.InvalidElementId)
                templateLegend = doc.GetElement(TemplateLegendId) as View;
            if (templateLegend?.ViewType != ViewType.Legend)
                templateLegend = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Views).Cast<View>()
                    .FirstOrDefault(v => v.ViewType == ViewType.Legend);
            if (!UpdateMode && templateLegend == null)
            {
                Log("No Legend view found in project. Create one first via View → New Legend.", "fail");
                fail++;
                return;
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
            catch (Exception __lex) { LemoineLog.Swallowed("LegendCreator: load AutoFilters rule map", __lex); }

            // ── Collect needed (colorHex, fill) pairs → display name for FRT ──
            // First block with a given (color, fill) pair wins the name slot.
            var neededFrts = new Dictionary<(string ColorHex, string Fill), string>();
            foreach (var row in rows)
                foreach (var grp in row.Groups ?? new List<LegendGroupConfig>())
                    foreach (var blk in grp.Blocks ?? new List<LegendBlockConfig>())
                    {
                        if (!blk.Visible) continue;
                        var rgb = ResolveColor(blk, ruleMap);
                        if (!rgb.HasValue) continue;
                        var key = (ToHex(rgb.Value), blk.Fill ?? "solid");
                        if (neededFrts.ContainsKey(key)) continue;
                        string displayName =
                            (!string.IsNullOrEmpty(blk.SourceRuleId)
                                && ruleMap.TryGetValue(blk.SourceRuleId, out var r)
                                && !string.IsNullOrEmpty(r.Name))
                            ? r.Name
                            : (!string.IsNullOrEmpty(blk.Name) ? blk.Name : "Custom");
                        neededFrts[key] = displayName;
                    }

            // ── Generate unique legend view name ───────────────────────────────
            // Computed in both modes: create uses it directly, and an update whose
            // target view is gone falls back to creating a fresh view with it.
            string baseTitle  = string.IsNullOrWhiteSpace(layout.Title) ? "Legend" : layout.Title.Trim();
            string legendName = baseTitle;
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
                foreach (var kvp in neededFrts)
                {
                    string colorHex   = kvp.Key.ColorHex;
                    string fill       = kvp.Key.Fill;
                    string filterName = kvp.Value;

                    var rgb = TryParseHex(colorHex);
                    if (!rgb.HasValue) continue;

                    ElementId patId = FillPatternId(fill, solidFillId, hatchFillId, dotsFillId);
                    string tname   = $"LegendCreator_{SanitizeName(filterName)}_{colorHex.TrimStart('#')}_{fill}";

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
                        try { newFRT.LineWeight = 1; } catch (Exception __lex) { LemoineLog.Swallowed("LegendCreator: set filled-region line weight", __lex); }
                        frtMap[(colorHex, fill)] = newFRT.Id;
                        frtByKey[tname]          = newFRT.Id;
                    }
                    catch (Exception ex) { logMsgs.Add($"Swatch type: {ex.Message}"); }
                }

                Progress(60, pass, fail, skip);

                // Removes previous filled regions / text notes so a redraw starts clean.
                void ClearLegendContents(View v)
                {
                    // Match FilledRegion by CLASS — matching its category (OST_FilledRegion)
                    // missed the regions, which is why "Update Legend" stacked new colour
                    // squares on top of the old ones instead of replacing them.
                    var ids = new FilteredElementCollector(doc, v.Id)
                        .WherePasses(new LogicalOrFilter(
                            new ElementClassFilter(typeof(FilledRegion)),
                            new ElementClassFilter(typeof(TextNote))))
                        .ToElementIds().ToList();
                    foreach (var id in ids)
                        try { doc.Delete(id); } catch (Exception __lex) { LemoineLog.Swallowed("LegendCreator: clear legend content", __lex); }
                }

                // ── Find/create the target legend view ───────────────────────
                View? dv = null;
                bool updating = UpdateMode;
                if (updating)
                {
                    if (TargetLegendId != null && TargetLegendId != ElementId.InvalidElementId)
                        dv = doc.GetElement(TargetLegendId) as View;
                    else
                    {
                        var legends = new FilteredElementCollector(doc)
                            .OfCategory(BuiltInCategory.OST_Views).Cast<View>()
                            .Where(v => v.ViewType == ViewType.Legend)
                            .ToList();
                        // Revit auto-suffixes a duplicated legend with " (n)"; match the exact title
                        // first, then fall back to a "<title> (n)" variant so an update still finds it.
                        dv = legends.FirstOrDefault(v => v.Name == baseTitle)
                          ?? legends.FirstOrDefault(v =>
                                 v.Name.StartsWith(baseTitle + " (", StringComparison.Ordinal) &&
                                 v.Name.EndsWith(")", StringComparison.Ordinal));
                    }
                    if (dv == null || dv.ViewType != ViewType.Legend)
                    {
                        // The bound view was deleted, or this is a different project. Don't
                        // dead-end the run — fall through to creating a fresh legend, which
                        // also rebinds the entry via OnLegendCreated.
                        Log("Bound legend view not found in this project — creating a new legend instead.", "info");
                        updating = false;
                        dv       = null;
                    }
                    else
                    {
                        ClearLegendContents(dv);
                    }
                }

                bool createdNew = false;
                if (!updating)
                {
                    if (templateLegend == null)
                    {
                        Log("No Legend view found in project. Create one first via View → New Legend.", "fail");
                        fail++;
                        tx.Commit();
                        return;
                    }
                    ElementId newLegendId = templateLegend.Duplicate(ViewDuplicateOption.Duplicate);
                    dv = doc.GetElement(newLegendId) as View;
                    if (dv == null)
                    {
                        Log("Failed to duplicate the template legend view.", "fail");
                        fail++;
                        tx.Commit();
                        return;
                    }
                    dv.Name = legendName;
                    createdNew = true;
                    // Clear any content carried over from the template legend before drawing.
                    ClearLegendContents(dv);
                }

                // Both branches either assigned dv or returned — this guard makes that
                // provable (and defends the invariant if the flow above ever changes).
                if (dv == null)
                {
                    Log("No legend view resolved.", "fail");
                    fail++;
                    tx.Commit();
                    return;
                }

                // ── Realized view scale drives ALL model-space sizing ─────────
                // Request the configured scale, then read back what the view actually
                // carries. Sizing from the realized value keeps text-vs-swatch paper
                // proportions identical to the preview even when the set is refused.
                try { dv.Scale = requestedScale; }
                catch (Exception __lex) { LemoineLog.Swallowed("LegendCreator: set legend view scale", __lex); }
                double scale = dv.Scale > 0 ? dv.Scale : requestedScale;
                if ((int)scale != requestedScale)
                    Log($"View scale is 1:{(int)scale} (requested 1:{requestedScale}) — legend sized for the actual scale.", "info");

                // Paper-inch → model-foot: feet = paper_inches × scale ÷ 12
                double swatchW  = LegendLayout.InchesToFeet(layout.SwatchW, scale);
                double swatchH  = LegendLayout.InchesToFeet(layout.SwatchH, scale);
                double gapFt    = LegendLayout.InchesToFeet(layout.SwatchLabelGap, scale);
                double colGapFt = LegendLayout.InchesToFeet(layout.ColGap, scale);
                double rowGapFt = LegendLayout.InchesToFeet(layout.RowGap, scale);

                // ── Resolve per-role TextNoteType IDs (validated against THIS doc) ──
                ElementId titleTid  = ResolveTypeId(doc, TitleTypeId,       textTypeId);
                ElementId subTid    = ResolveTypeId(doc, SubtitleTypeId,    textTypeId);
                ElementId headerTid = ResolveTypeId(doc, GroupHeaderTypeId, textTypeId);
                ElementId labelTid  = ResolveTypeId(doc, LabelTypeId,       textTypeId);

                // Model-space font heights from each TextNoteType's registered size.
                // TEXT_SIZE is stored in Revit internal units (feet, paper-space).
                // Multiply by view scale to get model-space height.
                double titleFontH  = ModelFontH(doc, titleTid,  scale, layout.FontPt);
                double subFontH    = ModelFontH(doc, subTid,    scale, layout.FontPt);
                double headerFontH = ModelFontH(doc, headerTid, scale, layout.FontPt);
                double labelFontH  = ModelFontH(doc, labelTid,  scale, layout.FontPt);

                // All vertical/column math goes through LegendLayout (paper inches → feet) so
                // the WPF preview, which calls the same formulas, matches this output exactly.
                double entryHIn = LegendLayout.EntryHeightIn(layout.SwatchH, labelFontH * 12.0 / scale);
                double entryH   = LegendLayout.InchesToFeet(entryHIn, scale);
                // hdrPad: group header band top → first block CENTRE.
                double hdrPad = LegendLayout.InchesToFeet(
                    LegendLayout.HeaderAdvanceIn(headerFontH * 12.0 / scale, entryHIn), scale);

                // Estimated label width in model feet, via the shared paper-inch estimate.
                double LabelWidthFt(string text, double fontH)
                {
                    double capIn = scale > 0 ? fontH * 12.0 / scale : 0;
                    return LegendLayout.InchesToFeet(LegendLayout.LabelWidthIn(text, capIn), scale);
                }

                // PlaceNote: single-line (no width arg) so a long label never wraps and
                // overlaps the next entry — column spacing comes from the measured width.
                // VerticalAlignment is pinned to MIDDLE and every origin below is a band
                // CENTRE: top-vs-baseline anchor ambiguity (which mis-stacked swatches
                // against labels) is eliminated rather than compensated for.
                int textFails = 0;
                void PlaceNote(ElementId viewId, XYZ centerOrigin, string text, ElementId typeId)
                {
                    if (string.IsNullOrEmpty(text)) return;
                    var o = new TextNoteOptions { TypeId = typeId };
                    try { o.HorizontalAlignment = HorizontalTextAlignment.Left; } catch (Exception __lex) { LemoineLog.Swallowed("LegendCreator: set text-note alignment", __lex); }
                    try { o.VerticalAlignment = VerticalTextAlignment.Middle; } catch (Exception __lex) { LemoineLog.Swallowed("LegendCreator: set text-note vertical alignment", __lex); }
                    try   { TextNote.Create(doc, viewId, centerOrigin, text, o); }
                    catch (Exception ex) { logMsgs.Add($"TextNote '{text}': {ex.Message}"); textFails++; }
                }

                double cy = 0.0;
                double titlePadFt = LegendLayout.InchesToFeet(LegendLayout.TitlePadIn, scale);
                double subPadFt   = LegendLayout.InchesToFeet(LegendLayout.SubPadIn,   scale);

                // ── Title / Subtitle above first row ──────────────────────────
                // cy tracks the TOP of the current band; notes are placed at the band's
                // vertical centre (Middle-aligned).
                if (!string.IsNullOrWhiteSpace(layout.Title))
                {
                    PlaceNote(dv.Id, new XYZ(0, cy - titleFontH / 2.0, 0), layout.Title.Trim(), titleTid);
                    cy -= titleFontH + titlePadFt;
                }
                if (!string.IsNullOrWhiteSpace(layout.Subtitle))
                {
                    PlaceNote(dv.Id, new XYZ(0, cy - subFontH / 2.0, 0), layout.Subtitle.Trim(), subTid);
                    cy -= subFontH + subPadFt;
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
                        // Group header: cy is the band top; the Middle-aligned note goes
                        // at the band centre.
                        string header = string.IsNullOrWhiteSpace(grp.Title)
                            ? "—" : grp.Title.ToUpperInvariant();
                        PlaceNote(dv.Id, new XYZ(cx, cy - headerFontH / 2.0, 0), header, headerTid);

                        // First block center is below the header by hdrPad.
                        double blockY    = cy - hdrPad;
                        int    visCount  = 0;
                        double maxLabelW = 0;

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
                                catch (Exception ex) { logMsgs.Add($"Swatch '{blk.Name}': {ex.Message}"); fail++; }
                            }
                            else if (rgb.HasValue)
                            {
                                // The colour resolved but its FilledRegionType wasn't built
                                // (failure already in logMsgs) — say which block lost its
                                // swatch instead of dropping it silently.
                                logMsgs.Add($"Swatch '{blk.Name}': no swatch type for {hex}/{fill} — drawn without a swatch.");
                            }

                            // Label: Middle-aligned at blockY — the swatch is also centred
                            // at blockY, so they align exactly. Width drives column stride.
                            string label = string.IsNullOrEmpty(blk.Name) ? blk.Id : blk.Name;
                            PlaceNote(dv.Id,
                                new XYZ(cx + swatchW + gapFt, blockY, 0),
                                label, labelTid);
                            double lw = LabelWidthFt(label, labelFontH);
                            if (lw > maxLabelW) maxLabelW = lw;

                            blockY -= entryH;
                            visCount++;
                            blocksDone++;
                            Progress(60 + (int)(blocksDone * 35.0 / Math.Max(1, totalBlocks)),
                                pass, fail, skip);
                        }

                        double groupDepth = visCount * entryH + hdrPad;
                        if (groupDepth > rowMaxDepth) rowMaxDepth = groupDepth;

                        // Column stride from this group's actual content: swatch + gap + widest
                        // label (or the header if wider), then the inter-column gap.
                        double entryW  = swatchW + gapFt + maxLabelW;
                        double headerW = LabelWidthFt(header, headerFontH);
                        cx += Math.Max(entryW, headerW) + colGapFt;
                    }

                    cy = rowStartY - rowMaxDepth - rowGapFt;
                }

                tx.Commit();
                // Deliverable = legend blocks/swatches drawn, not a hardcoded 1 for the view.
                pass += blocksDone;
                fail += textFails; // text notes that failed to create (details in logMsgs)

                // Notify the caller whenever a NEW view exists — including the update-mode
                // fallback, so the legend entry rebinds to the fresh view's id.
                if (createdNew) OnLegendCreated?.Invoke(dv.Id);

                foreach (var l in logMsgs) Log(l, "info");
                Log(createdNew
                    ? $"Created legend view '{legendName}' — {pass} block(s) drawn, {skip} hidden, {fail} failed."
                    : $"Updated legend view '{baseTitle}' — {pass} block(s) drawn, {skip} hidden, {fail} failed.",
                    fail > 0 ? "fail" : "pass");
            }
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
            try { return el.Name; } catch (Exception __lex) { LemoineLog.Swallowed("LegendCreator: read element name", __lex); }
            try { return el.get_Parameter(BuiltInParameter.SYMBOL_NAME_PARAM)?.AsString(); } catch (Exception __lex) { LemoineLog.Swallowed("LegendCreator: read symbol name parameter", __lex); }
            return null;
        }

        // Returns the candidate when it resolves to a real TextNoteType in THIS document,
        // else the fallback. The per-role ids persist in settings across sessions and
        // projects, so a stale id from another model must not reach TextNote.Create —
        // it throws, and that note (e.g. the title) silently never appears.
        private static ElementId ResolveTypeId(Document doc, ElementId? candidate, ElementId fallback)
            => (candidate != null && candidate != ElementId.InvalidElementId
                && doc.GetElement(candidate) is TextNoteType)
                ? candidate
                : fallback;

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
            catch (Exception __lex) { LemoineLog.Swallowed("LegendCreator: read text-note type size", __lex); }
            return fallbackPt / 72.0 / 12.0 * scale;
        }

        private static string SanitizeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "Custom";
            var sb = new System.Text.StringBuilder();
            foreach (char c in name)
            {
                if (char.IsLetterOrDigit(c)) sb.Append(c);
                else if (c == ' ' || c == '-') sb.Append('_');
            }
            string result = sb.ToString().Trim('_');
            if (result.Length > 40) result = result.Substring(0, 40);
            return result.Length == 0 ? "Custom" : result;
        }

        // Mirror to the durable diagnostic log — the Legend Creator window runs with
        // PushLog == null, so without this every failure reason was silently discarded
        // (the user saw "Completed with N error(s)" and never the why).
        private void Log(string t, string s)
        {
            PushLog?.Invoke(t, s);
            if (s == "fail") LemoineLog.Warn("LegendCreator", t);
            else             LemoineLog.Info("LegendCreator", t);
        }
        private void Progress(int p, int a, int f, int sk) => OnProgress?.Invoke(p, a, f, sk);
        private void Complete(int p, int f, int s) => OnComplete?.Invoke(p, f, s);
    }
}
