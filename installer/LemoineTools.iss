; Lemoine Tools - per-user installer (no administrator privileges required)
;
; Build with Inno Setup 6 (https://jrsoftware.org/isdl.php):
;     iscc installer\LemoineTools.iss
;
; Optional overrides (pass with /D on the iscc command line):
;     iscc /DRevitYear=2025 /DAppVersion=1.2.0 /DSourceDir="..\publish" installer\LemoineTools.iss
;
; By default the two payload files are expected next to this .iss file.

#ifndef RevitYear
  #define RevitYear "2024"
#endif
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "."
#endif

[Setup]
; A stable, installer-specific GUID (distinct from the Revit AddInId).
AppId={{A1E9C7D4-6B82-4F3A-9C21-7E5D0B3F4A60}
AppName=Lemoine Tools
AppVersion={#AppVersion}
AppPublisher=Lemoine Tools
AppPublisherURL=https://github.com/onegut/lemoine-revit-tools

; Install into the current user's Revit add-ins folder. No admin needed.
DefaultDirName={userappdata}\Autodesk\Revit\Addins\{#RevitYear}
DisableDirPage=yes
DisableProgramGroupPage=yes

; Run unelevated; if a user launches "as administrator" anyway, fall back to
; a per-user install rather than dumping files in a machine-wide location.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

OutputBaseFilename=LemoineTools-Setup-{#RevitYear}
Uninstallable=yes
Compression=lzma2
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64
WizardStyle=modern

[Files]
Source: "{#SourceDir}\LemoineTools.dll";   DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\LemoineTools.addin"; DestDir: "{app}"; Flags: ignoreversion

[Messages]
; Friendly closing note.
FinishedLabel=Lemoine Tools has been installed for the current user.%n%nRestart Revit {#RevitYear} to load the add-in.
