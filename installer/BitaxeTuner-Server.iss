; Inno Setup: BitaxeTuner-Server als Windows-Dienst (für einen zweiten PC/Mini-PC, der 24/7 läuft)
; Gebaut von build.ps1 bzw. .github/workflows/release.yml
#ifndef MyAppVersion
  #define MyAppVersion "0.3.0"
#endif
#define MyAppName "BitaxeTuner-Server"
#define MyAppExe "BitaxeTuner.Server.exe"
#define MyService "BitaxeTuner"
#define MyPort "8484"
#define MyAppUrl "https://github.com/Elemirus1996/BitaxeTuner"

[Setup]
AppId={{C4F1A2D7-8B3E-4F59-A6D2-7E9B1C3F5A84}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher=BitaxeTuner contributors
AppPublisherURL={#MyAppUrl}
AppSupportURL={#MyAppUrl}/issues
AppUpdatesURL={#MyAppUrl}/releases
DefaultDirName={autopf}\BitaxeTuner Server
DisableProgramGroupPage=yes
DefaultGroupName={#MyAppName}
; Ein Dienst braucht Administratorrechte
PrivilegesRequired=admin
LicenseFile=..\LICENSE
OutputDir=..\artifacts
OutputBaseFilename=BitaxeTuner-Server-Setup-{#MyAppVersion}
SetupIconFile=..\src\BitaxeTuner.App\Assets\app.ico
UninstallDisplayName={#MyAppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64

[Languages]
Name: "german"; MessagesFile: "compiler:Languages\German.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "..\artifacts\publish-server-win\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\BitaxeTuner-Server öffnen"; Filename: "{code:ServerUrl}"
Name: "{autoprograms}\BitaxeTuner-Server Datenordner"; Filename: "{commonappdata}\BitaxeTuner"

[Run]
; Dienst anlegen (Autostart, verzögert nach dem Netzwerk), bei Absturz nach 10 s neu starten
Filename: "{sys}\sc.exe"; Parameters: "create {#MyService} binPath= ""\""{app}\{#MyAppExe}\"""" start= delayed-auto DisplayName= ""BitaxeTuner-Server"""; Flags: runhidden waituntilterminated; StatusMsg: "Dienst wird eingerichtet …"; Check: not ServiceExists
Filename: "{sys}\sc.exe"; Parameters: "description {#MyService} ""Überwachung und Tuning für Bitaxe/NerdAxe – Browser: Port {#MyPort} (Startmenü: BitaxeTuner-Server öffnen)"""; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "failure {#MyService} reset= 86400 actions= restart/10000/restart/10000/restart/60000"; Flags: runhidden waituntilterminated
; Firewall: nur im privaten Netzwerkprofil (Heimnetz), nicht öffentlich
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""BitaxeTuner-Server"""; Flags: runhidden waituntilterminated
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""BitaxeTuner-Server"" dir=in action=allow protocol=TCP localport={#MyPort} profile=private"; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "start {#MyService}"; Flags: runhidden waituntilterminated; StatusMsg: "Dienst wird gestartet …"
Filename: "{code:ServerUrl}"; Description: "Oberfläche im Browser öffnen (Einrichtung)"; Flags: shellexec postinstall skipifsilent nowait

[UninstallRun]
Filename: "{sys}\sc.exe"; Parameters: "stop {#MyService}"; Flags: runhidden waituntilterminated; RunOnceId: "StopService"
Filename: "{sys}\sc.exe"; Parameters: "delete {#MyService}"; Flags: runhidden waituntilterminated; RunOnceId: "DeleteService"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""BitaxeTuner-Server"""; Flags: runhidden waituntilterminated; RunOnceId: "DeleteFirewall"

[Messages]
german.FinishedLabel=Der BitaxeTuner-Server läuft jetzt als Windows-Dienst.%n%nEinrichtung im Browser: Startmenü → „BitaxeTuner-Server öffnen“ (neue Installationen verschlüsselt: https://localhost:{#MyPort}/ – die Warnung wegen des selbst signierten Zertifikats bestätigen).%nDer Einrichtungs-Code steht in C:\ProgramData\BitaxeTuner\SETUP-CODE.txt (nur für Administratoren lesbar, z. B. Editor „Als Administrator ausführen“).%n%nDaten: C:\ProgramData\BitaxeTuner (bleiben bei Updates und Deinstallation erhalten).

[Code]
// Adresse der Oberfläche: Neuinstallationen starten mit HTTPS (Audit S4), bestehende behalten ihre Wahl
// (server-settings.json im Datenordner; ohne Datei und mit vorhandenen Daten: HTTP wie bisher)
function ServerUrl(Param: String): String;
var
  Data: String;
  Content: AnsiString;
begin
  Data := ExpandConstant('{commonappdata}\BitaxeTuner');
  Result := 'http://localhost:{#MyPort}/';
  if LoadStringFromFile(Data + '\server-settings.json', Content) then
  begin
    if Pos('true', Lowercase(String(Content))) > 0 then Result := 'https://localhost:{#MyPort}/';
  end
  else if (not FileExists(Data + '\server-auth.json')) and (not FileExists(Data + '\config.json')) then
    Result := 'https://localhost:{#MyPort}/';
end;

function ServiceExists(): Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec(ExpandConstant('{sys}\sc.exe'), 'query {#MyService}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

// Vor dem Kopieren: laufenden Dienst stoppen (er stellt dabei Benchmark-Einstellungen wieder her) und warten
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode, I: Integer;
begin
  if (CurStep = ssInstall) and ServiceExists() then
  begin
    Exec(ExpandConstant('{sys}\sc.exe'), 'stop {#MyService}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    for I := 1 to 60 do
    begin
      Exec(ExpandConstant('{sys}\cmd.exe'), '/c sc query {#MyService} | find "STOPPED"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      if ResultCode = 0 then Break;
      Sleep(1000);
    end;
  end;
end;
