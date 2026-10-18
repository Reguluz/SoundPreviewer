; Stable AppId and install directory preserve the upgrade identity across versions.
#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef PublishDirectory
  #error PublishDirectory is required
#endif
#ifndef AppNumericVersion
  #error AppNumericVersion is required
#endif
#ifndef ReleaseDirectory
  #error ReleaseDirectory is required
#endif

[Setup]
AppId={{39F6B6EE-0C06-4E99-9F2F-0397C4D32612}
AppName=SoundPreviewer
AppVersion={#AppVersion}
VersionInfoVersion={#AppNumericVersion}
DefaultDirName={localappdata}\Programs\SoundPreviewer
DefaultGroupName=SoundPreviewer
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#ReleaseDirectory}
OutputBaseFilename=SoundPreviewer-{#AppVersion}-win-x64-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\SoundPreviewer.exe
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#PublishDirectory}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\SoundPreviewer"; Filename: "{app}\SoundPreviewer.exe"
Name: "{autodesktop}\SoundPreviewer"; Filename: "{app}\SoundPreviewer.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\SoundPreviewer.exe"; Description: "Launch SoundPreviewer"; Flags: nowait postinstall skipifsilent

; No user-data directories or registry settings are deleted during upgrade or uninstall.
