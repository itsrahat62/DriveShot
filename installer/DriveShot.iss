; DriveShot installer (Inno Setup 6).
; Build: dotnet publish (self-contained single file) into ..\release, then
;   ISCC.exe installer\DriveShot.iss
; Installs per user (no admin rights needed) into %LOCALAPPDATA%\Programs\DriveShot.

#define AppName "DriveShot"
#define AppVersion "1.1.0"
#define AppExe "DriveShot.exe"
#define RepoUrl "https://github.com/itsrahat62/DriveShot"

[Setup]
AppId={{8C1F5B2E-4D7A-4F6B-9E3A-2B7C1D9E0A11}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=itsrahat62
AppPublisherURL={#RepoUrl}
AppSupportURL={#RepoUrl}/issues
AppUpdatesURL={#RepoUrl}/releases
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\release
OutputBaseFilename=DriveShot-Setup
SetupIconFile=..\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=no

[Tasks]
Name: "autostart"; Description: "Start DriveShot automatically when Windows starts (recommended, so PrtSc always works)"; GroupDescription: "Startup:"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\release\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\apps-script\Code.gs"; DestDir: "{app}\Google Apps Script"; Flags: ignoreversion

[INI]
Filename: "{app}\How to connect Google Drive.url"; Section: "InternetShortcut"; Key: "URL"; String: "{#RepoUrl}/blob/main/docs/connect-google-drive.md"

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; Comment: "Screenshot tool that uploads to Google Drive"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[UninstallDelete]
Type: files; Name: "{app}\How to connect Google Drive.url"

[Registry]
; Lets DriveShot know it was installed (so it doesn't ask about startup again).
Root: HKCU; Subkey: "Software\DriveShot"; ValueType: string; ValueName: "InstallDir"; ValueData: "{app}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "DriveShot"; ValueData: """{app}\{#AppExe}"" --autostart"; Tasks: autostart; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\{#AppExe}"; Parameters: "--exit"; Flags: runhidden waituntilterminated; RunOnceId: "ExitDriveShot"
Filename: "{sys}\taskkill.exe"; Parameters: "/f /im {#AppExe}"; Flags: runhidden waituntilterminated; RunOnceId: "KillDriveShot"

[Code]
procedure StopRunningApp();
var
  rc: Integer;
begin
  if FileExists(ExpandConstant('{app}\{#AppExe}')) then
    Exec(ExpandConstant('{app}\{#AppExe}'), '--exit', '', SW_HIDE, ewWaitUntilTerminated, rc);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, rc);
  Sleep(300);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopRunningApp();
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  // Unticking "Start with Windows" on a reinstall removes the old startup entry.
  if (CurStep = ssPostInstall) and not WizardIsTaskSelected('autostart') then
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'DriveShot');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    if SuppressibleMsgBox('Also delete your DriveShot settings and upload history?' + #13#10 +
                          '(Your screenshots in Google Drive and in Pictures\DriveShot are kept.)',
                          mbConfirmation, MB_YESNO, IDNO) = IDYES then
      DelTree(ExpandConstant('{userappdata}\DriveShot'), True, True, True);
end;
