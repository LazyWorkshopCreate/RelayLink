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
UninstallDisplayName=RelayLink Agent

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion
Source: "install-agent-service.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "uninstall-agent-service.ps1"; DestDir: "{app}"; Flags: ignoreversion

[Code]
var
  ConfigPage: TInputFileWizardPage;

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
  ConfigPage := CreateInputFilePage(wpWelcome, '选择 Agent 配置文件',
    '安装前必须选择由服务端生成的 Agent JSON 配置文件。',
    '安装程序会校验配置并复制到受保护的 ProgramData 目录。');
  ConfigPage.Add('Agent 配置文件：', 'JSON 文件|*.json|所有文件|*.*', '.json');
  ConfigPage.Values[0] := ExpandConstant('{param:CONFIG|}');
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Problem: String;
begin
  Result := True;
  if CurPageID = ConfigPage.ID then begin
    Problem := ConfigProblem;
    if Problem <> '' then begin
      MsgBox(Problem, mbError, MB_OK);
      Result := False;
    end;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := ConfigProblem;
  if Result <> '' then Exit;
  if RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\RelayLinkAgent') then
    Result := 'RelayLinkAgent 服务已存在。为避免覆盖现有服务，请先明确卸载旧版本。';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  Params: String;
begin
  if CurStep <> ssPostInstall then Exit;
  Params := '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' +
    ExpandConstant('{app}\install-agent-service.ps1') + '" -ExecutablePath "' +
    ExpandConstant('{app}\RelayLink.Agent.exe') + '" -ConfigurationPath "' +
    SelectedConfig + '"';
  if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
      Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    RaiseException('无法启动 Agent 服务安装程序：' + SysErrorMessage(ResultCode));
  if ResultCode <> 0 then
    RaiseException('Agent 配置校验或服务安装失败（退出码 ' + IntToStr(ResultCode) +
      '）。请检查配置文件、权限和 Windows 事件日志。');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
  Params: String;
begin
  if CurUninstallStep <> usUninstall then Exit;
  Params := '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' +
    ExpandConstant('{app}\uninstall-agent-service.ps1') + '" -ExecutablePath "' +
    ExpandConstant('{app}\RelayLink.Agent.exe') + '"';
  if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
      Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
    RaiseException('无法停止或删除 RelayLinkAgent 服务，安装文件已保留。');
end;
