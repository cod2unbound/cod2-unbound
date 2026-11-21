#include "release.iss"

; Launcher integration point. Its future publish output belongs under layout/launcher.
#if FileExists(Payload + "\launcher\CoD2Unbound.exe")
  #define HasLauncher
#endif

#define Product "CoD2 Unbound"
#define InstallDir "{autopf}\CoD2 Unbound"

[Setup]
AppId=CoD2Unbound
AppName={#Product}
AppVersion={#ReleaseVersion}
AppPublisher={#Company}
VersionInfoCompany={#Company}
VersionInfoProductName={#Product}
VersionInfoProductVersion={#ReleaseVersion}
VersionInfoProductTextVersion={#ReleaseVersion}
VersionInfoVersion={#ReleaseVersion}
VersionInfoTextVersion={#ReleaseVersion}
DefaultDirName={#InstallDir}
DefaultGroupName={#Product}
DisableDirPage=yes
DisableProgramGroupPage=yes
UsePreviousAppDir=no
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
SetupArchitecture=x64
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.22000
OutputDir={#Artifacts}
OutputBaseFilename=CoD2-Unbound-{#ReleaseVersion}-windows-x64-setup
SignedUninstaller=yes
SignedUninstallerDir={#Uninstallers}
Compression=lzma2
SolidCompression=yes
LZMANumBlockThreads=1
CloseApplications=no
RestartApplications=no
SetupMutex=CoD2UnboundSetup
UninstallFilesDir={app}
SetupLogging=yes
#ifdef HasLauncher
ChangesAssociations=yes
UninstallDisplayIcon={app}\launcher\CoD2Unbound.exe
#endif

[Messages]
ConfirmUninstall=Remove %1 and its installed files? Your configs, screenshots, demos and CD key will stay in place.

[Files]
Source: "{#Payload}\original\cod2_win32.exe"; DestDir: "{app}\original"; Flags: ignoreversion
Source: "{#Payload}\original\cod2_win32_ded.exe"; DestDir: "{app}\original"; Flags: ignoreversion
Source: "{#Payload}\cod2x\cod2_win32.exe"; DestDir: "{app}\cod2x"; Flags: ignoreversion
Source: "{#Payload}\cod2x\cod2_win32_ded.exe"; DestDir: "{app}\cod2x"; Flags: ignoreversion
Source: "{#Payload}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Payload}\NOTICE.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Payload}\CREDITS.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Payload}\THIRD-PARTY.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Payload}\RELEASE_VERSION"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Payload}\SHA256SUMS"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Payload}\README-server.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Payload}\server.cfg"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Payload}\licenses\*.txt"; DestDir: "{app}\licenses"; Flags: ignoreversion
#ifdef HasLauncher
Source: "{#Payload}\launcher\*"; DestDir: "{app}\launcher"; Flags: ignoreversion recursesubdirs createallsubdirs
#endif

[Icons]
#ifdef HasLauncher
Name: "{group}\CoD2 Unbound"; Filename: "{app}\launcher\CoD2Unbound.exe"; WorkingDir: "{app}"
#else
Name: "{group}\CoD2 Unbound (Original 1.3)"; Filename: "{app}\original\cod2_win32.exe"; WorkingDir: "{app}"
Name: "{group}\CoD2 Unbound (CoD2x)"; Filename: "{app}\cod2x\cod2_win32.exe"; WorkingDir: "{app}"
#endif
Name: "{group}\Dedicated Server Guide"; Filename: "{app}\README-server.txt"; WorkingDir: "{app}"

#ifdef HasLauncher
[Tasks]
Name: "links"; Description: "Handle cod2x and cod2 links when no other app handles them"; Flags: unchecked

[Registry]
Root: HKA; Subkey: "Software\Classes\CoD2Unbound.Demo"; ValueType: string; ValueData: "Call of Duty 2 demo"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\CoD2Unbound.Demo\shell\open\command"; ValueType: string; ValueData: """{app}\launcher\CoD2Unbound.exe"" ""%1"""
Root: HKA; Subkey: "Software\Classes\.dm_1\OpenWithProgids"; ValueType: none; ValueName: "CoD2Unbound.Demo"; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\CoD2Unbound.Config"; ValueType: string; ValueData: "Call of Duty 2 config"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\CoD2Unbound.Config\shell\open\command"; ValueType: string; ValueData: """{app}\launcher\CoD2Unbound.exe"" ""%1"""
Root: HKA; Subkey: "Software\Classes\.cfg\OpenWithProgids"; ValueType: none; ValueName: "CoD2Unbound.Config"; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\cod2x"; ValueType: string; ValueData: "URL:CoD2x server link"; Tasks: links; Check: CanRegisterLink('cod2x')
Root: HKA; Subkey: "Software\Classes\cod2x"; ValueType: string; ValueName: "URL Protocol"; Tasks: links; Check: CanRegisterLink('cod2x')
Root: HKA; Subkey: "Software\Classes\cod2x\shell\open\command"; ValueType: string; ValueData: """{app}\launcher\CoD2Unbound.exe"" ""%1"""; Tasks: links; Check: CanRegisterLink('cod2x')
Root: HKA; Subkey: "Software\Classes\cod2"; ValueType: string; ValueData: "URL:CoD2x server link"; Tasks: links; Check: CanRegisterLink('cod2')
Root: HKA; Subkey: "Software\Classes\cod2"; ValueType: string; ValueName: "URL Protocol"; Tasks: links; Check: CanRegisterLink('cod2')
Root: HKA; Subkey: "Software\Classes\cod2\shell\open\command"; ValueType: string; ValueData: """{app}\launcher\CoD2Unbound.exe"" ""%1"""; Tasks: links; Check: CanRegisterLink('cod2')
#endif

[Code]
function CreateFileW(FileName: String; Access, ShareMode: DWORD; Security: NativeUInt;
  Creation, Flags: DWORD; Template: NativeUInt): NativeInt;
  external 'CreateFileW@kernel32.dll stdcall';

function CloseHandle(Handle: NativeInt): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';

function GetFileAttributesW(FileName: String): DWORD;
  external 'GetFileAttributesW@kernel32.dll stdcall';

function IsReparsePoint(const Path: String): Boolean;
var
  Attributes: DWORD;
begin
  Attributes := GetFileAttributesW(Path);
  Result := (Attributes <> $FFFFFFFF) and ((Attributes and $400) <> 0);
end;

function Busy(const FileName: String): Boolean;
var
  Handle: NativeInt;
begin
  Result := False;
  if FileExists(FileName) then begin
    { GENERIC_WRITE, no sharing, OPEN_EXISTING. Check without changing the file. }
    Handle := CreateFileW(FileName, $40000000, 0, 0, 3, 0, 0);
    Result := Handle = -1;
    if not Result then CloseHandle(Handle);
  end;
end;

function InstalledFilesBusy(const Root: String): Boolean;
begin
  Result := Busy(Root + '\original\cod2_win32.exe') or
    Busy(Root + '\original\cod2_win32_ded.exe') or
    Busy(Root + '\cod2x\cod2_win32.exe') or
    Busy(Root + '\cod2x\cod2_win32_ded.exe') or
    Busy(Root + '\launcher\CoD2Unbound.exe');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Root: String;
begin
  Root := ExpandConstant('{app}');
  Result := '';
  if (CompareText(RemoveBackslashUnlessRoot(Root), ExpandConstant('{#InstallDir}')) <> 0) or
    IsReparsePoint(Root) or IsReparsePoint(Root + '\original') or
    IsReparsePoint(Root + '\cod2x') or IsReparsePoint(Root + '\licenses') or
    IsReparsePoint(Root + '\launcher') then
    Result := 'Install CoD2 Unbound in its standard Programs folder.'
  else if InstalledFilesBusy(Root) then
    Result := 'Close CoD2 Unbound, the game and any hosted servers, then run Setup again.';
end;

function InitializeUninstall(): Boolean;
begin
  Result := not InstalledFilesBusy(ExpandConstant('{app}'));
  if not Result then
    SuppressibleMsgBox('Close CoD2 Unbound, the game and any hosted servers, then try again.',
      mbError, MB_OK, IDOK);
end;

#ifdef HasLauncher
function ClassesRoot(): HKEY;
begin
  if IsAdminInstallMode then Result := HKLM64 else Result := HKCU64;
end;

function LinkCommand(): String;
begin
  Result := '"' + ExpandConstant('{app}\launcher\CoD2Unbound.exe') + '" "%1"';
end;

function OwnsLink(const Scheme: String): Boolean;
var
  Command: String;
begin
  Result := RegQueryStringValue(ClassesRoot(), 'Software\Classes\' + Scheme + '\shell\open\command',
    '', Command) and (CompareText(Command, LinkCommand()) = 0);
end;

var
  AllowCod2x, AllowCod2: Boolean;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then begin
    AllowCod2x := OwnsLink('cod2x') or not RegKeyExists(HKCR64, 'cod2x');
    AllowCod2 := OwnsLink('cod2') or not RegKeyExists(HKCR64, 'cod2');
  end;
end;

function CanRegisterLink(const Scheme: String): Boolean;
begin
  if Scheme = 'cod2x' then Result := AllowCod2x else Result := AllowCod2;
end;

procedure DeleteLink(const Scheme: String);
var
  Key: String;
  Root: HKEY;
begin
  if not OwnsLink(Scheme) then Exit;
  Root := ClassesRoot();
  Key := 'Software\Classes\' + Scheme;
  RegDeleteValue(Root, Key + '\shell\open\command', '');
  RegDeleteKeyIfEmpty(Root, Key + '\shell\open\command');
  RegDeleteKeyIfEmpty(Root, Key + '\shell\open');
  RegDeleteKeyIfEmpty(Root, Key + '\shell');
  RegDeleteValue(Root, Key, 'URL Protocol');
  RegDeleteValue(Root, Key, '');
  RegDeleteKeyIfEmpty(Root, Key);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then begin
    DeleteLink('cod2x');
    DeleteLink('cod2');
  end;
end;
#endif
