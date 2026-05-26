# Plan: Discover Tab

## Goal

Add a **Discover** tab to `GlobalSettingsWindow` that scans selected Revit links for unique parameter values and proposes colour-coded filter rules from the results. One trade per link (named after the link file). Rules can be committed directly into `AutoFiltersSettings`.

---

## Design Decisions (locked)

| Decision | Answer |
|---|---|
| One trade per link | ✓ Trade name = link filename without `.rvt` |
| Category granularity | Main categories only (no subcategories) |
| Noise filter | Show all discovered values, no element-count threshold |
| Color memory key | Raw Revit parameter value (e.g. `"Supply Air"`) |
| Color memory location | `%AppData%\LemoineTools\ColorMemory.xml` (XML, consistent with other settings) |
| Duplicate detection | Flag results where a rule with the same name already exists in the matched trade |
| Re-scan | Show everything; flag duplicates |

---

## New Files

### 1. `Source/Tools/T01-AutoFilters/ColorMemory.cs`

Singleton. Persists a `Dictionary<string, string>` (parameter value → hex colour) as XML to `%AppData%\LemoineTools\ColorMemory.xml`.

```
ColorMemory
  .Instance                            // Lazy<ColorMemory> singleton
  .TryGetColor(paramValue, out hex)    // lookup
  .SetColor(paramValue, hex)           // write + save
  .Save()                              // XmlSerializer → disk
  .Load()                              // XmlSerializer ← disk
```

Storage class:
```csharp
[XmlRoot("ColorMemory")]
public class ColorMemoryData {
    [XmlArray("Entries")]
    [XmlArrayItem("Entry")]
    public List<ColorMemoryEntry> Entries { get; set; } = new();
}

public class ColorMemoryEntry {
    [XmlAttribute] public string Value { get; set; } = "";
    [XmlAttribute] public string Hex   { get; set; } = "#888888";
}
```

---

### 2. `Source/Tools/T01-AutoFilters/DiscoverViewModel.cs`

Plain C# class (not `ILemoineTool`). Holds all state for the Discover tab. Implements `INotifyPropertyChanged`.

**Key types defined here:**

```csharp
public class LinkEntry {
    public ElementId  Id       { get; set; }  // RevitLinkInstance.Id
    public string     Label    { get; set; }  // e.g. "MECH.rvt"
    public string     TradeName { get; set; } // Label without ".rvt"
    public bool       IsSelected { get; set; } = true;
}

public class CategoryGroupEntry {
    public string                    GroupName  { get; set; }  // "MEP", "Architectural", etc.
    public bool                      IsExpanded { get; set; } = true;
    public List<CategoryEntry>       Categories { get; set; } = new();
}

public class CategoryEntry {
    public BuiltInCategory BuiltIn   { get; set; }
    public string          Label     { get; set; }
    public bool            IsSelected { get; set; } = false;
}

public class DiscoveredRuleRow : INotifyPropertyChanged {
    public bool   IsIncluded      { get; set; } = true;
    public string ParameterValue  { get; set; }   // raw Revit value — color memory key
    public string RuleName        { get; set; }   // editable; starts as ParameterValue
    public string HexColor        { get; set; }   // auto-filled from ColorMemory or default
    public int    ElementCount    { get; set; }
    public bool   IsDuplicate     { get; set; }   // name clash with existing rule in trade
    public string TradeName       { get; set; }   // link filename without .rvt
    public ElementId LinkId       { get; set; }
}
```

**ViewModel state:**

```csharp
public class DiscoverViewModel : INotifyPropertyChanged {
    public ObservableCollection<LinkEntry>          Links          { get; }
    public List<CategoryGroupEntry>                 CategoryGroups { get; }  // static, hardcoded
    public string                                   SelectedParameter { get; set; } // default "System Classification"
    public ObservableCollection<DiscoveredRuleRow>  Results        { get; }
    public bool                                     IsScanning     { get; set; }
    public string                                   StatusText     { get; set; }

    public void SetLinks(IEnumerable<(ElementId id, string label)> links) { ... }
    public void SetResults(IEnumerable<DiscoveredRuleRow> rows) { ... }  // called by handler
    public void CommitSelected() { ... }  // adds rules to AutoFiltersSettings + saves ColorMemory
}
```

**Hardcoded category groups:**

| Group | Categories (BuiltInCategory) |
|---|---|
| Ducts | `OST_DuctCurves`, `OST_DuctFitting`, `OST_DuctAccessory`, `OST_DuctTerminal` |
| Pipes | `OST_PipeCurves`, `OST_PipeFitting`, `OST_PipeAccessory` |
| Cable Tray | `OST_CableTray`, `OST_CableTrayFitting` |
| Conduit | `OST_Conduit`, `OST_ConduitFitting` |
| Mechanical Equipment | `OST_MechanicalEquipment` |
| Electrical Equipment | `OST_ElectricalEquipment` |
| Electrical Fixtures | `OST_ElectricalFixtures` |
| Lighting Fixtures | `OST_LightingFixtures` |
| Sprinklers | `OST_Sprinklers` |
| Plumbing Fixtures | `OST_PlumbingFixtures` |
| Walls | `OST_Walls` |
| Floors | `OST_Floors` |
| Ceilings | `OST_Ceilings` |
| Roofs | `OST_Roofs` |
| Doors | `OST_Doors` |
| Windows | `OST_Windows` |
| Stairs | `OST_Stairs` |
| Structural Framing | `OST_StructuralFraming` |
| Structural Columns | `OST_StructuralColumns` |
| Generic Models | `OST_GenericModel` |

These are grouped under: **MEP** (Ducts → Plumbing Fixtures), **Architectural** (Walls → Stairs), **Structural** (Structural Framing, Structural Columns), **Other** (Generic Models).

**Scannable parameters:**

```
"System Classification"  → BuiltInParameter.RBS_SYSTEM_CLASSIFICATION_PARAM
"Type Name"              → element.Name (type element lookup)
"Family Name"            → (FamilySymbol)typeEl).FamilyName
"Level"                  → BuiltInParameter.FAMILY_LEVEL_PARAM
"Comments"               → BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS
```

**`CommitSelected()` logic:**
1. For each `DiscoveredRuleRow` where `IsIncluded == true`:
   - Find existing `FilterTradeConfig` in `AutoFiltersSettings.Instance.Trades` where `Label == row.TradeName`, or create one with a new 4-char hex ID.
   - Skip if `IsDuplicate` and a rule with `row.RuleName` already exists (don't overwrite existing rules).
   - Build `FilterRuleConfig`: `Name = row.RuleName`, `Parameter = selectedParameter`, `Match = [row.ParameterValue]`, `MatchType = "equals"`, `BuiltInCategories` from the selected categories that were scanned, `CutColor = SurfColor = LineColor = row.HexColor`, `Enabled = true`.
   - Append to `trade.Rules`.
   - Call `ColorMemory.Instance.SetColor(row.ParameterValue, row.HexColor)`.
2. `AutoFiltersSettings.Instance.Save()`.
3. Update `StatusText` with count of rules added.

---

### 3. `Source/Tools/T01-AutoFilters/DiscoverEventHandler.cs`

`IExternalEventHandler`. Triggered when the user clicks Scan.

**Input (set on handler before Raise):**
```csharp
public List<ElementId>         SelectedLinkIds       { get; set; }
public List<BuiltInCategory>   SelectedCategories    { get; set; }
public string                  ParameterName         { get; set; }
public DiscoverViewModel       TargetVm              { get; set; }
```

**`Execute(UIApplication app)` logic:**
1. Get `doc = app.ActiveUIDocument.Document`.
2. For each `linkId`:
   - `var li = doc.GetElement(linkId) as RevitLinkInstance;`
   - `var linkedDoc = li?.GetLinkDocument();` — skip if null.
   - `var tradeName = Path.GetFileNameWithoutExtension(linkedDoc.Title ?? li.Name);`
3. For each `selectedCategory`:
   - `new FilteredElementCollector(linkedDoc).OfCategory(category).WhereElementIsNotElementType()`
   - For each element: read the parameter value using a helper `ReadParameterValue(Element el, string paramName, Document linkedDoc)`.
   - Skip null/empty values.
4. Group by `(linkId, tradeName, paramValue)` → count.
5. For each group, build `DiscoveredRuleRow`:
   - `ParameterValue = paramValue`
   - `RuleName = paramValue`
   - `HexColor = ColorMemory.Instance.TryGetColor(paramValue, out var h) ? h : NextAutoColor()`
   - `ElementCount = group.Count`
   - `IsDuplicate = AutoFiltersSettings.Instance.Trades.Any(t => t.Label == tradeName && t.Rules.Any(r => r.Name == paramValue))`
   - `TradeName = tradeName`
   - `LinkId = linkId`
6. Sort by: tradeName asc, elementCount desc.
7. Dispatch to UI: `TargetVm.Dispatcher.Invoke(() => TargetVm.SetResults(rows))`.

**Auto-colour picker:** Cycle through a curated 20-colour palette (distinct hues, readable on dark/light) in order of first encounter. Not random — deterministic based on insertion order so re-scans assign the same colour to the same value when ColorMemory has no entry.

---

### 4. `Source/Lemoine/T01-AutoFilters/GlobalSettingsWindow.Discover.cs`

Partial class of `GlobalSettingsWindow`. Single method: `BuildDiscoverContent()`.

**UI layout (all programmatic WPF, no XAML):**

```
┌──────────────────────────────────────────────────────────────┐
│ SCAN SETUP (LemoineSectionCard-style)                        │
│                                                              │
│  Links to Scan              Categories        Parameter      │
│  ─────────────────          ─────────────     ───────────    │
│  ☑ MECH.rvt                 ▼ MEP             ● Sys. Class. │
│  ☑ ARCH.rvt                   ☑ Ducts         ○ Type Name   │
│  □  STRUCT.rvt                ☑ Pipes         ○ Family Name │
│                               □ Cable Tray    ○ Level       │
│                             ▶ Architectural   ○ Comments    │
│                             ▶ Structural                    │
│                                               [  Scan  ]    │
├──────────────────────────────────────────────────────────────┤
│ RESULTS  "23 values discovered"        [Add Selected Rules]  │
│                                                              │
│  ☑ [■] Supply Air          MECH   512   (auto: blue)        │
│  ☑ [■] Return Air          MECH   248                       │
│  □ [■] Exhaust             MECH    89                       │
│  ☑ [■] Supply Air          ARCH     3   ⚠ duplicate         │
│  ...                                                         │
└──────────────────────────────────────────────────────────────┘
```

**Results row detail:**
- Checkbox (`CheckBox`)
- Colour swatch (`Border` 16×16, `Background = HexColor`, click → `LemoineColorPickerWindow`, updates row and ColorMemory candidate)
- `TextBox` bound to `RuleName` (inline edit, no special control needed)
- Trade badge: small pill TextBlock with `TradeName`
- Element count: right-aligned `TextBlock`
- Duplicate badge: orange `⚠` `TextBlock` visible only when `IsDuplicate == true`

Results are in a `ScrollViewer` > `StackPanel` (same pattern as filter rule list in Filters tab, not a DataGrid to keep styling consistent).

`_discoverVm` is a field on `GlobalSettingsWindow`, lazily created when the tab first opens. It is not recreated on each `SwitchTab("discover")` call — state persists while the window is open.

---

## Modified Files

### `Source/App.cs`

Add after the Auto Filters suite section:

```csharp
// ── Discover ──────────────────────────────────────────────────────────────
internal static DiscoverEventHandler? DiscoverHandler { get; private set; }
internal static ExternalEvent?        DiscoverEvent   { get; private set; }
```

In `OnStartup`:

```csharp
DiscoverHandler = new DiscoverEventHandler();
DiscoverEvent   = ExternalEvent.Create(DiscoverHandler);
```

---

### `Source/Commands/OpenSettingsCommand.cs`

Extend the existing Revit query block (which already has a valid `doc`) to also build a link list:

```csharp
var linkEntries = new List<(ElementId id, string label)>();
if (doc != null)
{
    // existing fill/line pattern queries ...

    foreach (var li in new FilteredElementCollector(doc)
        .OfClass(typeof(RevitLinkInstance))
        .Cast<RevitLinkInstance>()
        .Where(l => l.GetLinkDocument() != null))
    {
        var ld = li.GetLinkDocument();
        linkEntries.Add((li.Id, ld.Title ?? li.Name));
    }
}
```

Then call `App.GlobalSettings.SetLinkList(linkEntries)` (new method added to `GlobalSettingsWindow`).

---

### `Source/Lemoine/GlobalSettingsWindow.xaml.cs`

**Field:**
```csharp
private DiscoverViewModel? _discoverVm;
```

**Nav entry** — insert after `("filters", "Filters / Color")`:
```csharp
("discover", "Discover"),
```

**`SwitchTab` switch** — add case:
```csharp
case "discover": content = BuildDiscoverContent(); break;
```

**New method** (delegating to partial class):
```csharp
internal void SetLinkList(IEnumerable<(ElementId id, string label)> links)
{
    _discoverVm ??= new DiscoverViewModel();
    _discoverVm.SetLinks(links);
    // If discover tab is currently active, refresh the link list section
    if (_activeTabId == "discover") SwitchTab("discover");
}
```

---

## File Summary

| Action | File |
|---|---|
| **Create** | `Source/Tools/T01-AutoFilters/ColorMemory.cs` |
| **Create** | `Source/Tools/T01-AutoFilters/DiscoverViewModel.cs` |
| **Create** | `Source/Tools/T01-AutoFilters/DiscoverEventHandler.cs` |
| **Create** | `Source/Lemoine/T01-AutoFilters/GlobalSettingsWindow.Discover.cs` |
| **Modify** | `Source/App.cs` |
| **Modify** | `Source/Commands/OpenSettingsCommand.cs` |
| **Modify** | `Source/Lemoine/GlobalSettingsWindow.xaml.cs` |

---

## Implementation Order

1. `ColorMemory.cs` — no dependencies
2. `DiscoverViewModel.cs` — depends on `ColorMemory`, `AutoFiltersSettings`
3. `DiscoverEventHandler.cs` — depends on `DiscoverViewModel`
4. `App.cs` patch — register handler
5. `OpenSettingsCommand.cs` patch — pass link list
6. `GlobalSettingsWindow.xaml.cs` patch — field, nav entry, switch case, `SetLinkList`
7. `GlobalSettingsWindow.Discover.cs` — full UI build method

---

## Out of Scope

- Editing or deleting color memory entries (future "Color Memory" sub-panel)
- Minimum element count threshold (user chose to show all)
- Merging rules across links (one trade per link)
- Subcategory filtering
