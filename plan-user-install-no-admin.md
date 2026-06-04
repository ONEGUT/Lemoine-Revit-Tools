# Plan — End-User Install (No Admin Required)

## Goal
Let users install Lemoine Tools without administrator privileges by targeting
the **per-user** Revit add-ins folder instead of the machine-wide one.

- All-users (needs admin): `C:\ProgramData\Autodesk\Revit\Addins\2024\`
- Per-user (no admin):     `%AppData%\Autodesk\Revit\Addins\2024\`

Revit scans both at startup, so dropping the manifest + DLL into `%AppData%`
loads the plugin with zero elevation. The payload is self-contained: just
`LemoineTools.dll` + `LemoineTools.addin` (Revit API refs are `Private=False`;
families/fonts/icons are embedded resources).

## What will be added (new `installer/` folder — no existing files modified)

| File | Purpose |
|------|---------|
| `installer/Install-LemoineTools.ps1`   | Copies the two payload files into `%AppData%\Autodesk\Revit\Addins\<year>\` for each requested Revit year. No admin. |
| `installer/Uninstall-LemoineTools.ps1` | Removes them from the per-user folder(s). |
| `installer/Install-LemoineTools.cmd`   | Double-click wrapper — runs the `.ps1` with `-ExecutionPolicy Bypass`. |
| `installer/Uninstall-LemoineTools.cmd` | Double-click wrapper for the uninstall script. |
| `installer/LemoineTools.iss`           | Inno Setup config — per-user installer (`PrivilegesRequired=lowest`, installs to `{userappdata}`), auto-generates an uninstaller in Add/Remove Programs. |
| `installer/README.md`                  | Staging + usage instructions for both paths, and the note about removing any old `ProgramData` copy. |

## Out of scope (offered earlier, not requested here)
- Repointing `DeployDir` in `LemoineTools.csproj` to `$(AppData)` for local
  dev builds. Easy follow-up if wanted.

## Notes / risks
- A leftover copy in `ProgramData` can win the load or duplicate ribbon panels;
  README documents removing it for a clean per-user story.
- Scripts surface failures (warnings + non-zero exit) — no silent failure.
- These are PowerShell/Inno artifacts, not C#; no Revit/WPF code changes.

## Branch
Developing on the designated branch `claude/tender-darwin-YTB1q`.
