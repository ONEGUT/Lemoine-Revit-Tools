# Plan: Discover Rules Tool

## Goal

A new **Discover Rules** tool opened from the Revit ribbon. It scans selected loaded links for unique parameter values and proposes colour-coded filter rules from the results. Follows the standard `StepFlowWindow` / `ILemoineTool` pattern. Rules are committed to `AutoFiltersSettings` on Run.

A second new ribbon button — **Filters Settings** — opens `GlobalSettingsWindow` directly on the Filters/Color tab.

---

## Design Decisions (locked)

| Decision | Answer |
|---|---|
| UI pattern | `StepFlowWindow` + `ILemoineTool` (5 steps, accordion) |
| One trade per link | ✓ Trade name = link filename without `.rvt` |
| Category granularity | Main categories only (no subcategories) |
| Noise filter | Show all discovered values, no element-count threshold |
| Scan By source | **Dynamic** — pre-scan discovers which parameters have data for the selected links + categories |
| Color memory key | Raw Revit parameter value (e.g. `"Supply Air"`) |
| Color memory location | `%AppData%\LemoineTools\ColorMemory.xml` (XML, consistent with other settings) |
| Duplicate detection | Flag results where a rule with the same name already exists in the matched trade |
| Re-scan | Show everything; flag duplicates |

---

## Step Flow (5 steps)

| Step | ID | Title | `required` | `IsValid` when | `SummaryFor` |
|---|---|---|---|---|---|
| 1 | `S1` | Select Links | `true` | ≥1 link checked | `"MECH, ARCH"` |
| 2 | `S2` | Select Categories | `true` | ≥1 category checked | `"Ducts, Pipes + 3 more"` |
| 3 | `S3` | Scan by Parameter | `true` | parameter selected | `"System Classification"` |
| 4 | `S4` | Review Rules | `true` | ≥1 rule included | `"18 of 23 rules selected"` |
| 5 | `S5` | Review & Run | `false` | always | `"Ready"` |

### Step interaction detail

**S1 — Select Links**
- Content: scrollable checkbox list. One row per loaded `RevitLinkInstance` in the host document.
- Link list is passed into the VM constructor at window open (queried by `DiscoverLaunchCommand`, same pattern as `LinkViewsLevelCommand`).
- `IsValid("S1")`: `_selectedLinkIds.Count > 0`

**S2 — Select Categories**
- Content: `LemoineMultiSelectTabs` with 4 tabs — **MEP**, **Architectural**, **Structural**, **Other**.
- Tab items are the 20 hardcoded main categories (see table below).
- `IsValid("S2")`: `_selectedCategories.Count > 0`

**S3 — Scan by Parameter**
- `GetStepContent("S3")` fires a **pre-scan** (`DiscoverMode.PreScan`) if the link/category selection has changed since the last pre-scan, or if no pre-scan has run yet.
- While pre-scan runs: spinner + `"Discovering parameters…"`.
- After pre-scan: `LemoineSingleSelect` populated with discovered parameter names. `"Type Name"` and `"Family Name"` pinned to top; remainder sorted alphabetically.
- `IsValid("S3")`: `_selectedParameter != null`

**S4 — Review Rules**
- `GetStepContent("S4")` fires a **main scan** (`DiscoverMode.MainScan`) if the parameter/category/link selection has changed since the last main scan, or if no main scan has run yet.
- While scan runs: spinner + `"Scanning…"`.
- After scan: results list (see layout below).
- `IsValid("S4")`: `_results.Any(r => r.IsIncluded)`

**S5 — Review & Run**
- Content: `LemoineReviewSummary` listing rules grouped by trade (trade name → list of coloured rule chips).
- Run button label: `"Add Rules to Filters →"`
- `Run()` → sets `DiscoverMode.Commit` on handler → raises `App.DiscoverEvent`.

---

## Results List Layout (S4)

```
┌─ Results  23 values found ────────────────── [☑ All] [✕ None] ─┐
│  col:  [■]  Name ──────────────────── Trade    #    ⚠           │
│ ────────────────────────────────────────────────────────────────│
│  ☑  [■]  Supply Air _____________  [MECH]  512                  │
│  ☑  [■]  Return Air _____________  [MECH]  248                  │
│  □  [■]  Exhaust Air ____________  [MECH]   89                  │
│  ☑  [■]  Supply Air _____________  [ARCH]    3   ⚠              │
│  ...  (ScrollViewer)                                            │
└─────────────────────────────────────────────────────────────────┘
```

Each row is a `Grid` with fixed column widths:

| Col | Width | Control |
|---|---|---|
| Checkbox | 28 px | Styled `CheckBox` |
| Colour swatch | 22 px | `LemoineColorPickerWindow.BuildColorPickerSwatch()` |
| Rule name | `*` (flex) | `LemoineInlineEdit` |
| Trade badge | 60 px | `LemoineCategoryChip` (one colour per link) |
| Element count | 44 px | Right-aligned `TextBlock` |
| Duplicate `⚠` | 20 px | `TextBlock` in `LemoineWarnBg`, visible when `IsDuplicate` |

**Row visual treatments:**
- Alternating row tints: `LemoineBg` / `LemoineRaised`
- Duplicate rows: amber `⚠` glyph + dim overall opacity
- Sort: trade name A→Z, then element count desc (keeps same-link values together)
- Sticky header row above scroll (dim `TextBlock` labels for Name, Trade, #)

---

## New Files

### 1. `Source/Tools/T01-AutoFilters/ColorMemory.cs`

Singleton, persists to `%AppData%\LemoineTools\ColorMemory.xml` via `XmlSerializer`.

```csharp
// Storage shape
[XmlRoot("ColorMemory")]
public class ColorMemoryData {
    [XmlArray("Entries"), XmlArrayItem("Entry")]
    public List<ColorMemoryEntry> Entries { get; set; } = new();
}
public class ColorMemoryEntry {
    [XmlAttribute] public string Value { get; set; } = "";
    [XmlAttribute] public string Hex   { get; set; } = "#888888";
}

// Public API
public sealed class ColorMemory {
    public static ColorMemory Instance { get; }   // Lazy singleton
    public bool   TryGetColor(string value, out string hex) { ... }
    public void   SetColor(string value, string hex) { ... }  // saves immediately
}
```

---

### 2. `Source/Tools/T01-AutoFilters/DiscoverViewModel.cs`

Implements `ILemoineTool`. Contains nested types `LinkEntry`, `CategoryEntry`, `CategoryGroupEntry`, `DiscoveredRuleRow`.

```csharp
public class DiscoverViewModel : ILemoineTool
{
    public string Title    => "Discover Rules";
    public string RunLabel => "Add Rules to Filters →";

    public StepDefinition[] Steps => new[]
    {
        new StepDefinition("S1", "Select Links",        required: true),
        new StepDefinition("S2", "Select Categories",   required: true),
        new StepDefinition("S3", "Scan by Parameter",   required: true),
        new StepDefinition("S4", "Review Rules",        required: true),
        new StepDefinition("S5", "Review & Run",        required: false),
    };

    // Injected at construction (from DiscoverLaunchCommand)
    private readonly List<LinkEntry>               _availableLinks;
    private readonly DiscoverEventHandler          _handler;
    private readonly ExternalEvent                 _event;

    // S1 state
    private readonly HashSet<ElementId>            _selectedLinkIds = new();

    // S2 state
    private readonly HashSet<BuiltInCategory>      _selectedCategories = new();

    // S3 state
    private List<string>                           _availableParameters = new();
    private string?                                _selectedParameter;
    private bool                                   _isPreScanning;
    // Tracks what the last pre-scan was run against (to know if re-scan needed)
    private HashSet<ElementId>                     _prescanLinks      = new();
    private HashSet<BuiltInCategory>               _prescanCategories = new();

    // S4 state
    private List<DiscoveredRuleRow>                _results = new();
    private bool                                   _isScanning;
    // Tracks what the last main scan was run against
    private HashSet<ElementId>                     _mainScanLinks      = new();
    private HashSet<BuiltInCategory>               _mainScanCategories = new();
    private string?                                _mainScanParameter;

    // Live S3/S4 UI handles (for spinner ↔ content swap)
    private ContentPresenter? _s3Content;
    private ContentPresenter? _s4Content;
    private Dispatcher?       _dispatcher;
}
```

**Hardcoded category groups:**

| Tab | Category | `BuiltInCategory` |
|---|---|---|
| **MEP** | Ducts | `OST_DuctCurves`, `OST_DuctFitting`, `OST_DuctAccessory`, `OST_DuctTerminal` |
| | Pipes | `OST_PipeCurves`, `OST_PipeFitting`, `OST_PipeAccessory` |
| | Cable Tray | `OST_CableTray`, `OST_CableTrayFitting` |
| | Conduit | `OST_Conduit`, `OST_ConduitFitting` |
| | Mechanical Equipment | `OST_MechanicalEquipment` |
| | Electrical Equipment | `OST_ElectricalEquipment` |
| | Electrical Fixtures | `OST_ElectricalFixtures` |
| | Lighting Fixtures | `OST_LightingFixtures` |
| | Sprinklers | `OST_Sprinklers` |
| | Plumbing Fixtures | `OST_PlumbingFixtures` |
| **Architectural** | Walls | `OST_Walls` |
| | Floors | `OST_Floors` |
| | Ceilings | `OST_Ceilings` |
| | Roofs | `OST_Roofs` |
| | Doors | `OST_Doors` |
| | Windows | `OST_Windows` |
| | Stairs | `OST_Stairs` |
| **Structural** | Structural Framing | `OST_StructuralFraming` |
| | Structural Columns | `OST_StructuralColumns` |
| **Other** | Generic Models | `OST_GenericModel` |

**`CommitSelected()` — called from handler on Commit mode:**
1. For each `DiscoveredRuleRow` where `IsIncluded == true`:
   - Find or create `FilterTradeConfig` in `AutoFiltersSettings.Instance.Trades` by `Label == row.TradeName`. New trade gets a fresh 4-char hex ID.
   - Skip if a rule with `row.RuleName` already exists in that trade (duplicate, don't overwrite).
   - Append new `FilterRuleConfig`: `Name = row.RuleName`, `Parameter = _selectedParameter`, `Match = [row.ParameterValue]`, `MatchType = "equals"`, `BuiltInCategories` from `_selectedCategories`, `CutColor = SurfColor = LineColor = row.HexColor`, `Enabled = true`.
   - `ColorMemory.Instance.SetColor(row.ParameterValue, row.HexColor)`.
2. `AutoFiltersSettings.Instance.Save()`.

---

### 3. `Source/Tools/T01-AutoFilters/DiscoverEventHandler.cs`

Single `IExternalEventHandler` with three modes. One `ExternalEvent` registration in `App.cs` covers all three.

```csharp
public enum DiscoverMode { PreScan, MainScan, Commit }

public class DiscoverEventHandler : IExternalEventHandler
{
    // Inputs — set before Raise()
    public DiscoverMode              Mode               { get; set; }
    public List<ElementId>           SelectedLinkIds    { get; set; } = new();
    public List<BuiltInCategory>     SelectedCategories { get; set; } = new();
    public string?                   ParameterName      { get; set; }   // MainScan + Commit
    public DiscoverViewModel?        TargetVm           { get; set; }

    // Callbacks — set before Raise()
    public Action<string, string>?          PushLog    { get; set; }
    public Action<int, int, int, int>?      OnProgress { get; set; }
    public Action<int, int, int>?           OnComplete { get; set; }
}
```

#### PreScan mode

Triggered from `GetStepContent("S3")` when link/category selection has changed.

1. For each selected link → `linkedDoc` via `RevitLinkInstance`.
2. For each selected category → sample **up to 200 elements** (`FilteredElementCollector.Take(200)`).
3. For each sampled element → iterate `element.Parameters`:
   - Keep if `StorageType == String`, value non-empty, name not in blocklist (`"Image"`, `"Edited by"`, `"URL"`, `"Phase Created"`, `"Phase Demolished"`).
4. Always prepend synthetic `"Type Name"` and `"Family Name"` (pinned).
5. Collect union → sort alphabetically → dispatch to `TargetVm.SetAvailableParameters(names)`.

#### MainScan mode

Triggered from `GetStepContent("S4")` when any scan input has changed.

1. Full `FilteredElementCollector` (no sampling) for selected links + categories.
2. For each element: `ReadParameterValue(el, ParameterName, linkedDoc)`.
3. Group by `(tradeName, paramValue)` → count.
4. For each group → `DiscoveredRuleRow`:
   - `HexColor`: `ColorMemory.TryGetColor(v, out h) ? h : _palette[index % 20]`
   - `IsDuplicate`: check `AutoFiltersSettings.Instance.Trades`
5. Sort: tradeName A→Z, elementCount desc.
6. Dispatch `TargetVm.SetResults(rows)`.

#### Commit mode

Triggered by `Run()`.

1. Calls `TargetVm.CommitSelected()` (pure in-memory + XML write — no Revit API access needed).
2. Logs summary via `PushLog`.
3. Calls `OnComplete`.

#### `ReadParameterValue` helper

```csharp
private static string? ReadParameterValue(Element el, string paramName, Document doc)
{
    if (paramName == "Type Name")
        return (doc.GetElement(el.GetTypeId()) as ElementType)?.Name;
    if (paramName == "Family Name")
        return (doc.GetElement(el.GetTypeId()) as FamilySymbol)?.FamilyName;
    var p = el.LookupParameter(paramName);
    return p?.StorageType == StorageType.String ? p.AsString() : null;
}
```

#### Auto-colour palette

20-entry array of distinct, theme-readable hex colours, cycled by insertion order. Deterministic — same value always gets the same slot on re-scan if not in `ColorMemory`.

---

### 4. `Source/Commands/T01-AutoFilters/DiscoverLaunchCommand.cs`

```csharp
[Transaction(TransactionMode.ReadOnly)]
public class DiscoverLaunchCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var doc = commandData.Application.ActiveUIDocument?.Document;
        if (doc == null) { message = "No active document."; return Result.Failed; }

        // Collect loaded links (same pattern as LinkViewsLevelCommand)
        var links = new List<DiscoverViewModel.LinkEntry>();
        foreach (var li in new FilteredElementCollector(doc)
            .OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>()
            .Where(l => l.GetLinkDocument() != null))
        {
            var ld = li.GetLinkDocument();
            links.Add(new DiscoverViewModel.LinkEntry
            {
                Id        = li.Id,
                Label     = ld.Title ?? li.Name,
                TradeName = Path.GetFileNameWithoutExtension(ld.Title ?? li.Name),
            });
        }

        if (links.Count == 0)
        {
            TaskDialog.Show("Discover Rules", "No loaded Revit links found in the current document.");
            return Result.Succeeded;
        }

        var vm = new DiscoverViewModel(links, App.DiscoverHandler, App.DiscoverEvent);
        new StepFlowWindow(vm).Show();
        return Result.Succeeded;
    }
}
```

---

### 5. `Source/Commands/T01-AutoFilters/OpenFiltersSettingsCommand.cs`

```csharp
[Transaction(TransactionMode.ReadOnly)]
public class OpenFiltersSettingsCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var doc = commandData.Application.ActiveUIDocument?.Document;

        // Same setup as OpenSettingsCommand (fill/line patterns + category cache)
        var fillNames = new List<string>();
        var lineNames = new List<string> { "Solid" };
        if (doc != null)
        {
            fillNames.AddRange(new FilteredElementCollector(doc)
                .OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
                .Select(fp => fp.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
            lineNames.AddRange(new FilteredElementCollector(doc)
                .OfClass(typeof(LinePatternElement)).Cast<LinePatternElement>()
                .Select(lp => lp.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
            AutoFiltersSettings.PopulateCategoryCache(doc);
        }

        if (App.GlobalSettings != null && App.GlobalSettings.IsVisible)
        {
            App.GlobalSettings.SetPatternLists(fillNames, lineNames);
            App.GlobalSettings.ActivateTab("filters");
            App.GlobalSettings.Activate();
            return Result.Succeeded;
        }

        App.GlobalSettings = new GlobalSettingsWindow();
        App.GlobalSettings.SetPatternLists(fillNames, lineNames);
        App.GlobalSettings.Closed += (s, e) => App.GlobalSettings = null;
        App.GlobalSettings.ActivateTab("filters");   // ← jump straight to Filters tab
        App.GlobalSettings.Show();
        return Result.Succeeded;
    }
}
```

---

## Modified Files

### `Source/App.cs`

**Add statics** after the Auto Filters suite:
```csharp
internal static DiscoverEventHandler? DiscoverHandler { get; private set; }
internal static ExternalEvent?        DiscoverEvent   { get; private set; }
```

**Register in `OnStartup`:**
```csharp
DiscoverHandler = new DiscoverEventHandler();
DiscoverEvent   = ExternalEvent.Create(DiscoverHandler);
```

**Add ribbon buttons** to `filtersPanel` (after the existing stacked items):
```csharp
filtersPanel.AddItem(Btn(
    "LT_DiscoverRules", "Discover\nRules", "DiscoverLaunchCommand",
    "Scan loaded links for unique parameter values and propose colour-coded filter rules."));

filtersPanel.AddItem(Btn(
    "LT_FiltersSettings", "Filters\nSettings", "OpenFiltersSettingsCommand",
    "Open the Filters / Color settings panel."));
```

---

### `Source/Lemoine/GlobalSettingsWindow.xaml.cs`

Add one internal method so `OpenFiltersSettingsCommand` can jump to a specific tab:

```csharp
internal void ActivateTab(string tabId)
{
    if (_activeTabId != tabId)
        SwitchTab(tabId);
}
```

---

## File Summary

| Action | File |
|---|---|
| **Create** | `Source/Tools/T01-AutoFilters/ColorMemory.cs` |
| **Create** | `Source/Tools/T01-AutoFilters/DiscoverViewModel.cs` |
| **Create** | `Source/Tools/T01-AutoFilters/DiscoverEventHandler.cs` |
| **Create** | `Source/Commands/T01-AutoFilters/DiscoverLaunchCommand.cs` |
| **Create** | `Source/Commands/T01-AutoFilters/OpenFiltersSettingsCommand.cs` |
| **Modify** | `Source/App.cs` |
| **Modify** | `Source/Lemoine/GlobalSettingsWindow.xaml.cs` |

> `GlobalSettingsWindow.Discover.cs` from the previous plan is **removed** — no longer needed.

---

## Implementation Order

1. `ColorMemory.cs` — no dependencies
2. `DiscoverEventHandler.cs` — depends on `ColorMemory`, `AutoFiltersSettings`, `DiscoverViewModel` (forward ref only)
3. `DiscoverViewModel.cs` — depends on `ColorMemory`, `AutoFiltersSettings`, `DiscoverEventHandler`
4. `DiscoverLaunchCommand.cs` — depends on `DiscoverViewModel`
5. `OpenFiltersSettingsCommand.cs` — depends on `GlobalSettingsWindow.ActivateTab`
6. `GlobalSettingsWindow.xaml.cs` patch — add `ActivateTab()`
7. `App.cs` patch — register handler + event + ribbon buttons

---

## Out of Scope

- Editing or deleting color memory entries
- Merging rules across links
- Subcategory filtering
- Auto-running Auto Filters after Discover commits rules
