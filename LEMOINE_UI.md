# Lemoine Tools — UI Architecture Reference

> **Purpose:** Everything needed to completely understand, remake, or extend the Lemoine Tools WPF UI. This document covers the design system, all components, the tool contract, the settings spec, and the step-by-step pattern for adding new tools.

---

## Table of Contents

1. [High-Level Architecture](#1-high-level-architecture)
2. [The Theme System](#2-the-theme-system)
3. [Resource Keys — Full Reference](#3-resource-keys--full-reference)
4. [StepFlowWindow Layout](#4-stepflowwindow-layout)
5. [IStepFlowTool — The Tool Contract](#5-istepflowtool--the-tool-contract)
6. [IToolSettings — Persistent Settings](#6-itoolsettings--persistent-settings)
7. [ToolSettingsSpec — Declarative Settings](#7-toolsettingsspec--declarative-settings)
8. [Framework Control Library](#8-framework-control-library)
9. [How to Add a New Tool — Step by Step](#9-how-to-add-a-new-tool--step-by-step)
10. [File & Folder Map](#10-file--folder-map)

---

## 1. High-Level Architecture

```
Revit Ribbon Button
      │
      ▼
IExternalCommand.Execute()
      │  creates
      ▼
StepFlowWindow(new YourViewModel())
      │  drives via
      ▼
IStepFlowTool  ◄──────────  YourViewModel
      │                        │
      │  GetStepContent()      │  holds state, builds WPF controls
      │  IsValid()             │  wires events → fires ValidationChanged
      │  SummaryFor()          │
      │  Run()                 │  calls ExternalEvent.Raise()
      ▼                        ▼
StepFlowWindow renders     ExternalEventHandler
accordion, progress,       executes Revit API,
log, settings overlay      calls back pushLog/onProgress/onComplete
```

**Rules:**
- `StepFlowWindow` owns ALL chrome (toolbar, progress, accordion, log, footer). You never subclass or touch it.
- `YourViewModel` is a plain C# class that implements `IStepFlowTool` (and optionally `IToolSettings`).
- All controls use `SetResourceReference(prop, "LemoineXxx")` — never hard-code colours or sizes.
- Theming and scaling propagate automatically via WPF's dynamic resource system. No rebuild required.
- Revit API calls happen exclusively inside `IExternalEventHandler.Execute()`, never on the WPF thread.

---

## 2. The Theme System

### 2.1 ThemePalette

**File:** `Source/Framework/ThemePalette.cs`

A `ThemePalette` is a plain C# object holding every colour and font for the UI. There are **8 built-in themes**:

| Name | Swatch hex | Character |
|---|---|---|
| Dark Mono | `#1a1a1a` | Neutral grey, cool blue accent |
| Dark Navy | `#0b0d14` | Deep blue-tinted, bright blue accent |
| Light Clean | `#ffffff` | GitHub-style white/grey, blue accent |
| Light Warm | `#fdf8f2` | Cream/parchment, amber accent |
| Stone Gray | `#3c3c3c` | Mid-grey, steel-blue accent |
| Terra | `#faf6f0` | Earthy greens, forest-green accent |
| Sahara | `#faf5ee` | Sun-baked linen, burnt-sienna accent |
| Obsidian | `#09090b` | Near-black zinc, violet accent |

The complete list is `ThemePalette.All[]` (display order for the picker).

**Adding a new theme:** add a new `public static readonly ThemePalette MyTheme = new ThemePalette { … }` entry and include it in the `All` array.

### 2.2 AppSettings singleton

**File:** `Source/Framework/AppSettings.cs`

```csharp
AppSettings.Instance.ActiveTheme  // ThemePalette
AppSettings.Instance.UiSize       // UiSize { Small, Medium, Large }
AppSettings.Instance.Scale        // 0.85 / 1.0 / 1.20

AppSettings.Instance.SetTheme(ThemePalette.DarkNavy);
AppSettings.Instance.SetUiSize(UiSize.Large);
```

Settings persist to `%AppData%\LemoineTools\UISettings.xml`.

**Events (subscribe in your window constructor, unsubscribe on Close):**
```csharp
AppSettings.Instance.ThemeChanged  += OnThemeChanged;   // Action<ThemePalette>
AppSettings.Instance.UiSizeChanged += OnUiSizeChanged;  // Action<UiSize>
```

**Applying to a window:**
```csharp
AppSettings.Instance.ApplyTo(Resources);    // colours + scale
AppSettings.Instance.ApplyScaleTo(Resources); // scale only (on resize)
```

### 2.3 Animation constants

All durations are read from `AppSettings.Instance`:

| Property | ms | Use |
|---|---|---|
| `AnimFast` | 180 | Hover highlights |
| `AnimMed` | 220 | Panel slides, settings overlay close |
| `AnimExpand` | 280 | Step accordion open |
| `AnimProgress` | 350 | Progress bar fill |

---

## 3. Resource Keys — Full Reference

Every control binds to these keys via `SetResourceReference(prop, "Key")`. Never use literal values.

### 3.1 Colour keys (from ThemePalette)

| Key | Role |
|---|---|
| `LemoineBg` | Primary window background |
| `LemoinePageBg` | Outer page background (slightly darker) |
| `LemoineSurface` | Toolbar, tab bar, footer |
| `LemoineRaised` | Cards, input backgrounds |
| `LemoineSelectBg` | Text input / ComboBox background |
| `LemoineBorder` | Default border / separator |
| `LemoineBorderMid` | Mid-weight secondary border |
| `LemoineText` | Primary body text |
| `LemoineTextSub` | Secondary labels, helper text |
| `LemoineTextDim` | Placeholders, disabled labels |
| `LemoineAccent` | Interactive — links, focus rings, active state |
| `LemoineAccentDim` | Accent tint background (behind accent text) |
| `LemoineGreen` | Success / pass status |
| `LemoineGreenDim` | Dimmed success background |
| `LemoineRed` | Error / destructive / fail status |
| `LemoineRedDim` | Dimmed error background |
| `LemoineMonoFont` | Monospaced font (Consolas) |
| `LemoineUiFont` | UI font (Segoe UI, or theme-specific) |
| `LemoineKnobOn` | Toggle knob colour when ON |
| `LemoineKnobOff` | Toggle knob colour when OFF |
| `LemoineWarnBg` | Warning banner background (alias of `LemoineRedDim`) |
| `LemoineWarnBorder` | Warning banner border (alias of `LemoineRed`) |
| `LemoineWarnText` | Warning banner text (alias of `LemoineRed`) |

### 3.2 Font size keys (scaled by UiSize)

| Key | Base px | Use |
|---|---|---|
| `LemoineFS_SM` | 10.5 | Timestamps, badges, labels |
| `LemoineFS_MD` | 12.0 | Body text, button labels |
| `LemoineFS_LG` | 13.0 | Section headings |
| `LemoineFS_XL` | 15.0 | Close × glyph |
| `LemoineFS_Chevron` | 20.0 | Accordion chevron |

### 3.3 Height / size keys (scaled)

| Key | Base px | Use |
|---|---|---|
| `LemoineH_Toolbar` | 38 | Toolbar row height |
| `LemoineH_Footer` | 42 | Footer row height |
| `LemoineH_BtnMin` | 28 | Minimum button height |
| `LemoineH_BtnSm` | 26 | Small button height |
| `LemoineH_Input` | 30 | Input field height |
| `LemoineH_Circle` | 18 | Step number circle diameter |
| `LemoineH_Pip` | 3 | Step pip bar height |
| `LemoineH_ProgBar` | 4 | Progress bar height |
| `LemoineH_Icon_SM` | 13 | Small icon / separator height |
| `LemoineH_Icon_MD` | 20 | Medium icon height |
| `LemoineH_Pill_W` | 28 | Toggle pill width |
| `LemoineH_Pill_H` | 15 | Toggle pill height |
| `LemoineH_Knob` | 11 | Toggle knob diameter |
| `LemoineH_Sep` | 1 | Separator line (unscaled) |
| `LemoineH_LogArea` | 90 | Log scroll area height |

### 3.4 Corner radius keys (not scaled)

| Key | Value | Use |
|---|---|---|
| `LemoineRadius_SM` | 3 | Small badges, inputs |
| `LemoineRadius_MD` | 4 | Step list border |
| `LemoineRadius_Card` | 6 | Section cards |
| `LemoineRadius_LG` | 8 | Large panels |
| `LemoineRadius_Circle` | 9 | Step number circles |

### 3.5 Thickness / spacing keys (scaled)

| Key | Base (l,t,r,b) | Use |
|---|---|---|
| `LemoineTh_ToolbarMar` | 14,0,14,0 | Toolbar inner margin |
| `LemoineTh_StepListMar` | 12,0,12,4 | Step list outer margin |
| `LemoineTh_ProgressMar` | 12,8,12,0 | Progress strip margin |
| `LemoineTh_FooterPad` | 12,0,12,0 | Footer padding |
| `LemoineTh_RowPad` | 12,8,12,8 | Step row header padding |
| `LemoineTh_ContentPad` | 14,0,14,12 | Step content padding |
| `LemoineTh_CardPad` | 10,7,10,7 | Card inner padding |
| `LemoineTh_CardMar` | 0,4,0,0 | Card outer margin |
| `LemoineTh_SectionPad` | 14,10,14,14 | Section card content padding |
| `LemoineTh_SectionHdrPad` | 14,12,14,12 | Section card header padding |
| `LemoineTh_BtnPad` | 14,0,14,0 | Standard button padding |
| `LemoineTh_BtnSmPad` | 8,3,8,3 | Small button padding |
| `LemoineTh_InputPad` | 7,5,7,5 | Input field padding |
| `LemoineTh_LogPad` | 8,6,8,6 | Log area padding |
| `LemoineTh_LogMar` | 12,8,12,8 | Log area margin |
| `LemoineTh_ItemMar` | 0,0,0,5 | List item bottom margin |
| `LemoineTh_HeaderMar` | 0,0,0,16 | Header bottom margin |
| `LemoineTh_SubLabelMar` | 0,0,0,8 | Sub-label bottom margin |
| `LemoineTh_ProgCountMar` | 10,0,10,0 | Progress counter margin |
| `LemoineTh_CircleMar` | 0,0,8,0 | Step circle right margin |
| `LemoineTh_GearMar` | 0,0,10,0 | Gear icon right margin |

---

## 4. StepFlowWindow Layout

**File:** `Source/Framework/StepFlowWindow.xaml` + `StepFlowWindow.xaml.cs`

The window is **500 × 720 px**, min **420 × 560 px**, resizable via grip. It uses `WindowStyle="None"` with a custom chrome shell.

### 4.1 Grid rows

```
Row 0 — 38 px   Toolbar (TitleBar, step counter, close ×, ⚙ settings)
Row 1 — Auto    Progress strip (status, bar, pip dots, pass/fail/skip counters)
Row 2 — *       Step accordion (ScrollViewer + StackPanel of step rows)
Row 3 — Auto    Log tab area (tab bar + scrollable log output)
Row 4 — 42 px   Footer (Reset left, Close right)
```

Row 0 and Row 4 heights update when `UiSizeChanged` fires.

### 4.2 Toolbar (Row 0)

Built by `BuildToolbar()`. Uses `TitleBar` control (see §8.4). Right-side content contains:
- ⚙ gear glyph → `ToggleSettings()` — slides in the tool-settings overlay
- Vertical separator
- Step counter badge (e.g. `Step 2 / 4`, `Running…`, `Complete`)
- `×` close button

### 4.3 Progress strip (Row 1)

Built by `BuildProgressStrip()`. Contains:
- Status text (`● Configuring…` / `● Running…` / `● Done`) — colour `LemoineAccent` → `LemoineGreen` on done
- Animated progress bar fill (`LemoineAccent` → `LemoineGreen` on done)
- Pass / fail / skip counters (right-aligned)
- One pip rectangle per step (bottom row) — `LemoineBorder` → `LemoineAccent` (active) → `LemoineGreen` (done)

### 4.4 Step accordion (Row 2)

Built by `BuildStepAccordion()` then `BuildStepRow()` per step.

Each step row layout (inside a `Border` → inner `Grid` of 2 cols):
```
Col 0 (2px)  — Accent bar (LemoineGreen=done, LemoineAccent=active, Transparent=future)
Col 1 (*)    — StackPanel:
                 DockPanel (header):
                   Left: circle (step number / ✓)
                   Body: StackPanel
                           idText  (step.Id, mono, dim)
                           titleTb (step.Title, medium weight)
                           summaryTb  (italic, shown when done)
                           waitingTb  ("Waiting…", shown when future)
                           runningTb  ("● Processing in Revit…", accent, shown when running)
                 Border (content — animated MaxHeight 0↔600):
                   Padding: LemoineTh_ContentPad
                   StackPanel:
                     [Your GetStepContent() element]
                     validationTb  ("✗ Required before proceeding", LemoineRed)
                     Grid (3-col): [← Back]  [spacer]  [Confirm → / Run Label]
```

**Step states:**

| State | AccentBar | Circle bg | Circle border | Circle text | Row bg |
|---|---|---|---|---|---|
| Active | LemoineAccent | LemoineAccentDim | LemoineAccent | LemoineAccent | LemoineAccentDim |
| Done | LemoineGreen | LemoineGreenDim | LemoineGreen | LemoineGreen | Transparent |
| Future | Transparent | Transparent | LemoineBorder | LemoineTextDim | Transparent |

**Content animation:** `MaxHeight` animates `0 → 600` (expand, `AnimExpand` ms) or `600 → 0` (collapse, `AnimMed` ms) with `CubicEase`.

### 4.5 Log area (Row 3)

Built by `BuildLogArea()` + `BuildTabBar()`. Supports multiple tabs via `RegisterLogTab(id, label, content)` (call before `Show()`). Default tab is "Output Log".

Tab bar sits on a `LemoineSurface` background with `LemoineBorder` top edge. Active tab has a 2px `LemoineAccent` bottom border.

Log entries added by `PushLog(text, status)`:
- `"pass"` → `✓` in `LemoineGreen`
- `"fail"` → `✗` in `LemoineRed`
- `"info"` → `·` in `LemoineTextDim`

Each row: `[HH:mm:ss.mmm]  [icon]  [message text]`

### 4.6 Footer (Row 4)

`LemoineSurface` background, `LemoineBorder` top edge.
- **Reset** (left, ghost button) — resets to step 1, clears log
- **Close ✓** (right, accent button) — enabled only after run completes; turns `LemoineGreen` on done

### 4.7 Settings overlay

A slide-in panel from the right edge (`minWidth = 360`). Z-index 100. Slides with `CubicEase` animation. A transparent dismiss layer behind it (Z-index 99) closes it on click-outside.

The overlay is built from `IToolSettings.GetSettingsSpec()`. If the tool doesn't implement that interface, shows "No settings for this tool."

---

## 5. IStepFlowTool — The Tool Contract

**File:** `Source/Framework/IStepFlowTool.cs`

```csharp
public interface IStepFlowTool
{
    string           Title    { get; }   // shown in toolbar
    string           RunLabel { get; }   // label on final step's button

    StepDefinition[] Steps    { get; }   // ordered array of steps

    FrameworkElement? GetStepContent(string stepId);
    bool              IsValid(string stepId);
    string            SummaryFor(string stepId);

    void Run(
        Action<string, string>     pushLog,     // (text, status) status ∈ {"pass","fail","info"}
        Action<int, int, int, int> onProgress,  // (pct 0-100, pass, fail, skip)
        Action<int, int, int>      onComplete); // (pass, fail, skip)

    event EventHandler? ValidationChanged;
}
```

### StepDefinition

```csharp
new StepDefinition(
    id:       "S1",              // short key used in GetStepContent/IsValid/SummaryFor
    title:    "DWG Source File", // display name in accordion header
    required: true               // when true, Confirm is disabled until IsValid returns true
)
```

### Validation flow

1. ViewModel fires `ValidationChanged` whenever user input changes.
2. `StepFlowWindow` calls `IsValid(activeStep.Id)`.
3. If `required && !valid` → Confirm button disabled + validation text shown.
4. `SummaryFor` is called when a step is collapsed (after confirming) to show a one-liner.

### Run() threading rules

`Run()` is called on the **WPF UI thread**. You must NOT call Revit API here.

Correct pattern:
```csharp
public void Run(Action<string,string> pushLog, Action<int,int,int,int> onProgress, Action<int,int,int> onComplete)
{
    _handler.SomeParam  = _someValue;
    _handler.PushLog    = pushLog;
    _handler.OnProgress = onProgress;
    _handler.OnComplete = onComplete;
    _event.Raise();   // Revit schedules Execute() on its own thread
}
```

Inside `IExternalEventHandler.Execute(UIApplication app)`:
- Do all Revit API work here.
- Call `Dispatcher.BeginInvoke(...)` before calling the callbacks — `StepFlowWindow.StartRun()` already wraps them in `Dispatcher.BeginInvoke`, so direct invocation from `Execute()` is also safe.

---

## 6. IToolSettings — Persistent Settings

**File:** `Source/Framework/IToolSettings.cs`

Optional. Implement alongside `IStepFlowTool` on your ViewModel.

```csharp
public interface IToolSettings
{
    ToolSettingsSpec? GetSettingsSpec();
    void ApplySettings(string groupId, string settingId, object value);
}
```

`GetSettingsSpec()` seeds every `Default` from your `YourToolSettings.Instance.*`.
`ApplySettings()` is called once per changed value when the user clicks a control. Write back to `YourToolSettings.Instance.*` and call `.Save()`.

Tool-specific settings persist separately from UI settings. The typical pattern:

```csharp
// YourToolSettings.cs
public sealed class YourToolSettings
{
    public static YourToolSettings Instance { get; } = new YourToolSettings();
    public bool SomeOption { get; set; } = true;
    public void Save() { /* JSON or XML to %AppData%\LemoineTools\YourTool.json */ }
}
```

---

## 7. ToolSettingsSpec — Declarative Settings

**File:** `Source/Framework/ToolSettingsSpec.cs`

Return one of these from `GetSettingsSpec()`. Both the in-tool ⚙ overlay and `GlobalSettingsWindow` consume it automatically.

### Structure

```
ToolSettingsSpec
  .Id          "T01"
  .Label       "Auto Filters"
  .Icon        "01"
  .Description "One-sentence description"
  .Groups[]
      SettingsGroup
        .Id            "grp1"
        .Title         "Group heading"
        .Hint          "Optional sub-heading"
        .OpenByDefault true
        .Settings[]
            SettingDef
              .Id      "myKey"
              .Label   "Setting label"
              .Hint    "Helper text"
              .Kind    "toggle"          // see table below
              .Options new ToggleOpts { … }
              .Default true
```

### Kind values and matching types

| Kind | Options type | Default type | Control rendered |
|---|---|---|---|
| `"toggle"` | `ToggleOpts` | `bool` | Single toggle switch |
| `"toggles"` | `TogglesOpts` | `Dictionary<string,bool>` | Multi-row toggle switches |
| `"single"` | `SingleSelectOpts` | `string` | `SingleSelect` dropdown |
| `"search"` | `SearchOpts` | `string` | `SearchAutocomplete` |
| `"multi"` | `MultiSelectOpts` | `List<string>` | `MultiSelectTabs` |
| `"text"` | `TextOpts` | `string` | `TextBox` (mono optional) |
| `"number"` | `NumberOpts` | `double` | `TextBox` + unit label |
| `"range"` | `RangeOpts` | `(double Min, double Max)` | `NumberRange` |
| `"file"` | `FileOpts` | `string` | `FileBrowser` |
| `"color"` | *(none)* | `string` hex | Color picker swatch |
| `"matrix"` | `MatrixOpts` | `Dictionary<string,string>` | `MatrixInput` |
| `"date"` | `DateOpts` | `string` ISO | `DateField` |
| `"info"` | `InfoOpts` | *(none)* | Read-only italic text block |

### Options classes quick reference

```csharp
new SingleSelectOpts  { Items = new List<string> { "A", "B", "C" } }
new MultiSelectOpts   { Groups = new Dictionary<string, List<string>> { ["Tab1"] = new List<string> { "X" } } }
new TogglesOpts       { Items = new List<ToggleItem> { new ToggleItem { Id="k", Label="Label", Desc="hint", DefaultOn=true } } }
new MatrixOpts        { Rows = …, Cols = …, Defaults = … }
new FileOpts          { Placeholder = "Select file…", Recents = new List<string> { "C:\\recent.dwg" } }
new DateOpts          { Mode = "single" }  // or "range"
new RangeOpts         { Unit="mm", MinLabel="Min", MaxLabel="Max", Step=0.5, AbsMin=0, AbsMax=9999 }
new SearchOpts        { Items = new List<string> { "Alpha", "Beta" } }
new ToggleOpts        { OnLabel = "Yes", OffLabel = "No" }
new TextOpts          { Placeholder = "Enter text…", Mono = true }
new NumberOpts        { Unit = "mm", Min = 0, Max = 1000, Step = 10 }
new InfoOpts          { Text = "This setting affects X." }
```

---

## 8. Framework Control Library

All controls live under `Source/Framework/Controls/`. Every control applies `SetResourceReference` internally — you just set properties and wire events.

### 8.1 Input controls

#### FileBrowser
**File:** `Controls/Layout/FileBrowser.xaml(.cs)`

```csharp
var fb = new FileBrowser
{
    Label       = "Select the DWG file to process.",
    Filter      = "AutoCAD DWG|*.dwg|All files|*.*",
    DialogTitle = "Select File",
    Placeholder = "No file selected",
    Recents     = new List<string> { @"C:\Recent\file.dwg" },
    Path        = initialValue,
};
fb.PathChanged += path => { _myPath = path ?? ""; OnValidationChanged(); };
```

#### SingleSelect
**File:** `Controls/Input/SingleSelect.xaml(.cs)`

```csharp
var sel = new SingleSelect
{
    Label        = "Choose level",
    Items        = myStringList,
    SelectedItem = currentValue,
};
sel.SelectionChanged += v => _myValue = v;
```

#### SearchAutocomplete
**File:** `Controls/Input/SearchAutocomplete.xaml(.cs)`

```csharp
var sa = new SearchAutocomplete
{
    Items       = allStrings,
    Placeholder = "Search…",
    Value       = currentValue,
};
sa.SelectionChanged += v => _myValue = v;
```

#### ToggleSwitches
**File:** `Controls/Input/ToggleSwitches.xaml(.cs)`

```csharp
var tog = new ToggleSwitches();
tog.SetItems(new List<ToggleItem>
{
    new ToggleItem { Id = "walls",   Label = "Walls",   Desc = "Split along walls",   DefaultOn = true },
    new ToggleItem { Id = "columns", Label = "Columns", Desc = "Split along columns", DefaultOn = false },
});
// Optional: seed current state
tog.SetItems(items, new Dictionary<string,bool> { ["walls"] = false });
tog.StateChanged += state => { /* state is Dictionary<string,bool> */ };
```

#### MultiSelectTabs
**File:** `Controls/Input/MultiSelectTabs.xaml(.cs)`

```csharp
var tabs = new MultiSelectTabs();
tabs.SetGroups(new Dictionary<string, List<string>>
{
    ["Architectural"] = new List<string> { "Walls", "Floors" },
    ["Structural"]    = new List<string> { "Beams", "Columns" },
});
tabs.SelectionChanged += selected => { /* selected is List<string> */ };
```

#### LemoineNumberStepper
**File:** `Controls/Input/LemoineNumberStepper.xaml(.cs)`

A numeric up/down control. Wire via `ValueChanged` event.

#### NumberRange
**File:** `Controls/Input/NumberRange.xaml(.cs)`

```csharp
var nr = new NumberRange
{
    MinLabel = "Min height",
    MaxLabel = "Max height",
    Unit     = "mm",
    Step     = 50,
    AbsMin   = 0,
    AbsMax   = 5000,
};
nr.SetValues(500, 2500);
nr.RangeChanged += (min, max) => { _min = min; _max = max; };
```

#### TagChipInput
**File:** `Controls/Input/TagChipInput.xaml(.cs)`

Tag/chip text input for free-form multi-value entry.

#### InlineEdit
**File:** `Controls/Input/InlineEdit.xaml(.cs)`

Inline text that flips to an edit TextBox on click.

#### DateField
**File:** `Controls/Input/DateField.xaml(.cs)`

Single or range date picker. Mode set via `DateOpts.Mode`.

### 8.2 Color controls

#### ColorPickerWindow / ColorPickerPanel
**File:** `Controls/Color/`

For inline settings use the factory:
```csharp
var swatch = ColorPickerWindow.BuildColorPickerSwatch(
    getHex: () => currentHex,
    setHex: h => { currentHex = h; ApplySettings(groupId, settingId, h); });
```

#### SwatchPicker / SwatchGlyph / EyeGlyph
Supporting controls for the color picker UI.

### 8.3 Layout controls

#### SectionCard
**File:** `Controls/Layout/SectionCard.xaml(.cs)`

A collapsible card with a header and content area. Used inside `GlobalSettingsWindow`.

#### ReviewSummary
**File:** `Controls/Layout/ReviewSummary.xaml(.cs)`

A summary display for the final "Review & Run" step.

#### TitleBar
**File:** `Controls/Layout/TitleBar.xaml(.cs)`

```csharp
new TitleBar
{
    Title          = _tool.Title,
    AllowsMaximize = true,         // double-click header = maximize/restore
    RightContent   = myStackPanel, // any UIElement placed right of title
    // IconGlyph = "⚙"            // optional glyph left of title (reserved for future SVG)
}
```

Handles drag-to-move and double-click maximize internally.

#### WarnBanner
**File:** `Controls/Layout/WarnBanner.cs`

A warning banner using `LemoineWarnBg/Border/Text` semantic colour aliases.

#### FileBrowser
(see §8.1 — also a layout control housing the path input + browse button)

### 8.4 Legend controls

All in `Controls/Legend/`. Used by AutoFilters legend builder:

- `LegendBuilder` — full legend construction UI
- `LegendGroupCard` — one legend group
- `LegendRow` / `LegendBlockRow` — individual legend rows
- `LegendPalette` — colour palette picker for the legend
- `LegendPreview` — rendered preview of the legend
- `LegendLayoutBar` — layout toolbar

### 8.5 Other controls

#### CategoryChip
**File:** `Controls/CategoryChip.xaml(.cs)`

A small coloured chip/badge for displaying category labels.

### 8.6 ControlStyles
**File:** `Source/Framework/ControlStyles.cs`

Injects themed ControlTemplate overrides for `ScrollBar`, `ComboBox`, and `CheckBox` via `XamlReader.Parse`. Called once by `StepFlowWindow.InjectControlStyles()` with `scrollBarWidth: 5`.

Also exposes `BuildFlatButtonTemplate()` — the template used for all `Button` instances in the UI.

---

## 9. How to Add a New Tool — Step by Step

### Step 1 — Create the ViewModel

Copy `Source/Tools/Ceilings/ProjectedCeilingGridsViewModel.cs` to your new folder, e.g.:

`Source/Tools/Views/MyFeatureViewModel.cs`

```csharp
public class MyFeatureViewModel : IStepFlowTool  // add IToolSettings if needed
{
    public string Title    => "My Feature";
    public string RunLabel => "Run in Revit →";

    public StepDefinition[] Steps => new[]
    {
        new StepDefinition("S1", "Pick a file",     required: true),
        new StepDefinition("S2", "Choose options",  required: true),
        new StepDefinition("S3", "Review & Run",    required: false),
    };

    // State
    private string _path = "";
    private string _option = "Default";

    // Validation change
    public event EventHandler? ValidationChanged;
    private void OnValidationChanged() => ValidationChanged?.Invoke(this, EventArgs.Empty);

    // ExternalEvent wiring
    private readonly MyEventHandler _handler;
    private readonly Autodesk.Revit.UI.ExternalEvent _event;
    public MyFeatureViewModel(MyEventHandler handler, Autodesk.Revit.UI.ExternalEvent ev)
    { _handler = handler; _event = ev; }

    public FrameworkElement? GetStepContent(string stepId)
    {
        if (stepId == "S1")
        {
            var fb = new FileBrowser { Label = "Select your file.", Filter = "DWG|*.dwg" };
            fb.PathChanged += p => { _path = p ?? ""; OnValidationChanged(); };
            return fb;
        }
        if (stepId == "S2")
        {
            var sel = new SingleSelect { Label = "Mode", Items = new List<string> { "Default", "Advanced" } };
            sel.SelectionChanged += v => { _option = v ?? "Default"; OnValidationChanged(); };
            return sel;
        }
        if (stepId == "S3")
        {
            var tb = new TextBlock { Text = "Click Run to execute.", TextWrapping = TextWrapping.Wrap };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineTextDim");
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "LemoineUiFont");
            return tb;
        }
        return null;
    }

    public bool IsValid(string stepId)
    {
        if (stepId == "S1") return !string.IsNullOrWhiteSpace(_path);
        if (stepId == "S2") return !string.IsNullOrWhiteSpace(_option);
        return true;
    }

    public string SummaryFor(string stepId)
    {
        if (stepId == "S1") return string.IsNullOrEmpty(_path) ? "—" : Path.GetFileName(_path);
        if (stepId == "S2") return _option;
        return "Ready to run";
    }

    public void Run(Action<string,string> pushLog, Action<int,int,int,int> onProgress, Action<int,int,int> onComplete)
    {
        _handler.Path       = _path;
        _handler.Option     = _option;
        _handler.PushLog    = pushLog;
        _handler.OnProgress = onProgress;
        _handler.OnComplete = onComplete;
        pushLog("Raising ExternalEvent…", "info");
        _event.Raise();
    }
}
```

### Step 2 — Create the ExternalEventHandler

`Source/Tools/Views/MyEventHandler.cs`

```csharp
public class MyEventHandler : IExternalEventHandler
{
    public string  Path       { get; set; } = "";
    public string  Option     { get; set; } = "";
    public Action<string, string>?     PushLog    { get; set; }
    public Action<int, int, int, int>? OnProgress { get; set; }
    public Action<int, int, int>?      OnComplete { get; set; }

    public string GetName() => "MyFeature";

    public void Execute(UIApplication app)
    {
        int pass = 0, fail = 0, skip = 0;
        try
        {
            // … Revit API work …
            PushLog?.Invoke("Done!", "pass");
            pass++;
            OnProgress?.Invoke(100, pass, fail, skip);
        }
        catch (Exception ex)
        {
            PushLog?.Invoke($"Error: {ex.Message}", "fail");
            fail++;
        }
        OnComplete?.Invoke(pass, fail, skip);
    }
}
```

### Step 3 — Create the Command class

`Source/Commands/Views/MyFeatureCommand.cs`

```csharp
[Transaction(TransactionMode.Manual)]
public class MyFeatureCommand : IExternalCommand
{
    // Created once in App.cs, stored statically
    internal static MyEventHandler   Handler;
    internal static ExternalEvent    Event;

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var win = new StepFlowWindow(new MyFeatureViewModel(Handler, Event));
        win.Show();
        return Result.Succeeded;
    }
}
```

### Step 4 — Register in App.cs

```csharp
// In App.cs, inside CreateRibbonPanel() or equivalent:

MyFeatureCommand.Handler = new MyEventHandler();
MyFeatureCommand.Event   = ExternalEvent.Create(MyFeatureCommand.Handler);

var btn = new PushButtonData(
    "MyFeature",
    "My\nFeature",
    Assembly.GetExecutingAssembly().Location,
    typeof(MyFeatureCommand).FullName);
// btn.LargeImage = …
panel.AddItem(btn);
```

### Step 5 (optional) — Add settings

Implement `IToolSettings` on your ViewModel. Return a `ToolSettingsSpec` from `GetSettingsSpec()`, write back in `ApplySettings()`.

---

## 10. File & Folder Map

```
Source/
├── App.cs                              Ribbon registration, ExternalEvent creation
├── Commands/                           ← mirrors Tools/ below, one command per ribbon button
│   ├── OpenSettingsCommand.cs          Opens GlobalSettingsWindow
│   ├── OpenOverviewCommand.cs          Opens ToolsOverviewWindow
│   ├── Setup/
│   ├── CopyFromLink/
│   ├── Modify/
│   ├── Ceilings/
│   ├── Views/
│   ├── FiltersLegends/
│   ├── Dimensioning/
│   ├── Sheets/
│   ├── Export/
│   └── Debuggers/
├── Framework/                           ← UI FRAMEWORK (never edit unless changing the framework)
│   ├── IStepFlowTool.cs                 Tool contract + StepDefinition
│   ├── IToolSettings.cs         Optional persistent settings contract
│   ├── ThemePalette.cs                 All themes + design tokens
│   ├── AppSettings.cs              Singleton: active theme, scale, persistence
│   ├── ToolSettingsSpec.cs      Declarative settings data model
│   ├── ControlStyles.cs         ScrollBar / ComboBox / CheckBox templates
│   ├── GlobalSettingsWindow.xaml(.cs)  Global settings (theme, UI size, all tools)
│   ├── GlobalSettingsWindow.*.cs       Cross-cutting partial classes for GlobalSettingsWindow
│   ├── ToolsOverviewWindow.xaml(.cs)   Read-only tools guide (NOT StepFlowWindow)
│   ├── StepFlowWindow.xaml(.cs)        ← THE MAIN TOOL WINDOW (never subclass)
│   ├── RelayCommand.cs                 ICommand helper for MVVM bindings
│   ├── BrushHelper.cs                  Brush utility helpers
│   ├── Templates/
│   │   └── TemplateStore.cs     Template storage/retrieval
│   └── Controls/
│       ├── Color/                      Color picker controls
│       ├── Input/                      SingleSelect, ToggleSwitches,
│       │                               MultiSelectTabs, SearchAutocomplete,
│       │                               NumberRange, TagChipInput,
│       │                               InlineEdit, InlineStepper,
│       │                               DateField, TokenInput
│       ├── Layout/                     TitleBar, SectionCard,
│       │                               FileBrowser, ReviewSummary,
│       │                               WarnBanner
│       ├── Legend/                     Legend builder controls (AutoFilters tool)
│       └── CategoryChip.xaml    Category badge chip
├── Tools/                              ← TOOL VIEWMODELS + EVENT HANDLERS, one folder per ribbon panel
│   ├── Setup/                          Upgrade Links, Link Audit, Align/Compare/Push Coordinates
│   │   └── Windows/                    LinkAuditWindow (standalone report — not StepFlowWindow)
│   ├── CopyFromLink/                   Copy Datums, Copy Linear, Copy Elements
│   ├── Modify/                         Split Elements, Extend Walls
│   ├── Ceilings/                       Ceiling Heatmap, Ceiling Grids
│   │   └── Windows/                    GlobalSettingsWindow partial for the Ceilings tab
│   ├── Views/                          Bulk Views by Level, Duplicate Views
│   │   ├── Windows/                    GlobalSettingsWindow partial for the Views tab
│   │   ├── ScopeBoxes/                 Scope Box Creator + Manager
│   │   └── ExplodeViews/               Explode View by Trade
│   ├── FiltersLegends/                 Auto Filters
│   │   ├── Windows/                    FiltersSettingsWindow + GlobalSettingsWindow partial
│   │   └── LegendCreator/              Legend Creation
│   ├── Dimensioning/                   Clash Definitions/Finder/Elevation, Auto-Dimension engine
│   │   └── Windows/                    ClashDefinitionsWindow
│   ├── Sheets/                         Bulk Rename, Place Dependent Views, Align Sheet Views
│   ├── Export/                         Bulk Export, Print View
│   └── Debuggers/                      Debug harnesses (reserved Developer panel button)
└── Helpers/
    └── MepColorMap.cs                  MEP category → colour mapping
```

---

## Quick-reference: "Do / Don't" table

| Do | Don't |
|---|---|
| `SetResourceReference(prop, "LemoineXxx")` for all colours, sizes, fonts | Hard-code `#RRGGBB`, `14.0`, `Segoe UI` |
| Implement `IStepFlowTool` on a plain ViewModel class | Subclass `StepFlowWindow` |
| Fire `ValidationChanged` every time user input changes | Return stale `IsValid` results |
| Call Revit API only inside `IExternalEventHandler.Execute()` | Call Revit API in `Run()` or `GetStepContent()` |
| Use `Dispatcher.BeginInvoke` for UI updates from background threads | Directly update UI from `Execute()` |
| Persist tool settings via a dedicated `YourToolSettings` class | Store mutable state in static fields on the command class |
| Add new themes to `ThemePalette.All[]` | Fork `ThemePalette` into a separate class |
| Use `AppSettings.Instance.S(value)` to scale a raw px value | Multiply by a magic number |
| Build review panels using `LemoineRaised` background cards | Use inline styles or hardcoded backgrounds |
