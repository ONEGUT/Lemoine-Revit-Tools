; ============================================================================
;  Lemoine Tools — Inno Setup installer script
; ============================================================================
;  Packages the plugin straight from the location LemoineTools.csproj deploys
;  each year's build to — its DeployDir / OutputPath:
;
;      %ProgramData%\Autodesk\Revit\Addins\<year>\
;
;  Flow: build the plugin normally (Visual Studio Build/F5 or `dotnet build`,
;  which deploys every year there), then compile this script. Only Lemoine's own
;  files are packaged — that is Revit's SHARED add-ins folder, so other vendors'
;  add-ins may sit alongside ours and must never be swept in.
;
;  Build + package in one step:
;      installer\build-installer.ps1
;
;  Or, after building the plugin, compile manually:
;      ISCC /DMyAppVersion=1.2.3 installer\LemoineTools.iss
;
;  Requires Inno Setup 6 (ISPP preprocessor — the default install).
; ============================================================================

#define MyAppName      "Lemoine Tools"
#define MyAppPublisher "Lemoine Tools"

; Version can be injected by build-installer.ps1 via /DMyAppVersion=...
#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif

; SOURCE root on the BUILD machine = the parent of each year's csproj DeployDir.
; Derived from this machine's ProgramData (matches the csproj default); override
; with /DAddinsRoot=... only if you redirected DeployDir somewhere else.
#ifndef AddinsRoot
  #define AddinsRoot GetEnv('ProgramData') + "\Autodesk\Revit\Addins"
#endif

; DESTINATION on the TARGET machine. {autoappdata} adapts to the chosen install
; mode: for an all-users (admin) install it resolves to {commonappdata}
; (%ProgramData%\...) — the machine-wide add-ins folder; for a per-user (no-admin)
; install it resolves to {userappdata} (%AppData%\Roaming\...). Revit reads add-in
; manifests from BOTH locations, so either install makes the plugin load.
#define AddinsFor(str Year) "{autoappdata}\Autodesk\Revit\Addins\" + Year

; True when year <Year>'s build is actually present at the source (our DLL there).
#define YearBuilt(str Year) FileExists(AddinsRoot + "\" + Year + "\LemoineTools.dll")

[Setup]
; A unique AppId keeps upgrades/uninstall tied to this product. Do not change it
; between versions or Windows will treat a new build as a separate product.
AppId={{5D9E7A24-3C81-4B6F-A0D2-9E14F7C63B85}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
VersionInfoVersion={#MyAppVersion}

; The plugin files go to ProgramData (see [Files]); {app} only holds the
; uninstaller, so it lives in Program Files, out of Revit's folder.
DefaultDirName={autopf}\Lemoine Tools
DisableDirPage=yes
DisableProgramGroupPage=yes
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\unins000.exe

; Default to a per-user install that needs NO admin rights (installs into
; %AppData%\Roaming\Autodesk\Revit\Addins\<year>\). PrivilegesRequiredOverridesAllowed=dialog
; adds a "for all users / for me only" chooser at the start, so anyone with admin
; can still pick the machine-wide ProgramData install; a standard user just picks
; "for me only" and is never prompted for elevation.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
; Ask for the install mode on EVERY run instead of silently reusing the previous
; choice (Inno's default), so "for me only" vs "for all users" can be re-picked each time.
UsePreviousPrivileges=no
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible

Compression=lzma2
SolidCompression=yes
WizardStyle=modern
OutputDir=output
OutputBaseFilename=LemoineToolsSetup-{#MyAppVersion}

[Types]
Name: "full";   Description: "All built Revit versions"
Name: "custom"; Description: "Choose Revit versions"; Flags: iscustom

; One component per Revit year, but only for years whose build is actually
; present at the source (a year that never built has no LemoineTools.dll there).
[Components]
#if YearBuilt("2024")
Name: "y2024"; Description: "Revit 2024"; Types: full custom
#endif
#if YearBuilt("2025")
Name: "y2025"; Description: "Revit 2025"; Types: full custom
#endif
#if YearBuilt("2026")
Name: "y2026"; Description: "Revit 2026"; Types: full custom
#endif
#if YearBuilt("2027")
Name: "y2027"; Description: "Revit 2027"; Types: full custom
#endif

; Ships ONLY Lemoine's own artifacts from each year's deploy folder — never the
; whole (shared) folder, so other vendors' add-ins are untouched. RevitAPI /
; RevitAPIUI are Private=False in the csproj (never deployed there), so they are
; never shipped. The .deps.json / Strings\ entries are guarded so both the net48
; (2024) and net8 (2025+) output shapes package cleanly.
;
; WPF-ONLY BUILD: the WebView2 runtime DLLs (WebView2Loader.dll,
; Microsoft.Web.WebView2.*.dll) and the Web\ HTML assets are DELIBERATELY NOT
; shipped. Web UI is hard-disabled on this branch, so WebView2 never initializes;
; shipping our pinned WebView2.Core would only risk the assembly-version clash with
; the copy Revit itself preloads (the "Assembly version conflict" crash). Leaving
; them out removes that risk and slims the installer.
[Files]
#if YearBuilt("2024")
Source: "{#AddinsRoot}\2024\LemoineTools.dll";   DestDir: "{#AddinsFor('2024')}"; Flags: ignoreversion; Components: y2024
Source: "{#AddinsRoot}\2024\LemoineTools.addin"; DestDir: "{#AddinsFor('2024')}"; Flags: ignoreversion; Components: y2024
  #if FileExists(AddinsRoot + "\2024\LemoineTools.deps.json")
Source: "{#AddinsRoot}\2024\LemoineTools.deps.json"; DestDir: "{#AddinsFor('2024')}"; Flags: ignoreversion; Components: y2024
  #endif
  #if DirExists(AddinsRoot + "\2024\Strings")
Source: "{#AddinsRoot}\2024\Strings\*"; DestDir: "{#AddinsFor('2024')}\Strings"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: y2024
  #endif
#endif

#if YearBuilt("2025")
Source: "{#AddinsRoot}\2025\LemoineTools.dll";   DestDir: "{#AddinsFor('2025')}"; Flags: ignoreversion; Components: y2025
Source: "{#AddinsRoot}\2025\LemoineTools.addin"; DestDir: "{#AddinsFor('2025')}"; Flags: ignoreversion; Components: y2025
  #if FileExists(AddinsRoot + "\2025\LemoineTools.deps.json")
Source: "{#AddinsRoot}\2025\LemoineTools.deps.json"; DestDir: "{#AddinsFor('2025')}"; Flags: ignoreversion; Components: y2025
  #endif
  #if DirExists(AddinsRoot + "\2025\Strings")
Source: "{#AddinsRoot}\2025\Strings\*"; DestDir: "{#AddinsFor('2025')}\Strings"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: y2025
  #endif
#endif

#if YearBuilt("2026")
Source: "{#AddinsRoot}\2026\LemoineTools.dll";   DestDir: "{#AddinsFor('2026')}"; Flags: ignoreversion; Components: y2026
Source: "{#AddinsRoot}\2026\LemoineTools.addin"; DestDir: "{#AddinsFor('2026')}"; Flags: ignoreversion; Components: y2026
  #if FileExists(AddinsRoot + "\2026\LemoineTools.deps.json")
Source: "{#AddinsRoot}\2026\LemoineTools.deps.json"; DestDir: "{#AddinsFor('2026')}"; Flags: ignoreversion; Components: y2026
  #endif
  #if DirExists(AddinsRoot + "\2026\Strings")
Source: "{#AddinsRoot}\2026\Strings\*"; DestDir: "{#AddinsFor('2026')}\Strings"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: y2026
  #endif
#endif

#if YearBuilt("2027")
Source: "{#AddinsRoot}\2027\LemoineTools.dll";   DestDir: "{#AddinsFor('2027')}"; Flags: ignoreversion; Components: y2027
Source: "{#AddinsRoot}\2027\LemoineTools.addin"; DestDir: "{#AddinsFor('2027')}"; Flags: ignoreversion; Components: y2027
  #if FileExists(AddinsRoot + "\2027\LemoineTools.deps.json")
Source: "{#AddinsRoot}\2027\LemoineTools.deps.json"; DestDir: "{#AddinsFor('2027')}"; Flags: ignoreversion; Components: y2027
  #endif
  #if DirExists(AddinsRoot + "\2027\Strings")
Source: "{#AddinsRoot}\2027\Strings\*"; DestDir: "{#AddinsFor('2027')}\Strings"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: y2027
  #endif
#endif

[Code]
{ Returns True if a Revit.exe process is currently running. The plugin DLL is
  file-locked while Revit is open, so installing over a live Revit would fail to
  overwrite it — warn the user before that happens. }
function RevitIsRunning(): Boolean;
var
  ResultCode: Integer;
begin
  Result := False;
  { `find` exits 0 when Revit.exe appears in the tasklist, 1 otherwise. }
  if Exec(ExpandConstant('{cmd}'),
          '/C tasklist /FI "IMAGENAME eq Revit.exe" | find /I "Revit.exe"',
          '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Result := (ResultCode = 0);
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
  if RevitIsRunning() then
  begin
    if MsgBox('Revit appears to be running.' + #13#10 +
              'Please close all Revit windows before continuing, otherwise the ' +
              'plugin files may be locked and cannot be updated.' + #13#10 + #13#10 +
              'Continue anyway?', mbConfirmation, MB_YESNO) = IDNO then
      Result := False;
  end;
end;
