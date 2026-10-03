; Claude Pet installer. Build with scripts\build-installer.ps1, which publishes both apps into
; artifacts\publish first and then compiles this script with ISCC.

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#define AppName "Claude Pet"
; Must match ServiceHost.ServiceName.
#define ServiceName "ClaudePetStatusHub"
#define PublishDir "..\artifacts\publish"
#define DeskPetExe "DeskPet.App.exe"
#define RunKey "Software\Microsoft\Windows\CurrentVersion\Run"
#define RunValue "ClaudePet"

[Setup]
AppId={{8F3C2A51-6B7E-4D2A-9C1F-3E5B7A9D0C42}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Tenko
DefaultDirName={autopf}\ClaudePet
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\artifacts\installer
OutputBaseFilename=ClaudePet-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\DeskPet\{#DeskPetExe}
; The start-at-login value lives in HKCU of the installing user on purpose.
UsedUserAreasWarning=no

[Tasks]
Name: "autostart"; Description: "登录 Windows 时自动启动桌宠"; Flags: unchecked

[InstallDelete]
; Upgrades replace the program folders completely, so removed files do not linger.
Type: filesandordirs; Name: "{app}\StatusHub"
Type: filesandordirs; Name: "{app}\DeskPet"
Type: filesandordirs; Name: "{app}\plugin"

[Files]
Source: "{#PublishDir}\StatusHub\*"; DestDir: "{app}\StatusHub"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PublishDir}\DeskPet\*"; DestDir: "{app}\DeskPet"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\plugin\*"; DestDir: "{app}\plugin"; Flags: ignoreversion recursesubdirs createallsubdirs

[Dirs]
Name: "{commonappdata}\ClaudePet\hooks"

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\DeskPet\{#DeskPetExe}"
Name: "{group}\卸载 {#AppName}"; Filename: "{uninstallexe}"

[Registry]
Root: HKCU; Subkey: "{#RunKey}"; ValueType: string; ValueName: "{#RunValue}"; ValueData: """{app}\DeskPet\{#DeskPetExe}"""; Tasks: autostart

[Run]
; Re-register the plugin from the installed copy; removing first replaces a marketplace added from a repo checkout.
Filename: "{cmd}"; Parameters: "/c claude plugin marketplace remove deskpet-local & claude plugin marketplace add ""{app}\plugin"" && claude plugin install deskpet-hooks@deskpet-local"; StatusMsg: "正在注册 Claude Code 插件..."; Flags: runasoriginaluser runhidden waituntilterminated; Check: ClaudeCliFound
Filename: "{app}\DeskPet\{#DeskPetExe}"; Description: "启动 {#AppName}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{cmd}"; Parameters: "/c claude plugin uninstall deskpet-hooks@deskpet-local & claude plugin marketplace remove deskpet-local"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveClaudePlugin"

[Code]
const
  ServiceName = '{#ServiceName}';
  ErrorServiceMarkedForDelete = 1072;

var
  ClaudeCliChecked: Boolean;
  ClaudeCliPresent: Boolean;

function RunHidden(const FileName, Params: String): Integer;
var
  ResultCode: Integer;
begin
  if not Exec(FileName, Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    ResultCode := -1;
  Result := ResultCode;
end;

function Sc(const Params: String): Integer;
begin
  Result := RunHidden(ExpandConstant('{sys}\sc.exe'), Params);
end;

// Stops the service (net stop waits until it has stopped) and removes its registration.
procedure RemoveService;
begin
  RunHidden(ExpandConstant('{sys}\net.exe'), 'stop ' + ServiceName);
  Sc('delete ' + ServiceName);
end;

procedure CloseDeskPet;
begin
  RunHidden(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#DeskPetExe}');
end;

function ClaudeCliFound: Boolean;
var
  ResultCode: Integer;
begin
  if not ClaudeCliChecked then
  begin
    ClaudeCliPresent := ExecAsOriginalUser(ExpandConstant('{cmd}'), '/c where claude', '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
      and (ResultCode = 0);
    ClaudeCliChecked := True;
  end;
  Result := ClaudeCliPresent;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  // Upgrade: release the files that are about to be replaced.
  RemoveService;
  CloseDeskPet;
  Result := '';
end;

procedure InstallService;
var
  Exe, DataDir: String;
  Code, Attempt: Integer;
begin
  Exe := ExpandConstant('{app}\StatusHub\StatusHub.Service.exe');
  DataDir := ExpandConstant('{commonappdata}\ClaudePet\hooks');
  // A service deleted moments ago can stay "marked for deletion" briefly.
  for Attempt := 1 to 20 do
  begin
    Code := Sc('create ' + ServiceName
      + ' binPath= "\"' + Exe + '\" --HookIngest:DataDirectory=\"' + DataDir + '\""'
      + ' start= auto DisplayName= "Claude Pet StatusHub"');
    if Code <> ErrorServiceMarkedForDelete then
      Break;
    Sleep(500);
  end;
  if Code <> 0 then
  begin
    MsgBox('注册后台服务失败（sc.exe 返回 ' + IntToStr(Code) + '）。可以稍后用管理员身份运行 scripts\install-service.ps1 手动注册。',
      mbError, MB_OK);
    Exit;
  end;
  Sc('description ' + ServiceName + ' "Receives Claude Code hook events on localhost and broadcasts the aggregated status."');
  // Restart after a crash: 5 s delay, failure count resets after one day.
  Sc('failure ' + ServiceName + ' reset= 86400 actions= restart/5000/restart/5000/restart/5000');
  Sc('start ' + ServiceName);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    InstallService;
    if not ClaudeCliFound and not WizardSilent then
      MsgBox('没有找到 claude 命令，Claude Code 插件未注册。安装 Claude Code 后，在命令行执行：' + #13#10#13#10
        + 'claude plugin marketplace add "' + ExpandConstant('{app}\plugin') + '"' + #13#10
        + 'claude plugin install deskpet-hooks@deskpet-local', mbInformation, MB_OK);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    RemoveService;
    CloseDeskPet;
    // Also covers the value written by the app's own "开机自启" menu item.
    RegDeleteValue(HKCU, '{#RunKey}', '{#RunValue}');
  end;
end;
