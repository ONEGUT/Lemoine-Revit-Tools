using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LemoineTools.Lemoine;
using LemoineTools.Lemoine.Controls;

using WpfGrid    = System.Windows.Controls.Grid;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace LemoineTools.Tools.Testing
{
    public class CreateSheetsViewModel : ILemoineTool, ILemoineToolSettings, ILemoineReviewable
    {
        // ── Constants ─────────────────────────────────────────────────────────
        private static readonly string[] Modes =
            { "By Level", "By Room", "By Scope Box", "From CSV" };

        private static readonly (string Label, string Token)[] SheetNamingTokens =
        {
            ("Level Name",   "{LevelName}"),
            ("Room Name",    "{RoomName}"),
            ("Room Number",  "{RoomNumber}"),
            ("Scope Box",    "{ScopeBoxName}"),
            ("Sheet Number", "{SheetNumber}"),
        };

        // ── ILemoineTool ──────────────────────────────────────────────────────
        public string Title    => "Create Sheets";
        public string RunLabel => "Create Sheets →";

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("S1", "Source",       required: true),
            new StepDefinition("S2", "Options",      required: true),
            new StepDefinition("S3", "Review & Run", required: false),
        };

        public event EventHandler? ValidationChanged;
        private void OnValidationChanged() => ValidationChanged?.Invoke(this, EventArgs.Empty);

        // ── State ─────────────────────────────────────────────────────────────
        private string _mode = "By Level";

        private List<ElementId> _selectedElementIds = new List<ElementId>();

        private readonly Dictionary<string, ElementId> _levelMap;
        private readonly Dictionary<string, ElementId> _roomMap;
        private readonly Dictionary<string, ElementId> _scopeBoxMap;

        private readonly List<string>                  _titleblockNames;
        private readonly Dictionary<string, ElementId> _titleblockMap;
        private string _selectedTitleblock = "";

        private int    _startingNumber = 1;
        private string _namingPattern  = "{LevelName}";
        private string _csvFilePath    = "";

        // ── Revit wiring ──────────────────────────────────────────────────────
        private readonly CreateSheetsEventHandler? _handler;
        private readonly ExternalEvent?             _event;

        // ── Constructor ───────────────────────────────────────────────────────
        /// <summary>
        /// Full constructor — used when opening the tool from Revit.
        /// </summary>
        public CreateSheetsViewModel(
            CreateSheetsEventHandler? handler,
            ExternalEvent?             externalEvent,
            List<FamilySymbol>?        titleblocks,
            List<Level>?               levels,
            List<SpatialElement>?      rooms,
            List<Element>?             scopeBoxes)
        {
            _handler = handler;
            _event   = externalEvent;

            // ── Titleblock map ────────────────────────────────────────────────
            _titleblockMap  = new Dictionary<string, ElementId>();
            _titleblockNames = new List<string>();
            foreach (var tb in titleblocks ?? Enumerable.Empty<FamilySymbol>())
            {
                string label = $"{tb.FamilyName} : {tb.Name}";
                if (!_titleblockMap.ContainsKey(label))
                {
                    _titleblockMap[label] = tb.Id;
                    _titleblockNames.Add(label);
                }
            }

            // Pre-select from saved settings
            var settings = CreateSheetsSettings.Instance;
            if (!string.IsNullOrEmpty(settings.DefaultTitleblockName) &&
                _titleblockNames.Contains(settings.DefaultTitleblockName))
                _selectedTitleblock = settings.DefaultTitleblockName;
            else if (_titleblockNames.Count > 0)
                _selectedTitleblock = _titleblockNames[0];

            _startingNumber = settings.DefaultStartingNumber;
            _namingPattern  = settings.DefaultNamingScheme;

            // ── Level map ─────────────────────────────────────────────────────
            _levelMap = new Dictionary<string, ElementId>();
            foreach (var l in (levels ?? Enumerable.Empty<Level>()).OrderBy(x => x.Elevation))
            {
                if (!_levelMap.ContainsKey(l.Name))
                    _levelMap[l.Name] = l.Id;
            }

            // ── Room map ──────────────────────────────────────────────────────
            _roomMap = new Dictionary<string, ElementId>();
            foreach (var r in rooms ?? Enumerable.Empty<SpatialElement>())
            {
                string number = r.get_Parameter(BuiltInParameter.ROOM_NUMBER)?.AsString() ?? "";
                string name   = r.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString()   ?? r.Name;
                string key    = string.IsNullOrEmpty(number) ? name : $"{number} — {name}";
                string unique = key;
                int    suffix = 1;
                while (_roomMap.ContainsKey(unique)) unique = $"{key} ({suffix++})";
                _roomMap[unique] = r.Id;
            }

            // ── Scope-box map ─────────────────────────────────────────────────
            _scopeBoxMap = new Dictionary<string, ElementId>();
            foreach (var sb in (scopeBoxes ?? Enumerable.Empty<Element>()).OrderBy(x => x.Name))
            {
                if (!_scopeBoxMap.ContainsKey(sb.Name))
                    _scopeBoxMap[sb.Name] = sb.Id;
            }
        }

        /// <summary>
        /// Settings-only constructor — used by GlobalSettingsWindow when no document is open.
        /// </summary>
        public CreateSheetsViewModel()
            : this(null, null, null, null, null, null) { }

        // ═════════════════════════════════════════════════════════════════════
        //  GetStepContent
        // ═════════════════════════════════════════════════════════════════════
        public FrameworkElement? GetStepContent(string stepId)
        {
            switch (stepId)
            {
                case "S1": return BuildS1();
                case "S2": return BuildS2();
                case "S3": return null; // framework renders review (ILemoineReviewable)
                default:   return null;
            }
        }

        // ── Step 1 — Source ───────────────────────────────────────────────────
        private FrameworkElement BuildS1()
        {
            var outer = new StackPanel();

            var modeSelect = new LemoineSingleSelect { Label = "Source Mode" };
            modeSelect.Items = new List<string>(Modes);
            modeSelect.SelectedItem = _mode;
            outer.Children.Add(modeSelect);

            var separator = new Border
            {
                Height          = 1,
                Margin          = new Thickness(0, 10, 0, 10),
                BorderThickness = new Thickness(0),
            };
            separator.SetResourceReference(Border.BackgroundProperty, "LemoineBorder");
            outer.Children.Add(separator);

            var dynamic = new ContentControl();
            outer.Children.Add(dynamic);

            Action RebuildDynamic = () =>
            {
                _selectedElementIds.Clear();
                OnValidationChanged();
                dynamic.Content = BuildSourceSection();
            };

            modeSelect.SelectionChanged += sel =>
            {
                _mode = sel ?? "By Level";
                RebuildDynamic();
            };

            dynamic.Content = BuildSourceSection();
            return outer;
        }

        private FrameworkElement BuildSourceSection()
        {
            if (_mode == "From CSV")
            {
                var fb = new LemoineFileBrowser
                {
                    Label  = "CSV File",
                    Filter = "CSV Files (*.csv)|*.csv|All Files (*.*)|*.*",
                };
                if (!string.IsNullOrEmpty(_csvFilePath)) fb.Path = _csvFilePath;
                fb.PathChanged += p =>
                {
                    _csvFilePath = p ?? "";
                    OnValidationChanged();
                };

                var note = new TextBlock
                {
                    Text         = "Expected columns: SheetNumber, SheetName. Any additional columns are matched to Revit parameter names on the sheet.",
                    TextWrapping = TextWrapping.Wrap,
                    Margin       = new Thickness(0, 8, 0, 0),
                    FontStyle    = FontStyles.Italic,
                };
                note.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                note.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                note.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");

                var stack = new StackPanel();
                stack.Children.Add(fb);
                stack.Children.Add(note);
                return stack;
            }

            var currentMap = CurrentElementMap();

            if (currentMap.Count == 0)
            {
                string typeName = _mode.Replace("By ", "").ToLower();
                var msg = new TextBlock
                {
                    Text         = $"No {typeName} elements found in the active document.",
                    TextWrapping = TextWrapping.Wrap,
                };
                msg.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                msg.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                msg.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                return msg;
            }

            var tabs = new LemoineMultiSelectTabs();
            tabs.SetGroups(BuildElementGroups(currentMap));
            tabs.SelectionChanged += selected =>
            {
                _selectedElementIds = selected
                    .Where(n => currentMap.ContainsKey(n))
                    .Select(n => currentMap[n])
                    .ToList();
                OnValidationChanged();
            };
            return tabs;
        }

        private Dictionary<string, ElementId> CurrentElementMap()
        {
            switch (_mode)
            {
                case "By Level":     return _levelMap;
                case "By Room":      return _roomMap;
                case "By Scope Box": return _scopeBoxMap;
                default:             return new Dictionary<string, ElementId>();
            }
        }

        private Dictionary<string, List<string>> BuildElementGroups(Dictionary<string, ElementId> map)
        {
            if (_mode == "By Room")
            {
                var groups = new Dictionary<string, List<string>>();
                foreach (var key in map.Keys)
                {
                    // Group by numeric prefix first digit — fallback "Other"
                    string prefix = key.Length > 0 && char.IsDigit(key[0])
                        ? (key[0] - '0' < 4 ? "Floors 0–3" : "Floors 4+")
                        : "Other";
                    if (!groups.ContainsKey(prefix)) groups[prefix] = new List<string>();
                    groups[prefix].Add(key);
                }
                return groups;
            }

            // Levels and Scope Boxes: single alphabetical group
            string groupName = _mode == "By Level" ? "Levels" : "Scope Boxes";
            return new Dictionary<string, List<string>>
            {
                { groupName, map.Keys.ToList() },
            };
        }

        // ── Step 2 — Options ──────────────────────────────────────────────────
        private FrameworkElement BuildS2()
        {
            if (_mode == "From CSV")
                return BuildCsvPreview();

            var outer = new StackPanel();

            // ── Title Block ───────────────────────────────────────────────────
            var tbLabel = new TextBlock
            {
                Text   = "TITLE BLOCK",
                Margin = new Thickness(0, 0, 0, 4),
            };
            tbLabel.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tbLabel.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            tbLabel.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            outer.Children.Add(tbLabel);

            var tbSelect = new LemoineSingleSelect();
            tbSelect.Items = _titleblockNames.Count > 0
                ? _titleblockNames
                : new List<string> { "(no title blocks found)" };
            if (!string.IsNullOrEmpty(_selectedTitleblock))
                tbSelect.SelectedItem = _selectedTitleblock;
            tbSelect.SelectionChanged += sel =>
            {
                _selectedTitleblock = sel ?? "";
                OnValidationChanged();
            };
            outer.Children.Add(tbSelect);

            // ── Starting Number ───────────────────────────────────────────────
            var numLabel = new TextBlock
            {
                Text   = "STARTING SHEET NUMBER",
                Margin = new Thickness(0, 14, 0, 4),
            };
            numLabel.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            numLabel.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            numLabel.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            outer.Children.Add(numLabel);

            var numBox = new WpfTextBox
            {
                Text                = _startingNumber.ToString(),
                MinWidth            = 80,
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding             = new Thickness(8, 4, 8, 4),
            };
            numBox.SetResourceReference(WpfTextBox.BackgroundProperty,  "LemoineSelectBg");
            numBox.SetResourceReference(WpfTextBox.ForegroundProperty,  "LemoineText");
            numBox.SetResourceReference(WpfTextBox.BorderBrushProperty, "LemoineBorderMid");
            numBox.SetResourceReference(WpfTextBox.FontFamilyProperty,  "LemoineUiFont");
            numBox.SetResourceReference(WpfTextBox.FontSizeProperty,    "LemoineFS_MD");
            numBox.SetResourceReference(WpfTextBox.MinHeightProperty,   "LemoineH_Input");
            numBox.TextChanged += (s, e) =>
            {
                if (int.TryParse(numBox.Text, out int v) && v >= 1)
                    _startingNumber = v;
                OnValidationChanged();
            };
            outer.Children.Add(numBox);

            // ── Naming Pattern ────────────────────────────────────────────────
            var patLabel = new TextBlock
            {
                Text   = "SHEET NAMING PATTERN",
                Margin = new Thickness(0, 14, 0, 4),
            };
            patLabel.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            patLabel.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            patLabel.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            outer.Children.Add(patLabel);

            var tokenInput = new LemoineTokenInput(SheetNamingTokens);
            tokenInput.Text = _namingPattern;
            tokenInput.TextChanged += (s, e) =>
            {
                _namingPattern = tokenInput.Text;
                OnValidationChanged();
            };
            outer.Children.Add(tokenInput);

            // ── Preview ───────────────────────────────────────────────────────
            var prevLabel = new TextBlock
            {
                Text   = "PREVIEW",
                Margin = new Thickness(0, 14, 0, 4),
            };
            prevLabel.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            prevLabel.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            prevLabel.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            outer.Children.Add(prevLabel);

            var previewText = new TextBlock { TextWrapping = TextWrapping.Wrap, FontStyle = FontStyles.Italic };
            previewText.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            previewText.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            previewText.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");

            var placeholders = new Dictionary<string, string>
            {
                { "LevelName",    "Level 1" },
                { "RoomName",     "Office"  },
                { "RoomNumber",   "101"     },
                { "ScopeBoxName", "Zone A"  },
                { "SheetNumber",  _startingNumber.ToString() },
            };
            Action UpdatePreview = () =>
            {
                placeholders["SheetNumber"] = _startingNumber.ToString();
                previewText.Text = LemoineTokenInput.Resolve(_namingPattern, placeholders);
            };
            UpdatePreview();
            ValidationChanged += (s, e) => UpdatePreview();
            outer.Children.Add(previewText);

            return outer;
        }

        private FrameworkElement BuildCsvPreview()
        {
            if (string.IsNullOrEmpty(_csvFilePath) || !File.Exists(_csvFilePath))
            {
                var hint = new TextBlock
                {
                    Text         = "Select a CSV file in Step 1 to preview its contents here.\n\nExpected columns: SheetNumber, SheetName (plus any Revit parameter names to write).",
                    TextWrapping = TextWrapping.Wrap,
                };
                hint.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                hint.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                hint.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                return hint;
            }

            var outer = new StackPanel();
            try
            {
                var rows = CsvParser.Parse(_csvFilePath);
                if (rows.Count == 0)
                {
                    var empty = new TextBlock { Text = "The CSV file appears to be empty." };
                    empty.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                    empty.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                    empty.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                    return empty;
                }

                var headers  = rows[0];
                int colCount = headers.Length;
                int rowCount = Math.Min(rows.Count - 1, 8);

                var grid = new WpfGrid();
                for (int c = 0; c < colCount; c++)
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                for (int r = 0; r <= rowCount; r++)
                    grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                // Header row
                for (int c = 0; c < colCount; c++)
                {
                    var hdr = new TextBlock
                    {
                        Text       = headers[c],
                        FontWeight = FontWeights.SemiBold,
                        Margin     = new Thickness(4, 2, 4, 4),
                    };
                    hdr.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                    hdr.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                    hdr.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                    WpfGrid.SetRow(hdr, 0);
                    WpfGrid.SetColumn(hdr, c);
                    grid.Children.Add(hdr);
                }

                // Data rows
                for (int r = 1; r <= rowCount; r++)
                {
                    var dataRow = rows[r];
                    for (int c = 0; c < colCount; c++)
                    {
                        string cellText = c < dataRow.Length ? dataRow[c] : "";
                        var cell = new TextBlock
                        {
                            Text   = cellText,
                            Margin = new Thickness(4, 1, 4, 1),
                        };
                        cell.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                        cell.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
                        cell.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                        WpfGrid.SetRow(cell, r);
                        WpfGrid.SetColumn(cell, c);
                        grid.Children.Add(cell);
                    }
                }

                var tableBorder = new Border
                {
                    BorderThickness = new Thickness(1),
                    CornerRadius    = new CornerRadius(3),
                    Child           = grid,
                    Padding         = new Thickness(6),
                };
                tableBorder.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
                tableBorder.SetResourceReference(Border.BackgroundProperty,  "LemoineRaised");
                outer.Children.Add(tableBorder);

                int dataRowCount = rows.Count - 1;
                int extra        = dataRowCount - rowCount;
                string countMsg  = extra > 0
                    ? $"Showing first {rowCount} of {dataRowCount} rows — {dataRowCount} sheet(s) will be created."
                    : $"{dataRowCount} sheet(s) will be created.";

                var countText = new TextBlock
                {
                    Text   = countMsg,
                    Margin = new Thickness(0, 6, 0, 0),
                };
                countText.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
                countText.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                countText.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                outer.Children.Add(countText);
            }
            catch (Exception ex)
            {
                var err = new TextBlock
                {
                    Text         = $"Error reading CSV: {ex.Message}",
                    TextWrapping = TextWrapping.Wrap,
                };
                err.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
                err.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
                err.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
                return err;
            }
            return outer;
        }

        // ── Step 3 — Review & Run ─────────────────────────────────────────────
                // ── ILemoineReviewable (P3) — framework renders the review step ───
        public IList<(string id, string label)> ReviewItems { get; } = new List<(string, string)>
        {
            ("mode",   "Source Mode"),
            ("source", "Source"),
            ("tb",     "Title Block"),
            ("naming", "Naming / Numbering"),
        };

        public IDictionary<string, string> ReviewValues => new Dictionary<string, string>
        {
            ["mode"]   = _mode,
            ["source"] = _mode == "From CSV"
                ? (string.IsNullOrEmpty(_csvFilePath) ? "—" : Path.GetFileName(_csvFilePath))
                : (_selectedElementIds.Count == 0 ? "— (none selected)" : $"{_selectedElementIds.Count} element(s) selected"),
            ["tb"]     = string.IsNullOrEmpty(_selectedTitleblock) ? "—" : _selectedTitleblock,
            ["naming"] = _mode == "From CSV" ? "From CSV columns" : $"Pattern: {_namingPattern}  |  Start: {_startingNumber}",
        };

        public IList<string>? ReviewChips   => null;
        public string?        ReviewNote    => null;
        public string?        ReviewWarning => null;

        private void AddCard(WpfGrid grid, string label, Func<string> val, int row, int col)
        {
            var card = new Border
            {
                Margin          = new Thickness(col == 0 ? 0 : 4, row == 0 ? 0 : 4, 0, 0),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(3),
            };
            card.SetResourceReference(Border.PaddingProperty,    "LemoineTh_CardPad");
            card.SetResourceReference(Border.BackgroundProperty, "LemoineRaised");
            card.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");

            var lbl = new TextBlock { Text = label.ToUpper(), Margin = new Thickness(0, 0, 0, 2) };
            lbl.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            lbl.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");

            var valText = new TextBlock { FontWeight = FontWeights.Medium, TextWrapping = TextWrapping.Wrap };
            valText.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_MD");
            valText.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");
            valText.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineMonoFont");
            valText.Text = val();
            ValidationChanged += (s, e) => valText.Text = val();

            var sp = new StackPanel();
            sp.Children.Add(lbl);
            sp.Children.Add(valText);
            card.Child = sp;
            WpfGrid.SetRow(card, row);
            WpfGrid.SetColumn(card, col);
            grid.Children.Add(card);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  IsValid / SummaryFor / Run
        // ═════════════════════════════════════════════════════════════════════
        public bool IsValid(string stepId)
        {
            if (stepId == "S1")
            {
                if (_mode == "From CSV")
                    return !string.IsNullOrEmpty(_csvFilePath) && File.Exists(_csvFilePath);
                return _selectedElementIds.Count > 0;
            }
            if (stepId == "S2")
            {
                if (_mode == "From CSV") return true;
                return !string.IsNullOrEmpty(_selectedTitleblock) &&
                       _titleblockMap.ContainsKey(_selectedTitleblock) &&
                       !string.IsNullOrEmpty(_namingPattern);
            }
            return true;
        }

        public string SummaryFor(string stepId)
        {
            if (stepId == "S1")
                return _mode == "From CSV"
                    ? (string.IsNullOrEmpty(_csvFilePath) ? "—" : Path.GetFileName(_csvFilePath))
                    : (_selectedElementIds.Count == 0 ? "—" : $"{_selectedElementIds.Count} selected");
            if (stepId == "S2")
                return _mode == "From CSV"
                    ? "From CSV"
                    : $"{_selectedTitleblock}  |  {_namingPattern}";
            if (stepId == "S3")
                return "Ready to run";
            return "—";
        }

        public void Run(
            Action<string, string>     pushLog,
            Action<int, int, int, int> onProgress,
            Action<int, int, int>      onComplete)
        {
            if (_handler == null || _event == null) return;

            // Persist defaults
            var s = CreateSheetsSettings.Instance;
            if (!string.IsNullOrEmpty(_selectedTitleblock))
                s.DefaultTitleblockName = _selectedTitleblock;
            s.DefaultNamingScheme   = _namingPattern;
            s.DefaultStartingNumber = _startingNumber;
            s.Save();

            _handler.SourceMode       = _mode;
            _handler.SourceElementIds = new List<ElementId>(_selectedElementIds);
            _handler.TitleBlockTypeId = _titleblockMap.TryGetValue(_selectedTitleblock, out var tbId)
                                        ? tbId : ElementId.InvalidElementId;
            _handler.StartingNumber   = _startingNumber;
            _handler.NamingPattern    = _namingPattern;
            _handler.CsvFilePath      = _csvFilePath;
            _handler.PushLog          = pushLog;
            _handler.OnProgress       = onProgress;
            _handler.OnComplete       = onComplete;

            _event.Raise();
        }

        // ═════════════════════════════════════════════════════════════════════
        //  ILemoineToolSettings
        // ═════════════════════════════════════════════════════════════════════
        public LemoineToolSettingsSpec GetSettingsSpec()
        {
            var s = CreateSheetsSettings.Instance;
            return new LemoineToolSettingsSpec
            {
                Id          = "tz",
                Label       = "Create Sheets",
                Icon        = "",
                Description = "Title block, naming scheme, and starting number defaults.",
                Groups = new List<LemoineSettingsGroup>
                {
                    new LemoineSettingsGroup
                    {
                        Id            = "G1",
                        Title         = "Create Sheets Defaults",
                        OpenByDefault = true,
                        Settings      = new List<LemoineSettingDef>
                        {
                            new LemoineSettingDef
                            {
                                Id      = "titleblock",
                                Label   = "Default Title Block",
                                Hint    = "Pre-selected title block family when opening the tool.",
                                Kind    = "text",
                                Default = s.DefaultTitleblockName,
                                Options = new TextOpts { Placeholder = "Family : Type" },
                            },
                            new LemoineSettingDef
                            {
                                Id      = "namingScheme",
                                Label   = "Default Naming Scheme",
                                Hint    = "Token pattern used to name sheets, e.g. {LevelName}.",
                                Kind    = "text",
                                Default = s.DefaultNamingScheme,
                                Options = new TextOpts { Placeholder = "{LevelName}", Mono = true },
                            },
                            new LemoineSettingDef
                            {
                                Id      = "startingNumber",
                                Label   = "Default Starting Number",
                                Hint    = "Sheet numbering begins at this value.",
                                Kind    = "number",
                                Default = (double)s.DefaultStartingNumber,
                                Options = new NumberOpts { Min = 1, Max = 9999, Step = 1 },
                            },
                        },
                    },
                },
            };
        }

        public void ApplySettings(string groupId, string settingId, object value)
        {
            var s   = CreateSheetsSettings.Instance;
            var str = value?.ToString() ?? "";
            switch (settingId)
            {
                case "titleblock":
                    s.DefaultTitleblockName = str;
                    if (_titleblockNames.Contains(str)) _selectedTitleblock = str;
                    break;
                case "namingScheme":
                    s.DefaultNamingScheme = str;
                    _namingPattern = str;
                    break;
                case "startingNumber":
                    if (int.TryParse(str, out int n) && n >= 1)
                    {
                        s.DefaultStartingNumber = n;
                        _startingNumber = n;
                    }
                    break;
            }
            s.Save();
            OnValidationChanged();
        }
    }
}
