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

## Note on an existing machine-wide copy

Revit loads manifests from both locations together. If an all-users manifest
(`ProgramData`) shares the **same filename** as the per-user one, Revit ignores
the all-users copy — so our `LemoineTools.addin` in `%AppData%` wins and you
won't load a stale machine-wide build. Duplicate ribbon panels would only occur
if a *differently named* manifest also points at Lemoine Tools. For a clean
install, removing any old `ProgramData` copy is still tidiest (that deletion
**does** require admin, since it's a machine-wide folder).

### Revit 2027+

The per-user `%AppData%\Autodesk\Revit\Addins\<year>\` path is unchanged, so
these installers keep working. Only the *all-users* path moved — to
`C:\Program Files\Autodesk\Revit\Addins\2027\` (`ProgramData` is ignored by
Revit 2027). Not relevant to a per-user install.
