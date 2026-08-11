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
- Ships only Lemoine's own files: `LemoineTools.dll`, `LemoineTools.addin`, the
  `.deps.json` where present (net8 years only), and the two loose folders the plugin
  reads from beside its DLL at runtime — `Strings\` (user-facing text) and `Seed\`
  (default trade/legend/clash libraries). It never ships `RevitAPI.dll` /
  `RevitAPIUI.dll` — Revit provides those. There are no third-party runtime DLLs:
  this is a WPF-only add-in with zero `PackageReference`s.
- Lets the user tick which Revit versions to install (only versions that were built
  are offered).
- Warns if Revit is running (the plugin DLL is file-locked while Revit is open).
- Registers a proper entry in **Add/Remove Programs** with a clean uninstall.

The installer packages these **straight from the location `LemoineTools.csproj`
deploys to** — its `DeployDir` / `OutputPath`,
`%ProgramData%\Autodesk\Revit\Addins\<year>\` — so a normal build is all the
staging that's needed. Because that is Revit's *shared* add-ins folder, the script
copies only Lemoine's named files, never the whole folder, so other vendors'
add-ins are left alone.

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
installer\build-installer.ps1
```

That's the whole command for a routine release. With `LemoineToolsSetup-1.0.1.exe`
currently in the shared folder, it builds **1.0.2**, copies it there, and deletes
**1.0.1** — so the folder always holds exactly one, current installer.

Options:

- `-Version <x.y.z>` — set the version by hand instead of auto-detecting it.
- `-Bump patch|minor|major|none` — which part of the detected version to increment.
  Default `patch` (`1.0.1` → `1.0.2`); `minor` gives `1.1.0`, `major` gives `2.0.0`,
  `none` rebuilds the published version in place.
- `-PublishDir "<path>"` — the shared folder to read the current version from and
  publish into. Defaults to the synced VDC library (see below).
- `-NoPublish` — build locally only; never touch the shared folder.
- `-KeepOld` — publish the new installer but leave the previous ones in place.
- `-CheckPublish` — show which folder it resolved to, what's published there now, and
  what version it would build. Builds nothing and changes nothing. Run this first if
  publishing isn't working.
- `-Years 2024,2025` — build only specific years. Default is all four.
- `-SkipBuild` — don't rebuild; just package whatever is already deployed (e.g. after
  a Visual Studio build).
- `-Iscc "<path>\ISCC.exe"` — if Inno Setup isn't on `PATH`.

The script:

1. Reads the shared folder, takes the highest `LemoineToolsSetup-<version>.exe` it
   finds there, and bumps it — that becomes this build's version. (`-Version` skips
   the detection.)
2. Builds each `Release<year>` to the location the csproj already deploys to
   (`%ProgramData%\Autodesk\Revit\Addins\<year>\`) — unless `-SkipBuild` is passed.
3. Detects which years are actually present there and packages only those.
4. Runs `ISCC`, which copies Lemoine's own files from that same location into
   `installer\output\LemoineToolsSetup-<version>.exe`.
5. Publishes: copies that `.exe` to the shared folder, verifies the copy by SHA256,
   then deletes the older installers.

`installer\output\` is a git-ignored build artifact — the local copy is kept there
as well as published, so there's always something to fall back on.

> If you redirected the csproj's `DeployDir` somewhere non-standard, point the
> installer at it with `ISCC /DAddinsRoot=<parent-of-year-folders> ...`.

## Publishing to the shared VDC folder

`-PublishDir` defaults to the OneDrive-synced VDC Department library:

```
%USERPROFILE%\The Lemoine Company\VDC Department - Documents\1 General\1.5 Software\Software Resources\Revit\Plugin
```

It's built from `%USERPROFILE%` rather than hardcoded, so it resolves for anyone who
has that SharePoint library synced. Pass `-PublishDir "<path>"` for anywhere else.

Safety rules the publish step follows, all of them deliberate:

- **Compile local, then copy.** `ISCC` writes to `installer\output\` first; only a
  finished `.exe` is copied to the shared folder. An interrupted compile can never
  leave a half-written installer where people download it from.
- **Copy first, verify, then delete.** The new installer is copied in and SHA256-checked
  against the file that was just built *before* any old one is removed — so a failed or
  corrupted publish leaves the previous version usable rather than an empty folder.
- **Only our own filenames are ever deleted.** Nothing outside the exact
  `LemoineToolsSetup-<numeric version>.exe` shape is touched.
- **A newer installer is never deleted.** If the folder holds a version higher than the
  one being published (a mistyped `-Version`, or an auto-detect that fell back because
  OneDrive was offline), it's kept and reported instead of overwritten.
- **A missing publish folder is not fatal.** If OneDrive hasn't synced the library, the
  build still completes into `installer\output\` and the script says it did not publish.

If you'd rather have `ISCC` write straight into a folder without the script, the `.iss`
takes an output-folder override:

```powershell
ISCC /DMyAppVersion=1.0.2 /DOutputDir="C:\some\folder" installer\LemoineTools.iss
```

Note that this writes the compiler's output directly to that folder and does none of the
version detection, verification, or old-file cleanup above.

### Troubleshooting: nothing arrives in the shared folder

Run the preflight first — it answers the question in a couple of seconds without a build:

```powershell
installer\build-installer.ps1 -CheckPublish
```

It prints the folder it resolved to, whether that folder exists, which installers are in
it, and the version it would build next.

**If a setup wizard opens asking you to install, the script did not run.**
`build-installer.ps1` never launches the installer it builds — it only compiles and
copies. A wizard opening means the **Inno Setup Compiler IDE** compiled it: its
**Run (F9)** command compiles *and* runs the output. That path knows nothing about
versioning or publishing, so it stamps the `.iss` default version and leaves the `.exe`
in `installer\output\`. Either use **Build > Compile (Ctrl+F9)** in that IDE, or run the
script — the script is what does the versioning and publishing.

Other things worth checking:

- **Run it from PowerShell, from the repo root**, e.g. `installer\build-installer.ps1`.
  If it's blocked by execution policy, use
  `powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1`.
- **Read the first three lines of output.** The script prints `Publish folder: <path>`
  before it does anything, and warns loudly if that folder doesn't exist.
- **OneDrive must have the library synced locally.** The path has to exist on disk as a
  real folder; a library you've only opened in the browser won't be there. Pass
  `-PublishDir "<path>"` if yours syncs somewhere else.

## Install / uninstall on a target machine

- Run `LemoineToolsSetup-<version>.exe`. Choose **"Install for me only"** for a
  no-admin per-user install, or **"Install for all users"** (needs admin) for a
  machine-wide one. Pick the Revit versions, finish. Start Revit — the **Lemoine
  Tools** ribbon loads.
- Uninstall from **Settings → Apps** (or Control Panel). Only the files the
  installer placed are removed; user settings in `%AppData%\LemoineTools\` are left
  in place so a reinstall keeps them.

### Where your data lives (and why the install mode doesn't change it)

Everything the plugin writes goes to `%AppData%\LemoineTools\` — settings, the colour
picker, naming patterns, `diagnostics.log`, and the optional `Seed\` override. That is a
*per-user* location, so it is the same folder whichever install mode you pick, and it
survives uninstall and reinstall. Per-project data (trade/legend/clash libraries, filter
ownership) is stored inside the `.rvt` itself, so it travels with the model rather than
the machine. Nothing the plugin writes ever lands next to the installed DLL — `Strings\`
and `Seed\` are read-only there.

Because settings are per-user and per-machine, installing for **all users** does not give
every user your settings: each Windows account starts fresh from the shipped defaults. To
push an office-standard starting library, drop a seed file into each user's
`%AppData%\LemoineTools\Seed\`, or replace the shipped `Seed\` folder before building
the installer.

### If you switch install modes

Revit loads add-ins from **both** the all-users and per-user folders, so having the plugin
in both would load it twice — duplicate ribbon panels. The installer detects a copy in the
other location and offers to remove it (it deletes only the `.addin` manifest, which is
enough to stop Revit loading it; your settings are untouched). If it can't remove the file —
typically a machine-wide copy while you're doing a no-admin install — it tells you the exact
path to delete by hand.

## Not included (yet)

- **Code signing** — the `setup.exe` is unsigned, so Windows SmartScreen will show
  an "unknown publisher" warning. Signing needs a code-signing certificate.
- **Auto-update / version check.**
