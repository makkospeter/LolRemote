#ifndef AppVersion
  #define AppVersion "1.1.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\out\app"
#endif
#ifndef OutputDir
  #define OutputDir "..\out"
#endif

[Setup]
AppId={{F373BC11-7E50-42DC-B8D3-4FA8B5F1B0FE}
AppName=LolRemote
AppVersion={#AppVersion}
AppVerName=LolRemote {#AppVersion}
AppPublisher=makkospeter
AppPublisherURL=https://github.com/makkospeter/LolRemote
AppSupportURL=https://github.com/makkospeter/LolRemote/issues
DefaultDirName={autopf}\LolRemote
DefaultGroupName=LolRemote
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=LolRemote-Setup
SetupIconFile=..\src\LolRemote.Companion\app.ico
UninstallDisplayIcon={app}\LolRemote.exe
UninstallDisplayName=LolRemote
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=force
RestartApplications=no
VersionInfoVersion={#AppVersion}
VersionInfoCompany=makkospeter
VersionInfoDescription=LolRemote telepítő
VersionInfoProductName=LolRemote

[Languages]
Name: "hungarian"; MessagesFile: "compiler:Languages\Hungarian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\LolRemote"; Filename: "{app}\LolRemote.exe"
Name: "{autodesktop}\LolRemote"; Filename: "{app}\LolRemote.exe"; Tasks: desktopicon

[Run]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=all program=""{app}\LolRemote.exe"""; Flags: runhidden; StatusMsg: "Tűzfal beállítása..."
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""LolRemote"" dir=in action=allow program=""{app}\LolRemote.exe"" enable=yes profile=any"; Flags: runhidden; StatusMsg: "Tűzfal beállítása..."
Filename: "{app}\LolRemote.exe"; Description: "{cm:LaunchProgram,LolRemote}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=all program=""{app}\LolRemote.exe"""; Flags: runhidden; RunOnceId: "RemoveFirewallRule"
