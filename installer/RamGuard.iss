; RamGuard installer (Inno Setup 6). Built by build.ps1 -Installer.
; Installs for the current user only, so it never needs admin rights.

#define AppName "RamGuard"
#define AppVersion "1.0.0"

[Setup]
AppId={{6C1D3B7E-5A2F-4C8E-9B1A-2F7D4E8C9A01}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=RamGuard
DefaultDirName={localappdata}\Programs\RamGuard
DefaultGroupName=RamGuard
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=RamGuard-Setup-{#AppVersion}
SetupIconFile=..\assets\RamGuard.ico
UninstallDisplayIcon={app}\RamGuard.exe
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=no

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"
Name: "startup"; Description: "Start RamGuard in the tray when I sign in to Windows"; GroupDescription: "Startup:"

[Files]
Source: "..\bin\RamGuard.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\RamGuard"; Filename: "{app}\RamGuard.exe"; Comment: "Keeps RAM for the apps you're using"
Name: "{autodesktop}\RamGuard"; Filename: "{app}\RamGuard.exe"; Comment: "Keeps RAM for the apps you're using"; Tasks: desktopicon

[Registry]
; Same value name the app's own "Start with Windows" checkbox uses, so they stay in sync.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "RamGuard"; \
    ValueData: """{app}\RamGuard.exe"" --minimized"; Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\RamGuard.exe"; Description: "Open RamGuard now"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/IM RamGuard.exe /F"; Flags: runhidden; RunOnceId: "StopRamGuard"

[Code]
// Close a running RamGuard (it lives in the tray) so its files can be replaced.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM RamGuard.exe /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(500);
  Result := '';
end;
