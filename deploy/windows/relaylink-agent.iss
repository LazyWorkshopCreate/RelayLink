#ifndef PublishDir
  #error PublishDir must point to a win-x64 Agent publish directory.
#endif
#ifndef PackageVersion
  #define PackageVersion "1.0.0"
#endif

[Setup]
AppId={{F34F4AB2-43B3-4E22-9A04-58CC1F15E78B}
AppName=RelayLink Agent
AppVersion={#PackageVersion}
DefaultDirName={autopf}\RelayLink\Agent
DisableDirPage=yes
DisableProgramGroupPage=yes
OutputDir=..\..\artifacts\installer
OutputBaseFilename=RelayLink-Agent-win-x64-{#PackageVersion}
Compression=lzma2
SolidCompression=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=no
SetupLogging=yes
UninstallDisplayName=RelayLink Agent

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion
Source: "install-agent-service.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "register-agent-service-legacy.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "uninstall-agent-service.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "agent-monitor-shortcut.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "agent-installer-preflight.ps1"; Flags: dontcopy

[Code]
var
  DependencyPage: TWizardPage;
  OsStatusLabel: TNewStaticText;
  ArchitectureStatusLabel: TNewStaticText;
  VcStatusLabel: TNewStaticText;
  DotNetStatusLabel: TNewStaticText;
  VcDownloadLabel: TNewStaticText;
  DotNetDownloadLabel: TNewStaticText;
  DependencyChecksPassed: Boolean;
  ModePage: TInputOptionWizardPage;
  ConfigPage: TInputFileWizardPage;
  ExistingInstall: Boolean;

function IsSupportedWindows: Boolean;
var
  Version: TWindowsVersion;
begin
  GetWindowsVersionEx(Version);
  Result := (Version.Major > 6) or ((Version.Major = 6) and (Version.Minor >= 3));
end;

function IsVcRuntimeInstalled: Boolean;
var
  Installed: Cardinal;
begin
  Result := IsWin64 and
    RegQueryDWordValue(HKLM64, 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64',
      'Installed', Installed) and (Installed = 1);
end;

function HasDotNet10SharedFramework(const FrameworkName: String): Boolean;
var
  Versions: TArrayOfString;
  Index: Integer;
  Key: String;
begin
  Result := False;
  Key := 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\' + FrameworkName;
  { The official x64 .NET installer records shared frameworks in the 32-bit
    registry view (physically under WOW6432Node). Check both views to support
    the official layout and installations created by older tooling. }
  if RegGetValueNames(HKLM32, Key, Versions) then
    for Index := 0 to GetArrayLength(Versions) - 1 do
      if Pos('10.', Versions[Index]) = 1 then begin
        Result := True;
        Exit;
      end;
  if RegGetValueNames(HKLM64, Key, Versions) then
    for Index := 0 to GetArrayLength(Versions) - 1 do
      if Pos('10.', Versions[Index]) = 1 then begin
        Result := True;
        Exit;
      end;
end;

function IsDotNetRuntimeInstalled: Boolean;
begin
  Result := HasDotNet10SharedFramework('Microsoft.NETCore.App') and
    HasDotNet10SharedFramework('Microsoft.AspNetCore.App');
end;

procedure SetDependencyStatus(StatusLabel: TNewStaticText; Passed: Boolean; const Text: String);
begin
  if Passed then begin
    StatusLabel.Caption := '✓  ' + Text;
    StatusLabel.Font.Color := clGreen;
  end else begin
    StatusLabel.Caption := '✗  ' + Text;
    StatusLabel.Font.Color := clRed;
  end;
end;

procedure OpenVcDownload(Sender: TObject);
var
  ErrorCode: Integer;
begin
  ShellExec('open', 'https://aka.ms/vs/17/release/vc_redist.x64.exe', '', '',
    SW_SHOWNORMAL, ewNoWait, ErrorCode);
end;

procedure OpenDotNetDownload(Sender: TObject);
var
  ErrorCode: Integer;
begin
  ShellExec('open', 'https://dotnet.microsoft.com/download/dotnet/10.0', '', '',
    SW_SHOWNORMAL, ewNoWait, ErrorCode);
end;

procedure RefreshDependencyChecks;
var
  OsPassed: Boolean;
  ArchitecturePassed: Boolean;
  VcPassed: Boolean;
  DotNetPassed: Boolean;
begin
  OsPassed := IsSupportedWindows;
  ArchitecturePassed := IsWin64;
  VcPassed := IsVcRuntimeInstalled;
  DotNetPassed := IsDotNetRuntimeInstalled;

  SetDependencyStatus(OsStatusLabel, OsPassed, 'Windows Server 2012 R2 / Windows 8.1 或更高版本');
  SetDependencyStatus(ArchitectureStatusLabel, ArchitecturePassed, 'x64 操作系统');
  SetDependencyStatus(VcStatusLabel, VcPassed, 'Microsoft Visual C++ 2015–2022 Redistributable (x64)');
  SetDependencyStatus(DotNetStatusLabel, DotNetPassed, '.NET 10 ASP.NET Core Runtime (x64)');
  VcDownloadLabel.Visible := not VcPassed;
  DotNetDownloadLabel.Visible := not DotNetPassed;
  DependencyChecksPassed := OsPassed and ArchitecturePassed and VcPassed and DotNetPassed;
  Log(Format('Dependency checks: OS=%d, x64=%d, VC=%d, dotnet-aspnetcore-10=%d', [Ord(OsPassed), Ord(ArchitecturePassed), Ord(VcPassed), Ord(DotNetPassed)]));
end;

function InstallMode: String;
begin
  if not ExistingInstall then
    Result := 'Install'
  else if WizardSilent then begin
    Result := Lowercase(Trim(ExpandConstant('{param:MODE|}')));
    if Result = 'update' then Result := 'Update'
    else if Result = 'reconfigure' then Result := 'Reconfigure';
  end else if ModePage.Values[0] then
    Result := 'Update'
  else
    Result := 'Reconfigure';
end;

function NeedsConfig: Boolean;
begin
  Result := InstallMode <> 'Update';
end;

function SelectedConfig: String;
begin
  if WizardSilent then
    Result := Trim(ExpandConstant('{param:CONFIG|}'))
  else
    Result := Trim(ConfigPage.Values[0]);
end;

function ConfigProblem: String;
var
  ConfigPath: String;
begin
  Result := '';
  if not NeedsConfig then Exit;
  ConfigPath := SelectedConfig;
  if ConfigPath = '' then
    Result := '必须选择 Agent JSON 配置文件，未指定配置不得安装。'
  else if not FileExists(ConfigPath) then
    Result := '配置文件不存在：' + ConfigPath
  else if CompareText(ExtractFileExt(ConfigPath), '.json') <> 0 then
    Result := '请选择 .json 格式的 Agent 配置文件。';
end;

procedure InitializeWizard;
begin
  ExistingInstall := RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\RelayLinkAgent');
  // The {app} constant is not initialized while InitializeWizard is running.
  if not ExistingInstall then
    ExistingInstall := FileExists(ExpandConstant('{autopf}\RelayLink\Agent\RelayLink.Agent.exe'));
  DependencyPage := CreateCustomPage(wpWelcome, '安装依赖检查',
    '必须通过以下检查才能安装 RelayLink Agent。');
  OsStatusLabel := TNewStaticText.Create(DependencyPage);
  OsStatusLabel.Parent := DependencyPage.Surface;
  OsStatusLabel.SetBounds(ScaleX(0), ScaleY(8), DependencyPage.SurfaceWidth, ScaleY(20));
  ArchitectureStatusLabel := TNewStaticText.Create(DependencyPage);
  ArchitectureStatusLabel.Parent := DependencyPage.Surface;
  ArchitectureStatusLabel.SetBounds(ScaleX(0), ScaleY(38), DependencyPage.SurfaceWidth, ScaleY(20));
  VcStatusLabel := TNewStaticText.Create(DependencyPage);
  VcStatusLabel.Parent := DependencyPage.Surface;
  VcStatusLabel.SetBounds(ScaleX(0), ScaleY(68), DependencyPage.SurfaceWidth, ScaleY(20));
  VcDownloadLabel := TNewStaticText.Create(DependencyPage);
  VcDownloadLabel.Parent := DependencyPage.Surface;
  VcDownloadLabel.Caption := '下载 Microsoft Visual C++ x64 运行库';
  VcDownloadLabel.SetBounds(ScaleX(22), ScaleY(90), DependencyPage.SurfaceWidth, ScaleY(20));
  VcDownloadLabel.Font.Color := clBlue;
  VcDownloadLabel.Font.Style := [fsUnderline];
  VcDownloadLabel.Cursor := crHand;
  VcDownloadLabel.OnClick := @OpenVcDownload;
  DotNetStatusLabel := TNewStaticText.Create(DependencyPage);
  DotNetStatusLabel.Parent := DependencyPage.Surface;
  DotNetStatusLabel.SetBounds(ScaleX(0), ScaleY(122), DependencyPage.SurfaceWidth, ScaleY(20));
  DotNetDownloadLabel := TNewStaticText.Create(DependencyPage);
  DotNetDownloadLabel.Parent := DependencyPage.Surface;
  DotNetDownloadLabel.Caption := '下载 .NET 10 ASP.NET Core Runtime (x64)';
  DotNetDownloadLabel.SetBounds(ScaleX(22), ScaleY(144), DependencyPage.SurfaceWidth, ScaleY(20));
  DotNetDownloadLabel.Font.Color := clBlue;
  DotNetDownloadLabel.Font.Style := [fsUnderline];
  DotNetDownloadLabel.Cursor := crHand;
  DotNetDownloadLabel.OnClick := @OpenDotNetDownload;
  RefreshDependencyChecks;

  ModePage := CreateInputOptionPage(DependencyPage.ID, '已有 RelayLink Agent 安装',
    '请选择升级方式', '仅更新保留本地配置和日志；重新配置会清空 Agent 管理的本地状态和日志。', True, False);
  ModePage.Add('仅更新软件程序（保留配置、身份、端口状态和日志）');
  ModePage.Add('重新配置（必须选择新的 JSON，并清空本地配置和日志）');
  ModePage.Values[0] := True;
  ConfigPage := CreateInputFilePage(ModePage.ID, '选择 Agent 配置文件',
    '首次安装或重新配置必须选择由服务端生成的 Agent JSON 文件。',
    '重新配置会删除原有配置、端到端身份、端口状态和诊断日志。');
  ConfigPage.Add('Agent 配置文件：', 'JSON 文件|*.json|所有文件|*.*', '.json');
  ConfigPage.Values[0] := ExpandConstant('{param:CONFIG|}');
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = DependencyPage.ID then RefreshDependencyChecks;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := ((PageID = ModePage.ID) and (not ExistingInstall)) or
    ((PageID = ConfigPage.ID) and (not NeedsConfig));
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Problem: String;
begin
  Result := True;
  if (CurPageID = DependencyPage.ID) and (not DependencyChecksPassed) then begin
    MsgBox('存在未满足的安装依赖。请使用页面中的官方下载链接完成安装，然后重新检查。', mbError, MB_OK);
    Result := False;
    Exit;
  end;
  if (CurPageID = ModePage.ID) and ExistingInstall and (InstallMode = 'Reconfigure') then begin
    Result := MsgBox('重新配置会删除现有 Agent 的配置、端到端身份、端口状态和诊断日志。请确认已备份所需文件，并准备好新的客户端 JSON。是否继续？',
      mbConfirmation, MB_YESNO) = IDYES;
  end;
  if CurPageID = ConfigPage.ID then begin
    Problem := ConfigProblem;
    if Problem <> '' then begin
      MsgBox(Problem, mbError, MB_OK);
      Result := False;
    end;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
  Params: String;
begin
  Result := '';
  RefreshDependencyChecks;
  if not DependencyChecksPassed then begin
    Result := '安装依赖检查未通过；请查看安装日志中的 Dependency checks 记录。';
    Exit;
  end;
  if ExistingInstall and (InstallMode <> 'Update') and (InstallMode <> 'Reconfigure') then begin
    Result := '已有安装的静默升级必须指定 /MODE=update 或 /MODE=reconfigure。';
    Exit;
  end;
  Result := ConfigProblem;
  if Result <> '' then Exit;
  ExtractTemporaryFile('agent-installer-preflight.ps1');
  Params := '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' +
    ExpandConstant('{tmp}\agent-installer-preflight.ps1') + '" -Mode ' + InstallMode +
    ' -ExecutablePath "' + ExpandConstant('{app}\RelayLink.Agent.exe') + '"';
  if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
      Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
    Result := 'Agent 服务检查或停止失败（退出码 ' + IntToStr(ResultCode) + '）。请检查服务归属和 Windows 事件日志。';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  Params: String;
  ErrorReportPath: String;
  ErrorDetails: AnsiString;
begin
  if CurStep = ssPostInstall then begin
    ErrorReportPath := ExpandConstant('{tmp}\relaylink-service-install-error.txt');
    DeleteFile(ErrorReportPath);
    Params := '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' +
      ExpandConstant('{app}\install-agent-service.ps1') + '" -ExecutablePath "' +
      ExpandConstant('{app}\RelayLink.Agent.exe') + '" -Mode ' + InstallMode +
      ' -ErrorReportPath "' + ErrorReportPath + '"';
    if NeedsConfig then Params := Params + ' -ConfigurationPath "' + SelectedConfig + '"';
    Log('Starting Agent configuration and service registration.');
    if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
        Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
      RaiseException('无法启动 Agent 服务安装程序：' + SysErrorMessage(ResultCode));
    if ResultCode <> 0 then begin
      ErrorDetails := '未生成错误详情';
      LoadStringFromFile(ErrorReportPath, ErrorDetails);
      Log('Agent service registration failed: ' + ErrorDetails);
      RaiseException('Agent 配置校验或服务安装失败（退出码 ' + IntToStr(ResultCode) +
        '）：' + ErrorDetails);
    end;
    Log('Agent service registration completed successfully.');
  end;
end;

procedure DeinitializeSetup;
var
  LogDirectory: String;
  LogDestination: String;
  SourceLog: String;
begin
  SourceLog := ExpandConstant('{log}');
  if (SourceLog = '') or (not FileExists(SourceLog)) then Exit;
  LogDirectory := ExpandConstant('{commonappdata}\RelayLink\Agent\installer-logs');
  if ForceDirectories(LogDirectory) then begin
    LogDestination := LogDirectory + '\install-' + GetDateTimeString('yyyymmddhhnnss', '-', ':') + '.log';
    if not CopyFile(SourceLog, LogDestination, False) then
      Log('Could not persist installer log to ' + LogDestination);
  end else
    Log('Could not create installer log directory ' + LogDirectory);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
  Params: String;
  ErrorReportPath: String;
  ErrorDetails: AnsiString;
begin
  if CurUninstallStep <> usUninstall then Exit;
  ErrorReportPath := ExpandConstant('{tmp}\relaylink-service-uninstall-error.txt');
  DeleteFile(ErrorReportPath);
  Params := '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' +
    ExpandConstant('{app}\uninstall-agent-service.ps1') + '" -ExecutablePath "' +
    ExpandConstant('{app}\RelayLink.Agent.exe') + '" -ErrorReportPath "' + ErrorReportPath + '"';
  if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
      Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
  begin
    ErrorDetails := '未生成错误详情';
    LoadStringFromFile(ErrorReportPath, ErrorDetails);
    RaiseException('无法停止或删除 RelayLinkAgent 服务，安装文件已保留：' + ErrorDetails);
  end;
end;
