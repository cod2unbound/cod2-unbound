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
Name: "links"; Description: "Handle cod2x and cod2 links when no other app handles them"; Flags: unchecked

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
#endif

[Code]
const
  ArmEmulationNotice = 'CoD2 Unbound has not been tested on Arm PCs. It runs here through Windows'' x64 emulation and may not work as well as on an x64 PC.';

var
  ArmNotice: TOutputMsgWizardPage;
#ifdef HasLauncher
  UpdateBackup: String;
  UpdateCommitted: Boolean;
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

procedure DeinitializeSetup;
var
  Root, Failed: String;
begin
  if UpdateCommitted or (UpdateBackup = '') then Exit;
  Root := ExpandConstant('{app}');
  Failed := ExpandConstant('{src}\failed-install');
  if (not DirExists(Root)) or RenameFile(Root, Failed) then begin
    if RenameFile(UpdateBackup, Root) then
      Log('Restored the previous installation after the update stopped.')
    else begin
      Log('Could not restore the previous installation from ' + UpdateBackup);
      if not RenameFile(Failed, Root) then
        Log('Could not put the installation folder back.');
    end;
  end else
    Log('Could not move the incomplete installation. Previous files are at ' + UpdateBackup);
end;
#endif

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Root: String;
#ifdef HasLauncher
  Attempt: Integer;
  Backup, Scratch: String;
#endif
begin
  Root := ExpandConstant('{app}');
  Result := '';
  if (CompareText(RemoveBackslashUnlessRoot(Root), ExpandConstant('{#InstallDir}')) <> 0) or
    IsReparsePoint(Root) or IsReparsePoint(Root + '\original') or
    IsReparsePoint(Root + '\cod2x') or IsReparsePoint(Root + '\licenses') or
    IsReparsePoint(Root + '\launcher') then
    Result := 'Install CoD2 Unbound in its standard Programs folder.'
  else begin
#ifdef HasLauncher
    if IsLauncherUpdate() then
      for Attempt := 1 to 40 do begin
        if not Busy(Root + '\launcher\{#Launcher}') then Break;
        Sleep(200);
      end;
#endif
    if InstalledFilesBusy(Root) then
      Result := 'Close CoD2 Unbound, the game and any hosted servers, then run Setup again.'
#ifdef HasLauncher
    else if IsLauncherUpdate() and (UpdateBackup = '') then begin
      Backup := ExpandConstant('{src}\previous-install');
      Scratch := ExpandConstant('{src}\preparing-install');
      if (not FileExists(Root + '\launcher\{#Launcher}')) or
        (CompareText(ExtractFileDrive(Root), ExtractFileDrive(Backup)) <> 0) or
        (CompareText(Copy(Backup, 1, Length(Root) + 1), AddBackslash(Root)) = 0) or
        DirExists(Backup) or DirExists(Scratch) or (not CopyInstall(Root, Scratch)) or
        (not RenameFile(Scratch, Backup)) then
        Result := 'Setup could not save the previous installation.'
      else
        UpdateBackup := Backup;
    end
#endif
    ;
  end;
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
  Result := '"' + ExpandConstant('{app}\launcher\{#Launcher}') + '" "%1"';
end;

function OwnsLink(const Scheme: String): Boolean;
var
  Command: String;
begin
  Result := RegQueryStringValue(ClassesRoot(), 'Software\Classes\' + Scheme + '\shell\open\command',
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
  AllowCod2x, AllowCod2: Boolean;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssInstall then begin
    AllowCod2x := CanClaimLink('cod2x');
    AllowCod2 := CanClaimLink('cod2');
  end;
  if (CurStep = ssDone) and IsLauncherUpdate() then begin
    UpdateCommitted := True;
    if not SaveStringToFile(ExpandConstant('{src}\installed.ok'), '{#ReleaseVersion}', False) then
      Log('Could not record the completed update.');
    if not Exec(ExpandConstant('{app}\launcher\{#Launcher}'), '', ExpandConstant('{app}'),
      SW_SHOWNORMAL, ewNoWait, ResultCode) then
      Log('Could not reopen CoD2 Unbound after the update.');
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
