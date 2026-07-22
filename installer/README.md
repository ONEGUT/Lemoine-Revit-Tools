# Lemoine Tools — Installer

A one-command build that produces a double-clickable `setup.exe` (via **Inno
Setup**) to install the plugin into Revit's add-ins folder on any Windows machine.

## What it produces

`installer/output/LemoineToolsSetup-<version>.exe` — a per-machine installer that:

- Installs into `C:\ProgramData\Autodesk\Revit\Addins\<year>\` for each Revit year
  it was built with (2024–2027).
- Ships only Lemoine's own files (`LemoineTools.dll`, `LemoineTools.addin`,
  `WebView2Loader.dll`, the WebView2 managed DLLs, and the `Strings\` and `Web\`
  folders). It never ships `RevitAPI.dll` / `RevitAPIUI.dll` — Revit provides those.
- Lets the user tick which Revit versions to install (only versions that were built
  are offered).
- Warns if Revit is running (the plugin DLL is file-locked while Revit is open).
- Registers a proper entry in **Add/Remove Programs** with a clean uninstall.

## Prerequisites (Windows only)

1. **.NET SDK** and the **Revit API DLLs** present for each year you want, in
   `libs\` (2024), `libs2025\`, `libs2026\`, `libs2027\`. Years without real API
   DLLs are skipped automatically.
2. **Inno Setup 6** — <https://jrsoftware.org/isdl.php>. After installing, make sure
   `ISCC.exe` is on your `PATH` (it lives in `C:\Program Files (x86)\Inno Setup 6\`),
   or pass its full path with `-Iscc`.

## Build

From the repo root, in PowerShell:

```powershell
installer\build-installer.ps1 -Version 1.2.0
```

Options:

- `-Version <x.y.z>` — stamped into the installer and the output filename. Default `1.0.0`.
- `-Years 2024,2025` — build only specific years. Default is all four.
- `-Iscc "<path>\ISCC.exe"` — if Inno Setup isn't on `PATH`.

The script:

1. Builds each `Release<year>` straight into a clean `installer\stage\<year>\`
   (via a `DeployDir` override, so it never touches the live ProgramData Addins
   folder or picks up another vendor's add-in files).
2. Skips any year that doesn't build and warns you.
3. Runs `ISCC` to emit `installer\output\LemoineToolsSetup-<version>.exe`.

`installer\stage\` and `installer\output\` are git-ignored build artifacts.

## Install / uninstall on a target machine

- Run `LemoineToolsSetup-<version>.exe`, accept the elevation prompt (writing to
  ProgramData needs admin), pick the Revit versions, finish. Start Revit — the
  **Lemoine Tools** ribbon loads.
- Uninstall from **Settings → Apps** (or Control Panel). Only the files the
  installer placed are removed; user settings in `%AppData%\LemoineTools\` are left
  in place so a reinstall keeps them.

## Not included (yet)

- **Code signing** — the `setup.exe` is unsigned, so Windows SmartScreen will show
  an "unknown publisher" warning. Signing needs a code-signing certificate.
- **Auto-update / version check.**
- **Bundling the WebView2 Evergreen Runtime** — assumed already present (Revit ships
  and uses WebView2).
