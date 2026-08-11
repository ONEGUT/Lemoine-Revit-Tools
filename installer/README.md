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

There are two ways to release, and **both auto-version and publish to the shared VDC
folder**. Pick whichever suits you — they agree on version numbering.

### A. From the Inno Setup IDE (compile the `.iss` directly)

1. Build the plugin in Visual Studio as normal, so each Revit year is deployed to
   `%ProgramData%\Autodesk\Revit\Addins\<year>\`.
2. Open `installer\LemoineTools.iss` in Inno Setup.
3. **Build → Compile (Ctrl+F9).**

That's it. While the script preprocesses it runs `publish-version.ps1`, which reads
the newest `LemoineToolsSetup-<version>.exe` in the shared folder, bumps the patch
number, and clears the old one out; the compiler then writes the new `setup.exe`
straight into that folder. With `1.0.1` published, you get `1.0.2` — and `1.0.1` gone.

The version bump, the output folder, and any warning are printed in the IDE's
compiler output window, e.g.:

```
Auto-publish: 1.0.1 -> 1.0.2 (patch); removed 1 old installer(s), backed up to C:\...\Temp\LemoineToolsSetup-backup
Auto-publish output folder: C:\Users\...\Revit\Plugin
```

> **Use Compile (Ctrl+F9), not Run (F9).** Run compiles *and launches* the installer,
> so it tries to install the plugin on your own machine. That's the wizard you've been
> closing — and it's a different code path that does no versioning or publishing.

This mode only packages the Revit years already deployed on your machine; it does not
build the plugin. Use option B if you want the four-year build done for you too.

To compile without publishing (a local test build into `installer\output\`), use
`/DAutoPublish=0`, or change `#define AutoPublish 1` to `0` near the top of the `.iss`.

### B. From PowerShell (also builds all four Revit years)

From the repo root:

```powershell
installer\build-installer.ps1
```

With `LemoineToolsSetup-1.0.1.exe` currently in the shared folder, it builds **1.0.2**,
copies it there, and deletes **1.0.1**. It passes `/DAutoPublish=0` so the `.iss`'s own
publishing stays out of its way, and does the publish itself afterwards — compiling
locally first and verifying the copy by SHA256 before removing anything.

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

Safety rules both modes follow, all of them deliberate:

- **Only our own filenames are ever deleted.** Nothing outside the exact
  `LemoineToolsSetup-<numeric version>.exe` shape is touched.
- **A newer installer is never deleted.** If the folder holds a version higher than the
  one being published (a mistyped version, or a detect that fell back because OneDrive
  was offline), it's kept and reported instead of overwritten.
- **A missing publish folder is not fatal.** If OneDrive hasn't synced the library, the
  build still completes into `installer\output\` and says it did not publish.
- **A failed version lookup aborts the compile.** If `publish-version.ps1` can't run —
  execution policy, a crash, an unreadable folder — it doesn't write its success marker
  and the `.iss` stops with an error, rather than stamping a guessed version.

### The two modes differ in *when* the old installer is removed

This is the one real difference, and it's forced by Inno Setup: **ISPP has no
post-compile hook.** Anything the `.iss` does happens while it preprocesses — before
`setup.exe` exists.

- **Option A (IDE)** removes the old installer *before* compiling the new one. Every
  removed file is copied to `%TEMP%\LemoineToolsSetup-backup\` first, and that path is
  printed in the compiler output. So if the compile then fails, the shared folder is
  briefly without an installer and you restore it from that backup (or just fix the
  error and compile again).
- **Option B (PowerShell)** compiles locally first, copies to the shared folder,
  SHA256-verifies the copy, and *only then* deletes the old one — so a failure at any
  point leaves the previous installer in place.

If you want that stronger guarantee, use option B. For a routine release where the
plugin already builds cleanly, option A is fine and is a single keystroke.

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

**If a setup wizard opens asking you to install, you used Run (F9), not Compile.**
In the Inno Setup IDE, **Run (F9)** compiles *and then launches* the installer, so it
tries to install the plugin on your own machine. Use **Build → Compile (Ctrl+F9)**
instead. Neither `build-installer.ps1` nor a plain Compile ever launches the installer.

**If the compile stops with "Auto-publish failed",** `publish-version.ps1` didn't
produce a version. Run it by hand to see the real error — it prints nothing when the
compiler launches it:

```powershell
cd installer
.\publish-version.ps1 -OutFile test.isi
type test.isi        # PublishNextVersion on success, PublishError on failure
```

The most likely cause is PowerShell being blocked from running scripts. The `.iss`
already invokes it with `-ExecutionPolicy Bypass`, so if it's still blocked that's a
machine-wide Group Policy, which `Bypass` cannot override — tell me and we'll do the
versioning without PowerShell. Compile with `/DAutoPublish=0` to keep building locally
in the meantime.

**If Inno says "The [Setup] section must include an AppVersion or AppVerName
directive",** you're on an old copy of this script — that was the symptom when the
version was passed back through an INI file and arrived empty. Pull the latest
`installer\` and it will report the actual reason instead.

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
