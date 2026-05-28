# Plan: Discover Rules UX improvements

Branch: `claude/hopeful-fermi-bZEKc`

## Changes

### 1. Scroll pass-through
Call `LemoineControlStyles.WireBubblingScroll(sv)` on every inner ScrollViewer:
- S1 link list
- S2 cards scroll area
- S3 log scroll area (new)
- S4 rule review scroll area (was S3)

### 2. ILemoineTool.cs — add ILemoineNavigable
New optional interface in the same file. DiscoverViewModel implements it.
StepFlowWindow checks `_tool is ILemoineNavigable` and wires the event.
All other VMs unchanged (interface is opt-in).

### 3. StepFlowWindow.xaml.cs — one line in constructor
After `_tool.ValidationChanged +=` subscription, add:
```csharp
if (_tool is ILemoineNavigable nav)
    nav.NavigateRequested += (s, idx) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => ActivateStep(idx)));
```

### 4. DiscoverViewModel.cs — full rewrite (5 steps)

#### Steps
| ID | Label | Notes |
|----|-------|-------|
| S1 | Select Links | unchanged |
| S2 | Configure Links | Discover button navigates to S3, then raises event |
| S3 | Scanning | NEW — live log + progress bar |
| S4 | Review Rules | was S3 |
| S5 | Confirm & Commit | was S4 |

#### ILemoineNavigable
`event EventHandler<int> NavigateRequested` — fired with:
- index 2 (S3) when Discover is clicked
- index 3 (S4) when scan completes

#### Single-expand accordion
`private readonly List<(Action Open, Action Close)> _cardActions`
cleared in `RebuildS2Cards`, populated in `BuildLinkCard`.
`ExpandCard(int index)` opens one, closes all others.
Header click calls `ExpandCard(myIndex)` — clicking any closed card opens it.
Initial state: first card open, rest closed.

#### Combined parameter combo (Whole Category as first item)
Per-category rows go from 4 cols → 3 cols:
- Col 0: Category label (*)
- Col 1: ComboBox with `["Whole Category", ...params]` — 200px  
- Col 2: ⓘ info indicator — 20px (hidden when Whole Category selected)

Mode is derived from combo selection; `ScanConfigRow.Mode` remains unchanged as data.

#### S3 Scanning step content
- `TextBlock _s3StatusTb` — "Scanning… N rule(s) found"
- `ProgressBar _s3Progress` — value 0–100
- `ScrollViewer _s3LogScroll` + `StackPanel _s3LogStack` — live log entries
- `AppendScanLog(text, status)` — dispatched from Revit thread via `_wpfDispatcher`
- `UpdateScanProgress(pct)` — same
- `_handler.PushLog` and `_handler.OnProgress` now wired (were null)

#### Field renames
| Old | New |
|-----|-----|
| `_s3Panel` | `_s4Panel` |
| `_s4Review` | `_s5Review` |

#### Method renames
| Old | New |
|-----|-----|
| `BuildS3` (review) | `BuildS4` |
| `PopulateS3` | `PopulateS4` |
| `BuildS3Header` | `BuildS4Header` |
| `BuildS3RuleRow` | `BuildS4RuleRow` |
| `BuildS4` (confirm) | `BuildS5` |
| `UpdateS4Summary` | `UpdateS5Summary` |

`BuildS3` is now the new scanning step.

#### IsValid
| Step | Condition |
|------|-----------|
| S2 | `_links.Any(l => l.IsSelected && l.ConfigRows.Count > 0)` |
| S3 | `_scanComplete` — disabled during scan, auto-navigates away when done |
| S4 | `_discoveredRules.Any(r => r.IsIncluded)` |
| S5 | `true` |

## Files changed
- `Source/Lemoine/ILemoineTool.cs`
- `Source/Lemoine/StepFlowWindow.xaml.cs`
- `Source/Tools/T01-AutoFilters/DiscoverViewModel.cs`
