; ScreenVault Inno Setup Installer Script
; Windows Installer Specification per ScreenVault_Installer_Prompt.md

#ifndef AppVersion
  #define AppVersion "1.2.0"
#endif

#define AppName "ScreenVault"
#define AppExe "ScreenVault.exe"
#define AppMutexName "ScreenVault-7C0A45B9-F2D7-4952-B79D-5B6608C42589"

[Setup]
AppId={{7C0A45B9-F2D7-4952-B79D-5B6608C42589}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=ScreenVault
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableWelcomePage=no
DisableDirPage=auto
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog commandline
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
WizardStyle=modern
SetupIconFile=assets\ScreenVault.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
LicenseFile=assets\LICENSE.txt
OutputDir=..\artifacts\installer
OutputBaseFilename=ScreenVault_Setup_{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
SetupMutex=ScreenVaultSetupMutex
CloseApplications=yes
RestartApplications=no
UsePreviousAppDir=yes
UsePreviousTasks=yes

[Messages]
WelcomeLabel2=This will install [name/ver] on your computer.%n%nScreenVault records your screen, microphone and system audio so you never miss anything from a meeting. A tray icon always shows when recording is active.
FinishedLabel=Setup has finished installing [name]. ScreenVault runs in the system tray — press Start Recording when you're ready.

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"
Name: "startwithwindows"; Description: "Start ScreenVault when I sign in (tray only, does not record)"; GroupDescription: "Startup:"
Name: "startrecording"; Description: "Start recording automatically when ScreenVault starts"; GroupDescription: "Startup:"; Flags: unchecked

[Files]
Source: "..\artifacts\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; All-users autostart only. Per-user installs: the app manages its own HKCU Run value.
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AppName}"; \
  ValueData: """{app}\{#AppExe}"" --autostart --minimize-to-tray"; Flags: uninsdeletevalue; \
  Tasks: startwithwindows; Check: IsAdminInstallMode

[Run]
; Upgrade while the app was running: relaunch silently (also in silent installs), resume recording if it was recording.
Filename: "{app}\{#AppExe}"; Parameters: "--minimize-to-tray --after-upgrade"; Flags: nowait runasoriginaluser; Check: WasRunningBefore
; Normal install: optional launch from the Finish page.
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent runasoriginaluser; Check: not WasRunningBefore

[UninstallDelete]
Type: files; Name: "{app}\install-defaults.json"

[Code]
const
  DRIVE_FIXED = 3;

function GetDriveType(lpRootPathName: String): Cardinal;
  external 'GetDriveTypeW@kernel32.dll stdcall';

var
  StoragePage: TInputDirWizardPage;
  lblPrimaryFree: TNewStaticText;
  lblBackupFree: TNewStaticText;
  WasRunning: Boolean;
  StoragePageShown: Boolean;
  DefaultPrimaryValue: String;
  UninstPrimaryDir: String;
  UninstBackupDir: String;

{ Helper: JSON escape backslashes and double quotes }
function JsonEscape(const S: String): String;
var
  I: Integer;
  R: String;
begin
  R := '';
  for I := 1 to Length(S) do
  begin
    if S[I] = '\' then
      R := R + '\\'
    else if S[I] = '"' then
      R := R + '\"'
    else
      R := R + S[I];
  end;
  Result := R;
end;

{ Helper: Get root path with trailing backslash }
function GetPathDriveRoot(const Path: String): String;
var
  Drive: String;
begin
  if (Length(Path) >= 2) and (Path[1] = '\') and (Path[2] = '\') then
  begin
    Result := ''; { UNC path has no drive letter root }
    Exit;
  end;
  Drive := ExtractFileDrive(Path);
  if Drive <> '' then
    Result := AddBackslash(Drive)
  else
    Result := '';
end;

{ Format free space on disk in GB }
function FormatDiskFree(const Path: String): String;
var
  DriveRoot: String;
  FreeBytes, TotalBytes: Int64;
  FreeGb: Int64;
begin
  Result := '';
  if Trim(Path) = '' then Exit;
  if (Length(Path) >= 2) and (Path[1] = '\') and (Path[2] = '\') then
  begin
    Result := 'Network folder';
    Exit;
  end;

  DriveRoot := GetPathDriveRoot(Path);
  if (DriveRoot <> '') and GetSpaceOnDisk64(DriveRoot, FreeBytes, TotalBytes) then
  begin
    FreeGb := FreeBytes / (1024 * 1024 * 1024);
    Result := IntToStr(FreeGb) + ' GB free on ' + DriveRoot;
  end;
end;

{ Event handler for edit box changes to update free space labels }
procedure OnStorageEditChange(Sender: TObject);
begin
  if Assigned(lblPrimaryFree) and Assigned(StoragePage) then
    lblPrimaryFree.Caption := FormatDiskFree(StoragePage.Values[0]);

  if Assigned(lblBackupFree) and Assigned(StoragePage) then
    lblBackupFree.Caption := FormatDiskFree(StoragePage.Values[1]);
end;

{ Detect the largest secondary fixed drive }
function DetectDefaultBackupLocation(const PrimaryDriveRoot: String): String;
var
  DriveCode: Integer;
  Root: String;
  FreeBytes, TotalBytes: Int64;
  LargestBytes: Int64;
  BestDrive: String;
begin
  LargestBytes := 0;
  BestDrive := '';

  for DriveCode := Ord('C') to Ord('Z') do
  begin
    Root := Chr(DriveCode) + ':\';
    if GetDriveType(Root) = DRIVE_FIXED then
    begin
      if not SameText(Root, PrimaryDriveRoot) then
      begin
        if GetSpaceOnDisk64(Root, FreeBytes, TotalBytes) then
        begin
          if TotalBytes > LargestBytes then
          begin
            LargestBytes := TotalBytes;
            BestDrive := Root;
          end;
        end;
      end;
    end;
  end;

  if BestDrive <> '' then
    Result := AddBackslash(BestDrive) + 'ScreenVault Backup'
  else
    Result := '';
end;

{ Determine if an upgrade is happening }
function IsUpgrade(): Boolean;
var
  OldExe: String;
begin
  OldExe := AddBackslash(WizardDirValue) + '{#AppExe}';
  Result := (WizardForm.PrevAppDir <> '') or FileExists(OldExe);
end;

function WasRunningBefore(): Boolean;
begin
  Result := WasRunning;
end;

procedure InitializeWizard();
var
  ParamPrimary, ParamBackup: String;
  PrimaryRoot: String;
  DefaultBackupValue: String;
  ExtraTop: Integer;
begin
  StoragePageShown := False;
  WasRunning := False;

  { 1. Construct Custom Recording Storage Page }
  StoragePage := CreateInputDirPage(
    wpSelectDir,
    'Recording storage',
    'Where should ScreenVault save your recordings?',
    'Recordings are saved to the primary folder. If that drive gets low on space, ScreenVault automatically continues in the backup folder, so a meeting is never lost. You can change this later in Settings -> Storage.',
    False,
    'New Folder');

  StoragePage.Add('Primary folder:');
  StoragePage.Add('Backup folder (on a different drive, optional):');

  { Default values }
  DefaultPrimaryValue := ExpandConstant('{%USERPROFILE}') + '\Videos\Screen Recordings';
  PrimaryRoot := GetPathDriveRoot(DefaultPrimaryValue);
  DefaultBackupValue := DetectDefaultBackupLocation(PrimaryRoot);

  { Command-line parameter overrides for silent installs }
  ParamPrimary := ExpandConstant('{param:PRIMARY|}');
  ParamBackup := ExpandConstant('{param:BACKUP|}');

  if ParamPrimary <> '' then
    StoragePage.Values[0] := ParamPrimary
  else
    StoragePage.Values[0] := DefaultPrimaryValue;

  if ParamBackup <> '' then
    StoragePage.Values[1] := ParamBackup
  else
    StoragePage.Values[1] := DefaultBackupValue;

  { Adjust layout to insert free-space status labels under each edit control }
  ExtraTop := ScaleY(18);

  lblPrimaryFree := TNewStaticText.Create(StoragePage);
  lblPrimaryFree.Parent := StoragePage.Surface;
  lblPrimaryFree.Left := StoragePage.Edits[0].Left;
  lblPrimaryFree.Top := StoragePage.Edits[0].Top + StoragePage.Edits[0].Height + ScaleY(2);
  lblPrimaryFree.Width := StoragePage.Edits[0].Width;
  lblPrimaryFree.Height := ScaleY(14);
  lblPrimaryFree.Font.Color := clGrayText;
  lblPrimaryFree.Caption := FormatDiskFree(StoragePage.Values[0]);

  { Shift second prompt, edit and button down so labels do not overlap }
  StoragePage.PromptLabels[1].Top := StoragePage.PromptLabels[1].Top + ExtraTop;
  StoragePage.Edits[1].Top := StoragePage.Edits[1].Top + ExtraTop;
  StoragePage.Buttons[1].Top := StoragePage.Buttons[1].Top + ExtraTop;

  lblBackupFree := TNewStaticText.Create(StoragePage);
  lblBackupFree.Parent := StoragePage.Surface;
  lblBackupFree.Left := StoragePage.Edits[1].Left;
  lblBackupFree.Top := StoragePage.Edits[1].Top + StoragePage.Edits[1].Height + ScaleY(2);
  lblBackupFree.Width := StoragePage.Edits[1].Width;
  lblBackupFree.Height := ScaleY(14);
  lblBackupFree.Font.Color := clGrayText;
  lblBackupFree.Caption := FormatDiskFree(StoragePage.Values[1]);

  StoragePage.Edits[0].OnChange := @OnStorageEditChange;
  StoragePage.Edits[1].OnChange := @OnStorageEditChange;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  if Assigned(StoragePage) and (PageID = StoragePage.ID) then
  begin
    { Skip storage page on upgrade unless /RECONFIGURE was passed }
    if IsUpgrade() and (ExpandConstant('{param:RECONFIGURE|}') = '') then
    begin
      Result := True;
      StoragePageShown := False;
    end
    else
    begin
      Result := False;
      StoragePageShown := True;
    end;
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  PrimaryPath, BackupPath: String;
  PrimaryRoot, BackupRoot: String;
  FreeBytes, TotalBytes: Int64;
  FreeGb: Int64;
begin
  Result := True;
  if Assigned(StoragePage) and (CurPageID = StoragePage.ID) then
  begin
    StoragePageShown := True;
    PrimaryPath := Trim(StoragePage.Values[0]);
    BackupPath := Trim(StoragePage.Values[1]);

    { 1. Primary empty -> Error }
    if PrimaryPath = '' then
    begin
      MsgBox('Please specify a primary recording folder.', mbError, MB_OK);
      Result := False;
      Exit;
    end;

    { 2. Primary drive doesn't exist -> Error }
    PrimaryRoot := GetPathDriveRoot(PrimaryPath);
    if (PrimaryRoot <> '') and not DirExists(PrimaryRoot) then
    begin
      MsgBox('The drive for the primary recording folder (' + PrimaryRoot + ') does not exist.', mbError, MB_OK);
      Result := False;
      Exit;
    end;

    { 3. Primary and backup on the same drive -> Warning }
    BackupRoot := GetPathDriveRoot(BackupPath);
    if (BackupPath <> '') and (PrimaryRoot <> '') and (BackupRoot <> '') and SameText(PrimaryRoot, BackupRoot) then
    begin
      if MsgBox('The backup folder is on the same drive as the primary folder, so it will not help if that drive becomes full.' + #13#10 + #13#10 + 'Do you want to continue anyway?', mbConfirmation, MB_YESNO) <> IDYES then
      begin
        Result := False;
        Exit;
      end;
    end;

    { 4. Backup empty -> Warning }
    if BackupPath = '' then
    begin
      if MsgBox('Without a backup folder, recording will stop if the primary drive fills up.' + #13#10 + #13#10 + 'Do you want to continue without a backup folder?', mbConfirmation, MB_YESNO) <> IDYES then
      begin
        Result := False;
        Exit;
      end;
    end;

    { 5. UNC / network path warning }
    if ((Length(PrimaryPath) >= 2) and (PrimaryPath[1] = '\') and (PrimaryPath[2] = '\')) or
       ((Length(BackupPath) >= 2) and (BackupPath[1] = '\') and (BackupPath[2] = '\')) then
    begin
      if MsgBox('Network folders can disconnect unexpectedly during an active recording.' + #13#10 + #13#10 + 'Are you sure you want to use a network folder?', mbConfirmation, MB_YESNO) <> IDYES then
      begin
        Result := False;
        Exit;
      end;
    end;

    { 6. Primary drive free space < 10 GB warning }
    if (PrimaryRoot <> '') and GetSpaceOnDisk64(PrimaryRoot, FreeBytes, TotalBytes) then
    begin
      FreeGb := FreeBytes / (1024 * 1024 * 1024);
      if FreeGb < 10 then
      begin
        if MsgBox('The selected primary drive (' + PrimaryRoot + ') has only ' + IntToStr(FreeGb) + ' GB of free space.' + #13#10 + #13#10 + 'Do you want to continue?', mbConfirmation, MB_YESNO) <> IDYES then
        begin
          Result := False;
          Exit;
        end;
      end;
    end;
  end;
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo,
  MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
var
  S: String;
begin
  S := '';
  if MemoDirInfo <> '' then
    S := S + MemoDirInfo + NewLine + NewLine;

  if MemoTasksInfo <> '' then
    S := S + MemoTasksInfo + NewLine + NewLine;

  if Assigned(StoragePage) then
  begin
    S := S + 'Recording storage:' + NewLine;
    S := S + '  Primary: ' + StoragePage.Values[0] + NewLine;
    if Trim(StoragePage.Values[1]) <> '' then
      S := S + '  Backup:  ' + StoragePage.Values[1] + NewLine
    else
      S := S + '  Backup:  (None)' + NewLine;
  end;

  Result := S;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode, I: Integer;
  OldExe: String;
begin
  Result := '';
  OldExe := AddBackslash(WizardDirValue) + '{#AppExe}';
  WasRunning := CheckForMutexes('{#AppMutexName},Local\{#AppMutexName}');

  if WasRunning and FileExists(OldExe) then
  begin
    { Run as the ORIGINAL user: the app control pipe only accepts the logged-in user }
    ExecAsOriginalUser(OldExe, '--exit --wait', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

    for I := 1 to 30 do
    begin
      if not CheckForMutexes('{#AppMutexName},Local\{#AppMutexName}') then Break;
      Sleep(1000);
    end;

    if CheckForMutexes('{#AppMutexName},Local\{#AppMutexName}') then
      Result := 'ScreenVault is still running. Please exit it from the tray icon (right-click -> Exit), then click Next.';
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  DefaultsFile: String;
  PrimaryVal, BackupVal: String;
  PrimaryJson, BackupJson: String;
  ScopeJson, VersionJson, RevJson: String;
  StartWinJson, StartRecJson: String;
  JsonContent: String;
begin
  if CurStep = ssPostInstall then
  begin
    { Write install-defaults.json on fresh install or when storage page was shown }
    if (not IsUpgrade()) or StoragePageShown then
    begin
      DefaultsFile := AddBackslash(ExpandConstant('{app}')) + 'install-defaults.json';

      if IsAdminInstallMode then
        ScopeJson := '"AllUsers"'
      else
        ScopeJson := '"PerUser"';

      VersionJson := '"' + '{#AppVersion}' + '"';
      RevJson := '"' + GetDateTimeString('yyyy-mm-dd"T"hh:nn:ss', '-', ':') + '"';

      PrimaryVal := Trim(StoragePage.Values[0]);
      { If primary was left as default, pass null so the app uses real user special folder }
      if SameText(PrimaryVal, DefaultPrimaryValue) or (PrimaryVal = '') then
        PrimaryJson := 'null'
      else
        PrimaryJson := '"' + JsonEscape(PrimaryVal) + '"';

      BackupVal := Trim(StoragePage.Values[1]);
      if BackupVal = '' then
        BackupJson := 'null'
      else
        BackupJson := '"' + JsonEscape(BackupVal) + '"';

      if WizardIsTaskSelected('startwithwindows') then
        StartWinJson := 'true'
      else
        StartWinJson := 'false';

      if WizardIsTaskSelected('startrecording') then
        StartRecJson := 'true'
      else
        StartRecJson := 'false';

      JsonContent := '{' + #13#10 +
        '  "installScope": ' + ScopeJson + ',' + #13#10 +
        '  "installerVersion": ' + VersionJson + ',' + #13#10 +
        '  "defaultsRevision": ' + RevJson + ',' + #13#10 +
        '  "primaryLocation": ' + PrimaryJson + ',' + #13#10 +
        '  "backupLocation": ' + BackupJson + ',' + #13#10 +
        '  "startWithWindows": ' + StartWinJson + ',' + #13#10 +
        '  "startRecordingOnLaunch": ' + StartRecJson + #13#10 +
        '}';

      SaveStringToFile(DefaultsFile, JsonContent, False);
    end;
  end;
end;

{ Helper: Extract storage location from install-defaults.json }
function ExtractJsonStringValue(const Json, Key: String): String;
var
  P, ColonPos, QuoteStart, QuoteEnd: Integer;
begin
  Result := '';
  P := Pos('"' + Key + '"', Json);
  if P > 0 then
  begin
    ColonPos := Pos(':', Copy(Json, P, Length(Json) - P + 1));
    if ColonPos > 0 then
    begin
      P := P + ColonPos;
      QuoteStart := Pos('"', Copy(Json, P, Length(Json) - P + 1));
      if QuoteStart > 0 then
      begin
        P := P + QuoteStart;
        QuoteEnd := Pos('"', Copy(Json, P, Length(Json) - P + 1));
        if QuoteEnd > 0 then
          Result := Copy(Json, P, QuoteEnd - 1);
      end;
    end;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode, I: Integer;
  AppExePath, DefaultsFile: String;
  JsonContent: String;
  JsonAnsi: AnsiString;
  NoticeMsg: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    AppExePath := AddBackslash(ExpandConstant('{app}')) + '{#AppExe}';
    DefaultsFile := AddBackslash(ExpandConstant('{app}')) + 'install-defaults.json';

    { Read storage folders from install-defaults.json before deleting files }
    UninstPrimaryDir := '';
    UninstBackupDir := '';
    if FileExists(DefaultsFile) then
    begin
      if LoadStringFromFile(DefaultsFile, JsonAnsi) then
      begin
        JsonContent := String(JsonAnsi);
        UninstPrimaryDir := ExtractJsonStringValue(JsonContent, 'primaryLocation');
        UninstBackupDir := ExtractJsonStringValue(JsonContent, 'backupLocation');
      end;
    end;
    if UninstPrimaryDir = '' then
      UninstPrimaryDir := ExpandConstant('{%USERPROFILE}') + '\Videos\Screen Recordings';

    { 1. Graceful stop: ScreenVault.exe --exit --wait }
    if FileExists(AppExePath) and CheckForMutexes('{#AppMutexName},Local\{#AppMutexName}') then
    begin
      Exec(AppExePath, '--exit --wait', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      for I := 1 to 15 do
      begin
        if not CheckForMutexes('{#AppMutexName},Local\{#AppMutexName}') then Break;
        Sleep(1000);
      end;

      { 2. Force stop fallback }
      if CheckForMutexes('{#AppMutexName},Local\{#AppMutexName}') then
      begin
        Exec('taskkill.exe', '/F /T /IM {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
        Sleep(1000);
      end;
    end;
  end
  else if CurUninstallStep = usPostUninstall then
  begin
    { Remove HKCU Run key for per-user installations }
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', '{#AppName}');

    { Prompt to delete settings and logs }
    if MsgBox('Also delete your ScreenVault settings and logs?', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
    begin
      DelTree(ExpandConstant('{userappdata}\ScreenVault'), True, True, True);
      DelTree(ExpandConstant('{localappdata}\ScreenVault'), True, True, True);
    end;

    { Always notify that recordings were NOT deleted and where they are }
    NoticeMsg := 'Your recordings were not deleted.' + #13#10 + #13#10 +
      'They are located in:' + #13#10 +
      '  ' + UninstPrimaryDir;

    if UninstBackupDir <> '' then
      NoticeMsg := NoticeMsg + #13#10 + '  ' + UninstBackupDir;

    MsgBox(NoticeMsg, mbInformation, MB_OK);
  end;
end;
