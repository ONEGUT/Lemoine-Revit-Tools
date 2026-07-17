using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LemoineTools.Tools.AutoFilters;
using LemoineTools.Tools.FiltersLegends.LegendCreator;

namespace LemoineTools.Framework.Web
{
    /// <summary>
    /// Revit-free view model for the web Legend Creator (HTML analogue of
    /// <see cref="LegendSettingsWindow"/>). Edits <see cref="LegendCreatorSettings.Instance"/>
    /// directly and saves after every mutation (the WPF builder's auto-save behaviour); the
    /// window owns the Create/Update ExternalEvent run. The palette lists the Auto Filters
    /// trades' rules so they can be dragged into groups.
    /// </summary>
    public sealed class WebLegendCreator
    {
        private readonly List<(long Id, string Name)> _textTypes;
        private string  _activeLegendId = "";
        private string  _paletteTradeId = "";   // "" = all trades
        private string  _status = "";

        private static string T(string key, params object[] args) =>
            AppStrings.T("testing.legendCreator.builder." + key, args);

        // Standard Revit imperial view scales (label -> 1:n denominator), mirroring the WPF
        // dropdown. A stored non-standard denominator falls back to 1/4" = 1'-0" (1:48).
        private static readonly (string Label, int Denom)[] ImperialScales =
        {
            ("12\" = 1'-0\"",      1),
            ("6\" = 1'-0\"",       2),
            ("3\" = 1'-0\"",       4),
            ("1 1/2\" = 1'-0\"",   8),
            ("1\" = 1'-0\"",      12),
            ("3/4\" = 1'-0\"",    16),
            ("1/2\" = 1'-0\"",    24),
            ("3/8\" = 1'-0\"",    32),
            ("1/4\" = 1'-0\"",    48),
            ("3/16\" = 1'-0\"",   64),
            ("1/8\" = 1'-0\"",    96),
            ("3/32\" = 1'-0\"",  128),
            ("1/16\" = 1'-0\"",  192),
            ("1/32\" = 1'-0\"",  384),
        };

        public WebLegendCreator(List<(long Id, string Name)> textTypes)
        {
            _textTypes = textTypes ?? new List<(long, string)>();
            var s = LegendCreatorSettings.Instance;
            if (s.Legends.Count == 0) s.Legends.Add(NewEntry());
            _activeLegendId = s.Legends[0].Id;
        }

        public void SetStatus(string status) => _status = status ?? "";

        public LegendEntry? ActiveEntry() =>
            LegendCreatorSettings.Instance.Legends.FirstOrDefault(l => l.Id == _activeLegendId);

        private static void Save() => LegendCreatorSettings.Instance.Save();

        private static LegendEntry NewEntry() => new LegendEntry
        {
            Id     = "legend_" + Guid.NewGuid().ToString("N").Substring(0, 8),
            Layout = new LegendLayoutConfig { Title = T("window.defaults.newLegendTitle") },
            Rows   = new List<LegendRowConfig>(),
        };

        // ── Mutations (window re-sends init after each) ───────────────────────
        public void SelectLegend(string id) { _activeLegendId = id; }

        public void AddLegend()
        {
            var e = NewEntry();
            LegendCreatorSettings.Instance.Legends.Add(e);
            _activeLegendId = e.Id;
            Save();
        }

        public void ApplyLegendEdit(string id, string title, string subtitle)
        {
            var e = LegendCreatorSettings.Instance.Legends.FirstOrDefault(l => l.Id == id);
            if (e == null) return;
            if (e.Layout == null) e.Layout = new LegendLayoutConfig();
            if (!string.IsNullOrWhiteSpace(title)) e.Layout.Title = title.Trim();
            e.Layout.Subtitle = subtitle ?? "";
            e.DisplayName = null;   // mirror the title again
            Save();
        }

        public void DuplicateLegend(string id)
        {
            var s = LegendCreatorSettings.Instance;
            var e = s.Legends.FirstOrDefault(l => l.Id == id);
            if (e == null) return;
            var copy = e.Clone();
            copy.Id = "legend_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            copy.RevitViewId = -1;   // the copy has no Revit view yet
            if (copy.Layout == null) copy.Layout = new LegendLayoutConfig();
            copy.Layout.Title += T("window.defaults.copySuffix");
            copy.DisplayName = null;
            s.Legends.Insert(s.Legends.IndexOf(e) + 1, copy);
            _activeLegendId = copy.Id;
            Save();
        }

        public void DeleteLegend(string id)
        {
            var s = LegendCreatorSettings.Instance;
            var e = s.Legends.FirstOrDefault(l => l.Id == id);
            if (e == null) return;
            s.Legends.Remove(e);
            if (s.Legends.Count == 0) s.Legends.Add(NewEntry());
            if (_activeLegendId == id) _activeLegendId = s.Legends[0].Id;
            Save();
        }

        public void AddGroup()
        {
            var e = ActiveEntry(); if (e == null) return;
            var g = new LegendGroupConfig
            {
                Id    = "g_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                Title = T("groupCard.placeholders.groupTitle"),
            };
            e.Rows.Add(new LegendRowConfig
            {
                Id     = "r_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                Groups = new List<LegendGroupConfig> { g },
            });
            Save();
        }

        private LegendGroupConfig? FindGroup(string groupId, out LegendRowConfig? row)
        {
            row = null;
            var e = ActiveEntry(); if (e == null) return null;
            foreach (var r in e.Rows)
            {
                var g = r.Groups.FirstOrDefault(x => x.Id == groupId);
                if (g != null) { row = r; return g; }
            }
            return null;
        }

        /// <summary>
        /// Move a group to a builder position expressed against the CURRENT (pre-move) layout:
        /// either into row <paramref name="rowIndex"/> at column <paramref name="colIndex"/>, or —
        /// when <paramref name="newRow"/> — into a brand-new row inserted at
        /// <paramref name="rowIndex"/> (0 = above the first row, Rows.Count = below the last).
        /// Indexes are adjusted here for the removal of the group's old slot, and a row left
        /// empty by the move is deleted (the WPF lane-grid contract).
        /// </summary>
        public void MoveGroup(string groupId, int rowIndex, int colIndex, bool newRow)
        {
            var e = ActiveEntry(); if (e == null) return;
            var g = FindGroup(groupId, out var srcRow);
            if (g == null || srcRow == null) return;

            int srcRowIdx = e.Rows.IndexOf(srcRow);
            int srcColIdx = srcRow.Groups.IndexOf(g);

            srcRow.Groups.Remove(g);
            bool removedRow = srcRow.Groups.Count == 0;
            if (removedRow) e.Rows.Remove(srcRow);

            if (newRow)
            {
                if (removedRow && rowIndex > srcRowIdx) rowIndex--;
                rowIndex = Math.Max(0, Math.Min(rowIndex, e.Rows.Count));
                e.Rows.Insert(rowIndex, new LegendRowConfig
                {
                    Id     = "r_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                    Groups = new List<LegendGroupConfig> { g },
                });
            }
            else
            {
                if (removedRow && rowIndex > srcRowIdx) rowIndex--;
                if (e.Rows.Count == 0 || rowIndex < 0)
                {
                    e.Rows.Add(new LegendRowConfig
                    {
                        Id     = "r_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                        Groups = new List<LegendGroupConfig> { g },
                    });
                }
                else
                {
                    rowIndex = Math.Min(rowIndex, e.Rows.Count - 1);
                    var row = e.Rows[rowIndex];
                    if (row == srcRow && colIndex > srcColIdx) colIndex--;
                    colIndex = Math.Max(0, Math.Min(colIndex, row.Groups.Count));
                    row.Groups.Insert(colIndex, g);
                }
            }
            Save();
        }

        public void DeleteGroup(string groupId)
        {
            var e = ActiveEntry(); if (e == null) return;
            var g = FindGroup(groupId, out var row);
            if (g == null || row == null) return;
            row.Groups.Remove(g);
            if (row.Groups.Count == 0) e.Rows.Remove(row);
            Save();
        }

        public void ToggleGroupCollapsed(string groupId, bool collapsed)
        {
            var g = FindGroup(groupId, out _); if (g == null) return;
            g.Collapsed = collapsed;
            Save();
        }

        public void RenameGroup(string groupId, string title)
        {
            var g = FindGroup(groupId, out _); if (g == null) return;
            if (!string.IsNullOrWhiteSpace(title)) { g.Title = title.Trim(); Save(); }
        }

        /// <summary>Show-all if any block is hidden, else hide-all (the WPF eye contract).</summary>
        public void ToggleGroupVisibility(string groupId)
        {
            var g = FindGroup(groupId, out _); if (g == null || g.Blocks.Count == 0) return;
            bool anyHidden = g.Blocks.Any(b => !b.Visible);
            foreach (var b in g.Blocks) b.Visible = anyHidden;
            Save();
        }

        public void AddCustomBlock(string groupId)
        {
            var g = FindGroup(groupId, out _); if (g == null) return;
            g.Blocks.Add(new LegendBlockConfig
            {
                Id     = "b_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                Name   = T("groupCard.defaults.newBlockName"),
                Color  = "#8c8c8c",
                Custom = true,
            });
            Save();
        }

        public void DeleteBlock(string groupId, string blockId)
        {
            var g = FindGroup(groupId, out _); if (g == null) return;
            g.Blocks.RemoveAll(b => b.Id == blockId);
            Save();
        }

        public void ToggleBlockVisibility(string groupId, string blockId)
        {
            var g = FindGroup(groupId, out _);
            var b = g?.Blocks.FirstOrDefault(x => x.Id == blockId);
            if (b == null) return;
            b.Visible = !b.Visible;
            Save();
        }

        public void RenameBlock(string groupId, string blockId, string name)
        {
            var g = FindGroup(groupId, out _);
            var b = g?.Blocks.FirstOrDefault(x => x.Id == blockId);
            if (b == null || string.IsNullOrWhiteSpace(name)) return;
            b.Name = name.Trim();
            b.NameOverride = !b.Custom;
            Save();
        }

        /// <summary>Drop a palette chip (key "tradeId|ruleId") into a group.</summary>
        public void DropFilter(string groupId, string key)
        {
            var g = FindGroup(groupId, out _); if (g == null) return;
            var parts = (key ?? "").Split('|');
            if (parts.Length != 2) return;
            var trade = AutoFiltersSettings.Instance.Trades.FirstOrDefault(t => t.Id == parts[0]);
            var rule  = trade?.Rules.FirstOrDefault(r => r.Id == parts[1]);
            if (trade == null || rule == null) return;

            g.Blocks.Add(new LegendBlockConfig
            {
                Id            = "b_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                Name          = rule.Name,
                SourceTradeId = trade.Id,
                SourceRuleId  = rule.Id,
                Color         = rule.SurfColor,
            });
            Save();
        }

        public void SetSizing(string field, object? value)
        {
            var e = ActiveEntry(); if (e == null) return;
            if (e.Layout == null) e.Layout = new LegendLayoutConfig();
            var l = e.Layout;
            switch (field)
            {
                case "scale":
                    string label = value?.ToString() ?? "";
                    var match = ImperialScales.FirstOrDefault(s => s.Label == label);
                    if (match.Denom > 0) l.ViewScale = match.Denom;
                    break;
                case "SwatchW":        l.SwatchW        = Clamp(ToDouble(value, l.SwatchW), 0.05, 3.0); break;
                case "SwatchH":        l.SwatchH        = Clamp(ToDouble(value, l.SwatchH), 0.02, 2.0); break;
                case "RowGap":         l.RowGap         = Clamp(ToDouble(value, l.RowGap), 0.0, 3.0); break;
                case "ColGap":         l.ColGap         = Clamp(ToDouble(value, l.ColGap), 0.0, 3.0); break;
                case "SwatchLabelGap": l.SwatchLabelGap = Clamp(ToDouble(value, l.SwatchLabelGap), 0.0, 1.0); break;
            }
            Save();
        }

        public void SetTextStyle(string role, string typeName)
        {
            var e = ActiveEntry(); if (e == null) return;
            long id = _textTypes.FirstOrDefault(t => t.Name == typeName).Id;
            if (id == 0) id = -1;
            switch (role)
            {
                case "Title":       e.TitleTypeId       = id; break;
                case "Subtitle":    e.SubtitleTypeId    = id; break;
                case "GroupHeader": e.GroupHeaderTypeId = id; break;
                case "Label":       e.LabelTypeId       = id; break;
            }
            Save();
        }

        public void SetPaletteTrade(string tradeId) => _paletteTradeId = tradeId ?? "";

        // ── Templates ─────────────────────────────────────────────────────────
        public List<string> TemplateNames() =>
            LegendCreatorSettings.Templates.List().Select(t => t.Name).ToList();

        public bool TemplateLoad(string name, out string? error)
        {
            error = null;
            var info = LegendCreatorSettings.Templates.List().FirstOrDefault(t => t.Name == name);
            if (info == null) { error = T("builder.templatesPopup.loadFailedMessage"); return false; }
            if (!LegendCreatorSettings.Templates.Load(info, out var data, out error) || data == null) return false;
            LegendCreatorSettings.Instance.Legends.Clear();
            LegendCreatorSettings.Instance.Legends.AddRange(data.Legends);
            if (LegendCreatorSettings.Instance.Legends.Count == 0)
                LegendCreatorSettings.Instance.Legends.Add(NewEntry());
            _activeLegendId = LegendCreatorSettings.Instance.Legends[0].Id;
            Save();
            return true;
        }

        public bool TemplateSave(string name, out string? error) =>
            LegendCreatorSettings.Templates.Save(name, LegendCreatorSettings.Instance, out error);

        public bool TemplateDelete(string name, out string? error)
        {
            error = null;
            var info = LegendCreatorSettings.Templates.List().FirstOrDefault(t => t.Name == name);
            return info != null && LegendCreatorSettings.Templates.Delete(info, out error);
        }

        public bool ImportFrom(string path)
        {
            var data = LegendCreatorSettings.TryLoad(path);
            if (data == null || data.Legends.Count == 0) return false;
            LegendCreatorSettings.Instance.Legends.Clear();
            LegendCreatorSettings.Instance.Legends.AddRange(data.Legends);
            _activeLegendId = LegendCreatorSettings.Instance.Legends[0].Id;
            Save();
            return true;
        }

        public bool ExportTo(string path)
        {
            try { LegendCreatorSettings.ExportTo(path, LegendCreatorSettings.Instance); return true; }
            catch (Exception ex) { DiagnosticsLog.Error("WebLegendCreator: export", ex); return false; }
        }

        // ── Payload ───────────────────────────────────────────────────────────
        public Dictionary<string, object?> BuildPayload()
        {
            var s = LegendCreatorSettings.Instance;
            var e = ActiveEntry();

            int entries = 0, groups = 0, hidden = 0, rowCount = 0;
            var rows = new List<object?>();
            if (e != null)
            {
                rowCount = e.Rows.Count;
                foreach (var r in e.Rows)
                {
                    var laneGroups = new List<object?>();
                    foreach (var g in r.Groups)
                    {
                        groups++;
                        var blocks = new List<object?>();
                        foreach (var b in g.Blocks)
                        {
                            entries++;
                            if (!b.Visible) hidden++;
                            blocks.Add(new Dictionary<string, object?>
                            {
                                ["id"] = b.Id, ["name"] = b.Name, ["color"] = b.Color,
                                ["visible"] = b.Visible, ["custom"] = b.Custom,
                            });
                        }
                        laneGroups.Add(new Dictionary<string, object?>
                        {
                            ["id"] = g.Id, ["title"] = g.Title, ["color"] = GroupColor(g),
                            ["collapsed"] = g.Collapsed, ["count"] = g.Blocks.Count, ["blocks"] = blocks,
                        });
                    }
                    rows.Add(new Dictionary<string, object?> { ["groups"] = laneGroups });
                }
            }

            var layout = e?.Layout ?? new LegendLayoutConfig();
            string scaleLabel = ImperialScales.FirstOrDefault(x => x.Denom == layout.ViewScale).Label
                                ?? ImperialScales.First(x => x.Denom == 48).Label;

            var typeNames = _textTypes.Select(t => t.Name).ToList();
            string NameFor(long id) =>
                (_textTypes.FirstOrDefault(t => t.Id == id).Name)
                ?? (typeNames.Count > 0 ? typeNames[0] : "");

            return new Dictionary<string, object?>
            {
                ["title"]       = T("window.titleBar.title"),
                ["labels"]      = Labels(),
                ["status"]      = _status,
                ["hasRevitView"] = e != null && e.RevitViewId != -1,
                ["legends"]     = s.Legends.Select(l => (object?)new Dictionary<string, object?>
                {
                    ["id"] = l.Id, ["name"] = l.GetDisplayName(), ["active"] = l.Id == _activeLegendId,
                    ["legendTitle"] = l.Layout?.Title ?? "", ["legendSubtitle"] = l.Layout?.Subtitle ?? "",
                }).ToList(),
                ["rows"]        = rows,
                ["statusLine"]  = string.Format(CultureInfo.InvariantCulture,
                    "{0} entries · {1} groups · {2} rows · {3} hidden", entries, groups, rowCount, hidden),
                ["sizing"]      = new Dictionary<string, object?>
                {
                    ["scale"] = scaleLabel,
                    ["scaleOptions"] = ImperialScales.Select(x => (object?)x.Label).ToList(),
                    ["swatchW"] = layout.SwatchW, ["swatchH"] = layout.SwatchH,
                    ["rowGap"] = layout.RowGap, ["colGap"] = layout.ColGap, ["swatchLabel"] = layout.SwatchLabelGap,
                },
                ["textStyles"]  = new Dictionary<string, object?>
                {
                    ["title"] = NameFor(e?.TitleTypeId ?? -1), ["subtitle"] = NameFor(e?.SubtitleTypeId ?? -1),
                    ["groupHeader"] = NameFor(e?.GroupHeaderTypeId ?? -1), ["label"] = NameFor(e?.LabelTypeId ?? -1),
                    ["options"] = typeNames.Cast<object?>().ToList(),
                },
                ["palette"]     = BuildPalette(),
                ["templates"]   = TemplateNames().Cast<object?>().ToList(),
            };
        }

        // The group header tint follows its source trade's colour; groups holding mixed or
        // custom entries fall back to the first block's colour (matches the WPF card look).
        private static string GroupColor(LegendGroupConfig g)
        {
            if (!string.IsNullOrEmpty(g.SourceTradeId))
            {
                var t = AutoFiltersSettings.Instance.Trades.FirstOrDefault(x => x.Id == g.SourceTradeId);
                if (t != null) return t.Color;
            }
            return g.Blocks.FirstOrDefault()?.Color ?? "#8c8c8c";
        }

        private Dictionary<string, object?> BuildPalette()
        {
            var trades = AutoFiltersSettings.Instance.Trades;
            var chips = new List<object?>();
            foreach (var t in trades)
            {
                if (_paletteTradeId.Length > 0 && t.Id != _paletteTradeId) continue;
                foreach (var r in t.Rules.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
                    chips.Add(new Dictionary<string, object?>
                    {
                        ["key"] = t.Id + "|" + r.Id, ["name"] = r.Name,
                        ["tradeLabel"] = t.Label, ["color"] = r.SurfColor,
                    });
            }
            var activeTrade = trades.FirstOrDefault(t => t.Id == _paletteTradeId);
            return new Dictionary<string, object?>
            {
                ["tradeLabel"] = activeTrade?.Label ?? T("palette.scope.allTradesDefault"),
                ["trades"] = trades.Select(t => (object?)new Dictionary<string, object?>
                { ["id"] = t.Id, ["label"] = t.Label, ["color"] = t.Color }).ToList(),
                ["chips"] = chips,
            };
        }

        private static double ToDouble(object? v, double dflt) =>
            v is double d ? d : v is long l ? l : v is int i ? i :
            double.TryParse(v?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var r) ? r : dflt;

        private static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;

        private Dictionary<string, object?> Labels() => new Dictionary<string, object?>
        {
            ["templates"]     = T("window.sidebar.templatesPill"),
            ["editLegendTip"] = T("window.sidebar.editTooltip"),
            ["addLegend"]     = T("window.sidebar.addLegend"),
            ["preview"]       = T("window.actions.previewButton"),
            ["hidePreview"]   = T("window.actions.hidePreviewButton"),
            ["createLegend"]  = T("window.actions.createLegend"),
            ["updateLegend"]  = T("window.actions.updateLegend"),
            ["addGroup"]      = T("builder.canvas.addNewGroup"),
            ["toggleGroupVisTip"] = T("groupCard.tooltips.showAll"),
            ["addEntryTip"]   = T("groupCard.tooltips.addCustomBlock"),
            ["deleteGroupTip"] = T("groupCard.tooltips.deleteGroup"),
            ["toggleEntryVisTip"] = T("blockRow.tooltips.hide"),
            ["deleteEntryTip"] = T("blockRow.tooltips.delete"),
            ["dropHint"]      = T("groupCard.emptyState.dropHint"),
            ["sizing"]        = T("window.sizing.header"),
            ["scale"]         = T("window.sizing.scaleLabel"),
            ["swatchW"]       = T("window.sizing.swatchW"),
            ["swatchH"]       = T("window.sizing.swatchH"),
            ["rowGap"]        = T("window.sizing.rowGap"),
            ["colGap"]        = T("window.sizing.colGap"),
            ["swatchLabel"]   = T("window.sizing.swatchLabelGap"),
            ["textStyles"]    = T("window.textStyles.header"),
            ["noTypes"]       = T("window.textStyles.noTypesFound"),
            ["title"]         = T("window.textStyles.title"),
            ["subtitle"]      = T("window.textStyles.subtitle"),
            ["groupHeader"]   = T("window.textStyles.groupHeader"),
            ["label"]         = T("window.textStyles.label"),
            ["palette"]       = T("palette.labels.header"),
            ["paletteAll"]    = T("palette.scope.allPill"),
            ["allTrades"]     = T("palette.scope.allTradesDefault"),
            ["filtersHint"]   = T("palette.labels.filtersHeader"),
            ["searchFilters"] = T("palette.labels.searchPlaceholder"),
            ["noMatches"]     = T("palette.emptyState.noMatches"),
            // Edit-legend popup
            ["editTitleLabel"]    = T("window.editPopup.titleLabel"),
            ["editSubtitleLabel"] = T("window.editPopup.subtitleLabel"),
            ["editSave"]          = T("window.editPopup.save"),
            ["editDuplicate"]     = T("window.editPopup.duplicate"),
            ["editDelete"]        = T("window.editPopup.deleteLegend"),
            ["cancel"]            = T("window.editPopup.cancel"),
            // Templates popup
            ["tmplSavedHeader"] = T("builder.templatesPopup.savedTemplatesHeader"),
            ["tmplNone"]        = T("builder.templatesPopup.noSavedTemplates"),
            ["tmplSaveAs"]      = T("builder.templatesPopup.saveCurrentAsTemplate"),
            ["tmplSave"]        = T("builder.templatesPopup.saveButton"),
            ["tmplFileHeader"]  = T("builder.templatesPopup.fileSectionHeader"),
            ["tmplImport"]      = T("builder.templatesPopup.importFromFile"),
            ["tmplExport"]      = T("builder.templatesPopup.exportToFile"),
            ["tmplDeleteTip"]   = T("builder.templatesPopup.deleteConfirm"),
            ["creating"]        = T("window.status.creating"),
            ["updating"]        = T("window.status.updating"),
        };
    }
}
