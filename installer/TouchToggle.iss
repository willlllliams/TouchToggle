; TouchToggle 安装程序（Inno Setup 6）
; 构建：运行 installer\build-installer.ps1，输出到 dist\

#define AppName "TouchToggle"
#define AppExe "TouchToggle.exe"
#define AppVersion GetStringFileInfo("..\" + AppExe, "ProductVersion")
#define AppPublisher "willlllliams"
#define AppURL "https://github.com/willlllliams/TouchToggle"

[Setup]
; AppId 唯一标识本程序，升级安装靠它识别旧版本，不要修改
AppId={{8F2C6E4A-3D1B-4E7A-9C55-6B0D2A7E91F3}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}/issues
AppUpdatesURL={#AppURL}/releases
AppCopyright=Copyright © 2026 TouchToggle contributors

; 后台助手以 SYSTEM 身份运行安装目录里的程序，所以安装目录必须是只有管理员能写的 Program Files，不提供修改
DefaultDirName={autopf}\{#AppName}
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041

LicenseFile=..\LICENSE
SetupIconFile=TouchToggle.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
WizardSmallImageFile=wizard-small-55.bmp,wizard-small-83.bmp,wizard-small-110.bmp
ShowLanguageDialog=auto
; 托盘程序由下面的 [Code] 关闭，不用重启管理器
CloseApplications=no

OutputDir=..\dist
OutputBaseFilename={#AppName}-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes

VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} 安装程序

[Languages]
Name: "chs"; MessagesFile: "ChineseSimplified.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
chs.AutoStart=登录 Windows 时自动启动 TouchToggle
en.AutoStart=Start TouchToggle when I sign in to Windows
chs.RegisteringHelper=正在注册后台助手…
en.RegisteringHelper=Registering the background helper...
chs.HelperFailed=后台助手注册失败（错误代码 %1）。程序已安装，但开关触摸设备、调节 ScreenPad 亮度可能无法使用。可以稍后在程序面板中重新安装后台助手。
en.HelperFailed=Failed to register the background helper (error %1). TouchToggle is installed, but toggling touch devices and ScreenPad brightness may not work. You can reinstall the helper later from the app's panel.

[Tasks]
Name: "autostart"; Description: "{cm:AutoStart}"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
; 开机自启动写在当前用户的启动项里，所以要以实际用户（而不是管理员）身份执行
Filename: "{app}\{#AppExe}"; Parameters: "--autostart on"; Flags: runasoriginaluser runhidden waituntilterminated; Tasks: autostart
Filename: "{app}\{#AppExe}"; Parameters: "--autostart off"; Flags: runasoriginaluser runhidden waituntilterminated; Tasks: not autostart
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: postinstall nowait skipifsilent runasoriginaluser

[UninstallRun]
; 删除后台助手的计划任务和 %ProgramData%\TouchToggle；程序文件由卸载程序自己删除
Filename: "{app}\{#AppExe}"; Parameters: "--uninstall --keep-files"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveHelper"

[UninstallDelete]
Type: files; Name: "{app}\*.old"

[Registry]
; 只在卸载时删除程序设置（如“联动”）
Root: HKCU; Subkey: "Software\TouchToggle"; Flags: uninsdeletekey dontcreatekey

[Code]
procedure KillApp;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(300);
end;

// 安装或升级前关闭正在运行的托盘程序，否则无法覆盖文件
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  KillApp;
  Result := '';
end;

// 文件复制完成后注册后台助手（计划任务 + 数据目录）
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssPostInstall then
  begin
    WizardForm.StatusLabel.Caption := CustomMessage('RegisteringHelper');
    if not Exec(ExpandConstant('{app}\{#AppExe}'), '--install', '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
       or (ResultCode <> 0) then
      SuppressibleMsgBox(FmtMessage(CustomMessage('HelperFailed'), [IntToStr(ResultCode)]), mbError, MB_OK, IDOK);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  // 用户确认卸载后才关闭程序
  if CurUninstallStep = usUninstall then
    KillApp;
  // 删除开机启动项（不论是安装时勾选的，还是之后在面板里打开的）
  if CurUninstallStep = usPostUninstall then
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'TouchToggle');
end;
