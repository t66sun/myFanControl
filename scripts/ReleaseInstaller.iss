#ifndef SourceDir
  #error SourceDir must point to an extracted release ZIP.
#endif
#ifndef AppVersion
  #error AppVersion must match the release ZIP.
#endif

[Setup]
AppId={{9b42f13b-68b7-4888-be24-8bf8728cda45}
AppName=myFanControl
AppVersion={#AppVersion}
AppPublisher=t66sun
AppPublisherURL=https://github.com/t66sun/myFanControl
DefaultDirName={autopf}\myFanControl
DefaultGroupName=myFanControl
UninstallDisplayIcon={app}\myFanControl.exe
SetupArchitecture=x64
ArchitecturesAllowed=x64os
PrivilegesRequired=admin
WizardStyle=modern
InfoBeforeFile=InstallerPrerequisites.txt
AppMutex=Global\myFanControl.21CX.RpmOwner
CloseApplications=no
Compression=lzma2
SolidCompression=yes

[Languages]
Name: "zh"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
zh.CloseAppFirst=请先在 myFanControl 托盘菜单选择“恢复固件并退出”，然后重试。
en.CloseAppFirst=Exit myFanControl from its tray menu using "Restore firmware and exit", then try again.
zh.DriverDownload=下载 PawnIO 驱动
en.DriverDownload=Download PawnIO driver
zh.DriverDownloadDesc=正在从官方发布页面下载并校验驱动安装程序。
en.DriverDownloadDesc=Downloading and verifying the official driver installer.
zh.DriverError=PawnIO 准备失败。请检查网络或已有驱动，重试安装。错误：
en.DriverError=PawnIO preparation failed. Check your network or existing driver and retry. Error:

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "PawnIoPrerequisite.ps1"; Flags: dontcopy

[Icons]
Name: "{group}\myFanControl"; Filename: "{app}\myFanControl.exe"
Name: "{autodesktop}\myFanControl"; Filename: "{app}\myFanControl.exe"; Tasks: desktopicon

[Code]
var
  DriverDownloadPage: TDownloadWizardPage;
  DriverRestartRequired: Boolean;

function AppWindowOpen: Boolean;
begin
  Result := FindWindowByWindowName('myFanControl · 风扇控制') <> 0;
end;

procedure InitializeWizard;
begin
  DriverDownloadPage := CreateDownloadPage(ExpandConstant('{cm:DriverDownload}'), ExpandConstant('{cm:DriverDownloadDesc}'), nil);
  DriverDownloadPage.ShowBaseNameInsteadOfUrl := True;
end;

function ProbeDriver(Arguments: String): Integer;
var
  ExitCode: Integer;
begin
  Result := 20;
  if Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + ExpandConstant('{tmp}\PawnIoPrerequisite.ps1') + '" ' + Arguments,
    '', SW_HIDE, ewWaitUntilTerminated, ExitCode) then Result := ExitCode;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  State, ExitCode: Integer;
begin
  Result := '';
  if AppWindowOpen then begin
    Result := ExpandConstant('{cm:CloseAppFirst}');
    exit;
  end;
  if DriverRestartRequired then exit;
  try
    ExtractTemporaryFile('PawnIoPrerequisite.ps1');
    State := ProbeDriver('');
    if State = 0 then exit;
    if State <> 10 then RaiseException('Existing driver could not be verified (' + IntToStr(State) + ').');
    DriverDownloadPage.Clear;
    DriverDownloadPage.Add('{#PawnIOUrl}', 'PawnIO_setup.exe', '{#PawnIOSha256}');
    DriverDownloadPage.Show;
    try
      DriverDownloadPage.Download;
    finally
      DriverDownloadPage.Hide;
    end;
    if ProbeDriver('-InstallerPath "' + ExpandConstant('{tmp}\PawnIO_setup.exe') + '" -ExpectedHash "{#PawnIOSha256}"') <> 0 then
      RaiseException('Downloaded installer hash or Authenticode signature is invalid.');
    if not Exec(ExpandConstant('{tmp}\PawnIO_setup.exe'), '-install -silent', '', SW_HIDE, ewWaitUntilTerminated, ExitCode) then
      RaiseException('Cannot start the official PawnIO installer.');
    if ExitCode = 3010 then DriverRestartRequired := True
    else if ExitCode <> 0 then RaiseException('PawnIO installer returned ' + IntToStr(ExitCode) + '.')
    else if ProbeDriver('') <> 0 then RaiseException('PawnIO installation could not be verified.');
  except
    Result := ExpandConstant('{cm:DriverError}') + ' ' + GetExceptionMessage;
  end;
end;

function NeedRestart: Boolean;
begin
  Result := DriverRestartRequired;
end;

function InitializeSetup: Boolean;
begin
  Result := not AppWindowOpen;
  if not Result then MsgBox(ExpandConstant('{cm:CloseAppFirst}'), mbError, MB_OK);
end;

function InitializeUninstall: Boolean;
begin
  Result := not AppWindowOpen;
  if not Result then MsgBox(ExpandConstant('{cm:CloseAppFirst}'), mbError, MB_OK);
end;
