; MeetVault — per-user installer (Inno Setup 6).
;
; Regular-user shape: no admin, no UAC (PrivilegesRequired=lowest installs under
; %LOCALAPPDATA%\Programs), everything bundled so the app works offline right away, a Start-menu
; entry, an uninstaller, and a "launch now" checkbox.
;
; The payload is the SAME tree the portable zip is built from: tools\pack-portable.ps1 stages it
; (publish + the two shipped models + langid). Build with tools\build-installer.ps1 — it stages and
; then compiles this script. Never point StageDir at a hand-made folder: the shipped model set is
; declared in MeetVault.Core/EngineCatalog.cs and staged by that script.

#ifndef StageDir
  #define StageDir "..\publish\win-x64"
#endif
#define AppVersion "1.0.0"
#define AppPublisher "Supersanta"

[Setup]
; A stable AppId is what makes a later installer an UPGRADE of this one rather than a second copy.
AppId={{8F2A6C41-7B3D-4E58-9C1A-5D6E7F8A9B0C}
AppName=MeetVault
AppVersion={#AppVersion}
AppVerName=MeetVault {#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVersion}
VersionInfoDescription=MeetVault installer
DefaultDirName={autopf}\MeetVault
DefaultGroupName=MeetVault
DisableProgramGroupPage=yes
UninstallDisplayName=MeetVault
UninstallDisplayIcon={app}\MeetVault.exe
SetupIconFile=..\src\MeetVault\Assets\AppIcon.ico
OutputDir=..\publish
OutputBaseFilename=MeetVaultSetup-{#AppVersion}
; Per-user: no elevation prompt at all. An all-users install would need /ALLUSERS + admin.
PrivilegesRequired=lowest
; ...but allow an admin to ask for a machine-wide install explicitly (Setup.exe /ALLUSERS) without
; making the normal double-click path prompt for UAC.
PrivilegesRequiredOverridesAllowed=commandline
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
WizardStyle=modern
; The payload is ~1.9 GB and most of it (the Vosk model files) barely compresses: max/solid buys a
; few percent for many extra minutes. normal + solid is the sane dial; bump it if size ever wins.
Compression=lzma2/normal
SolidCompression=yes
; An upgrade must be able to replace the running exe.
CloseApplications=yes
RestartApplications=no
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Everything: MeetVault.exe + the .NET/WindowsAppSDK payload + libvosk/onnxruntime natives +
; vcruntime140*.dll + the shipped speech models (large-ja, large-vi, langid).
Source: "{#StageDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\MeetVault"; Filename: "{app}\MeetVault.exe"
Name: "{autodesktop}\MeetVault"; Filename: "{app}\MeetVault.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\MeetVault.exe"; Description: "{cm:LaunchProgram,MeetVault}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Deliberately NOT touching %APPDATA%\MeetVault: recordings, the meeting DB and the sign-in token
; are the user's data, and an uninstall must not destroy them.
