#ifndef AppVersion
  #error AppVersion must be supplied by build-installer.ps1
#endif
#ifndef PayloadRoot
  #error PayloadRoot must be supplied by build-installer.ps1
#endif
[Setup]
AppId={{6E2B5D8A-3F41-4C7B-9A0E-7D1F2C9B4E63}
AppName=VideoTrimmer
AppVersion={#AppVersion}
VersionInfoVersion={#AppVersion}
AppPublisher=VideoTrimmer
AppUpdatesURL=https://github.com/dgoldwas/VideoTrimmer/releases
DefaultDirName={autopf}\VideoTrimmer
UsePreviousAppDir=no
DefaultGroupName=VideoTrimmer
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\artifacts
OutputBaseFilename=VideoTrimmer-{#AppVersion}-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\assets\VideoTrimmer.ico
UninstallDisplayIcon={app}\VideoTrimmer.exe
CloseApplications=yes
ChangesAssociations=yes

[InstallDelete]
; Updates replace the whole bundled FFmpeg build rather than mixing DLLs from two versions.
Type: filesandordirs; Name: "{app}\tools"

[Files]
Source: "{#PayloadRoot}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\VideoTrimmer"; Filename: "{app}\VideoTrimmer.exe"
Name: "{autodesktop}\VideoTrimmer"; Filename: "{app}\VideoTrimmer.exe"; Tasks: desktopicon

[Tasks]
Name: desktopicon; Description: "Create a desktop shortcut"; Flags: unchecked

[Registry]
; Offer VideoTrimmer in Explorer's "Open with" menu for video files, without taking over any default app.
Root: HKA; Subkey: "Software\Classes\Applications\VideoTrimmer.exe"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "VideoTrimmer"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\Applications\VideoTrimmer.exe\DefaultIcon"; ValueType: string; ValueData: "{app}\VideoTrimmer.exe,0"
Root: HKA; Subkey: "Software\Classes\Applications\VideoTrimmer.exe\shell\open\command"; ValueType: string; ValueData: """{app}\VideoTrimmer.exe"" ""%1"""
Root: HKA; Subkey: "Software\Classes\Applications\VideoTrimmer.exe\SupportedTypes"; ValueType: string; ValueName: ".mp4"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\Applications\VideoTrimmer.exe\SupportedTypes"; ValueType: string; ValueName: ".mkv"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\Applications\VideoTrimmer.exe\SupportedTypes"; ValueType: string; ValueName: ".mov"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\Applications\VideoTrimmer.exe\SupportedTypes"; ValueType: string; ValueName: ".avi"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\Applications\VideoTrimmer.exe\SupportedTypes"; ValueType: string; ValueName: ".webm"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\Applications\VideoTrimmer.exe\SupportedTypes"; ValueType: string; ValueName: ".ts"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\Applications\VideoTrimmer.exe\SupportedTypes"; ValueType: string; ValueName: ".m2ts"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\Applications\VideoTrimmer.exe\SupportedTypes"; ValueType: string; ValueName: ".wmv"; ValueData: ""

[Run]
Filename: "{app}\VideoTrimmer.exe"; Description: "Launch VideoTrimmer"; Flags: nowait postinstall skipifsilent
