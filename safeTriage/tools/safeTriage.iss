#define AppName "安诊 safeTriage"
#define AppVersion GetVersionNumbersString(PayloadExe)
#define DisplayVersion Copy(AppVersion, 1, RPos(".", AppVersion) - 1)

[Setup]
AppId={{BA7045A7-25DC-40AF-9E57-30318D6A2038}
AppName={#AppName}
AppVersion={#DisplayVersion}
AppPublisher=safeTriage
AppPublisherURL=https://github.com/charm-xr/tool/tree/main/safeTriage
AppSupportURL=https://github.com/charm-xr/tool/issues
AppUpdatesURL=https://github.com/charm-xr/tool/releases
DefaultDirName={localappdata}\Programs\safeTriage
DefaultGroupName=safeTriage
DisableDirPage=no
DisableProgramGroupPage=yes
DisableWelcomePage=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
UninstallDisplayIcon={app}\safeTriage.exe
SetupIconFile=..\src\Icon.ico
OutputBaseFilename=safeTriage-Setup-Windows-x64
VersionInfoVersion={#AppVersion}
VersionInfoDescription=safeTriage Windows Installer
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "zhcn"; MessagesFile: "{#ChineseMessages}"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
zhcn.FrameworkRequired=安装安诊需要 .NET Framework 4.8 或更高版本。请先安装该组件。
en.FrameworkRequired=safeTriage requires .NET Framework 4.8 or later. Install it before continuing.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PayloadExe}"; DestDir: "{app}"; DestName: "safeTriage.exe"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\safeTriage.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\safeTriage.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\safeTriage.exe"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
function InitializeSetup(): Boolean;
begin
  Result := IsDotNetInstalled(net48, 0);
  if not Result then
    SuppressibleMsgBox(CustomMessage('FrameworkRequired'), mbError, MB_OK, IDOK);
end;
