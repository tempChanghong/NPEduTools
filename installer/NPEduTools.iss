; Compiles an already verified package; never publish from bin/Debug directly.
#if VER < EncodeVer(7, 1, 0)
  #error Inno Setup 7.1.0 or newer is required (full package paths can exceed MAX_PATH)
#endif
#ifndef PackageRoot
  #error PackageRoot is required
#endif
#ifndef ReleaseVersion
  #error ReleaseVersion is required
#endif
#ifndef InstallerVersion
  #error InstallerVersion is required
#endif
#ifndef OutputRoot
  #error OutputRoot is required
#endif
#define ProductId "{{B76C0D58-1319-4D53-AC50-D607C7B578A8}"
#define MaintenanceHash GetSHA256OfFile(SourcePath + "guard-maintenance.ps1")
#ifndef InstallerTestId
#define ManagedUninstallExeHash GetSHA256OfFile(PackageRoot + "\app\Host\NPEduTools.Host.exe")
#define ManagedUninstallHostHash GetSHA256OfFile(PackageRoot + "\app\Host\NPEduTools.Host.dll")
#define ManagedUninstallNpepHash GetSHA256OfFile(PackageRoot + "\app\Host\NPEduTools.Integrations.Npep.dll")
#endif
#ifdef InstallerTestId
  ; Only the isolated regression script defines this. Production wrapper never does.
  #define ProductId "NPEduTools-InstallerTest-" + InstallerTestId
#endif

[Setup]
AppId={#ProductId}
AppName=NPEduTools
AppVersion={#ReleaseVersion}
AppPublisher=Novark Power
AppPublisherURL=https://github.com/tempChanghong/NPEduTools
AppSupportURL=https://github.com/tempChanghong/NPEduTools/issues
AppUpdatesURL=https://github.com/tempChanghong/NPEduTools/releases
VersionInfoVersion={#InstallerVersion}
VersionInfoDescription=NPEduTools 安装程序
#ifdef InstallerTestId
DefaultDirName={localappdata}\NPEduTools-InstallerTest-{#InstallerTestId}
DefaultGroupName=NPEduTools-InstallerTest-{#InstallerTestId}
PrivilegesRequired=lowest
#else
DefaultDirName={autopf}\NPEduTools
DefaultGroupName=NPEduTools
PrivilegesRequired=admin
#endif
DisableProgramGroupPage=yes
UsePreviousAppDir=yes
ArchitecturesAllowed=x64compatible and not arm64
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
WizardStyle=modern dynamic windows11
WizardSizePercent=110
SetupIconFile=..\images\branding\npedutools.ico
UninstallDisplayIcon={app}\app\NPEduTools.App.exe
LicenseFile={#PackageRoot}\LICENSE
InfoBeforeFile=INSTALL-NOTES.txt
Compression=lzma2
SolidCompression=yes
OutputDir={#OutputRoot}
OutputBaseFilename=NPEduTools-{#ReleaseVersion}-win-x64-setup
CloseApplications=no
RestartApplications=no
; Busy collection/recording is never terminated; only owned guardian maintenance is automated.

[Languages]
Name: "chinesesimplified"; MessagesFile: "Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："; Flags: unchecked

[Files]
Source: "{#PackageRoot}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "guard-maintenance.ps1"; DestDir: "{app}\maintenance"; Flags: ignoreversion

#ifndef InstallerTestId
[Dirs]
Name: "{app}\school-policy-state"; Flags: uninsneveruninstall

[Icons]
Name: "{group}\NPEduTools"; Filename: "{app}\app\NPEduTools.App.exe"; WorkingDir: "{app}\app"
Name: "{group}\桥接插件与使用说明"; Filename: "{app}"
Name: "{autodesktop}\NPEduTools"; Filename: "{app}\app\NPEduTools.App.exe"; WorkingDir: "{app}\app"; Tasks: desktopicon

[Run]
Filename: "{app}\app\NPEduTools.App.exe"; WorkingDir: "{app}\app"; Description: "启动 NPEduTools（需要管理员授权）"; Flags: postinstall nowait skipifsilent unchecked runasoriginaluser
#endif

[Registry]
#ifdef InstallerTestId
Root: HKCU; Subkey: "Software\NPEduTools\InstallerTest\{#InstallerTestId}"; ValueType: string; ValueName: "InstallerVersion"; ValueData: "{#InstallerVersion}"; Flags: uninsdeletevalue uninsdeletekeyifempty
#else
Root: HKLM; Subkey: "Software\NPEduTools\Installer"; ValueType: string; ValueName: "InstallerVersion"; ValueData: "{#InstallerVersion}"; Flags: uninsdeletevalue uninsdeletekeyifempty
Root: HKLM; Subkey: "Software\NPEduTools\Installer"; ValueType: dword; ValueName: "SchoolAccessProvisioningVersion"; ValueData: "1"; Flags: uninsdeletevalue
Root: HKLM; Subkey: "Software\NPEduTools\Installer"; ValueType: dword; ValueName: "ManagedUninstallProtocol"; ValueData: "1"; Flags: uninsdeletevalue
Root: HKLM; Subkey: "Software\NPEduTools\Installer"; ValueType: string; ValueName: "ManagedUninstallRoot"; ValueData: "{app}"; Flags: uninsdeletevalue
#endif

[Code]
const
#ifdef InstallerTestId
  VersionRoot = HKEY_CURRENT_USER;
  VersionKey = 'Software\NPEduTools\InstallerTest\{#InstallerTestId}';
#else
  VersionRoot = HKEY_LOCAL_MACHINE;
  VersionKey = 'Software\NPEduTools\Installer';
#endif

var
  MaintenanceScript, MaintenanceDirectory, MaintenanceFailure: String;
  MaintenanceActive, MaintenanceCompleted, ManagedUninstallFresh, ManagedUninstallLegacyUpgrade: Boolean;

function InstallationProblem(AllowGuardian: Boolean): String;
var
  Service, Processes, Process: Variant;
  Query, Name, Path, InstallPrefix: String;
  Index, Count: Integer;
begin
  Result := '';
  try
    Service := CreateOleObject('WbemScripting.SWbemLocator');
    Service := Service.ConnectServer('', 'root\CIMV2');
#ifdef InstallerTestId
    Query := 'SELECT Name, ExecutablePath FROM Win32_Process WHERE Name = ''NPEduTools.InstallerFixture-{#InstallerTestId}.exe''';
#else
    Query := 'SELECT Name, ExecutablePath FROM Win32_Process WHERE Name LIKE ''NPEduTools.%'' OR Name = ''svchost.exe'' OR Name = ''ffmpeg.exe'' OR Name = ''ffprobe.exe''';
#endif
    Processes := Service.ExecQuery(Query);
    InstallPrefix := Lowercase(AddBackslash(ExpandConstant('{app}')));
    Count := Processes.Count;
    for Index := 0 to Count - 1 do begin
      Process := Processes.ItemIndex(Index);
      Name := Process.Name;
      Name := Lowercase(Name);
      if (Pos('npedutools.', Name) = 1) and (Copy(Name, Length(Name) - 3, 4) = '.exe') then begin
        Result := 'NPEduTools 组件仍在运行：' + Name + #13#10 +
          '请先停止并保存录课／监测，在托盘选择“停止后台并退出”，然后重试。仅关闭主窗口不会退出后台。';
        Exit;
      end;
      if Name = 'svchost.exe' then begin
        { Never identify or stop a Windows service by basename. Only this bundle's path is ours. }
        if not VarIsNull(Process.ExecutablePath) then begin
          Path := Lowercase(Process.ExecutablePath);
          if (not AllowGuardian) and (Path = InstallPrefix + 'app\guard\svchost.exe') then begin
            Result := '此安装的自习守护仍在待命。请维护人员按 GUARD-MAINTENANCE.md 停止本安装的守护并撤销启动登记后重试。';
            Exit;
          end;
        end;
      end;
      if (Name = 'ffmpeg.exe') or (Name = 'ffprobe.exe') then begin
        if VarIsNull(Process.ExecutablePath) then begin
          Result := '无法核实录制组件的程序位置。请完成录制并退出相关程序后重试。';
          Exit;
        end;
        Path := Process.ExecutablePath;
        Path := Lowercase(Path);
        if Pos(InstallPrefix, Path) = 1 then begin
          Result := '安装目录内的录制组件仍在运行，请等待保存完成并完整退出 NPEduTools 后重试。';
          Exit;
        end;
      end;
    end;
  except
    Log('Process inspection failed: ' + GetExceptionMessage);
    Result := '无法检查运行中的程序，本次不会继续。请检查 Windows WMI 服务后重试。';
    Exit;
  end;
end;

function GuardianRegistrationProblem: String;
var Service, Folder, Tasks, Task, Actions, Action: Variant;
    Index, ActionIndex: Integer;
    Name, Path, InstallPrefix: String;
begin
  Result := '';
  try
    Service := CreateOleObject('Schedule.Service');
    Service.Connect;
    Folder := Service.GetFolder('\');
    Tasks := Folder.GetTasks(1);
    InstallPrefix := Lowercase(AddBackslash(ExpandConstant('{app}')));
    for Index := 1 to Tasks.Count do begin
      Task := Tasks.Item(Index);
      Name := Lowercase(Task.Name);
      if Pos('npedutools.guard.', Name) = 1 then begin
        Actions := Task.Definition.Actions;
        for ActionIndex := 1 to Actions.Count do begin
          Action := Actions.Item(ActionIndex);
          { NPEduTools guard registrations contain executable actions only. An unexpected
            action fails the inspection below instead of being silently ignored. }
          Path := Lowercase(Action.Path);
          if Path = InstallPrefix + 'app\guard\svchost.exe' then begin
            Result := '此安装仍有自习守护启动登记。请维护人员按 GUARD-MAINTENANCE.md 撤销该实例登记后重试。';
            Exit;
          end;
        end;
      end;
    end;
  except
    Log('Guardian registration inspection failed: ' + GetExceptionMessage);
    Result := '无法核实守护启动登记，本次不继续。请检查任务计划程序服务及维护权限。';
  end;
end;

function MaintenanceCommand(Action: String): Boolean;
var Code, Index, Size: Integer;
    Launched: Boolean;
    Details: TArrayOfString;
    DetailPath, Reason: String;
begin
  MaintenanceFailure := '';
  Code := -1;
  Launched := Exec(ExpandConstant('{sysnative}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + MaintenanceScript +
    '" -Action ' + Action + ' -InstallRoot "' + ExpandConstant('{app}') +
    '" -TransactionDirectory "' + MaintenanceDirectory + '"', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Result := Launched and (Code = 0);
  if not Result then begin
    if Launched then
      MaintenanceFailure := '维护助手执行失败，退出码：' + IntToStr(Code)
    else
      MaintenanceFailure := '无法启动维护助手，Windows 错误：' + IntToStr(Code) + ' ' + SysErrorMessage(Code);
    DetailPath := MaintenanceDirectory + '\' + Action + '-result.txt';
    if not FileExists(DetailPath) then DetailPath := MaintenanceDirectory + '\result.txt';
    if FileSize(DetailPath, Size) and (Size >= 0) and (Size <= 65536) then
      if LoadStringsFromFile(DetailPath, Details) then begin
        Reason := '';
        for Index := 0 to GetArrayLength(Details) - 1 do begin
          if Length(Reason) >= 1000 then Break;
          if Reason <> '' then Reason := Reason + #13#10;
          Reason := Reason + Details[Index];
        end;
        Reason := Trim(Copy(Reason, 1, 1000));
        if (Reason <> '') and (Reason <> 'OK') then MaintenanceFailure := MaintenanceFailure + #13#10 + Reason;
      end;
    Log('Guardian maintenance failed: ' + Action + '; ' + MaintenanceFailure + '; result: ' + DetailPath);
  end;
end;

function PrepareGuardian: String;
begin
  Result := '';
  if MaintenanceActive then begin
    Result := '维护流程已启动，请退出本安装器后重新运行，避免重复接管同一事务。'; Exit;
  end;
  if FileExists(MaintenanceDirectory + '\journal.json') then begin
    Result := '安装维护记录已存在，本次不接管该记录：' + MaintenanceDirectory; Exit;
  end;
  if not MaintenanceCommand('Prepare') then begin
    MaintenanceActive := FileExists(MaintenanceDirectory + '\journal.json');
    Result := '无法自动完成本安装的守护维护，本次不继续。' + #13#10 + MaintenanceFailure + #13#10 +
      '诊断及恢复记录：' + MaintenanceDirectory;
    Exit;
  end;
  MaintenanceActive := True;
  Result := InstallationProblem(False);
  if Result = '' then Result := GuardianRegistrationProblem;
end;

function MaintenanceHelperReady(Uninstalling: Boolean): Boolean;
var Source: String;
begin
  MaintenanceScript := ExpandConstant('{tmp}\guard-maintenance.ps1');
  if Uninstalling then begin
    Source := ExpandConstant('{app}\maintenance\guard-maintenance.ps1');
    Result := FileExists(Source);
    if Result then Result := CompareText(GetSHA256OfFile(Source), '{#MaintenanceHash}') = 0;
    if Result then Result := FileCopy(Source, MaintenanceScript, False);
  end else begin
    ExtractTemporaryFile('guard-maintenance.ps1');
    Result := True;
  end;
  if Result then Result := CompareText(GetSHA256OfFile(MaintenanceScript), '{#MaintenanceHash}') = 0;
#ifdef InstallerTestId
  MaintenanceDirectory := ExpandConstant('{localappdata}\NPEduTools-InstallerTest-{#InstallerTestId}\maintenance-') + ExtractFileName(ExpandConstant('{tmp}'));
#else
  MaintenanceDirectory := ExpandConstant('{commonappdata}\NPEduTools\installer-maintenance\') + ExtractFileName(ExpandConstant('{tmp}'));
#endif
end;

procedure RestoreGuardian;
begin
  if MaintenanceActive and (not MaintenanceCompleted) then
    if not MaintenanceCommand('Restore') then
      SuppressibleMsgBox('守护未能自动恢复。' + #13#10 + MaintenanceFailure + #13#10 +
        '请保留记录并按 GUARD-MAINTENANCE.md 处理：' + MaintenanceDirectory, mbError, MB_OK, IDOK);
end;

procedure CompleteGuardian;
begin
  if MaintenanceActive then begin
    if not MaintenanceCommand('Complete') then
      RaiseException('安装文件已处理，但守护维护状态未能解除。' + #13#10 + MaintenanceFailure + #13#10 +
        '请核查记录：' + MaintenanceDirectory);
    MaintenanceCompleted := True;
  end;
end;

#ifndef InstallerTestId
function ProtectedInstallProblem: String;
begin
  Result := '';
  if not MaintenanceCommand('ValidateInstallRoot') then
    Result := '安装目录或其程序文件无法确认为管理员专用。本次不会继续。请使用 Program Files 或另一个受管理员保护的本地目录。';
end;
#endif

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  OldVersion: String;
  Previous, Incoming: Int64;
#ifndef InstallerTestId
  LegacyPending: Cardinal;
#endif
begin
  Result := InstallationProblem(True);
  if Result <> '' then Exit;
  ManagedUninstallFresh := not RegValueExists(VersionRoot, VersionKey, 'InstallerVersion') and
    not FileExists(ExpandConstant('{app}\app\Host\NPEduTools.Host.exe'));
#ifndef InstallerTestId
  ManagedUninstallLegacyUpgrade := RegValueExists(VersionRoot, VersionKey, 'InstallerVersion') and
    not RegValueExists(VersionRoot, VersionKey, 'ManagedUninstallBinding') and
    (not RegValueExists(VersionRoot, VersionKey, 'ManagedUninstallProtocol') or
     (RegQueryDWordValue(VersionRoot, VersionKey, 'ManagedUninstallLegacyUpgradePending', LegacyPending) and (LegacyPending = 1))) and
    FileExists(ExpandConstant('{app}\app\Host\NPEduTools.Host.exe'));
#endif
  if RegValueExists(VersionRoot, VersionKey, 'InstallerVersion') then begin
    if not RegQueryStringValue(VersionRoot, VersionKey, 'InstallerVersion', OldVersion) then begin
      Result := '已有安装的版本记录无法读取，请先核查，不覆盖安装。';
      Exit;
    end;
    if not StrToVersion(OldVersion, Previous) or not StrToVersion('{#InstallerVersion}', Incoming) then
      Result := '已有安装的版本记录无法识别，请先核查，不覆盖安装。'
    else if ComparePackedVersion(Previous, Incoming) > 0 then
      Result := '已安装更新的版本，不能直接降级覆盖。请备份配置并按回退说明处理。';
  end;
  if Result <> '' then Exit;
  if not MaintenanceHelperReady(False) then begin
    Result := '守护维护助手完整性检查失败，本次不继续。'; Exit;
  end;
#ifndef InstallerTestId
  Result := ProtectedInstallProblem;
  if Result <> '' then Exit;
#endif
  Result := PrepareGuardian;
#ifndef InstallerTestId
  if (Result = '') and ManagedUninstallLegacyUpgrade then
    if not RegWriteDWordValue(VersionRoot, VersionKey, 'ManagedUninstallLegacyUpgradePending', 1) then
      Result := '无法保留原绑定的升级状态，本次不继续。';
#endif
end;

#ifndef InstallerTestId
function ManagedUninstallCommand(Action, Argument: String): Boolean;
var
  ResultCode: Integer;
  HostDir: String;
begin
  Result := False;
  HostDir := ExpandConstant('{app}\app\Host\');
  try
    if (CompareText(GetSHA256OfFile(HostDir + 'NPEduTools.Host.exe'), '{#ManagedUninstallExeHash}') <> 0) or
       (CompareText(GetSHA256OfFile(HostDir + 'NPEduTools.Host.dll'), '{#ManagedUninstallHostHash}') <> 0) or
       (CompareText(GetSHA256OfFile(HostDir + 'NPEduTools.Integrations.Npep.dll'), '{#ManagedUninstallNpepHash}') <> 0) then Exit;
  except
    Exit;
  end;
  Result := Exec(HostDir + 'NPEduTools.Host.exe',
    '--managed-uninstall ' + Action + ' ' + Argument, ExpandConstant('{sys}'), SW_HIDE, ewWaitUntilTerminated, ResultCode)
    and (ResultCode = 0);
end;

procedure InitializeManagedUninstallBinding;
var InstallationKind: String;
begin
  InstallationKind := 'upgrade';
  if ManagedUninstallFresh then InstallationKind := 'fresh';
  if ManagedUninstallLegacyUpgrade then InstallationKind := 'legacy-upgrade';
  if not ManagedUninstallCommand('initialize', InstallationKind) then
    RaiseException('无法保存学校绑定的升级状态。请使用同一完整安装包修复，本次不启动应用。');
  RegDeleteValue(VersionRoot, VersionKey, 'ManagedUninstallLegacyUpgradePending');
end;

function ManagedUninstallProblem(Action: String): String;
var
  Attempt: String;
  Index: Integer;
begin
  Result := '无法确认本机可卸载。受学校管理的设备，请用原配对 Windows 账号打开 NPEduTools，并在 NPDownloader 联网验证。';
  Attempt := ExpandConstant('{param:NPUNINSTALLATTEMPT|-}');
  if Attempt <> '-' then begin
    if Length(Attempt) <> 36 then Exit;
    for Index := 1 to Length(Attempt) do
      if Pos(Attempt[Index], '0123456789abcdefABCDEF-') = 0 then Exit;
  end;
  if ManagedUninstallCommand(Action, Attempt) then Result := '';
end;

procedure DeliverSchoolAccess;
var
  Preset: String;
  Index, ResultCode: Integer;
begin
  Preset := ExpandConstant('{param:NPSCHOOLACCESS|}');
  if (Length(Preset) = 0) or (Length(Preset) > 10923) then Exit;
  for Index := 1 to Length(Preset) do
    if Pos(Preset[Index], 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_') = 0 then Exit;
  if not Exec(ExpandConstant('{app}\app\NPEduTools.App.exe'), '--provision-school-access ' + Preset,
              ExpandConstant('{sys}'), SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
    Log('School access preset could not be delivered.');
end;
#endif

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then begin
#ifndef InstallerTestId
    if not MaintenanceCommand('SecureSchoolPolicyState') then
      RaiseException('安装后的程序目录未能通过保护检查，未启动任何已安装组件。');
#endif
    CompleteGuardian;
#ifndef InstallerTestId
    InitializeManagedUninstallBinding;
    DeliverSchoolAccess;
#endif
  end;
end;

procedure DeinitializeSetup;
begin
  RestoreGuardian;
end;

function StartupProblem: String;
var
  Command, Executable: String;
#ifndef InstallerTestId
  Users: TArrayOfString;
  Index: Integer;
#endif
begin
  Result := '';
  Executable := Lowercase(AddBackslash(ExpandConstant('{app}')) + 'app\NPEduTools.App.exe');
#ifdef InstallerTestId
  if RegQueryStringValue(VersionRoot, VersionKey, 'StartupCommand', Command) then
    if Pos(Executable, Lowercase(Command)) > 0 then Result := '请先在应用设置中关闭此安装位置的登录启动，再完整退出后卸载。';
#else
  { Inspect loaded accounts, but never modify another user's startup entries. }
  if not RegGetSubkeyNames(HKEY_USERS, '', Users) then begin
    Result := '无法检查登录启动登记，本次不会卸载。请检查注册表访问权限后重试。';
    Exit;
  end;
  for Index := 0 to GetArrayLength(Users) - 1 do begin
    if RegQueryStringValue(HKEY_USERS, Users[Index] + '\Software\Microsoft\Windows\CurrentVersion\Run', 'NPEduTools', Command) then
      if Pos(Executable, Lowercase(Command)) > 0 then begin
        Result := '此安装位置仍登记了 NPEduTools 登录启动。请在对应用户的应用设置中关闭，再完整退出后卸载。';
        Exit;
      end;
  end;
#endif
end;

function InitializeUninstall: Boolean;
var Problem: String;
begin
  Problem := InstallationProblem(True);
  if (Problem = '') and (not MaintenanceHelperReady(True)) then Problem := '守护维护助手缺失或被修改，本次不卸载，请使用新版完整安装包修复。';
#ifndef InstallerTestId
  if Problem = '' then Problem := ProtectedInstallProblem;
  if Problem = '' then Problem := ManagedUninstallProblem('probe');
#endif
  if Problem = '' then Problem := StartupProblem;
  Result := Problem = '';
  if not Result then SuppressibleMsgBox(Problem, mbError, MB_OK, IDOK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var Problem: String;
begin
  if CurUninstallStep = usUninstall then begin
    Problem := InstallationProblem(True);
    if Problem = '' then Problem := StartupProblem;
#ifndef InstallerTestId
    if Problem = '' then Problem := ProtectedInstallProblem;
    if Problem = '' then Problem := ManagedUninstallProblem('consume');
#endif
    if Problem = '' then Problem := PrepareGuardian;
    if Problem <> '' then RaiseException(Problem);
  end;
  if CurUninstallStep = usPostUninstall then begin
    CompleteGuardian;
#ifndef InstallerTestId
    { The old school latch must stay until the authorized uninstall actually completes. }
    DeleteFile(ExpandConstant('{app}\school-policy-state\classroom-action-policy.json'));
    DeleteFile(ExpandConstant('{app}\school-policy-state\school-equipment.json'));
    DeleteFile(ExpandConstant('{app}\school-policy-state\noise-management.json.school'));
    DeleteFile(ExpandConstant('{app}\school-policy-state\noise-management.json.classroom-password-unavailable'));
    DeleteFile(ExpandConstant('{app}\school-policy-state\classroom-action-policy.json.tmp'));
    DeleteFile(ExpandConstant('{app}\school-policy-state\school-equipment.json.tmp'));
    DeleteFile(ExpandConstant('{app}\school-policy-state\noise-management.json.school.tmp'));
    DeleteFile(ExpandConstant('{app}\school-policy-state\noise-management.json.classroom-password-unavailable.tmp'));
    DeleteFile(ExpandConstant('{app}\school-policy-state\host.lock'));
    RemoveDir(ExpandConstant('{app}\school-policy-state'));
    if RegValueExists(VersionRoot, VersionKey, 'ManagedUninstallGrant') then
      if not RegDeleteValue(VersionRoot, VersionKey, 'ManagedUninstallGrant') then begin
        Log('Managed uninstall grant cleanup failed; old binding retained.');
        Exit;
      end;
    if RegValueExists(VersionRoot, VersionKey, 'ManagedUninstallBinding') then
      if not RegDeleteValue(VersionRoot, VersionKey, 'ManagedUninstallBinding') then
        Log('Managed uninstall binding cleanup failed.');
    RegDeleteValue(VersionRoot, VersionKey, 'SchoolPolicyAuthority');
    RegDeleteValue(VersionRoot, VersionKey, 'ManagedUninstallLegacyCandidates');
    RegDeleteValue(VersionRoot, VersionKey, 'ManagedUninstallLegacyUpgradePending');
#endif
  end;
end;

procedure DeinitializeUninstall;
begin
  RestoreGuardian;
end;
