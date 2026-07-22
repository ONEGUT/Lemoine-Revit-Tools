# Lemoine Tools — Installer

A one-command build that produces a double-clickable `setup.exe` (via **Inno
Setup**) to install the plugin into Revit's add-ins folder on any Windows machine.

## What it produces

`installer/output/LemoineToolsSetup-<version>.exe` — an installer that:

- Installs **per-user by default, no admin rights needed**, into
  `%AppData%\Roaming\Autodesk\Revit\Addins\<year>\` for each Revit year it was built
  with (2024–2027). A "for all users / for me only" chooser appears at the start, so
  anyone with admin can instead do a machine-wide install into
  `C:\ProgramData\Autodesk\Revit\Addins\<year>\`. Revit loads the plugin from either
  location.
- Ships only Lemoine's own files (`LemoineTools.dll`, `LemoineTools.addin`, the
  `.deps.json` where present, and the `Strings\` folder). It never ships
  `RevitAPI.dll` / `RevitAPIUI.dll` — Revit provides those.
- **WPF-only build:** the WebView2 runtime DLLs and the `Web\` HTML assets are
  deliberately *not* shipped. Web UI is disabled on this branch, so WebView2 never
  loads; omitting our pinned `WebView2.Core` avoids the assembly-version clash with
  the copy Revit itself preloads (the crash seen on Revit 2024).

The installer packages these **straight from the location `LemoineTools.csproj`
deploys to** — its `DeployDir` / `OutputPath`,
`%ProgramData%\Autodesk\Revit\Addins\<year>\` — so a normal build is all the
staging that's needed. Because that is Revit's *shared* add-ins folder, the script
copies only Lemoine's named files, never the whole folder, so other vendors'
add-ins are left alone.
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
- `-SkipBuild` — don't rebuild; just package whatever is already deployed (e.g. after
  a Visual Studio build).
- `-Iscc "<path>\ISCC.exe"` — if Inno Setup isn't on `PATH`.

The script:

1. Builds each `Release<year>` to the location the csproj already deploys to
   (`%ProgramData%\Autodesk\Revit\Addins\<year>\`) — unless `-SkipBuild` is passed.
2. Detects which years are actually present there and packages only those.
3. Runs `ISCC`, which copies Lemoine's own files from that same location into
   `installer\output\LemoineToolsSetup-<version>.exe`.

`installer\output\` is a git-ignored build artifact.

> If you redirected the csproj's `DeployDir` somewhere non-standard, point the
> installer at it with `ISCC /DAddinsRoot=<parent-of-year-folders> ...`.

## Install / uninstall on a target machine

- Run `LemoineToolsSetup-<version>.exe`. Choose **"Install for me only"** for a
  no-admin per-user install, or **"Install for all users"** (needs admin) for a
  machine-wide one. Pick the Revit versions, finish. Start Revit — the **Lemoine
  Tools** ribbon loads.
- Uninstall from **Settings → Apps** (or Control Panel). Only the files the
  installer placed are removed; user settings in `%AppData%\LemoineTools\` are left
  in place so a reinstall keeps them.

## Not included (yet)

- **Code signing** — the `setup.exe` is unsigned, so Windows SmartScreen will show
  an "unknown publisher" warning. Signing needs a code-signing certificate.
- **Auto-update / version check.**
- **Bundling the WebView2 Evergreen Runtime** — assumed already present (Revit ships
  and uses WebView2).
