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
; No automatic process termination, restart, startup registration, or recursive deletion.

[Languages]
Name: "chinesesimplified"; MessagesFile: "Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："; Flags: unchecked

[Files]
Source: "{#PackageRoot}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

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

function InstallationProblem: String;
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
    Query := 'SELECT Name, ExecutablePath FROM Win32_Process WHERE Name LIKE ''NPEduTools.%'' OR Name = ''ffmpeg.exe'' OR Name = ''ffprobe.exe''';
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

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  OldVersion: String;
  Previous, Incoming: Int64;
begin
  Result := InstallationProblem;
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
  Problem := InstallationProblem;
  if Problem = '' then Problem := StartupProblem;
  Result := Problem = '';
  if not Result then SuppressibleMsgBox(Problem, mbError, MB_OK, IDOK);
end;
