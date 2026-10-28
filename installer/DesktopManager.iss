#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif

#define MyAppName "桌面管家"
#define MyAppExeName "桌面管家.exe"

[Setup]
AppId={{B68C4C5A-DBF8-42EC-987A-1BA203187F81}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=Desktop Manager contributors
DefaultDirName={localappdata}\Programs\DesktopManager
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesInstallIn64BitMode=x64
OutputDir=..\artifacts
OutputBaseFilename=DesktopManager-Setup
SetupIconFile=..\src\DesktopManager\Assets\desktop-mascot.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=yes
VersionInfoVersion={#MyAppVersion}
VersionInfoProductName={#MyAppName}
VersionInfoDescription={#MyAppName} Windows installer

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加快捷方式："; Flags: unchecked

[Files]
Source: "..\artifacts\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "启动桌面管家"; Flags: postinstall nowait skipifsilent
