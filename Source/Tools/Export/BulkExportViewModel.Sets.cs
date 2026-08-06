using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using LemoineTools.Framework;
using LemoineTools.Framework.Controls;

using WpfVisibility = System.Windows.Visibility;

namespace LemoineTools.Tools.BulkExport
{
    /// <summary>
    /// The sets half of Bulk Export: the Step 1 target bar and row badges, and the whole of
    /// Step 2 (output granularity, set cards, ordering, overrides, auto-grouping, persistence).
    ///
    /// Kept tool-local in a partial file rather than a framework control — nothing else in the
    /// plugin groups items this way yet, and generalising ahead of a second caller would fix the
    /// API before it is understood. Methods shared with the main partial are `internal`, never
    /// `private` — a private member in one partial file is invisible to the other (CS0122).
    /// </summary>
    public partial class BulkExportViewModel
    {
        // ── Set state ─────────────────────────────────────────────────────────
        internal readonly List<ExportSet> _sets = new List<ExportSet>();

        /// <summary>Id of the set new checks are filed into; null = "All items (no set)".</summary>
        internal string? _targetSetId;

        internal PdfGranularity _granularity =
            ExportSetLayout.ParseGranularity(BulkExportSettings.Instance.PdfGranularity);

        /// <summary>Unsaved changes to the set layout — shown on the Step 2 header.</summary>
        internal bool _setsDirty;

        // Live handles so a change on one step repaints the other without a full rebuild.
        private BrowserTreePicker? _picker;
        // The rail's stable frame — its child is swapped to repaint without rebuilding Step 1.
        private Border?            _railHost;
        private TextBlock?         _dirtyLabel;
        // Two status lines, not one: Step 1's rail and Step 3's actions row are both built
        // eagerly, so a single field would be owned by whichever step was constructed last
        // and a Step 1 action would report into Step 3's hidden block.
        private TextBlock?         _railStatus;
        private TextBlock?         _setsStatus;

        // Which set cards are expanded — held on the ViewModel, not the visual tree, or every
        // navigation back to Step 2 collapses everything the user just opened.
        private readonly HashSet<string> _expandedSets = new HashSet<string>();

        // ══════════════════════════════════════════════════════════════════════
        //  Model helpers
        // ══════════════════════════════════════════════════════════════════════

        internal ExportSet? TargetSet()
            => _targetSetId == null ? null : _sets.FirstOrDefault(s => s.Id == _targetSetId);

        internal ExportSet? SetContaining(long id)
            => _sets.FirstOrDefault(s => s.Members.Any(m => m.IdValue == id));

        /// <summary>Selected items that are not in any named set — the implicit "Unassigned" group.</summary>
        internal List<long> UnassignedIds()
        {
            var assigned = new HashSet<long>(_sets.SelectMany(s => s.Members).Select(m => m.IdValue));
            return _selectedIds.Where(id => !assigned.Contains(id)).ToList();
        }

        internal ExportSetMember MakeMember(long id)
        {
            _idToName.TryGetValue(id, out var label);
            _browserRank.TryGetValue(id, out int rank);
            var el = _allSheets.FirstOrDefault(s => s.Id.Value == id) as Element
                     ?? _allViews.FirstOrDefault(v => v.Id.Value == id);
            return new ExportSetMember
            {
                IdValue     = id,
                UniqueId    = el?.UniqueId ?? "",
                Label       = label ?? id.ToString(),
                IsSheet     = el is ViewSheet,
                BrowserRank = rank,
            };
        }

        /// <summary>
        /// Files a newly-checked item into the active target set. Single-membership: an item moves
        /// between sets rather than joining several, so its badge always names exactly one set.
        /// </summary>
        internal void AssignToTarget(long id)
        {
            var target = TargetSet();
            if (target == null) return;                 // "no set" — it stays in Unassigned
            if (target.Members.Any(m => m.IdValue == id)) return;
            RemoveFromAllSets(id);
            target.Members.Add(MakeMember(id));
            SortMembers(target, "Browser");
            _setsDirty = true;
        }

        internal void RemoveFromAllSets(long id)
        {
            foreach (var s in _sets)
            {
                int before = s.Members.Count;
                s.Members.RemoveAll(m => m.IdValue == id);
                if (s.Members.Count != before) _setsDirty = true;
            }
        }

        /// <summary>Drops members that are no longer selected — a set can never hold an unchecked item.</summary>
        internal void PruneSetsToSelection()
        {
            var live = new HashSet<long>(_selectedIds);
            foreach (var s in _sets)
            {
                int before = s.Members.Count;
                s.Members.RemoveAll(m => !live.Contains(m.IdValue));
                if (s.Members.Count != before) _setsDirty = true;
            }
        }

        internal RowBadge? BadgeFor(long id)
        {
            var set = SetContaining(id);
            if (set == null) return null;
            return new RowBadge(set.Name, string.IsNullOrEmpty(set.AccentHex)
                ? ExportSet.AccentFor(_sets.IndexOf(set))
                : set.AccentHex);
        }

        internal ExportSet NewSet(string name)
        {
            var set = new ExportSet
            {
                Name      = name,
                AccentHex = ExportSet.AccentFor(_sets.Count),
            };
            _sets.Add(set);
            _setsDirty = true;
            return set;
        }

        internal static void SortMembers(ExportSet set, string mode)
        {
            switch (mode)
            {
                case "Browser":
                    set.Members = set.Members.OrderBy(m => m.BrowserRank).ToList();
                    break;
                case "SheetNo":
                    // Natural order — digit runs compare by value, so A.2 precedes A.17.
                    set.Members = set.Members
                        .OrderBy(m => m.Label, NaturalOrderComparer.OrdinalIgnoreCase).ToList();
                    break;
                case "Name":
                    set.Members = set.Members
                        .OrderBy(m => StripNumber(m.Label), NaturalOrderComparer.OrdinalIgnoreCase).ToList();
                    break;
                case "Reverse":
                    set.Members.Reverse();
                    break;
            }
        }

        // "A101 — Ground Floor" → "Ground Floor"; a view label has no number part and is unchanged.
        private static string StripNumber(string label)
        {
            int dash = label.IndexOf(" — ", StringComparison.Ordinal);
            return dash >= 0 ? label.Substring(dash + 3) : label;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Run payload / persistence
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// The sets this run exports: every enabled named set, plus Unassigned when it still holds
        /// something. Unassigned is never silently dropped and never silently shipped — Step 2 and
        /// the review step both warn about it once named sets exist.
        /// </summary>
        internal List<ExportSetSpec> BuildRunSetsFromModel()
        {
            var specs = _sets
                .Where(s => s.Enabled && s.Members.Count > 0)
                .Select(s => new ExportSetSpec
                {
                    Name              = s.Name,
                    MemberIds         = s.Members.Select(m => new ElementId(m.IdValue)).ToList(),
                    PatternOverride   = s.PatternOverride,
                    SubfolderOverride = s.SubfolderOverride,
                    PdfOverride       = s.PdfOverride,
                    DwgOverride       = s.DwgOverride,
                    NwcOverride       = s.NwcOverride,
                    IfcOverride       = s.IfcOverride,
                })
                .ToList();

            var loose = UnassignedIds();
            if (loose.Count > 0 && _unassignedEnabled)
                specs.Add(new ExportSetSpec
                {
                    Name      = _sets.Count == 0 ? "" : AppStrings.T("export.bulkExport.sets.unassigned"),
                    MemberIds = loose.Select(id => new ElementId(id)).ToList(),
                });

            return specs;
        }

        // Unassigned exports by default only while it IS the whole run. Once a named set exists,
        // a forgotten item would otherwise ship as a surprise "Unassigned.pdf" in the deliverable.
        internal bool _unassignedEnabled = true;

        internal ExportSetLayout BuildLayout() => new ExportSetLayout
        {
            GranularityValue = _granularity,
            Sets             = _sets.Select(s => s.Clone()).ToList(),
        };

        /// <summary>
        /// Restores a layout read from the document. Members are re-resolved against this
        /// session's ids and browser ranks; anything that no longer exists is dropped and
        /// reported, because a silently shorter set is indistinguishable from a broken load.
        /// </summary>
        internal void ApplyLayout(ExportSetLayout? layout, Action<string>? report = null)
        {
            if (layout == null) return;
            _sets.Clear();
            _granularity = layout.GranularityValue;

            int dropped = 0;
            foreach (var stored in layout.Sets)
            {
                var set = stored.Clone();
                var live = new List<ExportSetMember>();
                foreach (var m in set.Members)
                {
                    if (!_idToName.ContainsKey(m.IdValue)) { dropped++; continue; }
                    m.BrowserRank = _browserRank.TryGetValue(m.IdValue, out int r) ? r : int.MaxValue;
                    m.Label       = _idToName[m.IdValue];
                    live.Add(m);
                }
                set.Members = live;
                if (string.IsNullOrEmpty(set.AccentHex)) set.AccentHex = ExportSet.AccentFor(_sets.Count);
                _sets.Add(set);
            }

            // Loading a layout selects its members — otherwise the sets would exist but export
            // nothing, since PruneSetsToSelection strips anything unchecked.
            var ids = _sets.SelectMany(s => s.Members).Select(m => m.IdValue).Distinct();
            _selectedIds = OrderByBrowser(_selectedIds.Concat(ids).Distinct());

            _setsDirty = false;
            if (dropped > 0) report?.Invoke(AppStrings.T("export.bulkExport.sets.loadDropped", dropped));
        }

        internal void SaveSets()
        {
            var handler = App.BulkExportSetStoreHandler;
            var evt     = App.BulkExportSetStoreEvent;
            if (handler == null || evt == null) return;

            // The handler runs on Revit's main thread, so its callbacks must marshal back to this
            // window's dispatcher before touching any WPF element.
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            void OnUi(Action a)
            {
                if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
                dispatcher.BeginInvoke(a);
            }

            handler.Layout  = BuildLayout();
            handler.OnSaved = () => OnUi(() =>
            {
                _setsDirty = false;
                SetStatus(AppStrings.T("export.bulkExport.sets.saved", _sets.Count), isError: false);
                UpdateDirtyLabel();
            });
            handler.OnError = msg => OnUi(() => SetStatus(msg ?? "", isError: true));
            evt.Raise();
        }

        private void SetStatus(string text, bool isError)
        {
            Show(_railStatus);
            Show(_setsStatus);

            void Show(TextBlock? status)
            {
                if (status == null) return;
                status.Text = text;
                status.SetResourceReference(TextBlock.ForegroundProperty, isError ? "LemoineRed" : "LemoineTextDim");
                status.Visibility = WpfVisibility.Visible;
            }
        }

        private void UpdateDirtyLabel()
        {
            if (_dirtyLabel == null) return;
            _dirtyLabel.Text       = _setsDirty ? AppStrings.T("export.bulkExport.sets.unsaved") : "";
            _dirtyLabel.Visibility = _setsDirty ? WpfVisibility.Visible : WpfVisibility.Collapsed;
        }

        /// <summary>
        /// What this set produces on disk. With no naming step the rule is plain: a combined PDF
        /// is called what the set is called, and an individual file keeps its own sheet/view name.
        /// Shown on the card so the typed name is visibly the filename.
        /// </summary>
        internal string SetOutputPreview(ExportSet set)
        {
            if (_granularity == PdfGranularity.PerSheet)
            {
                string first = set.Members.Count > 0
                    ? SanitiseFilenamePreview(set.Members[0].Label) + ".pdf"
                    : AppStrings.T("export.bulkExport.sets.noMembers");
                return AppStrings.T("export.bulkExport.sets.previewPerSheet", set.Members.Count, first);
            }

            string name = SanitiseFilenamePreview(set.Name);
            if (!name.Any(char.IsLetterOrDigit)) name = AppStrings.T("export.bulkExport.sets.unnamedFile");

            return _granularity == PdfGranularity.SingleFile
                ? AppStrings.T("export.bulkExport.sets.previewSingleFile", name)
                : AppStrings.T("export.bulkExport.sets.previewPerSet", name);
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Auto-grouping
        // ══════════════════════════════════════════════════════════════════════

        private static readonly Regex SheetPrefix = new Regex(@"^([A-Za-z]+)", RegexOptions.Compiled);

        /// <summary>
        /// Groups the current selection into sets. Sheet-number prefix is the default because a
        /// discipline prefix (A-, S-, M-) survives a messier model than folder organisation does,
        /// and the users who most need sets are the ones whose browsers are least tidy.
        /// </summary>
        internal Dictionary<string, List<long>> ComputeAutoGroups(string mode)
        {
            var groups = new Dictionary<string, List<long>>(StringComparer.OrdinalIgnoreCase);
            foreach (long id in _selectedIds)
            {
                string key = mode == "Folder" ? BrowserFolderOf(id) : SheetPrefixOf(id);
                if (string.IsNullOrWhiteSpace(key)) key = AppStrings.T("export.bulkExport.sets.otherGroup");
                if (!groups.TryGetValue(key, out var list)) groups[key] = list = new List<long>();
                list.Add(id);
            }
            return groups;
        }

        private string SheetPrefixOf(long id)
        {
            if (!_idToName.TryGetValue(id, out var label)) return "";
            var m = SheetPrefix.Match(label.Trim());
            return m.Success ? m.Groups[1].Value.ToUpperInvariant() : "";
        }

        // Nearest enclosing folder title in the captured browser tree (skipping the root).
        private readonly Dictionary<long, string> _folderOf = new Dictionary<long, string>();
        private string BrowserFolderOf(long id)
        {
            if (_folderOf.Count == 0 && _browserTree != null)
            {
                void Walk(BrowserNode n, string folder, int depth)
                {
                    string here = (!n.IsLeaf && depth > 0) ? n.Title : folder;
                    if (n.Id.HasValue) _folderOf[n.Id.Value] = folder;
                    foreach (var c in n.Children) Walk(c, here, depth + 1);
                }
                foreach (var root in _browserTree.Roots) Walk(root, "", 0);
            }
            return _folderOf.TryGetValue(id, out var f) ? f : "";
        }

        /// <summary>
        /// Applies auto-grouping, merging groups below <paramref name="minSize"/> into "Other" so a
        /// large model cannot silently produce forty unusable cards.
        /// </summary>
        internal void ApplyAutoGroups(string mode, int minSize)
        {
            var groups = ComputeAutoGroups(mode);
            string otherKey = AppStrings.T("export.bulkExport.sets.otherGroup");

            var small = groups.Where(g => g.Value.Count < minSize && g.Key != otherKey).ToList();
            if (small.Count > 0)
            {
                if (!groups.TryGetValue(otherKey, out var other)) groups[otherKey] = other = new List<long>();
                foreach (var g in small) { other.AddRange(g.Value); groups.Remove(g.Key); }
            }

            _sets.Clear();
            foreach (var g in groups.OrderBy(g => g.Key, NaturalOrderComparer.OrdinalIgnoreCase))
            {
                var set = NewSet(g.Key);
                set.Members = g.Value.Select(MakeMember).OrderBy(m => m.BrowserRank).ToList();
            }
            _setsDirty = true;
        }
    }
}
