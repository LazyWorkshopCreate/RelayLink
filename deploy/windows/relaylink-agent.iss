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
Source: "agent-monitor-shortcut.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "agent-installer-preflight.ps1"; Flags: dontcopy

[Code]
var
  ModePage: TInputOptionWizardPage;
  ConfigPage: TInputFileWizardPage;
  ExistingInstall: Boolean;

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
  ModePage := CreateInputOptionPage(wpWelcome, '已有 RelayLink Agent 安装',
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
begin
  if CurStep <> ssPostInstall then Exit;
  Params := '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' +
    ExpandConstant('{app}\install-agent-service.ps1') + '" -ExecutablePath "' +
    ExpandConstant('{app}\RelayLink.Agent.exe') + '" -Mode ' + InstallMode;
  if NeedsConfig then Params := Params + ' -ConfigurationPath "' + SelectedConfig + '"';
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
