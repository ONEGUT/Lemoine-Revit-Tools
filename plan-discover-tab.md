# Plan: Discover Rules Tool

## Goal

A new **Discover Rules** tool opened from the Revit ribbon. It scans selected loaded links for unique parameter values and proposes colour-coded filter rules from the results. Follows the standard `StepFlowWindow` / `ILemoineTool` pattern. Rules are committed to `AutoFiltersSettings` on Run.

A second new ribbon button — **Filters Settings** — opens `GlobalSettingsWindow` directly on the Filters/Color tab.

---

## Relationship to Existing Tools

| Tool | Role |
|---|---|
| **Discover Rules** *(new)* | Scan links → populate rules into `AutoFiltersSettings` |
| **Filters/Color tab** | Manually manage those rules |
| **Auto Filters** | Read rules → create Revit `ParameterFilterElement` objects → apply to current view |
| **Apply Filters to Views** | Batch-apply already-created filter elements to other views |

Discover slots in at the top of the chain. Auto Filters and Apply Filters to Views are **not** obsolete.

---

## Design Decisions (locked)

| Decision | Answer |
|---|---|
| UI pattern | `StepFlowWindow` + `ILemoineTool` (5 steps, accordion) |
| One trade per link | ✓ Trade name = link filename without `.rvt` |
| Category granularity | Main categories only (no subcategories) |
| Noise filter | Show all discovered values, no element-count threshold |
| Scan By source | **Dynamic per category** — pre-scan in S3 discovers which parameters have data for each category independently |
| Category grouping | Configured in S3: user assigns the same group name to multiple categories; grouped categories are scanned together and produce rules sharing the same `BuiltInCategories` list |
| Whole-category mode | Per row in S3: toggle between "Per value" (scan a parameter) and "Whole category" (one rule for all elements, no parameter filter) |
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
| 3 | `S3` | Configure Scan | `true` | all Per-value rows have a parameter selected | `"4 configs, 1 group"` |
| 4 | `S4` | Review Rules | `true` | ≥1 rule included | `"18 of 23 rules selected"` |
| 5 | `S5` | Review & Run | `false` | always | `"Ready"` |

---

## Step Detail

### S1 — Select Links
- Scrollable checkbox list of loaded `RevitLinkInstance` objects, queried by `DiscoverLaunchCommand` and passed into the VM constructor.
- `IsValid("S1")`: `_selectedLinkIds.Count > 0`
- `SummaryFor("S1")`: trade names joined, e.g. `"MECH, ARCH"`

---

### S2 — Select Categories
- `LemoineMultiSelectTabs` with 4 tabs: **MEP**, **Architectural**, **Structural**, **Other**.
- Selecting/deselecting categories here adds/removes rows from S3's configuration table.
- `IsValid("S2")`: `_selectedCategories.Count > 0`
- `SummaryFor("S2")`: first 2 names + `"+ N more"` if needed.

---

### S3 — Configure Scan

The most complex step. Shows a **per-row configuration table** — one row per selected category (from S2).

**Columns:**

| Col | Width | Content |
|---|---|---|
| Category label | ~200 px | `TextBlock` (non-editable) |
| Parameter | ~260 px | `LemoineSingleSelect` (dynamic, from pre-scan); disabled when mode = Whole category |
| Mode | ~200 px | Two `RadioButton`-style pills: **Per value** / **Whole category** |
| Group | ~160 px | `LemoineInlineEdit` — optional free-text group name |

**Pre-scan behaviour:**
- Fires automatically (`DiscoverMode.PreScan`) when S3 content is first built, or when link/category selection changed since the last pre-scan.
- Returns `Dictionary<BuiltInCategory, List<string>>` — available string-valued parameters per category, with `"Type Name"` and `"Family Name"` always prepended.
- Each row's parameter dropdown is populated with that category's result.
- A small spinner replaces the parameter dropdown per row while its pre-scan is in flight.
- When the user assigns two rows the same group name, the pre-scan for that merged group re-runs (uses the union of categories to find shared parameters).

**Grouping logic:**
- Rows sharing the same non-empty group name are visually bracketed (a left-border accent + shared background tint).
- When grouped, all rows in the group must use the same parameter (or all be Whole-category). If they diverge, a `LemoineWarnBanner` appears: *"All categories in a group must use the same scan mode."*
- Groups can mix: e.g. Ducts (System Classification) and Pipes (System Classification) in group "HVAC" — legal. Ducts (System Classification) and Walls (Type Name) in the same group — flagged as invalid.

**Effect on scan results (S4):**

| Mode | Grouped? | Result |
|---|---|---|
| Per value | No | One `DiscoveredRuleRow` per unique parameter value found in that single category |
| Per value | Yes | One `DiscoveredRuleRow` per unique value found across ALL categories in the group; the row's `BuiltInCategories` covers the whole group |
| Whole category | No | One `DiscoveredRuleRow` for the category; default name = category label; `MatchType = "all"` |
| Whole category | Yes | One `DiscoveredRuleRow` for the group; default name = group name; `MatchType = "all"`; `BuiltInCategories` covers all grouped categories |

**`IsValid("S3")`:** All Per-value rows (or groups) have a parameter selected, and no group has a mode conflict.

**`SummaryFor("S3")`:** e.g. `"5 configs — 1 group (HVAC), 1 whole-category"`

---

### S4 — Review Rules

Main scan (`DiscoverMode.MainScan`) auto-fires when the step opens, or when any S1–S3 input changed since the last scan. Shows a spinner while running.

**Results list:**

```
┌─ Results  23 values found ─────────────────── [☑ All] [✕ None] ─┐
│  [■]  Name ──────────────────────── Trade    #    ⚠              │
│ ─────────────────────────────────────────────────────────────────│
│  ☑  [■]  Supply Air _____________  [MECH]  512                   │
│  ☑  [■]  Return Air _____________  [MECH]  248                   │
│  □  [■]  Exhaust Air ____________  [MECH]   89                   │
│  ☑  [■]  Supply Air _____________  [ARCH]    3   ⚠               │
│  ☑  [■]  Walls __________________ [MECH]  1042  (whole-cat)      │
│  ...  (ScrollViewer)                                              │
└──────────────────────────────────────────────────────────────────┘
```

Each row is a `Grid` with fixed column widths:

| Col | Width | Control |
|---|---|---|
| Checkbox | 28 px | Styled `CheckBox` |
| Colour swatch | 22 px | `LemoineColorPickerWindow.BuildColorPickerSwatch()` |
| Rule name | `*` (flex) | `LemoineInlineEdit` |
| Trade badge | 60 px | `LemoineCategoryChip` (one colour per link) |
| Element count | 44 px | Right-aligned `TextBlock` |
| Whole-cat badge | 56 px | `TextBlock` `"(whole-cat)"` in dim italic — visible for whole-category rows |
| Duplicate `⚠` | 20 px | Amber `TextBlock`, visible when `IsDuplicate` |

**Row visual treatments:**
- Alternating row tints: `LemoineBg` / `LemoineRaised`
- Duplicate rows: amber `⚠` + slightly dimmed opacity
- Sort: trade name A→Z, then element count desc

`IsValid("S4")`: `_results.Any(r => r.IsIncluded)`
`SummaryFor("S4")`: `"18 of 23 rules selected"`

---

### S5 — Review & Run
- `LemoineReviewSummary` grouping rules by trade (trade name → coloured rule chips).
- Run button label: `"Add Rules to Filters →"`
- `Run()` → `DiscoverMode.Commit` → commits rules + ColorMemory → calls `OnComplete`.

---

## `ScanConfigRow` — the S3 data model

```csharp
public class ScanConfigRow
{
    public List<BuiltInCategory>  Categories         { get; set; }  // 1-N after grouping
    public List<string>           CategoryLabels     { get; set; }  // display names
    public ScanMode               Mode               { get; set; }  // PerValue | WholeCategory
    public string?                Parameter          { get; set; }  // null when WholeCategory
    public string?                GroupName          { get; set; }  // null = not grouped
    public List<string>           AvailableParameters { get; set; } // from pre-scan
    public bool                   IsPreScanning      { get; set; }
}

public enum ScanMode { PerValue, WholeCategory }
```

When the user types a group name matching an existing row, those two rows are **merged** in `_scanConfig` (categories combined, `AvailableParameters` = intersection, pre-scan re-fires for the merged group). Un-typing clears the merge.

---

## New Files

### 1. `Source/Tools/T01-AutoFilters/ColorMemory.cs`

Singleton, persists to `%AppData%\LemoineTools\ColorMemory.xml` via `XmlSerializer`.

```csharp
[XmlRoot("ColorMemory")]
public class ColorMemoryData {
    [XmlArray("Entries"), XmlArrayItem("Entry")]
    public List<ColorMemoryEntry> Entries { get; set; } = new();
}
public class ColorMemoryEntry {
    [XmlAttribute] public string Value { get; set; } = "";
    [XmlAttribute] public string Hex   { get; set; } = "#888888";
}

public sealed class ColorMemory {
    public static ColorMemory Instance { get; }   // Lazy singleton
    public bool   TryGetColor(string value, out string hex) { ... }
    public void   SetColor(string value, string hex) { ... }
}
```

---

### 2. `Source/Tools/T01-AutoFilters/DiscoverViewModel.cs`

Implements `ILemoineTool`. Contains nested types: `LinkEntry`, `ScanConfigRow`, `DiscoveredRuleRow`.

**Key state:**

```csharp
// S1
private readonly List<LinkEntry>          _availableLinks;
private readonly HashSet<ElementId>       _selectedLinkIds = new();

// S2
private readonly HashSet<BuiltInCategory> _selectedCategories = new();

// S3 — one entry per category or merged group
private List<ScanConfigRow>               _scanConfig = new();
private HashSet<ElementId>                _prescanLinks      = new();
private HashSet<BuiltInCategory>          _prescanCategories = new();

// S4
private List<DiscoveredRuleRow>           _results = new();
private List<ScanConfigRow>               _mainScanConfig;  // snapshot at scan time
private HashSet<ElementId>                _mainScanLinkIds;

// Live UI handles
private StackPanel?   _s3Table;       // rebuilt when categories change
private ContentPresenter? _s4Content; // swaps spinner ↔ results
private Dispatcher?   _dispatcher;
```

**`DiscoveredRuleRow`:**

```csharp
public class DiscoveredRuleRow : INotifyPropertyChanged
{
    public bool                   IsIncluded       { get; set; } = true;
    public string                 ParameterValue   { get; set; }  // color memory key; null for whole-cat
    public string                 RuleName         { get; set; }  // editable
    public string                 HexColor         { get; set; }
    public int                    ElementCount     { get; set; }
    public bool                   IsDuplicate      { get; set; }
    public bool                   IsWholeCategory  { get; set; }
    public string                 TradeName        { get; set; }
    public ElementId              LinkId           { get; set; }
    public List<BuiltInCategory>  BuiltInCategories { get; set; } // from ScanConfigRow
    public string?                ScanParameter    { get; set; }  // which param was scanned
}
```

**`CommitSelected()`:**
1. For each `DiscoveredRuleRow` where `IsIncluded == true`:
   - Find or create `FilterTradeConfig` in `AutoFiltersSettings.Instance.Trades` by `Label == row.TradeName`.
   - Skip if a rule with `row.RuleName` already exists in that trade.
   - Build `FilterRuleConfig`:
     - `Name = row.RuleName`
     - `Parameter = row.ScanParameter ?? ""`
     - `Match = row.IsWholeCategory ? [] : [row.ParameterValue]`
     - `MatchType = row.IsWholeCategory ? "all" : "equals"`
     - `BuiltInCategories` from `row.BuiltInCategories` (OST_ strings)
     - `CutColor = SurfColor = LineColor = row.HexColor`
     - `Enabled = true`
   - `ColorMemory.Instance.SetColor(row.ParameterValue, row.HexColor)` (skip if whole-cat).
2. `AutoFiltersSettings.Instance.Save()`.

---

### 3. `Source/Tools/T01-AutoFilters/DiscoverEventHandler.cs`

Three modes in one handler.

```csharp
public enum DiscoverMode { PreScan, MainScan, Commit }

public class DiscoverEventHandler : IExternalEventHandler
{
    public DiscoverMode              Mode               { get; set; }
    public List<ElementId>           SelectedLinkIds    { get; set; } = new();
    // PreScan:
    public List<ScanConfigRow>       ScanConfig         { get; set; } = new();
    // MainScan: uses ScanConfig + SelectedLinkIds
    public DiscoverViewModel?        TargetVm           { get; set; }
    public Action<string, string>?   PushLog            { get; set; }
    public Action<int,int,int,int>?  OnProgress         { get; set; }
    public Action<int,int,int>?      OnComplete         { get; set; }
}
```

#### PreScan mode

For each `ScanConfigRow` (which may cover N categories after grouping):
1. Sample up to **200 elements** per category per link.
2. Collect union of string-valued parameter names with non-empty values.
3. Always prepend `"Type Name"` and `"Family Name"`.
4. Dispatch: `TargetVm.SetPrescanResult(row, parameterNames)` per row.

#### MainScan mode

For each `ScanConfigRow`:

**If `Mode == WholeCategory`:**
- Query all elements in `row.Categories` across selected links.
- Group by link → one `DiscoveredRuleRow` per link with `ElementCount = total`, `RuleName = row.GroupName ?? row.CategoryLabels[0]`, `IsWholeCategory = true`.

**If `Mode == PerValue`:**
- Query all elements in `row.Categories` across selected links.
- `ReadParameterValue(el, row.Parameter, linkedDoc)` per element.
- Group by `(tradeName, paramValue)` → count.
- One `DiscoveredRuleRow` per group.

For all rows:
- `HexColor`: `ColorMemory.TryGetColor(v, out h) ? h : _palette[...]`
- `IsDuplicate`: check `AutoFiltersSettings.Instance`
- `BuiltInCategories = row.Categories`
- `ScanParameter = row.Parameter`

Sort: tradeName A→Z, elementCount desc.
Dispatch: `TargetVm.SetResults(rows)`.

#### Commit mode

Calls `TargetVm.CommitSelected()`, logs summary, calls `OnComplete`.

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

20-entry array of distinct, theme-readable hex colours. Cycle by insertion order. Deterministic on re-scan.

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

        var links = new List<DiscoverViewModel.LinkEntry>();
        foreach (var li in new FilteredElementCollector(doc)
            .OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>()
            .Where(l => l.GetLinkDocument() != null))
        {
            var ld = li.GetLinkDocument();
            links.Add(new DiscoverViewModel.LinkEntry {
                Id        = li.Id,
                Label     = ld.Title ?? li.Name,
                TradeName = Path.GetFileNameWithoutExtension(ld.Title ?? li.Name),
            });
        }

        if (links.Count == 0) {
            TaskDialog.Show("Discover Rules", "No loaded Revit links found.");
            return Result.Succeeded;
        }

        var vm = new DiscoverViewModel(links, App.DiscoverHandler!, App.DiscoverEvent!);
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
        var fillNames = new List<string>();
        var lineNames = new List<string> { "Solid" };
        if (doc != null) {
            fillNames.AddRange(new FilteredElementCollector(doc)
                .OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
                .Select(fp => fp.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
            lineNames.AddRange(new FilteredElementCollector(doc)
                .OfClass(typeof(LinePatternElement)).Cast<LinePatternElement>()
                .Select(lp => lp.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
            AutoFiltersSettings.PopulateCategoryCache(doc);
        }

        if (App.GlobalSettings != null && App.GlobalSettings.IsVisible) {
            App.GlobalSettings.SetPatternLists(fillNames, lineNames);
            App.GlobalSettings.ActivateTab("filters");
            App.GlobalSettings.Activate();
            return Result.Succeeded;
        }

        App.GlobalSettings = new GlobalSettingsWindow();
        App.GlobalSettings.SetPatternLists(fillNames, lineNames);
        App.GlobalSettings.Closed += (s, e) => App.GlobalSettings = null;
        App.GlobalSettings.ActivateTab("filters");
        App.GlobalSettings.Show();
        return Result.Succeeded;
    }
}
```

---

## Modified Files

### `Source/App.cs`

Add statics + register + ribbon buttons:

```csharp
internal static DiscoverEventHandler? DiscoverHandler { get; private set; }
internal static ExternalEvent?        DiscoverEvent   { get; private set; }

// In OnStartup:
DiscoverHandler = new DiscoverEventHandler();
DiscoverEvent   = ExternalEvent.Create(DiscoverHandler);

// Ribbon (added to existing filtersPanel):
filtersPanel.AddItem(Btn(
    "LT_DiscoverRules", "Discover\nRules", "DiscoverLaunchCommand",
    "Scan loaded links for unique parameter values and propose colour-coded filter rules."));

filtersPanel.AddItem(Btn(
    "LT_FiltersSettings", "Filters\nSettings", "OpenFiltersSettingsCommand",
    "Open the Filters / Color settings panel."));
```

---

### `Source/Lemoine/GlobalSettingsWindow.xaml.cs`

One new internal method:

```csharp
internal void ActivateTab(string tabId)
{
    if (_activeTabId != tabId)
        SwitchTab(tabId);
}
```

---

## Hardcoded Category Groups (S2)

| Tab | Row label | `BuiltInCategory` values |
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

---

## Implementation Order

1. `ColorMemory.cs`
2. `DiscoverEventHandler.cs` (forward-ref `DiscoverViewModel`)
3. `DiscoverViewModel.cs`
4. `DiscoverLaunchCommand.cs`
5. `OpenFiltersSettingsCommand.cs`
6. `GlobalSettingsWindow.xaml.cs` patch (`ActivateTab`)
7. `App.cs` patch (handler + event + ribbon)

---

## Out of Scope

- Editing or deleting color memory entries
- Auto-running Auto Filters after Discover commits rules
- Sub-categories
