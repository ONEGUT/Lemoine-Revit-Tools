# Testing Folder — New Tool Policy

All new tools **must start here** before being promoted to their own permanent home.

## Workflow

1. **Develop** — Create your new tool's files inside `Source/Tools/Testing/<ToolName>/`
   and its command(s) inside `Source/Commands/Testing/`.
   Use namespace `LemoineTools.Tools.Testing` while in development.

2. **Review** — The tool lives in Testing until it is stable, tested against a real project,
   and signed off for release.

3. **Graduate** — Once approved, move the files to a new named folder
   (e.g. `Source/Tools/YourToolName/`) and assign it a descriptive namespace
   (`LemoineTools.Tools.YourToolName`). Update App.cs and the csproj comment accordingly.

## Current tools in Testing

| Tool | Folder | Notes |
|------|--------|-------|
| Coordination Drawing Set | `CoordSet/` | Generates filters, discipline views, legend, sheets |
| Legend Creator | `LegendCreator/` | Builds custom legend views with color swatches |

## Debuggers

Debug / diagnostic tools live separately under `Source/Tools/Debuggers/` and
`Source/Commands/Debuggers/`. Each debugger gets its own subfolder when there are
multiple files, or sits flat in `Debuggers/` for single-file tools.
New debuggers follow the same Testing-first rule before landing in `Debuggers/`.
