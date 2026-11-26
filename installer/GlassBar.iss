; GlassBar installer (Inno Setup 6). Build it with build-installer.bat, or in GitHub Actions on a version tag.
;   iscc /DAppVersion=1.2.3 /DSourceDir=..\out-installer installer\GlassBar.iss
; SourceDir must hold a self-contained publish (dotnet publish ... --self-contained true), so users don't need
; to install .NET first.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\out-installer"
#endif

[Setup]
; Never change AppId: Windows uses it to recognise upgrades and the uninstall entry.
AppId={{6F0C8E2B-3A51-4C7D-9B1E-2F7A4D9C5E10}
AppName=GlassBar
AppVersion={#AppVersion}
AppVerName=GlassBar {#AppVersion}
AppPublisher=Frosty9999
AppPublisherURL=https://github.com/Frosty9999/GlassBar
AppSupportURL=https://github.com/Frosty9999/GlassBar/issues
AppUpdatesURL=https://github.com/Frosty9999/GlassBar/releases
; Per-user install: no admin prompt. Lands in %LocalAppData%\Programs\GlassBar.
PrivilegesRequired=lowest
DefaultDirName={autopf}\GlassBar
DisableProgramGroupPage=yes
DisableDirPage=auto
DisableReadyPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
LicenseFile=..\LICENSE
OutputDir=..\out-setup
OutputBaseFilename=GlassBar-Setup-v{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\GlassBar.exe
UninstallDisplayName=GlassBar
; GlassBar is asked to quit through its quit event (see [Code]); Restart Manager is only the fallback.
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startup"; Description: "Start GlassBar when I sign in to Windows"
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\GlassBar"; Filename: "{app}\GlassBar.exe"
Name: "{autodesktop}\GlassBar"; Filename: "{app}\GlassBar.exe"; Tasks: desktopicon

[Registry]
; Same value GlassBar's own "Start GlassBar when I sign in" checkbox writes, so the two stay in step.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "GlassBar"; ValueData: """{app}\GlassBar.exe"""; Tasks: startup; Flags: uninsdeletevalue

[Run]
Filename: "{app}\GlassBar.exe"; Description: "Start GlassBar now"; Flags: nowait postinstall skipifsilent

[Code]
const
  EVENT_MODIFY_STATE = $0002;
  SYNCHRONIZE = $00100000;

function OpenEvent(dwDesiredAccess: Cardinal; bInheritHandle: Boolean; lpName: String): THandle;
  external 'OpenEventW@kernel32.dll stdcall';
function SetEvent(hEvent: THandle): Boolean;
  external 'SetEvent@kernel32.dll stdcall';
function OpenMutex(dwDesiredAccess: Cardinal; bInheritHandle: Boolean; lpName: String): THandle;
  external 'OpenMutexW@kernel32.dll stdcall';
function CloseHandle(hObject: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';

function GlassBarRunning(): Boolean;
var
  H: THandle;
begin
  H := OpenMutex(SYNCHRONIZE, False, 'GlassBar_SingleInstance');
  Result := H <> 0;
  if Result then
    CloseHandle(H);
end;

{ Asks a running GlassBar to exit cleanly, which gives the Windows taskbar back, and waits for it. }
procedure QuitGlassBar();
var
  H: THandle;
  I: Integer;
begin
  H := OpenEvent(EVENT_MODIFY_STATE, False, 'GlassBar_Quit');
  if H = 0 then
    Exit;
  SetEvent(H);
  CloseHandle(H);

  for I := 1 to 150 do
  begin
    if not GlassBarRunning() then
      Break;
    Sleep(100);
  end;
  { Let the crash watchdog (a second GlassBar.exe) see the exit and close too. }
  Sleep(1000);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  QuitGlassBar();
  Result := '';
end;

function InitializeUninstall(): Boolean;
begin
  QuitGlassBar();
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  RunValue: String;
begin
  { Remove the startup entry even if GlassBar's own settings checkbox wrote it, but only when it starts this copy. }
  if CurUninstallStep = usUninstall then
    if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'GlassBar', RunValue) then
      if Pos(Lowercase(ExpandConstant('{app}')), Lowercase(RunValue)) > 0 then
        RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'GlassBar');

  if (CurUninstallStep = usPostUninstall) and not UninstallSilent() then
    if DirExists(ExpandConstant('{userappdata}\GlassBar')) then
      if MsgBox('Also delete your GlassBar settings (pinned apps, appearance)?', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(ExpandConstant('{userappdata}\GlassBar'), True, True, True);
end;
