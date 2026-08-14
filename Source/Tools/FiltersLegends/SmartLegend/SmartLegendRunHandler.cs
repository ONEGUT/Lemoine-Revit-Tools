using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Framework;
using LemoineTools.Framework.Naming;
using LemoineTools.Tools.FiltersLegends.LegendCreator;

namespace LemoineTools.Tools.FiltersLegends.SmartLegend
{
    // =========================================================================
    // SmartLegendRunHandler — builds a legend per sheet from what is actually
    // coloured on that sheet.
    //
    // For each selected sheet: read its views, find every filter that genuinely
    // colours something visible there (SmartLegendScope), turn those into a
    // LegendEntry, draw it through the Legend Creator's own drawing engine, and
    // place it on the sheet.
    //
    // Nothing is invented. Filters are matched against the existing Auto Filters
    // rules first, so a colour already described by a rule reuses that rule's name,
    // colour and identity; only a filter with no matching rule is carried as a
    // standalone row, read from the view's own override. No filter and no rule is
    // ever created.
    // =========================================================================
    public sealed class SmartLegendRunHandler : IExternalEventHandler
    {
        // ── Callbacks ───────────────────────────────────────────────────────
        public Action<string, string>?     PushLog    { get; set; }
        public Action<int, int, int, int>? OnProgress { get; set; }
        public Action<int, int, int>?      OnComplete { get; set; }

        // ── Run payload ─────────────────────────────────────────────────────
        public List<ElementId>? SheetIds { get; set; }

        /// <summary>Legend view name pattern, e.g. "{SheetNumber} - CEILING LEGEND".</summary>
        public string NamePattern { get; set; } = "";
        /// <summary>Title drawn inside the legend. Same token vocabulary as the name.</summary>
        public string TitlePattern { get; set; } = "";

        public bool GroupByTrade    { get; set; } = true;
        public bool IncludeUnmatched { get; set; } = true;
        public bool PlaceOnSheet    { get; set; } = true;

        /// <summary>Max group columns before wrapping onto another row.</summary>
        public int GroupsPerRow { get; set; } = 4;
        public int ViewScale    { get; set; } = 48;
        public double SwatchW   { get; set; } = 0.25;
        public double SwatchH   { get; set; } = 0.13;

        /// <summary>Corner of the sheet's drawing area to place the legend in.</summary>
        public SmartLegendCorner Corner { get; set; } = SmartLegendCorner.TopRight;
        /// <summary>Gap from the title block edge, in sheet feet.</summary>
        public double Margin { get; set; } = 0.08;

        // Per-role TextNoteType ids, resolved by the ViewModel from names.
        public ElementId? TitleTypeId       { get; set; }
        public ElementId? GroupHeaderTypeId { get; set; }
        public ElementId? LabelTypeId       { get; set; }

        public string GetName() => "LemoineTools.Tools.FiltersLegends.SmartLegend.SmartLegendRunHandler";

        public void Execute(UIApplication app)
        {
            int pass = 0, fail = 0, skip = 0;
            try
            {
                var doc = app?.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    Log(AppStrings.T("filtersLegends.smartLegend.log.noActiveDoc"), "fail");
                    fail++;
                }
                else
                {
                    Run(doc, ref pass, ref fail, ref skip);
                }
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("SmartLegend: run aborted", ex);
                Log(AppStrings.T("filtersLegends.smartLegend.log.error", ex.Message), "fail");
                fail++;
            }
            finally
            {
                // Session-long static handler — drop the run's payload.
                SheetIds = null;
            }

            Progress(100, pass, fail, skip);
            Complete(pass, fail, skip);
        }

        // ─────────────────────────────────────────────────────────────────────
        private void Run(Document doc, ref int pass, ref int fail, ref int skip)
        {
            var sheetIds = SheetIds ?? new List<ElementId>();
            if (sheetIds.Count == 0)
            {
                Log(AppStrings.T("filtersLegends.smartLegend.log.noSheets"), "fail");
                fail++; return;
            }

            // One legend view per sheet, created/updated in its own pass so a sheet that
            // fails cannot take the others with it.
            var created = new List<(ElementId SheetId, ElementId LegendId)>();

            for (int i = 0; i < sheetIds.Count; i++)
            {
                if (RunState.CancelRequested)
                {
                    Log(AppStrings.T("common.log.stoppedByUser", i, sheetIds.Count), "warn");
                    break;   // falls through to placement of whatever was built
                }

                ElementId sheetId = sheetIds[i];
                if (!(doc.GetElement(sheetId) is ViewSheet sheet))
                {
                    Log(AppStrings.T("filtersLegends.smartLegend.log.sheetMissing", sheetId), "warn");
                    skip++; continue;
                }

                string sheetLabel = $"{sheet.SheetNumber} — {SafeName(sheet)}";

                // ── Scan ──────────────────────────────────────────────────
                var viewIds = SmartLegendScope.ViewsOnSheet(doc, sheetId);
                if (viewIds.Count == 0)
                {
                    // A sheet with no model views has nothing to key — say so rather than
                    // producing an empty legend.
                    Log(AppStrings.T("filtersLegends.smartLegend.log.noViewsOnSheet", sheetLabel), "warn");
                    skip++; continue;
                }

                var report = new SmartLegendUsage();
                var live = SmartLegendScope.CollectLiveFilters(
                    doc, viewIds,
                    AppStrings.T("filtersLegends.smartLegend.labels.otherGroup"),
                    IncludeUnmatched, report,
                    () => RunState.CancelRequested);

                foreach (string l in report.NonCascadingLinks.Distinct())
                    Log(AppStrings.T("filtersLegends.smartLegend.log.linkNotCascading", l), "warn");
                foreach (string n in report.ColorlessFilters.Distinct())
                    Log(AppStrings.T("filtersLegends.smartLegend.log.filterNoColour", n), "warn");
                foreach (string n in report.Unprovable.Distinct())
                    Log(AppStrings.T("filtersLegends.smartLegend.log.filterUnprovable", n), "warn");

                if (live.Count == 0)
                {
                    // Zero is a result, not silence.
                    Log(AppStrings.T("filtersLegends.smartLegend.log.noLiveFilters", sheetLabel, viewIds.Count), "warn");
                    skip++; continue;
                }

                int matched = live.Count(f => f.Matched);
                Log(AppStrings.T("filtersLegends.smartLegend.log.scanned",
                    sheetLabel, viewIds.Count, live.Count, matched, live.Count - matched), "info");

                // ── Build / reuse the legend entry ────────────────────────
                LegendEntry entry = ResolveEntry(doc, sheet);
                entry.Layout = BuildLayout(doc, sheet, entry.Layout);
                entry.Rows   = BuildRows(live);

                // ── Draw through the Legend Creator's own engine ──────────
                var links = LegendLinkSchema.ReadLinks(doc);
                bool isUpdate = links.TryGetValue(entry.Id, out long boundViewId)
                             && doc.GetElement(new ElementId(boundViewId)) is View bound
                             && bound.ViewType == ViewType.Legend;

                ElementId? drawnId = isUpdate ? new ElementId(boundViewId) : null;

                var drawer = new LegendCreatorEventHandler
                {
                    Layout            = entry.Layout,
                    Rows              = entry.Rows,
                    EntryId           = entry.Id,
                    ViewNameOverride  = ResolvePattern(doc, sheet, NamePattern,
                                            AppStrings.T("filtersLegends.smartLegend.labels.defaultName", sheet.SheetNumber ?? "")),
                    UpdateMode        = isUpdate,
                    TargetLegendId    = drawnId,
                    TitleTypeId       = TitleTypeId,
                    SubtitleTypeId    = TitleTypeId,
                    GroupHeaderTypeId = GroupHeaderTypeId,
                    LabelTypeId       = LabelTypeId,
                    PushLog           = (t, s) => Log(t, s),
                    OnLegendCreated   = id => drawnId = id,
                };

                int dPass = 0, dFail = 0, dSkip = 0;
                try
                {
                    drawer.CreateLegend(doc, ref dPass, ref dFail, ref dSkip);
                }
                catch (Exception ex)
                {
                    DiagnosticsLog.Error($"SmartLegend: draw legend for {sheetLabel}", ex);
                    Log(AppStrings.T("filtersLegends.smartLegend.log.drawFailed", sheetLabel, ex.Message), "fail");
                    fail++; continue;
                }
                finally
                {
                    // The drawer is a local instance, but it parks callbacks that close over
                    // this handler — drop them as soon as it has run.
                    drawer.PushLog         = null;
                    drawer.OnLegendCreated = null;
                }

                fail += dFail;
                if (drawnId == null || drawnId == ElementId.InvalidElementId)
                {
                    Log(AppStrings.T("filtersLegends.smartLegend.log.noLegendView", sheetLabel), "fail");
                    fail++; continue;
                }

                // Register the entry so the Legend Creator can open, edit and re-run it.
                StoreEntry(entry);
                created.Add((sheetId, drawnId));
                pass++;

                Log(isUpdate
                    ? AppStrings.T("filtersLegends.smartLegend.log.updated", sheetLabel, live.Count)
                    : AppStrings.T("filtersLegends.smartLegend.log.created", sheetLabel, live.Count),
                    "pass");

                Progress((int)((i + 1) * 80.0 / sheetIds.Count), pass, fail, skip);
            }

            // Persist the library once, not per sheet.
            try { LegendCreatorSettings.Instance.Save(); }
            catch (Exception ex) { DiagnosticsLog.Swallowed("SmartLegend: save legend library", ex); }

            if (PlaceOnSheet && created.Count > 0)
                PlaceLegends(doc, created, ref pass, ref fail, ref skip);

            Progress(95, pass, fail, skip);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Legend entry: reuse the one this tool made for the sheet last time
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The entry this tool already generated for the sheet, or a fresh one.
        ///
        /// Reuse matters more than it looks: the entry's Id keys the stamp on the Revit
        /// legend view (LegendLinkSchema), so minting a new id on every run would orphan
        /// the previous legend and leave a duplicate behind on each re-run.
        /// </summary>
        private LegendEntry ResolveEntry(Document doc, ViewSheet sheet)
        {
            string number = sheet.SheetNumber ?? "";
            try
            {
                var existing = LegendCreatorSettings.Instance.Legends
                    .FirstOrDefault(e => e != null
                        && !string.IsNullOrEmpty(e.SourceSheetNumber)
                        && string.Equals(e.SourceSheetNumber, number, StringComparison.OrdinalIgnoreCase));
                if (existing != null) return existing;
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed($"SmartLegend: look up existing entry for sheet {number}", ex);
            }

            return new LegendEntry
            {
                Id                = LegendIdGen.New("legend"),
                SourceSheetNumber = number,
                DisplayName       = AppStrings.T("filtersLegends.smartLegend.labels.entryName", number),
                PreviewVisible    = true,
            };
        }

        private void StoreEntry(LegendEntry entry)
        {
            try
            {
                var list = LegendCreatorSettings.Instance.Legends;
                if (!list.Any(e => e != null && string.Equals(e.Id, entry.Id, StringComparison.Ordinal)))
                    list.Add(entry);
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed($"SmartLegend: register legend entry {entry.Id}", ex);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Layout + rows
        // ─────────────────────────────────────────────────────────────────────

        private LegendLayoutConfig BuildLayout(Document doc, ViewSheet sheet, LegendLayoutConfig? existing)
        {
            var layout = existing ?? new LegendLayoutConfig();

            string title = ResolvePattern(doc, sheet, TitlePattern,
                AppStrings.T("filtersLegends.smartLegend.labels.defaultTitle"));

            layout.Title     = title;
            layout.ViewScale = ViewScale > 0 ? ViewScale : 48;
            layout.SwatchW   = SwatchW;
            layout.SwatchH   = SwatchH;
            layout.Normalize();
            return layout;
        }

        /// <summary>
        /// Turns the live filters into the legend's row/group/block tree: one group per
        /// trade (or one group overall), wrapped onto extra rows past GroupsPerRow.
        /// </summary>
        private List<LegendRowConfig> BuildRows(List<SmartLegendScope.LiveFilter> live)
        {
            var groups = new List<LegendGroupConfig>();

            // Grouped: one column per trade, "Other" carrying the unmatched filters.
            // Flat: a single column in scan order.
            IEnumerable<IGrouping<string, SmartLegendScope.LiveFilter>> buckets = GroupByTrade
                ? (IEnumerable<IGrouping<string, SmartLegendScope.LiveFilter>>)
                      live.GroupBy(f => f.GroupLabel, StringComparer.OrdinalIgnoreCase)
                : new List<IGrouping<string, SmartLegendScope.LiveFilter>>
                  {
                      new SingleGrouping(AppStrings.T("filtersLegends.smartLegend.labels.singleGroup"), live),
                  };

            foreach (var bucket in buckets)
            {
                var grp = new LegendGroupConfig
                {
                    Id    = LegendIdGen.New("group"),
                    Title = bucket.Key,
                };
                foreach (var f in bucket)
                {
                    grp.Blocks.Add(new LegendBlockConfig
                    {
                        Id            = LegendIdGen.New("block"),
                        Name          = f.DisplayName,
                        // An unmatched filter has no rule to read a colour from later, so its
                        // colour is pinned as an override — that is the only record of it.
                        SourceRuleId  = f.RuleId  ?? "",
                        SourceTradeId = f.TradeId ?? "",
                        Color         = f.ColorHex,
                        ColorOverride = !f.Matched,
                        Custom        = !f.Matched,
                        Fill          = "solid",
                        Kind          = "square",
                        Visible       = true,
                    });
                }
                if (grp.Blocks.Count > 0) groups.Add(grp);
            }

            int perRow = GroupsPerRow > 0 ? GroupsPerRow : 4;
            var rows = new List<LegendRowConfig>();
            for (int i = 0; i < groups.Count; i += perRow)
            {
                var row = new LegendRowConfig { Id = LegendIdGen.New("row") };
                row.Groups.AddRange(groups.Skip(i).Take(perRow));
                rows.Add(row);
            }
            return rows;
        }

        // IGrouping shim so the grouped and flat paths share one loop.
        private sealed class SingleGrouping : IGrouping<string, SmartLegendScope.LiveFilter>
        {
            private readonly List<SmartLegendScope.LiveFilter> _items;
            public SingleGrouping(string key, List<SmartLegendScope.LiveFilter> items)
            {
                Key = key; _items = items;
            }
            public string Key { get; }
            public IEnumerator<SmartLegendScope.LiveFilter> GetEnumerator() => _items.GetEnumerator();
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }

        private string ResolvePattern(Document doc, ViewSheet sheet, string pattern, string fallback)
        {
            if (string.IsNullOrWhiteSpace(pattern)) return fallback;
            try
            {
                var ctx = new TokenContext { Doc = doc, Target = sheet };
                string resolved = TokenResolver.Resolve(pattern, ctx,
                    w => DiagnosticsLog.Warn("SmartLegend", w));
                return TokenResolver.GuardDegenerate(resolved, ctx, fallback,
                    w => Log(AppStrings.T("filtersLegends.smartLegend.log.nameDegenerate", w), "warn"));
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed("SmartLegend: resolve name pattern", ex);
                return fallback;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Placement
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Places each legend on its sheet, tucked into the chosen corner of the title
        /// block's drawing area.
        ///
        /// Two phases with ONE regeneration between them: a viewport's real footprint
        /// (GetBoxOutline) is only valid after a regen following Viewport.Create, so the
        /// corner cannot be computed until every viewport exists. SetBoxCenter itself
        /// needs no further regen.
        /// </summary>
        private void PlaceLegends(
            Document doc, List<(ElementId SheetId, ElementId LegendId)> created,
            ref int pass, ref int fail, ref int skip)
        {
            var placed = new List<(Viewport Vp, ElementId SheetId)>();

            using (var tx = new Transaction(doc, "Smart Legend — Place Legends"))
            {
                var fho = tx.GetFailureHandlingOptions();
                fho.SetClearAfterRollback(true);
                fho.SetDelayedMiniWarnings(true);
                tx.SetFailureHandlingOptions(fho);
                tx.Start();

                foreach (var (sheetId, legendId) in created)
                {
                    if (!(doc.GetElement(sheetId) is ViewSheet sheet)) continue;
                    string sheetLabel = $"{sheet.SheetNumber} — {SafeName(sheet)}";

                    // Already on the sheet from an earlier run — leave it where the user put it.
                    if (AlreadyPlaced(doc, sheet, legendId))
                    {
                        Log(AppStrings.T("filtersLegends.smartLegend.log.alreadyPlaced", sheetLabel), "info");
                        continue;
                    }

                    try
                    {
                        if (!Viewport.CanAddViewToSheet(doc, sheetId, legendId))
                        {
                            Log(AppStrings.T("filtersLegends.smartLegend.log.cannotPlace", sheetLabel), "warn");
                            skip++; continue;
                        }
                        // Provisional point; corrected below once the real size is known.
                        var vp = Viewport.Create(doc, sheetId, legendId, XYZ.Zero);
                        if (vp == null)
                        {
                            Log(AppStrings.T("filtersLegends.smartLegend.log.placeFailed", sheetLabel, ""), "fail");
                            fail++; continue;
                        }
                        placed.Add((vp, sheetId));
                    }
                    catch (Exception ex)
                    {
                        DiagnosticsLog.Error($"SmartLegend: place legend on {sheetLabel}", ex);
                        Log(AppStrings.T("filtersLegends.smartLegend.log.placeFailed", sheetLabel, ex.Message), "fail");
                        fail++;
                    }
                }

                // One regen for the whole batch — per-viewport regeneration is the dominant
                // cost of any bulk sheet tool.
                if (placed.Count > 0)
                {
                    doc.Regenerate();

                    foreach (var (vp, sheetId) in placed)
                    {
                        try
                        {
                            var area = DrawingArea(doc, sheetId);
                            if (area == null)
                            {
                                // No title block means no drawing area to corner it against.
                                // The legend IS on the sheet, just at the origin — say so.
                                Log(AppStrings.T("filtersLegends.smartLegend.log.noTitleBlock",
                                    SheetLabel(doc, sheetId)), "warn");
                                continue;
                            }

                            Outline box = vp.GetBoxOutline();
                            double halfW = (box.MaximumPoint.X - box.MinimumPoint.X) / 2.0;
                            double halfH = (box.MaximumPoint.Y - box.MinimumPoint.Y) / 2.0;

                            var (min, max) = area.Value;
                            double cx = Corner == SmartLegendCorner.TopLeft || Corner == SmartLegendCorner.BottomLeft
                                ? min.X + Margin + halfW
                                : max.X - Margin - halfW;
                            double cy = Corner == SmartLegendCorner.TopLeft || Corner == SmartLegendCorner.TopRight
                                ? max.Y - Margin - halfH
                                : min.Y + Margin + halfH;

                            vp.SetBoxCenter(new XYZ(cx, cy, 0));
                        }
                        catch (Exception ex)
                        {
                            DiagnosticsLog.Swallowed($"SmartLegend: position legend viewport {vp.Id}", ex);
                            // The legend IS on the sheet — only its corner is off. Say so
                            // rather than reporting a clean placement.
                            Log(AppStrings.T("filtersLegends.smartLegend.log.positionFailed", vp.Id), "warn");
                        }
                    }
                }

                tx.Commit();
            }

            if (placed.Count > 0)
                Log(AppStrings.T("filtersLegends.smartLegend.log.placedSummary", placed.Count), "info");
        }

        private static bool AlreadyPlaced(Document doc, ViewSheet sheet, ElementId legendId)
        {
            try
            {
                foreach (ElementId vpId in sheet.GetAllViewports())
                    if (doc.GetElement(vpId) is Viewport vp && vp.ViewId == legendId) return true;
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed($"SmartLegend: check existing placement on sheet {sheet.Id}", ex);
            }
            return false;
        }

        /// <summary>
        /// The sheet's usable area — the placed title block's bounding box. Null when the
        /// sheet carries no title block, in which case the legend is left where Revit put it
        /// rather than positioned against a guessed rectangle.
        /// </summary>
        private static (XYZ Min, XYZ Max)? DrawingArea(Document doc, ElementId sheetId)
        {
            try
            {
                var tb = new FilteredElementCollector(doc, sheetId)
                    .OfCategory(BuiltInCategory.OST_TitleBlocks)
                    .WhereElementIsNotElementType()
                    .FirstElement();
                if (tb == null) return null;

                BoundingBoxXYZ? bb = tb.get_BoundingBox(doc.GetElement(sheetId) as View);
                if (bb == null) return null;
                return (bb.Min, bb.Max);
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed($"SmartLegend: read title block bounds on sheet {sheetId}", ex);
                return null;
            }
        }

        private static string SheetLabel(Document doc, ElementId sheetId)
            => doc.GetElement(sheetId) is ViewSheet s ? $"{s.SheetNumber} — {SafeName(s)}" : sheetId.ToString();

        private static string SafeName(Element el)
        {
            try { return el?.Name ?? ""; }
            catch (Exception ex)
            {
                DiagnosticsLog.Swallowed("SmartLegend: read element name", ex);
                return "";
            }
        }

        private void Log(string t, string s)
        {
            PushLog?.Invoke(t, s);
            if (s == "fail")      DiagnosticsLog.Warn("SmartLegend", t);
            else if (s == "warn") DiagnosticsLog.Warn("SmartLegend", t);
            else                  DiagnosticsLog.Info("SmartLegend", t);
        }
        private void Progress(int p, int a, int f, int sk) => OnProgress?.Invoke(p, a, f, sk);
        private void Complete(int p, int f, int s) => OnComplete?.Invoke(p, f, s);
    }

    /// <summary>Which corner of the sheet's drawing area a generated legend is tucked into.</summary>
    public enum SmartLegendCorner
    {
        TopRight = 0,
        TopLeft,
        BottomRight,
        BottomLeft,
    }
}
