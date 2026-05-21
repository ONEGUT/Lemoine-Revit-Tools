using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LemoineTools.Lemoine;
using LemoineTools.Lemoine.Controls;

namespace LemoineTools.Preview
{
    /// <summary>
    /// Fictional "Room Tagger" tool — demonstrates the full StepFlowWindow + ILemoineTool pattern
    /// without any Revit API dependency. State persists via PreviewState.
    /// </summary>
    internal sealed class DemoTool : ILemoineTool, ILemoineToolSettings
    {
        private readonly PreviewState _state;

        private string                      _filePath       = "";
        private IReadOnlyCollection<string> _levels         = Array.Empty<string>();
        private int                         _tagOffset      = 25;
        private Dictionary<string, bool>    _toggles        = new Dictionary<string, bool>();

        public DemoTool(PreviewState state)
        {
            _state     = state;
            _filePath  = state.DemoFilePath;
            _levels    = state.DemoLevels.Split(',').Where(s => !string.IsNullOrEmpty(s)).ToList();
            _tagOffset = state.DemoTagOffset;
            _toggles   = PreviewState.ParseToggles(state.DemoToggles);
        }

        // ── ILemoineTool identity ──────────────────────────────────────────────
        public string Title    => "Room Tagger  —  Preview Demo";
        public string RunLabel => "Tag Rooms →";

        public StepDefinition[] Steps => new[]
        {
            new StepDefinition("file",    "Source Spreadsheet",  required: true),
            new StepDefinition("levels",  "Select Levels",       required: true),
            new StepDefinition("options", "Tag Options",         required: false),
        };

        public event EventHandler? ValidationChanged;

        // ── Step content ──────────────────────────────────────────────────────
        public FrameworkElement? GetStepContent(string stepId)
        {
            switch (stepId)
            {
                case "file": return BuildFileStep();
                case "levels": return BuildLevelsStep();
                case "options": return BuildOptionsStep();
                default: return null;
            }
        }

        private FrameworkElement BuildFileStep()
        {
            var stack = new StackPanel();

            var hint = MakeHint("Provide a .xlsx or .csv with at least a 'Room Name' column. Leave blank to use existing Revit room names.");
            stack.Children.Add(hint);

            var fb = new LemoineFileBrowser
            {
                Label       = "Room data spreadsheet",
                Placeholder = "Browse for .xlsx or .csv…",
                Filter      = "Spreadsheets|*.xlsx;*.csv|All files|*.*",
                Path        = _filePath,
            };
            fb.PathChanged += path =>
            {
                _filePath = path;
                _state.DemoFilePath = path;
                _state.Save();
                ValidationChanged?.Invoke(this, EventArgs.Empty);
            };
            stack.Children.Add(fb);

            return stack;
        }

        private FrameworkElement BuildLevelsStep()
        {
            var stack = new StackPanel();

            var hint = MakeHint("Choose which floor levels to tag. All rooms on selected levels will receive tags.");
            stack.Children.Add(hint);

            var groups = new Dictionary<string, List<string>>
            {
                ["Above Grade"]  = new List<string> { "Level 1", "Level 2", "Level 3", "Level 4", "Roof Plant" },
                ["Below Grade"]  = new List<string> { "B1 Basement", "B2 Sub-Basement" },
                ["Mezzanines"]   = new List<string> { "Mezzanine A", "Mezzanine B" },
            };

            var mt = new LemoineMultiSelectTabs();
            mt.SetGroups(groups, _levels);
            mt.SelectionChanged += sel =>
            {
                _levels = sel;
                _state.DemoLevels = string.Join(",", sel);
                _state.Save();
                ValidationChanged?.Invoke(this, EventArgs.Empty);
            };
            stack.Children.Add(mt);

            return stack;
        }

        private FrameworkElement BuildOptionsStep()
        {
            var stack = new StackPanel();

            // ── Tag offset stepper ────────────────────────────────────────────
            var offsetLabel = MakeHint("Offset from wall face (mm)");
            offsetLabel.Margin = new Thickness(0, 0, 0, 4);
            stack.Children.Add(offsetLabel);

            var stepper = new LemoineNumberStepper
            {
                Value    = _tagOffset,
                MinValue = 0,
                MaxValue = 300,
                Step     = 5,
                Margin   = new Thickness(0, 0, 0, 16),
            };
            stepper.ValueChanged += v =>
            {
                _tagOffset = v;
                _state.DemoTagOffset = v;
                _state.Save();
            };
            stack.Children.Add(stepper);

            // ── Toggle switches ───────────────────────────────────────────────
            var toggles = new LemoineToggleSwitches();
            var items = new[]
            {
                new LemoineToggleSwitches.ToggleItem { Id = "tag_room_name", Label = "Room name",   Desc = "Include the room name on each tag",        DefaultOn = true  },
                new LemoineToggleSwitches.ToggleItem { Id = "tag_area",      Label = "Area (m²)",   Desc = "Include the calculated gross room area",    DefaultOn = true  },
                new LemoineToggleSwitches.ToggleItem { Id = "tag_number",    Label = "Room number",  Desc = "Include the room number parameter",         DefaultOn = false },
                new LemoineToggleSwitches.ToggleItem { Id = "tag_level",     Label = "Level name",   Desc = "Append the level name beneath each tag",    DefaultOn = false },
            };
            toggles.SetItems(items, _toggles);
            toggles.StateChanged += s =>
            {
                _toggles = new Dictionary<string, bool>(s);
                _state.DemoToggles = PreviewState.FormatToggles(s);
                _state.Save();
            };
            stack.Children.Add(toggles);

            return stack;
        }

        // ── Validation + summary ──────────────────────────────────────────────
        public bool IsValid(string stepId) => stepId switch
        {
            "file"   => true, // optional — blank means use Revit room names
            "levels" => _levels.Count > 0,
            _        => true,
        };

        public string SummaryFor(string stepId) => stepId switch
        {
            "file"    => string.IsNullOrWhiteSpace(_filePath)
                             ? "Using Revit room names"
                             : System.IO.Path.GetFileName(_filePath),
            "levels"  => _levels.Count == 0
                             ? "None selected"
                             : $"{_levels.Count} level{(_levels.Count == 1 ? "" : "s")} selected",
            "options" => $"Offset {_tagOffset} mm",
            _         => "",
        };

        // ── Run (simulated — no Revit API) ────────────────────────────────────
        public void Run(
            Action<string, string>     pushLog,
            Action<int, int, int, int> onProgress,
            Action<int, int, int>      onComplete)
        {
            var levels = _levels.ToList();

            if (!string.IsNullOrWhiteSpace(_filePath))
                pushLog($"Loaded spreadsheet: {System.IO.Path.GetFileName(_filePath)}", "info");
            else
                pushLog("No spreadsheet — using existing Revit room names", "info");

            int totalRooms = levels.Count * 4; // simulate 4 rooms per level
            int pass = 0;

            for (int i = 0; i < levels.Count; i++)
            {
                pushLog($"Processing {levels[i]}…", "info");

                int roomsOnLevel = 4;
                pass += roomsOnLevel;

                onProgress((i + 1) * 100 / levels.Count, pass, 0, 0);
                pushLog($"  Tagged {roomsOnLevel} rooms on {levels[i]}", "pass");
            }

            if (_toggles.TryGetValue("tag_area", out var area) && area)
                pushLog("Area values included on all tags", "pass");

            onComplete(pass, 0, 0);
        }

        // ── ILemoineToolSettings ──────────────────────────────────────────────
        public LemoineToolSettingsSpec? GetSettingsSpec() => new LemoineToolSettingsSpec
        {
            Id          = "room-tagger",
            Label       = "Room Tagger",
            Description = "Default values for the Room Tagger tool.",
            Groups      = new List<LemoineSettingsGroup>
            {
                new LemoineSettingsGroup
                {
                    Id            = "output",
                    Title         = "Output",
                    OpenByDefault = true,
                    Settings      = new List<LemoineSettingDef>
                    {
                        new LemoineSettingDef
                        {
                            Id      = "default_offset",
                            Label   = "Default tag offset (mm)",
                            Kind    = "number",
                            Default = "25",
                            Options = new NumberOpts { Min = 0, Max = 300, Step = 5, Unit = "mm" },
                        },
                        new LemoineSettingDef
                        {
                            Id      = "include_area",
                            Label   = "Include area by default",
                            Kind    = "toggle",
                            Default = "true",
                        },
                    },
                },
                new LemoineSettingsGroup
                {
                    Id       = "naming",
                    Title    = "Naming",
                    Settings = new List<LemoineSettingDef>
                    {
                        new LemoineSettingDef
                        {
                            Id      = "name_param",
                            Label   = "Room name parameter",
                            Kind    = "search",
                            Default = "Name",
                            Options = new SearchOpts
                            {
                                Items = new List<string> { "Name", "Room Name", "Description", "Comments", "Mark" },
                            },
                        },
                        new LemoineSettingDef
                        {
                            Id      = "number_param",
                            Label   = "Room number parameter",
                            Kind    = "search",
                            Default = "Number",
                            Options = new SearchOpts
                            {
                                Items = new List<string> { "Number", "Room Number", "Mark", "ID" },
                            },
                        },
                    },
                },
            },
        };

        public void ApplySettings(string groupId, string settingId, object value)
        {
            if (groupId == "output" && settingId == "default_offset" && value is double d)
            {
                _tagOffset = (int)d;
                _state.DemoTagOffset = _tagOffset;
                _state.Save();
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────
        private static TextBlock MakeHint(string text)
        {
            var tb = new TextBlock
            {
                Text         = text,
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 0, 0, 10),
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextSub");
            tb.SetResourceReference(TextBlock.FontSizeProperty,   "LemoineFS_SM");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            return tb;
        }
    }
}
