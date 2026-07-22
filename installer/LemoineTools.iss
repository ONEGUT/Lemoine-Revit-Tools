; ============================================================================
;  Lemoine Tools — Inno Setup installer script
; ============================================================================
;  Produces a per-machine setup.exe that installs the plugin into Revit's
;  add-ins folder for every Revit year that was staged.
;
;  DO NOT run ISCC on this file directly against a fresh checkout — it packages
;  from installer\stage\<year>\, which is produced by the build step. Use:
;
;      installer\build-installer.ps1
;
;  which builds each Release<year> configuration into installer\stage\<year>\
;  and then invokes ISCC on this script. To compile manually after staging:
;
;      ISCC /DMyAppVersion=1.2.3 installer\LemoineTools.iss
;
;  Requires Inno Setup 6 (ISPP preprocessor enabled — the default install).
; ============================================================================

#define MyAppName      "Lemoine Tools"
#define MyAppPublisher "Lemoine Tools"

; Version can be injected by build-installer.ps1 via /DMyAppVersion=...
#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif

; Staging root produced by the build step. SourcePath is this .iss file's
; directory (with a trailing backslash), so this resolves to installer\stage.
#define StageDir SourcePath + "stage"

; Revit's shared per-machine add-ins folder for a given year.
#define AddinsFor(str Year) "{commonappdata}\Autodesk\Revit\Addins\" + Year

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

; Writing to ProgramData requires elevation.
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible

Compression=lzma2
SolidCompression=yes
WizardStyle=modern
OutputDir=output
OutputBaseFilename=LemoineToolsSetup-{#MyAppVersion}

[Types]
Name: "full";   Description: "All staged Revit versions"
Name: "custom"; Description: "Choose Revit versions"; Flags: iscustom

; One component per Revit year, but only for years that were actually staged
; (a year whose libs<year>\ lacks the real Revit API DLLs never builds, so its
; stage\<year>\ folder is absent and its component/files are compiled out).
[Components]
#if DirExists(StageDir + "\2024")
Name: "y2024"; Description: "Revit 2024"; Types: full custom
#endif
#if DirExists(StageDir + "\2025")
Name: "y2025"; Description: "Revit 2025"; Types: full custom
#endif
#if DirExists(StageDir + "\2026")
Name: "y2026"; Description: "Revit 2026"; Types: full custom
#endif
#if DirExists(StageDir + "\2027")
Name: "y2027"; Description: "Revit 2027"; Types: full custom
#endif

; Each year's whole staged output (LemoineTools.dll, LemoineTools.addin,
; WebView2Loader.dll, Microsoft.Web.WebView2.*.dll, Strings\, Web\) is copied
; into that year's Addins folder. RevitAPI/RevitAPIUI are never staged, so they
; are never shipped. On uninstall Inno removes only the files it installed and
; leaves the folder if other add-ins still occupy it.
[Files]
#if DirExists(StageDir + "\2024")
Source: "{#StageDir}\2024\*"; DestDir: "{#AddinsFor('2024')}"; Excludes: "*.pdb"; Flags: recursesubdirs createallsubdirs ignoreversion; Components: y2024
#endif
#if DirExists(StageDir + "\2025")
Source: "{#StageDir}\2025\*"; DestDir: "{#AddinsFor('2025')}"; Excludes: "*.pdb"; Flags: recursesubdirs createallsubdirs ignoreversion; Components: y2025
#endif
#if DirExists(StageDir + "\2026")
Source: "{#StageDir}\2026\*"; DestDir: "{#AddinsFor('2026')}"; Excludes: "*.pdb"; Flags: recursesubdirs createallsubdirs ignoreversion; Components: y2026
#endif
#if DirExists(StageDir + "\2027")
Source: "{#StageDir}\2027\*"; DestDir: "{#AddinsFor('2027')}"; Excludes: "*.pdb"; Flags: recursesubdirs createallsubdirs ignoreversion; Components: y2027
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
