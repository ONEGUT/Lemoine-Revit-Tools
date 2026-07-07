#!/usr/bin/env python3
"""Find .cs/.xaml files under Source/ whose top-level type(s) have zero
references elsewhere in the repo.

Unlike a naive "filename == type name" check, this parses each .cs file for
its actual top-level type declarations (tracking brace depth, so nested/
private helper classes inside a file are never flagged on their own) and
groups partial-class declarations across files so a type split over several
files (e.g. GlobalSettingsWindow.*.cs) is judged as one unit.

A file is only ever a *candidate* here — verify each one before deleting
(reflection-string usage, XAML-only element usage, and dynamic dispatch can
all be real "uses" this script can't see).

Usage: python3 devtools/audit_unused_files.py [--out audit-unused-files.md]
"""
import argparse
import re
from pathlib import Path
from collections import defaultdict

REPO_ROOT = Path(__file__).resolve().parent.parent
SOURCE_DIR = REPO_ROOT / "Source"

EXCLUDED_DIRS = {"LemoinePreview", "LemoineNavisworks", "LemoineTools.PdfGeometry"}

EXTRA_SEARCH_FILES = [
    REPO_ROOT / "LemoineTools.csproj",
    REPO_ROOT / "LemoineTools.addin",
    REPO_ROOT / "CLAUDE.md",
    REPO_ROOT / "LEMOINE_UI.md",
]

TYPE_DECL_RE = re.compile(
    r"^\s*(?:\[[^\]]*\]\s*)*"                              # attributes
    r"(?:public|internal|private|protected)?\s*"
    r"(?:static\s+|sealed\s+|abstract\s+|partial\s+|readonly\s+)*"
    r"(?:class|interface|enum|struct|record)\s+"
    r"([A-Za-z_]\w*)"
)


def iter_files(exts):
    for ext in exts:
        for p in SOURCE_DIR.rglob(ext):
            if any(part in EXCLUDED_DIRS for part in p.parts):
                continue
            yield p


def top_level_types_in_file(path: Path):
    """Type names declared at namespace depth (depth==1), skipping nested types."""
    try:
        text = path.read_text(encoding="utf-8", errors="replace")
    except OSError:
        return []
    types = []
    depth = 0
    for line in text.splitlines():
        stripped = line.strip()
        m = TYPE_DECL_RE.match(line)
        if m and depth == 1:
            types.append(m.group(1))
        # Update depth AFTER checking (declaration line's own braces open depth 1->2)
        depth += stripped.count("{") - stripped.count("}")
    return types


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default=str(REPO_ROOT / "audit-unused-files.md"))
    args = ap.parse_args()

    cs_files = sorted(iter_files(["*.cs"]))
    xaml_files = sorted(iter_files(["*.xaml"]))
    all_files = cs_files + xaml_files

    # type_name -> set of files declaring it (handles partial classes)
    declared_in = defaultdict(set)
    # file -> list of type names it declares
    file_types = {}
    for path in cs_files:
        types = top_level_types_in_file(path)
        file_types[path] = types
        for t in types:
            declared_in[t].add(path)

    # Build search haystack once.
    haystack = []
    for p in all_files:
        try:
            haystack.append((p, p.read_text(encoding="utf-8", errors="replace")))
        except OSError:
            continue
    for p in EXTRA_SEARCH_FILES:
        if p.exists():
            try:
                haystack.append((p, p.read_text(encoding="utf-8", errors="replace")))
            except OSError:
                continue

    pattern_cache = {}

    def refs_outside(type_name, exclude_files):
        pat = pattern_cache.get(type_name)
        if pat is None:
            pat = re.compile(r"\b" + re.escape(type_name) + r"\b")
            pattern_cache[type_name] = pat
        hits = []
        for path, text in haystack:
            if path in exclude_files:
                continue
            n = len(pat.findall(text))
            if n > 0:
                hits.append((path, n))
        return hits

    # A .xaml.cs code-behind's own paired .xaml markup self-references the class
    # via x:Class — that's not a real external use, so pair them for exclusion.
    xaml_by_stem = {p.name[: -len(".xaml")]: p for p in xaml_files}

    candidates = []  # (file, [unreferenced type names])
    for path, types in sorted(file_types.items()):
        if not types:
            continue
        unreferenced = []
        for t in types:
            exclude = set(declared_in[t])  # every file declaring this type (partial-class safe)
            for decl_file in declared_in[t]:
                if decl_file.name.endswith(".xaml.cs"):
                    stem = decl_file.name[: -len(".xaml.cs")]
                    if stem in xaml_by_stem:
                        exclude.add(xaml_by_stem[stem])
            if not refs_outside(t, exclude):
                unreferenced.append(t)
        if unreferenced and len(unreferenced) == len(types):
            # every type this file declares is unreferenced elsewhere
            candidates.append((path, types, unreferenced))

    # XAML files: flag if the paired .xaml.cs is itself a candidate (or missing).
    xaml_candidates = []
    candidate_stems = {p.stem for p, _, _ in candidates}  # e.g. "Foo.xaml" -> stem "Foo.xaml" won't match; handle below
    cs_stems_all = {p.name[:-len(".xaml.cs")] for p in cs_files if p.name.endswith(".xaml.cs")}
    candidate_cs_stems = {p.name[:-len(".xaml.cs")] for p, _, _ in candidates if p.name.endswith(".xaml.cs")}
    for xp in xaml_files:
        stem = xp.name[: -len(".xaml")]
        if stem in candidate_cs_stems:
            xaml_candidates.append(xp)
        elif stem not in cs_stems_all:
            xaml_candidates.append(xp)  # orphaned XAML with no code-behind at all

    rel = lambda p: p.relative_to(REPO_ROOT).as_posix()

    lines = []
    lines.append("# Unused-file audit (scripted candidates)\n")
    lines.append(
        f"Scanned {len(cs_files)} `.cs` + {len(xaml_files)} `.xaml` files under `Source/` "
        f"(excluding sibling projects: {', '.join(sorted(EXCLUDED_DIRS))}).\n"
    )
    lines.append(
        "A `.cs` candidate below declares only top-level types (nested/private helper "
        "classes don't count) that have **zero** word-boundary references anywhere else "
        "in the repo — partial-class pieces are grouped so a type split across several "
        "files is judged as one unit. This still can't see reflection-string dispatch, "
        "XAML-only element usage, or intentionally-unused-for-now scaffolding — **verify "
        "each one before deleting.**\n"
    )

    if not candidates and not xaml_candidates:
        lines.append("\nNo candidates found.\n")
    else:
        lines.append(f"\n## `.cs` candidates ({len(candidates)})\n")
        for path, types, unreferenced in candidates:
            type_list = ", ".join(f"`{t}`" for t in types)
            lines.append(f"- `{rel(path)}` — declares {type_list}, none referenced elsewhere")

        lines.append(f"\n## `.xaml` candidates ({len(xaml_candidates)})\n")
        for xp in xaml_candidates:
            lines.append(f"- `{rel(xp)}`")

    Path(args.out).write_text("\n".join(lines) + "\n", encoding="utf-8")
    print(f"Wrote {len(candidates)} .cs + {len(xaml_candidates)} .xaml candidate(s) to {args.out}")


if __name__ == "__main__":
    main()
