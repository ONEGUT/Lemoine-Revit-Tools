using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using LemoineTools.Lemoine;
using LemoineTools.Lemoine.Controls;
using NavisApp = Autodesk.Navisworks.Api.Application;
using NavisDoc = Autodesk.Navisworks.Api.Document;

namespace LemoineNavisworks.FloorSplit
{
    // =========================================================================
    // FloorSplitViewModel — split one federated model into per-floor NWDs.
    //
    // Step flow:
    //   S1  Levels          — discovered levels (name + editable elevation); check
    //                         the ones that bound floors. N levels → N−1 floors.
    //   S2  Floors & output — preview of the derived bands, straddle rule, output
    //                         folder, filename pattern, export options.
    //   S3  Run             — per floor: hide out-of-band items, save a viewpoint,
    //                         export an NWD with hidden geometry excluded, restore.
    //
    // Runs on the Navisworks main (STA) thread, so Run() calls the API directly —
    // no ExternalEvent (same pattern as HelloNavis / DiscoverSearchSets).
    // =========================================================================
    public class FloorSplitViewModel : ILemoineTool, IStepAware
    {
        public string Title    => "Floor Splitter";
        public string RunLabel => "Export Floor NWDs →";

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", "Levels",           required: true),
            new StepDefinition("S2", "Floors & output",  required: true),
            new StepDefinition("S3", "Run",              required: false),
        };

        public event EventHandler? ValidationChanged;
        private void Changed() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        // ── State ─────────────────────────────────────────────────────────────────
        private readonly List<LevelDef> _levels;
        private readonly string _unit;

        private StraddleRule _straddle       = StraddleRule.KeepOverlapping;
        private string       _outFolder      = "";
        private string       _pattern        = "{floor}";
        private bool         _embedXrefs     = true;
        private bool         _keepProps      = true;
        private bool         _saveViewpoints = true;

        private bool _floorsDirty;
        private Action<string>? _rebuild;

        // Control refs held for in-step rebuilds.
        private StackPanel? _levelHost;

        private const string StraddleKeep     = "Keep overlapping on both floors";
        private const string StraddleCentroid = "Assign each element to one floor (by centre)";

        public FloorSplitViewModel()
        {
            var doc = NavisApp.ActiveDocument;
            _unit   = NavisFloorSplit.UnitSuffix(doc);
            _levels = (doc == null || doc.IsClear)
                ? new List<LevelDef>()
                : NavisFloorSplit.DiscoverLevels(doc);
        }

        // ── IStepAware ──────────────────────────────────────────────────────────
        public void SetContentRefreshCallback(Action<string> rebuild) => _rebuild = rebuild;

        public void OnStepActivated(string stepId)
        {
            if (stepId == "S2" && _floorsDirty)
            {
                _floorsDirty = false;
                _rebuild?.Invoke("S2");
            }
            else if (stepId == "S3")
            {
                _rebuild?.Invoke("S3");
            }
        }

        // ── Step content ────────────────────────────────────────────────────────
        public FrameworkElement? GetStepContent(string stepId)
        {
            switch (stepId)
            {
                case "S1": return BuildLevelsStep();
                case "S2": return BuildOutputStep();
                case "S3": return BuildRunStep();
            }
            return null;
        }

        // ── S1: Levels ────────────────────────────────────────────────────────────
        private FrameworkElement BuildLevelsStep()
        {
            var doc = NavisApp.ActiveDocument;
            if (doc == null || doc.IsClear)
                return Hint("No model open. Open your federated NWF/NWD and reopen this tool.");

            var panel = new StackPanel();
            panel.Children.Add(MakeSub(
                "Check the levels that bound your floors. A floor is created between each "
              + "checked level; the lowest floor stretches far below and the highest far above. "
              + "Elevations are inferred from the model and can be edited."));
            panel.Children.Add(Gap());

            _levelHost = new StackPanel();
            if (_levels.Count == 0)
                _levelHost.Children.Add(MakeSub("No levels found in the model — add them manually below."));
            else
                foreach (var lv in _levels) _levelHost.Children.Add(BuildLevelRow(lv));
            panel.Children.Add(_levelHost);

            panel.Children.Add(Gap());

            var btnRow = new Grid();
            btnRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            btnRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            btnRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var rescan = MakeFlatButton("Rescan model");
            rescan.Click += (s, e) => RescanLevels();
            Grid.SetColumn(rescan, 0);

            var add = MakeFlatButton("+ Add level");
            add.Click += (s, e) => AddLevel();
            Grid.SetColumn(add, 2);

            btnRow.Children.Add(rescan);
            btnRow.Children.Add(add);
            panel.Children.Add(btnRow);

            return panel;
        }

        private FrameworkElement BuildLevelRow(LevelDef lv)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                  // checkbox
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // name
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                  // elevation
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                  // remove

            var check = new CheckBox { IsChecked = lv.UseAsBoundary, VerticalAlignment = VerticalAlignment.Center };
            check.SetResourceReference(Control.ForegroundProperty, "LemoineText");
            check.Checked   += (s, e) => { lv.UseAsBoundary = true;  MarkFloorsDirty(); };
            check.Unchecked += (s, e) => { lv.UseAsBoundary = false; MarkFloorsDirty(); };
            Grid.SetColumn(check, 0);

            var name = MakeTextBox(lv.Name);
            name.Margin = new Thickness(8, 0, 0, 0);
            name.TextChanged += (s, e) => { lv.Name = name.Text ?? ""; MarkFloorsDirty(); };
            Grid.SetColumn(name, 1);

            var elev = new LemoineInlineStepper
            {
                Value    = lv.Elevation,
                Decimals = 2,
                Step     = 1,
                MinValue = -1_000_000,
                MaxValue =  1_000_000,
                Margin   = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            elev.ValueChanged += (s, v) => { lv.Elevation = v; MarkFloorsDirty(); };
            Grid.SetColumn(elev, 2);

            var remove = MakeFlatButton("Remove");
            remove.Margin = new Thickness(8, 0, 0, 0);
            remove.Click += (s, e) =>
            {
                _levels.Remove(lv);
                _levelHost?.Children.Remove(row);
                MarkFloorsDirty();
            };
            Grid.SetColumn(remove, 3);

            row.Children.Add(check);
            row.Children.Add(name);
            row.Children.Add(elev);
            row.Children.Add(remove);
            return row;
        }

        private void AddLevel()
        {
            var lv = new LevelDef("Level " + (_levels.Count + 1), 0, useAsBoundary: true);
            _levels.Add(lv);
            if (_levelHost != null)
            {
                // Drop the "no levels" placeholder if it's the only child.
                if (_levels.Count == 1 && _levelHost.Children.Count == 1 && _levelHost.Children[0] is TextBlock)
                    _levelHost.Children.Clear();
                _levelHost.Children.Add(BuildLevelRow(lv));
            }
            MarkFloorsDirty();
        }

        private void RescanLevels()
        {
            var doc = NavisApp.ActiveDocument;
            if (doc == null || doc.IsClear) return;

            var found = NavisFloorSplit.DiscoverLevels(doc);
            _levels.Clear();
            _levels.AddRange(found);
            _rebuild?.Invoke("S1");   // repopulate the whole levels step
            MarkFloorsDirty();
        }

        private void MarkFloorsDirty()
        {
            _floorsDirty = true;
            Changed();
        }

        // ── S2: Floors & output ─────────────────────────────────────────────────
        private FrameworkElement BuildOutputStep()
        {
            var panel = new StackPanel();

            // Band preview (derived live from the current level selection).
            panel.Children.Add(MakeSub("Floors to export:"));
            var bands = NavisFloorSplit.BuildBands(_levels);
            if (bands.Count == 0)
                panel.Children.Add(Hint("Check at least two levels on the previous step."));
            else
                foreach (var b in bands)
                    panel.Children.Add(MakeSub($"• {b.Name}:  {FmtZ(b.Low)} → {FmtZ(b.High)}"));
            panel.Children.Add(Gap());

            var straddle = new LemoineSingleSelect
            {
                Label         = "Elements crossing a floor line",
                Items         = new List<string> { StraddleKeep, StraddleCentroid },
                SelectedItem  = _straddle == StraddleRule.ByCentroid ? StraddleCentroid : StraddleKeep,
            };
            straddle.SelectionChanged += sel =>
            {
                _straddle = sel == StraddleCentroid ? StraddleRule.ByCentroid : StraddleRule.KeepOverlapping;
                Changed();
            };
            panel.Children.Add(straddle);
            panel.Children.Add(Gap());

            var folder = new LemoineFolderBrowser
            {
                Label       = "Output folder",
                Path        = _outFolder,
                DialogTitle = "Choose a folder for the floor NWDs",
            };
            folder.PathChanged += p => { _outFolder = p ?? ""; Changed(); };
            panel.Children.Add(folder);
            panel.Children.Add(Gap());

            var pattern = new LemoineTextField
            {
                Label       = "Filename pattern",
                Text        = _pattern,
                Placeholder = "{floor}",
            };
            pattern.TextChanged += t => { _pattern = t ?? ""; Changed(); };
            panel.Children.Add(pattern);
            panel.Children.Add(MakeSub("Tokens: {floor} = floor name, {model} = source model name. “.nwd” is added automatically."));
            panel.Children.Add(Gap());

            var options = new LemoineToggleSwitches();
            options.SetItems(
                new List<ToggleItem>
                {
                    new ToggleItem { Id = "xrefs",  Label = "Embed referenced files",     Desc = "Bake xrefs into each NWD",                     DefaultOn = _embedXrefs },
                    new ToggleItem { Id = "props",  Label = "Keep object properties",      Desc = "Off = smaller files, no property data",        DefaultOn = _keepProps },
                    new ToggleItem { Id = "views",  Label = "Save a viewpoint per floor",  Desc = "For review before/after export",               DefaultOn = _saveViewpoints },
                });
            options.StateChanged += st =>
            {
                if (st.TryGetValue("xrefs", out var x)) _embedXrefs     = x;
                if (st.TryGetValue("props", out var p)) _keepProps      = p;
                if (st.TryGetValue("views", out var v)) _saveViewpoints = v;
                Changed();
            };
            panel.Children.Add(options);
            panel.Children.Add(Gap());

            panel.Children.Add(MakeSub(
                "Each floor's NWD contains only that floor — geometry outside the band is hidden "
              + "and excluded on export (Navisworks 2026)."));

            return panel;
        }

        // ── S3: Run ─────────────────────────────────────────────────────────────
        private FrameworkElement BuildRunStep()
        {
            int floors = NavisFloorSplit.BuildBands(_levels).Count;
            string where = string.IsNullOrWhiteSpace(_outFolder) ? "(no folder chosen)" : _outFolder;
            return Hint(floors == 0
                ? "No floors defined yet — check at least two levels on step 1."
                : $"{floors} floor NWD(s) will be written to:\n{where}");
        }

        // ── Validation / summary ─────────────────────────────────────────────────
        public bool IsValid(string stepId)
        {
            switch (stepId)
            {
                case "S1": return _levels.Count(l => l.UseAsBoundary) >= 2;
                case "S2": return !string.IsNullOrWhiteSpace(_outFolder)
                               && NavisFloorSplit.BuildBands(_levels).Count > 0;
                default:   return true;
            }
        }

        public string SummaryFor(string stepId)
        {
            int checkedCount = _levels.Count(l => l.UseAsBoundary);
            switch (stepId)
            {
                case "S1": return checkedCount < 2
                    ? $"{checkedCount} boundary level(s) — need 2+"
                    : $"{checkedCount} levels → {Math.Max(0, checkedCount - 1)} floor(s)";
                case "S2": return string.IsNullOrWhiteSpace(_outFolder)
                    ? "No output folder"
                    : $"{NavisFloorSplit.BuildBands(_levels).Count} floor(s) → {_outFolder}";
                default:   return $"{NavisFloorSplit.BuildBands(_levels).Count} floor(s)";
            }
        }

        // ── Run ──────────────────────────────────────────────────────────────────
        public void Run(
            Action<string, string>     pushLog,
            Action<int, int, int, int> onProgress,
            Action<int, int, int>      onComplete)
        {
            NavisDoc doc = NavisApp.ActiveDocument;
            if (doc == null || doc.IsClear)
            {
                pushLog("No model open.", "info");
                onComplete(0, 0, 0);
                return;
            }

            var bands = NavisFloorSplit.BuildBands(_levels);
            if (bands.Count == 0)
            {
                pushLog("Check at least two boundary levels first.", "info");
                onComplete(0, 0, 0);
                return;
            }
            if (string.IsNullOrWhiteSpace(_outFolder))
            {
                pushLog("Choose an output folder first.", "info");
                onComplete(0, 0, 0);
                return;
            }

            try { Directory.CreateDirectory(_outFolder); }
            catch (Exception ex)
            {
                LemoineLog.Error("FloorSplit: create output folder", ex);
                pushLog("Cannot create output folder: " + ex.Message, "fail");
                onComplete(0, 1, 0);
                return;
            }

            pushLog("Scanning geometry…", "info");
            var items = NavisFloorSplit.GatherItemZ(doc, out _, out _);
            pushLog($"Found {items.Count} geometry item(s).", items.Count > 0 ? "pass" : "info");
            if (items.Count == 0)
            {
                pushLog("Nothing to export.", "info");
                onComplete(0, 0, 0);
                return;
            }

            var allItems       = items.Select(z => z.Item).ToList();
            var originalHidden = NavisFloorSplit.CurrentlyHidden(items);
            string modelBase   = SafeModelBase(doc);
            int ok = 0, fail = 0;

            pushLog($"Exporting {bands.Count} floor(s) — updating existing files by name…", "info");
            if (_saveViewpoints)
                pushLog("Viewpoints save the per-floor hide state only if Options ▸ Interface ▸ "
                      + "Viewpoint Defaults ▸ “Save Hide/Required Attributes” is enabled.", "info");
            try
            {
                for (int i = 0; i < bands.Count; i++)
                {
                    var band = bands[i];
                    var hide = NavisFloorSplit.HideSetFor(items, band, _straddle);

                    // Reset to all-visible, then hide everything outside this band.
                    NavisFloorSplit.SetHidden(doc, allItems, false);
                    NavisFloorSplit.SetHidden(doc, hide, true);

                    int kept = items.Count - hide.Count;
                    if (_saveViewpoints) NavisFloorSplit.SaveFloorViewpoint(doc, band.Name, pushLog);

                    string file = ResolveFileName(band.Name, modelBase);
                    string path = Path.Combine(_outFolder, file);
                    bool wrote  = NavisFloorSplit.ExportNwd(doc, path, _embedXrefs, _keepProps, pushLog);

                    if (wrote) { ok++;   pushLog($"{band.Name}: {kept} item(s) → {file}", "pass"); }
                    else       { fail++; pushLog($"{band.Name}: export failed → {file}", "fail"); }

                    onProgress((int)((i + 1) * 100.0 / bands.Count), ok, fail, 0);
                }
            }
            finally
            {
                // Restore the model's original visibility so nothing is left mutated.
                try
                {
                    NavisFloorSplit.SetHidden(doc, allItems, false);
                    NavisFloorSplit.SetHidden(doc, originalHidden, true);
                }
                catch (Exception ex) { LemoineLog.Swallowed("FloorSplit: restore visibility", ex); }

                items.Clear();
                allItems.Clear();
                originalHidden.Clear();
            }

            pushLog($"Done — {ok} exported, {fail} failed.", fail == 0 ? "pass" : "info");
            onComplete(ok, fail, 0);
        }

        // ── Filename helpers ──────────────────────────────────────────────────────
        private string ResolveFileName(string floorName, string modelBase)
        {
            string name = (_pattern ?? "")
                .Replace("{floor}", floorName ?? "")
                .Replace("{model}", modelBase ?? "");
            if (string.IsNullOrWhiteSpace(name)) name = floorName ?? "floor";

            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            if (!name.EndsWith(".nwd", StringComparison.OrdinalIgnoreCase)) name += ".nwd";
            return name;
        }

        private static string SafeModelBase(NavisDoc doc)
        {
            try
            {
                string t = doc.Title;
                if (!string.IsNullOrWhiteSpace(t)) return Path.GetFileNameWithoutExtension(t);
            }
            catch (Exception ex) { LemoineLog.Swallowed("FloorSplit: model base name", ex); }
            return "model";
        }

        private string FmtZ(double z)
        {
            if (double.IsNegativeInfinity(z)) return "far below";
            if (double.IsPositiveInfinity(z)) return "far above";
            return z.ToString("0.##") + _unit;
        }

        // ── Tiny UI helpers (shared idiom with DiscoverSearchSets) ────────────────
        private static Button MakeFlatButton(string text)
        {
            var b = new Button { Content = text };
            b.Template = LemoineControlStyles.BuildFlatButtonTemplate();
            b.SetResourceReference(Control.BackgroundProperty, "LemoineAccent");
            b.SetResourceReference(Control.ForegroundProperty, "LemoineBg");
            b.SetResourceReference(Control.FontFamilyProperty, "LemoineUiFont");
            b.SetResourceReference(Control.FontSizeProperty, "LemoineFS_MD");
            b.SetResourceReference(Control.MinHeightProperty, "LemoineH_BtnMin");
            b.SetResourceReference(Control.PaddingProperty, "LemoineTh_BtnPad");
            return b;
        }

        private static TextBox MakeTextBox(string initial)
        {
            var t = new TextBox { Text = initial ?? "" };
            t.SetResourceReference(Control.BackgroundProperty, "LemoineSelectBg");
            t.SetResourceReference(Control.ForegroundProperty, "LemoineText");
            t.SetResourceReference(Control.FontFamilyProperty, "LemoineUiFont");
            t.SetResourceReference(Control.FontSizeProperty, "LemoineFS_MD");
            t.SetResourceReference(Control.PaddingProperty, "LemoineTh_InputPad");
            t.SetResourceReference(Control.MinHeightProperty, "LemoineH_Input");
            return t;
        }

        private static TextBlock MakeSub(string text)
        {
            var tb = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            tb.SetResourceReference(TextBlock.FontSizeProperty, "LemoineFS_SM");
            return tb;
        }

        private static FrameworkElement Gap() => new Border { Height = 8 };

        private static TextBlock Hint(string text)
        {
            var tb = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            tb.SetResourceReference(TextBlock.FontSizeProperty, "LemoineFS_MD");
            tb.FontStyle = FontStyles.Italic;
            return tb;
        }
    }
}
