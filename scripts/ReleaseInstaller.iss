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

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\myFanControl"; Filename: "{app}\myFanControl.exe"
Name: "{autodesktop}\myFanControl"; Filename: "{app}\myFanControl.exe"; Tasks: desktopicon

[Code]
function AppWindowOpen: Boolean;
begin
  Result := FindWindowByWindowName('myFanControl · 风扇控制') <> 0;
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
