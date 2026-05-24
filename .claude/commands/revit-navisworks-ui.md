---
name: revit-navisworks-ui
description: >
  Implement structurally correct WPF UIs for Revit and Navisworks plugins targeting
  .NET Framework 4.8 (C# + XAML). Use this skill whenever the user wants to build,
  modify, or debug a WPF window or UserControl for a Revit or Navisworks plugin —
  even if they only say "build a UI for this", "add a window", "it's rendering wrong",
  "the layout is broken", or "fix the graphical bug". Always use this skill for any
  Revit/Navisworks WPF UI task — never improvise the structure. The Lemoine Tools
  plugin (LemoineTools) is the primary reference project: its architecture, threading
  model, resource system, and control patterns are the authoritative source for all
  structural decisions in this skill.
---

# Revit / Navisworks WPF UI Builder

Produces structurally correct WPF UIs for Revit and Navisworks plugins.
Focus: **layout correctness, rendering bug prevention, hosting-context safety**.
Style is NEVER copied from provided reference files — structure only.

> **Reference files** (read when relevant):
> - `references/layout-engine.md` — WPF Measure/Arrange rules, panel behaviour
> - `references/bug-catalog.md`   — Full graphical bug catalog with fixes
> - `references/navisworks.md`    — Navisworks-specific hosting patterns

---

## Step 0 — Intake (mandatory before writing any code)

### 0A — Hosting context (ask if not clear)

| Question | Why it matters |
|---|---|
| Revit or Navisworks? | Different threading models and window-owner patterns |
| Revit: modal (`ShowDialog`) or modeless (`Show` + ExternalEvent)? | Determines STA thread setup and dispatcher loop |
| Navisworks: `DockPanePlugin`, `CommandHandlerPlugin`, or standalone `Window`? | Affects whether a message pump is needed |
| Is there an existing `App.cs` / application entry point? | STA handler and ExternalEvent registration lives here |

### 0B — Structural reference files (critical)

If the user provides existing XAML, `.cs`, or `ResourceDictionary` files:

**Extract structure — never style.** Build this inventory:

```
STRUCTURAL INVENTORY
────────────────────────────────────────────────────────────
Source files scanned: [list]

WINDOW / ROOT
  Base class:      [Window | UserControl | custom base]
  Size:            [Width × Height or SizeToContent]
  Outer layout:    [Grid rows | DockPanel | StackPanel]

PANEL HIERARCHY (top-down)
  [Panel type]  →  [children]  →  sizing strategy
  ...

NAMING CONVENTIONS
  x:Name pattern:  [e.g. _camelCase, PascalCase, BtnVerb]
  Handler pattern: [e.g. OnBtnSave_Click, BtnSave_Click]

RESOURCE KEYS IN USE (structural — sizes, fonts, thicknesses)
  [Key]  →  [used for]
  ...
  ⚠ STYLE KEYS EXCLUDED — do not carry over Background, Foreground,
    BorderBrush, CornerRadius, or color-adjacent values.

CONTROL STYLES INJECTED
  [Class].[Method]() — e.g. LemoineControlStyles.InjectInto(resources, 5)

THREADING MODEL
  STA thread:   [yes/no — ManualResetEventSlim + Thread + Dispatcher.Run()]
  ExternalEvent: [registered in App.cs as App.YourHandler / App.YourEvent]
```

**Forbidden carry-overs from reference files:**
`Background`, `Foreground`, `BorderBrush`, `BorderThickness` values,
`CornerRadius`, `FontFamily`, `FontSize` (inline numeric values),
`Margin`/`Padding` (numeric literals), any hex color string, any brush key.

### 0C — Controls and interactions

For every interactive element, ask or infer:
- What data does it produce / consume?
- Is it required for validation?
- Does it have a conditional visibility dependency?
- Does any other control depend on its state?

---

## Step 1 — Layout planning (always before generating code)

Before writing a single line of XAML or C#, produce a **Panel Hierarchy Plan**:

```
PANEL HIERARCHY PLAN
────────────────────────────────────────────────────────────
Window  [W × H]
└── Grid  rows: [38px toolbar | * content | 42px footer]
    ├── Row 0: DockPanel (toolbar)
    │     ├── [left: title TextBlock]
    │     └── [right: close Button]
    ├── Row 1: ScrollViewer  ← always wrap variable-height content
    │     └── StackPanel (vertical)
    │           ├── [section: Border > Grid]
    │           └── [section: Border > Grid]
    └── Row 2: Grid  cols: [Auto | * spacer | Auto]
          ├── Col 0: Button "Back"
          └── Col 2: Button "Confirm"
```

**Rules enforced at planning time:**
- Every `StackPanel` that may grow beyond the window height → wrapped in `ScrollViewer`
- `Grid *` columns never inside an unconstrained `StackPanel`
- Button rows: 3-column Grid (Auto | * | Auto) — not DockPanel
- Toolbar and footer rows: fixed px height with `ClipToBounds="True"`
- `DockPanel.LastChildFill` defaults `True` — always verify the last child is the intended fill

> If any rule is violated in the plan, fix it before continuing to code generation.

---

## Step 2 — Revit hosting rules (apply to all Revit plugin windows)

Read `references/layout-engine.md` and `references/bug-catalog.md` before generating
any Revit WPF code.

### Threading model — mandatory pattern

All tool windows **must** use the STA thread + message pump pattern:

```csharp
// In YourToolCommand.Execute() — called on Revit's main thread
var ready = new ManualResetEventSlim(false);
var thread = new Thread(() =>
{
    var win = new YourWindow(handler, externalEvent);
    win.Closed += (s, e) => Dispatcher.CurrentDispatcher.BeginInvokeShutdown(
        System.Windows.Threading.DispatcherPriority.Background);
    ready.Set();
    win.Show();
    Dispatcher.Run();  // message pump — keeps thread alive while window is open
});
thread.SetApartmentState(ApartmentState.STA);
thread.Start();
ready.Wait();  // don't return from Execute() until window is shown
```

**Never:**
- Create a WPF window on the default (MTA) thread
- Use `ShowDialog()` on the STA thread without a dedicated pump
- Call `Dispatcher.Invoke(...)` from inside `IExternalEventHandler.Execute()` — callbacks are already wrapped in `BeginInvoke` by `StepFlowWindow.StartRun()`

### Window owner — always set

```csharp
// In the window constructor or Loaded handler:
var helper = new System.Windows.Interop.WindowInteropHelper(this);
helper.Owner = Autodesk.Windows.ComponentManager.ApplicationWindow;
```

Without this, the WPF window can go behind Revit's main window on Alt+Tab or focus change.

### ExternalEvent pattern

```csharp
// App.cs — registered once at startup
public static YourToolEventHandler YourHandler;
public static Autodesk.Revit.UI.ExternalEvent YourEvent;

// In App.OnStartup:
YourHandler = new YourToolEventHandler();
YourEvent   = ExternalEvent.Create(YourHandler);
```

- Set all handler properties **before** calling `YourEvent.Raise()`
- `Execute()` runs on Revit's main thread — no UI calls there unless via `BeginInvoke`
- Always configure failure handling at transaction start (see `references/bug-catalog.md`)

### Navisworks — read `references/navisworks.md` for this context

---

## Step 3 — Resource and style rules

### Always use resource references — never inline values

```csharp
// ✅ Correct
tb.SetResourceReference(TextBlock.FontSizeProperty, "LemoineFS_MD");
tb.SetResourceReference(TextBlock.ForegroundProperty, "LemoineText");

// ❌ Never
tb.FontSize = 12;
tb.Foreground = new SolidColorBrush(Colors.White);
```

### Transparent background — critical rule

```csharp
// ✅ Correct — direct assignment
element.Background = Brushes.Transparent;

// ❌ Never — "Transparent" is not a resource key; WPF falls back to null
//    making the element non-hit-testable
element.SetResourceReference(BackgroundProperty, "Transparent");
```

Never use a ternary inside `SetResourceReference`:

```csharp
// ❌ Wrong
element.SetResourceReference(prop, condition ? "KeyA" : "Transparent");

// ✅ Correct
if (condition) element.SetResourceReference(prop, "KeyA");
else           element.Background = Brushes.Transparent;
```

### Control styles — inject, never duplicate

All WPF control styles (ScrollBar, ComboBox, TextBox, CheckBox, DatePicker) live in
`LemoineControlStyles.InjectInto(resources, scrollBarWidth)`. Call once per window:

```csharp
// StepFlowWindow (scrollBarWidth = 5), GlobalSettingsWindow (= 8)
LemoineControlStyles.InjectInto(Resources, scrollBarWidth: 5);
```

**Never** redefine a style that already exists in this injection — it creates override conflicts.

### ScrollBar — both orientations required

The ScrollBar style **must** include a `<Style.Triggers>` block for `Orientation == Horizontal`
with its own template. Without it, horizontal scrollbars fall back to the OS default.

### ComboBox autocomplete guard

When an editable `ComboBox` lives in a panel that gets rebuilt (e.g. tab switching),
always include the `IsKeyboardFocusWithin` guard in `TextChanged`:

```csharp
combo.AddHandler(TextBoxBase.TextChangedEvent,
    new TextChangedEventHandler((s, e) =>
    {
        if (suppressing || !combo.IsKeyboardFocusWithin) return; // ← CRITICAL
        // ... filtering logic
    }), true);
```

After rebuilding panels with ComboBoxes:

```csharp
Dispatcher.BeginInvoke(new Action(() => Keyboard.ClearFocus()),
    System.Windows.Threading.DispatcherPriority.Input);
```

---

## Step 4 — .NET 4.8 / Revit 2024 hard constraints

These are absolute — never violated:

| ❌ Forbidden | ✅ Correct alternative |
|---|---|
| `TextBlock.LetterSpacing` | Does not exist in net48 WPF — remove |
| `DWGImportOptions.AutoCorrectAlmostVerticalLines` | Does not exist in Revit 2024 — remove |
| Anonymous tuple arrays with `Func<string>` | Use named `struct` (e.g. `CardDef`) |
| `FontSize = X` inline | `SetResourceReference(..., "LemoineFS_XX")` |
| `Height = X` on buttons | `MinHeight = X` (grows with content) |
| `LemoineTextSub` / `LemoineTextDim` for body text | `LemoineText` + `FontStyles.Italic` |
| `Track.TemplateProperty` | Does not exist — use `XamlReader.Parse()` |
| `Track.Background` in XAML | `Track` has no `Background` — remove |
| `GetSettingRows()` / `LemoineSettingRow` | Replaced by `GetSettingsSpec()` + `ApplySettings()` |
| `BuiltInParameter.RBS_SYSTEM_TYPE_PARAM` | Does not exist — use `RBS_SYSTEM_CLASSIFICATION_PARAM` |
| `LemoineFS_XS` | Removed — use `LemoineFS_SM` |
| `Window.Resources` block in GlobalSettingsWindow | Styles come from `LemoineControlStyles.InjectInto()` only |
| `PluginSettings` | Does not exist — ship `YourToolSettings.cs` XML singleton |
| `FilterCategoryConfig.BuiltInCategories` (List\<string\>) | V2: single `[XmlAttribute] string BuiltInCategory` |
| `BuiltInParameter.RBS_SYSTEM_TYPE_PARAM` | Use `RBS_SYSTEM_CLASSIFICATION_PARAM` |
| Bare `TextBox`, `Grid`, `Point` in ViewModel files | Alias: `using WpfTextBox = System.Windows.Controls.TextBox` |
| `Fixed Width` on `LemoineMultiSelectTabs` tab column | `Width="Auto" MinWidth="80" MaxWidth="160"` |
| `FrameworkElementFactory` for Track/Popup | Only for simple Border/Grid/StackPanel — use `XamlReader.Parse()` for primitives |

---

## Step 5 — Pre-output bug audit (mandatory)

Before presenting any code, run this checklist. Fix every unchecked item.

```
BUG AUDIT
────────────────────────────────────────────────────────────
LAYOUT
[ ] No StackPanel containing Grid * columns (infinite-height constraint)
[ ] All variable-height StackPanels wrapped in ScrollViewer
[ ] No content clipped by fixed-height parent without overflow handling
[ ] Button rows use 3-column Grid (Auto|*|Auto) — not DockPanel
[ ] DockPanel.LastChildFill is intentional for every DockPanel used
[ ] Toolbar and footer rows have ClipToBounds="True" and DockPanel VerticalAlignment=Center

RESOURCES / STYLES
[ ] No inline FontSize numeric values — all SetResourceReference
[ ] No inline color hex strings — all resource keys
[ ] No Brushes.Transparent via SetResourceReference — direct assignment only
[ ] No ternary inside SetResourceReference
[ ] No duplicate style keys already in LemoineControlStyles
[ ] ScrollBar style has both orientations (Vertical default + Horizontal trigger)
[ ] GlobalSettingsWindow has no <Window.Resources> block

THREADING / HOSTING
[ ] Window created on dedicated STA thread with Dispatcher.Run() pump
[ ] WindowInteropHelper.Owner set to ComponentManager.ApplicationWindow
[ ] No Dispatcher.Invoke inside IExternalEventHandler.Execute()
[ ] ExternalEvent.Raise() called only after all handler properties are set
[ ] Transaction has ConfigureFailures() called at start

HIT-TESTING / INTERACTIVITY
[ ] No transparent overlay panel blocking controls below (check ZIndex)
[ ] All interactive controls have IsEnabled / IsHitTestVisible=True (default)
[ ] ComboBox autocomplete guard (IsKeyboardFocusWithin) present
[ ] After tab rebuild: Dispatcher.BeginInvoke ClearFocus called

.NET 4.8 / REVIT 2024
[ ] No LetterSpacing on TextBlock
[ ] No AutoCorrectAlmostVerticalLines on DWGImportOptions
[ ] No anonymous tuple arrays — named structs only
[ ] No PluginSettings reference
[ ] No LemoineFS_XS in new code
[ ] No bare TextBox/Grid/Point in ViewModel — aliased usings

COMPLETENESS
[ ] Every event in XAML has a corresponding handler method
[ ] Every x:Name referenced in code-behind exists in XAML
[ ] Namespace in x:Class matches code-behind namespace + class
[ ] InitializeComponent() called in constructor before any control access
```

> Tag any construct that could still be risky with a `// ⚠` comment inline.

---

## Step 6 — Output format

Deliver in this order:

1. **Panel Hierarchy Plan** (from Step 1) — confirm structure before code
2. **Bug Audit result** — list any items that needed fixes (brief)
3. **Code** — full files with `// ⚠` inline on risky constructs
4. **Integration notes** — manual steps: using aliases, App.cs changes,
   resource dictionary additions, ribbon button registration
