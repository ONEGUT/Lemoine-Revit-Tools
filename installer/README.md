# Lemoine Tools — End-User Install (No Admin Required)

Revit loads add-ins from two locations on every launch:

| Scope     | Folder                                          | Admin? |
|-----------|-------------------------------------------------|--------|
| All users | `C:\ProgramData\Autodesk\Revit\Addins\2024\`    | **Yes** |
| Per user  | `%AppData%\Autodesk\Revit\Addins\2024\`         | **No**  |

These installers target the **per-user** folder, so they install without an
admin prompt. The whole plugin is two files:

- `LemoineTools.dll`
- `LemoineTools.addin`

Build the project on Windows, then copy those two files out of the build
output into this `installer\` folder (next to the scripts / `.iss`) before
using either option below.

---

## Option A — Script (simplest)

No installer framework. Good for quick personal installs.

**Install** — double-click `Install-LemoineTools.cmd`
(or run `Install-LemoineTools.ps1` from PowerShell).

**Uninstall** — double-click `Uninstall-LemoineTools.cmd`.

Target other / multiple Revit years:

```powershell
.\Install-LemoineTools.ps1   -RevitYears 2024,2025
.\Uninstall-LemoineTools.ps1 -RevitYears 2024,2025
```

The `.cmd` wrappers launch PowerShell with `-ExecutionPolicy Bypass` for that
process only, so there's no admin step and no machine policy change.

---

## Option B — Inno Setup installer (polished)

Produces a single `LemoineTools-Setup-2024.exe` with a wizard and a proper
entry in **Add/Remove Programs** (per-user), still with no UAC prompt.

1. Install [Inno Setup 6](https://jrsoftware.org/isdl.php).
2. Stage `LemoineTools.dll` + `LemoineTools.addin` next to `LemoineTools.iss`.
3. Compile:

   ```cmd
   iscc installer\LemoineTools.iss
   ```

   Optional overrides:

   ```cmd
   iscc /DRevitYear=2025 /DAppVersion=1.2.0 /DSourceDir="..\publish" installer\LemoineTools.iss
   ```

4. Distribute the generated `Output\LemoineTools-Setup-2024.exe`. Users run it,
   click through, done — the uninstaller is generated automatically.

Key settings (`PrivilegesRequired=lowest`, `DefaultDirName={userappdata}\...`)
keep it entirely within the user's profile.

---

## Important: remove any old machine-wide copy

If a copy already exists in `C:\ProgramData\Autodesk\Revit\Addins\2024\`,
Revit may load that one instead — or load both and show duplicate ribbon
panels. For a clean per-user install, delete the `ProgramData` copy first
(that deletion **does** require admin, since it's a machine-wide folder).
