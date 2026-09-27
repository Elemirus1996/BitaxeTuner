; Inno Setup script for BitaxeTuner – built by build.ps1 (locally) or .github/workflows/release.yml
#ifndef MyAppVersion
  #define MyAppVersion "0.3.0"
#endif
#define MyAppName "BitaxeTuner"
#define MyAppExe "BitaxeTuner.exe"
#define MyAppUrl "https://github.com/Elemirus1996/BitaxeTuner"

[Setup]
AppId={{8E3A5C2B-5D7E-4B8A-9C61-2F4B7A1D9E30}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher=BitaxeTuner contributors
AppPublisherURL={#MyAppUrl}
AppSupportURL={#MyAppUrl}/issues
AppUpdatesURL={#MyAppUrl}/releases
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
; Zielordner immer abfragen – z. B. F:\Programme\BitaxeTuner
DisableDirPage=no
; Installation für alle Benutzer (Admin) oder nur für den aktuellen Benutzer (ohne Admin) wählbar
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
LicenseFile=..\LICENSE
OutputDir=..\artifacts
OutputBaseFilename=BitaxeTuner-Setup-{#MyAppVersion}
SetupIconFile=..\src\BitaxeTuner.App\Assets\app.ico
UninstallDisplayIcon={app}\{#MyAppExe}
UninstallDisplayName={#MyAppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
CloseApplications=yes

[Languages]
Name: "german"; MessagesFile: "compiler:Languages\German.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\artifacts\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExe}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent
; Update aus der App (/SILENT): danach automatisch wieder starten
Filename: "{app}\{#MyAppExe}"; Flags: nowait; Check: WizardSilent
