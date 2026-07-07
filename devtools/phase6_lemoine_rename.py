#!/usr/bin/env python3
"""Phase 6 — remove the "Lemoine" prefix from file/type names (framework rename).

Applies the plan's 6.2 rename table (word-boundary, longest-pattern-first) across
every .cs/.xaml file under Source/, plus CLAUDE.md and LEMOINE_UI.md, then the
LemoineTools.Lemoine -> LemoineTools.Framework namespace rename (also a prefix
replace, so it correctly carries every LemoineTools.Lemoine.Controls.* etc. tail
along with it, and fixes clr-namespace="..." XAML attributes and x:Class values
for free since they're just text matches too).

Deliberately NOT touched (do-not-touch list, CLAUDE.md-persisted / cosmetic):
  - LemoineTools (assembly/root namespace/project files)
  - LemoineAutoFilters / LemoineAutoFiltersV2.xml (persisted XML contract)
  - Theme resource keys (LemoineText, LemoineBg, ... - none collide with a
    renamed type name, verified separately; this script's patterns never
    match a resource-key string because none of them equal a type name)
  - "Lemoine Tools" ribbon tab branding, "Lemoine —" transaction display names
  - The lowercase `lemoine:` xmlns alias in XAML (case-sensitive patterns only
    match the capitalized type/namespace names)
  - LemoinePreview/ sibling project (excluded from the file walk)

Dry-run by default; pass --apply to write.
"""
import re
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
SOURCE_DIR = REPO_ROOT / "Source"
EXCLUDED_DIRS = {"LemoinePreview", "LemoineNavisworks", "LemoineTools.PdfGeometry"}

# Type/interface renames from plan section 6.2. (old, new) pairs.
TYPE_RENAMES = [
    ("LemoineLog", "DiagnosticsLog"),
    ("LemoineRunLog", "RunLogSink"),
    ("LemoineRun", "RunState"),
    ("LemoineSettings", "AppSettings"),
    ("LemoineStrings", "AppStrings"),
    ("LemoineTheme", "ThemePalette"),
    ("LemoineMotion", "MotionEffects"),
    ("LemoineIcons", "GlyphIcons"),
    ("LemoineIcon", "GlyphIcon"),
    ("LemoineFailureCapture", "RevitFailureCapture"),
    ("LemoineDatePicker", "DateField"),
    ("LemoineUiSize", "UiSize"),
    ("LemoineButtonVariant", "ButtonVariant"),
    ("ILemoineTool", "IStepFlowTool"),
    ("ILemoineReviewable", "IReviewableTool"),
    ("ILemoineToolCleanup", "IToolCleanup"),
    ("ILemoineConditionalSteps", "IConditionalSteps"),
    ("ILemoineNavigable", "IStepNavigable"),
    ("ILemoineRunPausable", "IRunPausable"),
    ("ILemoineRunResult", "IRunResult"),
    ("ILemoineStepConfirmable", "IStepConfirmable"),
    ("ILemoineToolSettings", "IToolSettings"),
    ("LemoineToolSettingsSpec", "ToolSettingsSpec"),
    ("LemoineSettingDef", "SettingDef"),
    ("LemoineSettingsGroup", "SettingsGroup"),
    ("LemoineReloadHandler", "ReloadHandler"),
    ("LemoineBrowserNode", "BrowserNode"),
    ("LemoineBrowserTree", "BrowserTree"),
    ("LemoineBrowserTreePicker", "BrowserTreePicker"),
    ("LemoineControlStyles", "ControlStyles"),
    ("LemoineDragGhost", "DragGhost"),
    ("LemoineListReorder", "ListReorder"),
    ("LemoineEyeGlyph", "EyeGlyph"),
    ("LemoineSwatchGlyph", "SwatchGlyph"),
    ("LemoineFileBrowser", "FileBrowser"),
    ("LemoineFolderBrowser", "FolderBrowser"),
    ("LemoineInlineEdit", "InlineEdit"),
    ("LemoineInlineStepper", "InlineStepper"),
    ("LemoineLegendBlockRow", "LegendBlockRow"),
    ("LemoineLegendBuilder", "LegendBuilder"),
    ("LemoineLegendGroupCard", "LegendGroupCard"),
    ("LemoineLegendLayoutBar", "LegendLayoutBar"),
    ("LemoineLegendPalette", "LegendPalette"),
    ("LemoineLegendPreview", "LegendPreview"),
    ("LemoineLegendRow", "LegendRow"),
    ("LemoineMatrixInput", "MatrixInput"),
    ("LemoineNamingSlots", "NamingSlots"),
    ("LemoineNumberRange", "NumberRange"),
    ("LemoineTokenInput", "TokenInput"),
    ("LemoineTagChipInput", "TagChipInput"),
    ("LemoineTextField", "TextField"),
    ("LemoineMultiSelectTabs", "MultiSelectTabs"),
    ("LemoineSingleSelect", "SingleSelect"),
    ("LemoineSearchAutocomplete", "SearchAutocomplete"),
    ("LemoineColorPickerPanel", "ColorPickerPanel"),
    ("LemoineColorPickerWindow", "ColorPickerWindow"),
    ("LemoineSwatchPicker", "SwatchPicker"),
    ("LemoineCategoryChip", "CategoryChip"),
    ("LemoineReviewSummary", "ReviewSummary"),
    ("LemoineSectionCard", "SectionCard"),
    ("LemoineTitleBar", "TitleBar"),
    ("LemoineToggleSwitches", "ToggleSwitches"),
    ("LemoineWarnBanner", "WarnBanner"),
    ("LemoineTemplateInfo", "TemplateInfo"),
    ("LemoineTemplateStore", "TemplateStore"),
]

# Namespace rename — a prefix replace, so it also fixes every
# LemoineTools.Lemoine.Controls / .Templates tail and every clr-namespace/xmlns
# XAML attribute value for free.
NAMESPACE_RENAMES = [
    ("LemoineTools.Lemoine", "LemoineTools.Framework"),
]

ALL_RENAMES = sorted(TYPE_RENAMES + NAMESPACE_RENAMES, key=lambda p: -len(p[0]))

EXTRA_FILES = [REPO_ROOT / "CLAUDE.md", REPO_ROOT / "LEMOINE_UI.md"]


def iter_files():
    for ext in ("*.cs", "*.xaml"):
        for p in SOURCE_DIR.rglob(ext):
            if any(part in EXCLUDED_DIRS for part in p.parts):
                continue
            yield p
    for p in EXTRA_FILES:
        if p.exists():
            yield p


def main():
    dry_run = "--apply" not in sys.argv

    total_replacements = 0
    files_touched = 0

    for path in sorted(iter_files()):
        text = path.read_text(encoding="utf-8")
        original = text
        file_report = []
        for old, new in ALL_RENAMES:
            pattern = re.compile(r"\b" + re.escape(old) + r"\b")
            count = len(pattern.findall(text))
            if count == 0:
                continue
            text = pattern.sub(new, text)
            file_report.append((old, new, count))
            total_replacements += count

        if text != original:
            files_touched += 1
            rel = path.relative_to(REPO_ROOT).as_posix()
            print(f"{rel}:")
            for old, new, count in file_report:
                print(f"    {count:3d}x  {old}  ->  {new}")
            if not dry_run:
                path.write_text(text, encoding="utf-8")

    print(f"\n{'[DRY RUN] ' if dry_run else ''}{files_touched} file(s), {total_replacements} replacement(s) total.")
    if dry_run:
        print("Re-run with --apply to write changes.")


if __name__ == "__main__":
    main()
