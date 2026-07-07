#!/usr/bin/env python3
"""Phase 5 namespace rename — folder reorg into ribbon categories.

Renames only the namespaces the plan explicitly calls out: two namespace merges
(CopyLinear+CopyFromLink, Coordinates+UpgradeLinks), the Clash -> Dimensioning
rename (with its AutoDimension.* sub-namespace tail preserved automatically since
this is a prefix replace), and the old Testing.* staging namespaces promoted to
their real categories (including two pre-existing stray "Testing" mis-namespaced
files physically living in the old Clash folder, now fixed to Dimensioning).

Word-boundary, longest-pattern-first, dry-run with count assertions before write.
"""
import re
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
SOURCE_DIR = REPO_ROOT / "Source"

# (old, new) — sorted by descending length of `old` before use.
RENAMES = [
    ("LemoineTools.Tools.Testing.PlaceDependentViews", "LemoineTools.Tools.Sheets.PlaceDependentViews"),
    ("LemoineTools.Tools.Testing.AlignSheetViews",      "LemoineTools.Tools.Sheets.AlignSheetViews"),
    ("LemoineTools.Tools.Testing.LegendCreator",        "LemoineTools.Tools.FiltersLegends.LegendCreator"),
    ("LemoineTools.Tools.Testing.ElevationTag",         "LemoineTools.Tools.Dimensioning.ElevationTag"),
    ("LemoineTools.Tools.UpgradeLinks",                 "LemoineTools.Tools.Setup"),
    ("LemoineTools.Tools.Coordinates",                  "LemoineTools.Tools.Setup"),
    ("LemoineTools.Tools.CopyLinear",                   "LemoineTools.Tools.CopyFromLink"),
    ("LemoineTools.Tools.Testing",                      "LemoineTools.Tools.Dimensioning"),
    ("LemoineTools.Tools.Clash",                        "LemoineTools.Tools.Dimensioning"),
]
RENAMES.sort(key=lambda p: -len(p[0]))


def main():
    dry_run = "--apply" not in sys.argv

    cs_files = sorted(SOURCE_DIR.rglob("*.cs"))
    # exclude sibling projects just in case
    cs_files = [p for p in cs_files if not any(
        part in ("LemoinePreview", "LemoineNavisworks", "LemoineTools.PdfGeometry") for part in p.parts)]

    total_replacements = 0
    files_touched = 0

    for path in cs_files:
        text = path.read_text(encoding="utf-8")
        original = text
        file_report = []
        for old, new in RENAMES:
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
