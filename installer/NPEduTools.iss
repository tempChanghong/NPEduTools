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
  MaintenanceActive, MaintenanceCompleted: Boolean;

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

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  OldVersion: String;
  Previous, Incoming: Int64;
begin
  Result := InstallationProblem(True);
  if Result <> '' then Exit;
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
  Result := PrepareGuardian;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then CompleteGuardian;
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
  if Problem = '' then Problem := StartupProblem;
  if (Problem = '') and (not MaintenanceHelperReady(True)) then Problem := '守护维护助手缺失或被修改，本次不卸载，请使用新版完整安装包修复。';
  Result := Problem = '';
  if not Result then SuppressibleMsgBox(Problem, mbError, MB_OK, IDOK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var Problem: String;
begin
  if CurUninstallStep = usUninstall then begin
    Problem := InstallationProblem(True);
    if Problem = '' then Problem := StartupProblem;
    if Problem = '' then Problem := PrepareGuardian;
    if Problem <> '' then RaiseException(Problem);
  end;
  if CurUninstallStep = usPostUninstall then CompleteGuardian;
end;

procedure DeinitializeUninstall;
begin
  RestoreGuardian;
end;
