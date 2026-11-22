#include "release.iss"

#define Launcher "CoD2Unbound.Windows.exe"
#if FileExists(Payload + "\launcher\" + Launcher)
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
#ifdef HasLauncher
VersionInfoDescription=CoD2 Unbound Setup with recovery
#endif
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
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
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
UninstallDisplayIcon={app}\launcher\{#Launcher}
#endif

[Messages]
ConfirmUninstall=Remove %1 and its installed files? Your loadouts, settings, screenshots, demos and CD key will stay in place.

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
Source: "{#Payload}\payload.cat"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Payload}\launcher\*"; DestDir: "{app}\launcher"; Flags: ignoreversion recursesubdirs createallsubdirs
#endif

[Icons]
#ifdef HasLauncher
Name: "{group}\CoD2 Unbound"; Filename: "{app}\launcher\{#Launcher}"; WorkingDir: "{app}"
#else
Name: "{group}\CoD2 Unbound (Original 1.3)"; Filename: "{app}\original\cod2_win32.exe"; WorkingDir: "{app}"
Name: "{group}\CoD2 Unbound (CoD2x)"; Filename: "{app}\cod2x\cod2_win32.exe"; WorkingDir: "{app}"
#endif
Name: "{group}\Dedicated Server Guide"; Filename: "{app}\README-server.txt"; WorkingDir: "{app}"

#ifdef HasLauncher
[InstallDelete]
Type: files; Name: "{group}\CoD2 Unbound (Original 1.3).lnk"
Type: files; Name: "{group}\CoD2 Unbound (CoD2x).lnk"

[Tasks]
Name: "links"; Description: "Handle server and player setup links when no other app handles them"; Flags: unchecked

[Registry]
Root: HKA; Subkey: "Software\Classes\CoD2Unbound.Demo"; ValueType: string; ValueData: "Call of Duty 2 demo"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\CoD2Unbound.Demo\shell\open\command"; ValueType: string; ValueData: """{app}\launcher\{#Launcher}"" ""%1"""
Root: HKA; Subkey: "Software\Classes\.dm_1\OpenWithProgids"; ValueType: string; ValueName: "CoD2Unbound.Demo"; ValueData: ""; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\CoD2Unbound.Config"; ValueType: string; ValueData: "Call of Duty 2 config"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\CoD2Unbound.Config\shell\open\command"; ValueType: string; ValueData: """{app}\launcher\{#Launcher}"" ""%1"""
Root: HKA; Subkey: "Software\Classes\.cfg\OpenWithProgids"; ValueType: string; ValueName: "CoD2Unbound.Config"; ValueData: ""; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\cod2x"; ValueType: string; ValueData: "URL:CoD2x server link"; Tasks: links; Check: CanRegisterLink('cod2x')
Root: HKA; Subkey: "Software\Classes\cod2x"; ValueType: string; ValueName: "URL Protocol"; Tasks: links; Check: CanRegisterLink('cod2x')
Root: HKA; Subkey: "Software\Classes\cod2x\shell\open\command"; ValueType: string; ValueData: """{app}\launcher\{#Launcher}"" ""%1"""; Tasks: links; Check: CanRegisterLink('cod2x')
Root: HKA; Subkey: "Software\Classes\cod2"; ValueType: string; ValueData: "URL:CoD2x server link"; Tasks: links; Check: CanRegisterLink('cod2')
Root: HKA; Subkey: "Software\Classes\cod2"; ValueType: string; ValueName: "URL Protocol"; Tasks: links; Check: CanRegisterLink('cod2')
Root: HKA; Subkey: "Software\Classes\cod2\shell\open\command"; ValueType: string; ValueData: """{app}\launcher\{#Launcher}"" ""%1"""; Tasks: links; Check: CanRegisterLink('cod2')
Root: HKCU64; Subkey: "Software\Classes\cod2unbound"; ValueType: string; ValueData: "URL:CoD2 Unbound player setup link"; Tasks: links; Check: CanRegisterLink('cod2unbound')
Root: HKCU64; Subkey: "Software\Classes\cod2unbound"; ValueType: string; ValueName: "URL Protocol"; Tasks: links; Check: CanRegisterLink('cod2unbound')
Root: HKCU64; Subkey: "Software\Classes\cod2unbound\shell\open\command"; ValueType: string; ValueData: """{app}\launcher\{#Launcher}"" ""%1"""; Tasks: links; Check: CanRegisterLink('cod2unbound')
#endif

[Code]
const
#ifdef HasLauncher
  TransactionKey = 'Software\CoD2Unbound\InstallTransaction';
#endif
  ArmEmulationNotice = 'CoD2 Unbound has not been tested on Arm PCs. It runs here through Windows'' x64 emulation and may not work as well as on an x64 PC.';

var
  ArmNotice: TOutputMsgWizardPage;
#ifdef HasLauncher
  TransactionRoot: String;
  TransactionLock: NativeInt;
  UpdateCommitted: Boolean;
  LauncherHandoff: Boolean;
#endif

procedure InitializeWizard;
begin
  if IsArm64 then begin
    Log(ArmEmulationNotice);
    if not WizardSilent then
      ArmNotice := CreateOutputMsgPage(wpWelcome, 'Windows on Arm',
        'CoD2 Unbound runs through x64 emulation on this PC.',
        ArmEmulationNotice);
  end;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if ArmNotice = nil then Exit;
  if CurPageID = ArmNotice.ID then
    WizardForm.NextButton.Caption := '&Continue';
end;

function CreateFileW(FileName: String; Access, ShareMode: DWORD; Security: NativeUInt;
  Creation, Flags: DWORD; Template: NativeUInt): NativeInt;
  external 'CreateFileW@kernel32.dll stdcall';

function CloseHandle(Handle: NativeInt): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';

function GetFileAttributesW(FileName: String): DWORD;
  external 'GetFileAttributesW@kernel32.dll stdcall';

function MoveFileExW(ExistingFile, NewFile: String; Flags: DWORD): Boolean;
  external 'MoveFileExW@kernel32.dll stdcall';

function CreateMutexW(Attributes: NativeUInt; InitialOwner: Boolean; Name: String): NativeInt;
  external 'CreateMutexW@kernel32.dll stdcall';

function WaitForSingleObject(Handle: NativeInt; Milliseconds: DWORD): DWORD;
  external 'WaitForSingleObject@kernel32.dll stdcall';

function ReleaseMutex(Handle: NativeInt): Boolean;
  external 'ReleaseMutex@kernel32.dll stdcall';

function OpenProcess(Access: DWORD; InheritHandle: Boolean; ProcessId: DWORD): NativeInt;
  external 'OpenProcess@kernel32.dll stdcall';

function GetProcessTimes(Handle: NativeInt; var Created, Exited, Kernel, User: Int64): Boolean;
  external 'GetProcessTimes@kernel32.dll stdcall';

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
    Busy(Root + '\launcher\{#Launcher}') or
    Busy(Root + '\launcher\CoD2Unbound.exe');
end;

#ifdef HasLauncher
function IsLauncherUpdate(): Boolean;
begin
  Result := (not IsAdminInstallMode) and (ExpandConstant('{param:UNBOUNDUPDATE|0}') = '1');
end;

function WaitForLauncher(): Boolean;
var
  Handle: NativeInt;
  ProcessId: Integer;
  Expected, Created, Exited, Kernel, User: Int64;
begin
  Result := True;
  ProcessId := StrToIntDef(ExpandConstant('{param:UNBOUNDLAUNCHERPID|0}'), 0);
  Expected := StrToInt64Def(ExpandConstant('{param:UNBOUNDLAUNCHERSTART|0}'), 0);
  if (ProcessId <= 0) or (Expected <= 0) then Exit;
  Handle := OpenProcess($101000, False, ProcessId);
  if Handle = 0 then Exit;
  try
    if GetProcessTimes(Handle, Created, Exited, Kernel, User) and (Created = Expected) then
      Result := WaitForSingleObject(Handle, 45000) = 0;
  finally
    CloseHandle(Handle);
  end;
end;

function MoveInstall(const Source, Target: String): Boolean;
begin
  Result := MoveFileExW(Source, Target, 8);
end;

function CopyInstall(const Source, Target: String): Boolean;
var
  Entry: TFindRec;
  FromPath, ToPath: String;
begin
  Result := ForceDirectories(Target);
  if not Result then Exit;
  if FindFirst(Source + '\*', Entry) then begin
    try
      repeat
        if (Entry.Name <> '.') and (Entry.Name <> '..') then begin
          FromPath := Source + '\' + Entry.Name;
          ToPath := Target + '\' + Entry.Name;
          if (Entry.Attributes and $400) <> 0 then Result := False
          else if (Entry.Attributes and $10) <> 0 then Result := CopyInstall(FromPath, ToPath)
          else Result := FileCopy(FromPath, ToPath, True);
          if not Result then Exit;
        end;
      until not FindNext(Entry);
    finally
      FindClose(Entry);
    end;
  end;
end;

type
  TTokenUserBuffer = record
    Sid: NativeUInt;
    Attributes: DWORD;
    Storage: array [0..67] of Byte;
  end;

function GetTokenInformation(Token: NativeInt; Kind: DWORD; var Data: TTokenUserBuffer;
  Size: DWORD; var Required: DWORD): Boolean;
  external 'GetTokenInformation@advapi32.dll stdcall';

function ConvertSidToStringSidW(Sid: NativeUInt; var Text: NativeUInt): Boolean;
  external 'ConvertSidToStringSidW@advapi32.dll stdcall';

function LocalFree(Memory: NativeUInt): NativeUInt;
  external 'LocalFree@kernel32.dll stdcall';

function lstrlenW(Text: NativeUInt): Integer;
  external 'lstrlenW@kernel32.dll stdcall';

function lstrcpynW(Buffer: String; Text: NativeUInt; Length: Integer): NativeUInt;
  external 'lstrcpynW@kernel32.dll stdcall';

function CurrentUserSid(): String;
var
  TokenUser: TTokenUserBuffer;
  Required: DWORD;
  Text: NativeUInt;
  Count: Integer;
begin
  Result := '';
  { GetCurrentProcessToken pseudo-handle, TokenUser. The buffer includes SECURITY_MAX_SID_SIZE. }
  if not GetTokenInformation(-4, 1, TokenUser, SizeOf(TokenUser), Required) then Exit;
  if not ConvertSidToStringSidW(TokenUser.Sid, Text) then Exit;
  try
    Count := lstrlenW(Text);
    SetLength(Result, Count + 1);
    lstrcpynW(Result, Text, Count + 1);
    SetLength(Result, Count);
  finally
    LocalFree(Text);
  end;
end;

function AcquireTransaction(): Boolean;
var
  Wait: DWORD;
  Sid: String;
begin
  Result := TransactionLock <> 0;
  if Result then Exit;
  Sid := CurrentUserSid();
  if Sid = '' then Exit;
  TransactionLock := CreateMutexW(0, False, 'Local\CoD2Unbound.InstallTransaction-' + Sid);
  if TransactionLock = 0 then Exit;
  Wait := WaitForSingleObject(TransactionLock, 0);
  Result := (Wait = 0) or (Wait = $80);
  if not Result then begin
    CloseHandle(TransactionLock);
    TransactionLock := 0;
  end;
end;

procedure ReleaseTransaction;
begin
  if TransactionLock <> 0 then begin
    ReleaseMutex(TransactionLock);
    CloseHandle(TransactionLock);
    TransactionLock := 0;
  end;
end;

procedure MarkRestoreFailed;
begin
  Log('Could not restore the previous installation. Run Setup again after closing CoD2 Unbound.');
  if not RegWriteStringValue(HKCU64, TransactionKey, 'RestoreFailed', '1') then
    Log('Could not record the restore failure for the launcher.');
end;

function ClearTransaction(): Boolean;
begin
  Result := not RegKeyExists(HKCU64, TransactionKey) or RegDeleteKeyIncludingSubkeys(HKCU64, TransactionKey);
end;

function IsInstallLink(const Root: String): Boolean;
begin
  Result := IsReparsePoint(Root) or IsReparsePoint(Root + '\original') or
    IsReparsePoint(Root + '\cod2x') or IsReparsePoint(Root + '\licenses') or
    IsReparsePoint(Root + '\launcher') or IsReparsePoint(Root + '\launcher\{#Launcher}');
end;

function UnusedIncompletePath(const Root: String): String;
begin
  Result := Root + '.incomplete-install-' +
    ExtractFileName(GenerateUniqueName(ExtractFileDir(Root), '.tmp'));
end;

procedure CleanIncomplete(const Root: String);
var
  Incomplete: String;
begin
  Incomplete := Root + '.incomplete-install';
  if DirExists(Incomplete) and not DelTree(Incomplete, True, True, True) then
    if MoveInstall(Incomplete, UnusedIncompletePath(Root)) then
      Log('Moved the incomplete installation aside for later cleanup.')
    else
      Log('Could not clear the incomplete installation; Setup will continue.');
  if not DelTree(Incomplete + '-*', False, True, True) then
    Log('Some incomplete installation files still need cleanup.');
end;

function RestorePrevious(const Root: String; var Restored: Boolean): Boolean;
var
  Backup, Incomplete: String;
  Version: AnsiString;
begin
  Restored := False;
  Backup := Root + '.previous-install';
  Result := not IsReparsePoint(ExtractFileDir(Root)) and not IsInstallLink(Root) and not IsInstallLink(Backup);
  if not Result or not DirExists(Backup) then Exit;

  Incomplete := '';
  if DirExists(Root) then begin
    Incomplete := UnusedIncompletePath(Root);
    Result := MoveInstall(Root, Incomplete);
    if not Result then Exit;
  end;
  Result := MoveInstall(Backup, Root);
  if not Result then begin
    if Incomplete <> '' then
      if not MoveInstall(Incomplete, Root) then
        Log('Could not move the interrupted installation back to its original path.');
    Exit;
  end;
  Restored := True;
  if LoadStringFromFile(Root + '\RELEASE_VERSION', Version) then
    RegWriteStringValue(HKCU64, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\CoD2Unbound_is1',
      'DisplayVersion', Trim(Version));
  ClearTransaction();
  Log('Restored the previous installation after the update stopped.');
  CleanIncomplete(Root);
end;

procedure ReopenLauncher(const Root: String);
var
  Exe: String;
  ResultCode: Integer;
begin
  Exe := Root + '\launcher\{#Launcher}';
  if not IsInstallLink(Root) and FileExists(Exe) and
    not Exec(Exe, '', Root, SW_SHOWNORMAL, ewNoWait, ResultCode) then
    Log('Could not reopen CoD2 Unbound after Setup.');
end;

function InitializeSetup(): Boolean;
var
  Root, Attempt: String;
  Restored, Success: Boolean;
begin
  Result := ExpandConstant('{param:UNBOUNDRESTORE|0}') <> '1';
  if Result then Exit;

  { Abort before the wizard, including when restore arguments or checks fail. }
  if not IsLauncherUpdate() or (StrToIntDef(ExpandConstant('{param:UNBOUNDLAUNCHERPID|0}'), 0) <= 0) or
    (StrToInt64Def(ExpandConstant('{param:UNBOUNDLAUNCHERSTART|0}'), 0) <= 0) or
    not WaitForLauncher() or not AcquireTransaction() then Exit;

  Root := ExpandConstant('{#InstallDir}');
  Success := False;
  try
    if RegQueryStringValue(HKCU64, TransactionKey, 'Attempt', Attempt) and (Attempt <> '') and
      (Attempt = ExpandConstant('{param:UNBOUNDATTEMPT|}')) then begin
      Success := not InstalledFilesBusy(Root) and RestorePrevious(Root, Restored);
      if not Success then MarkRestoreFailed;
      if Success and Restored then Log('Restore-only Setup completed without installing files.');
    end;
  finally
    ReleaseTransaction;
  end;
  if Success then ReopenLauncher(Root);
end;

procedure DeinitializeSetup;
var
  Root: String;
  CanReopen, Restored: Boolean;
begin
  Root := TransactionRoot;
  CanReopen := True;
  if (Root <> '') and not UpdateCommitted and not RestorePrevious(Root, Restored) then begin
    CanReopen := False;
    MarkRestoreFailed;
    SuppressibleMsgBox('CoD2 Unbound needs repair. Close the game and run Setup again.', mbError, MB_OK, IDOK);
  end;
  ReleaseTransaction;
  if LauncherHandoff and CanReopen and WaitForLauncher() then
    ReopenLauncher(ExpandConstant('{#InstallDir}'));
end;
#endif

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Root: String;
#ifdef HasLauncher
  Backup, Completed, Attempt: String;
  Restored: Boolean;
#endif
begin
  Root := ExpandConstant('{app}');
  Result := '';
  if (CompareText(RemoveBackslashUnlessRoot(Root), ExpandConstant('{#InstallDir}')) <> 0) or
    IsReparsePoint(ExtractFileDir(Root)) or IsReparsePoint(Root) or
    IsReparsePoint(Root + '\original') or IsReparsePoint(Root + '\cod2x') or
    IsReparsePoint(Root + '\licenses') or IsReparsePoint(Root + '\launcher') then begin
    Result := 'Install CoD2 Unbound in its standard Programs folder.';
    Exit;
  end;
#ifdef HasLauncher
  LauncherHandoff := IsLauncherUpdate() and (ExpandConstant('{param:UNBOUNDLAUNCHERPID|0}') <> '0');
  if LauncherHandoff and not WaitForLauncher() then begin
    LauncherHandoff := False;
    Result := 'Close CoD2 Unbound and run Setup again.';
    Exit;
  end;
#endif
  if InstalledFilesBusy(Root) then begin
    Result := 'Close CoD2 Unbound, the game and any hosted servers, then run Setup again.';
    Exit;
  end;
#ifdef HasLauncher
  if IsAdminInstallMode then Exit;
  if not AcquireTransaction() then begin
    Result := 'Wait for CoD2 Unbound to finish repairing the installation, then run Setup again.';
    Exit;
  end;
  if not RestorePrevious(Root, Restored) then begin
    if DirExists(Root + '.previous-install') then MarkRestoreFailed;
    Result := 'The previous CoD2 Unbound installation could not be restored. Close the game and run Setup again.';
    Exit;
  end;
  if Restored then
    SuppressibleMsgBox('Setup restored the previous CoD2 Unbound installation.', mbInformation, MB_OK, IDOK);
  CleanIncomplete(Root);
  Completed := Root + '.completed-install';
  if IsReparsePoint(Completed) or (DirExists(Completed) and not DelTree(Completed, True, True, True)) then begin
    Result := 'Setup could not clear its previous repair files.';
    Exit;
  end;
  if not FileExists(Root + '\launcher\{#Launcher}') then Exit;
  if not ClearTransaction() then begin
    Result := 'Setup could not clear its previous installation record.';
    Exit;
  end;
  Attempt := ExpandConstant('{param:UNBOUNDATTEMPT|}');
  if LauncherHandoff and (Attempt <> '') and
    not RegWriteStringValue(HKCU64, TransactionKey, 'Attempt', Attempt) then begin
    Result := 'Setup could not record the installation attempt.';
    Exit;
  end;
  Backup := Root + '.previous-install';
  if not MoveInstall(Root, Backup) then begin
    Result := 'Setup could not save the previous installation.';
    Exit;
  end;
  TransactionRoot := Root;
  if not CopyInstall(Backup, Root) then
    Result := 'Setup could not prepare the installation. The previous copy will be restored.';
#endif
end;

function InitializeUninstall(): Boolean;
begin
  Result := not InstalledFilesBusy(ExpandConstant('{app}'));
  if not Result then
    SuppressibleMsgBox('Close CoD2 Unbound, the game and any hosted servers, then try again.',
      mbError, MB_OK, IDOK);
end;

#ifdef HasLauncher
function LinkClassesRoot(const Scheme: String): HKEY;
begin
  if IsAdminInstallMode and (Scheme <> 'cod2unbound') then Result := HKLM64 else Result := HKCU64;
end;

function LinkCommand(): String;
begin
  Result := '"' + ExpandConstant('{app}\launcher\{#Launcher}') + '" "%1"';
end;

function OwnsLink(const Scheme: String): Boolean;
var
  Command: String;
begin
  Result := RegQueryStringValue(LinkClassesRoot(Scheme), 'Software\Classes\' + Scheme + '\shell\open\command',
    '', Command) and (CompareText(Command, LinkCommand()) = 0);
end;

function CanClaimLink(const Scheme: String): Boolean;
var
  Key: String;
begin
  Key := 'Software\Classes\' + Scheme;
  Result := OwnsLink(Scheme) or
    (not RegKeyExists(HKCU64, Key) and not RegKeyExists(HKLM64, Key));
end;

var
  AllowCod2x, AllowCod2, AllowSetupLinks: Boolean;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then begin
    AllowCod2x := CanClaimLink('cod2x');
    AllowCod2 := CanClaimLink('cod2');
    AllowSetupLinks := CanClaimLink('cod2unbound');
  end;
  if (CurStep = ssDone) and (TransactionRoot <> '') then begin
    UpdateCommitted := MoveInstall(TransactionRoot + '.previous-install', TransactionRoot + '.completed-install');
    if UpdateCommitted then begin
      ClearTransaction();
      Log('Completed the CoD2 Unbound installation.');
      if not DelTree(TransactionRoot + '.completed-install', True, True, True) then
        Log('Setup will clear the completed backup on the next run.');
    end else
      Log('Could not commit the update. The previous copy will be restored.');
  end;
end;

function CanRegisterLink(const Scheme: String): Boolean;
begin
  if Scheme = 'cod2x' then Result := AllowCod2x
  else if Scheme = 'cod2' then Result := AllowCod2
  else Result := AllowSetupLinks;
end;

procedure DeleteLink(const Scheme: String);
var
  Key: String;
  Root: HKEY;
begin
  if not OwnsLink(Scheme) then Exit;
  Root := LinkClassesRoot(Scheme);
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
    DeleteLink('cod2unbound');
  end;
end;
#endif
