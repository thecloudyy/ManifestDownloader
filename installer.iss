; ManifestDownloader installer — per-user, no admin rights required.
;
; Built locally (this repo ships no CI):
;   cargo build --release --manifest-path src-tauri\Cargo.toml
;   copy src-tauri\target\release\manifest-downloader.exe dist\ManifestDownloader.exe
;   ISCC.exe installer.iss
;
; Installs the Tauri build to %LocalAppData%\Programs\ManifestDownloader.

#define MyAppName "ManifestDownloader"
#define MyAppPublisher "thecloudyy"
#define MyAppURL "https://github.com/thecloudyy/ManifestDownloader"
#define MyAppExeName "ManifestDownloader.exe"

[Setup]
AppId={{1860D793-005D-43F0-B0B0-8AF37F0C9660}
AppName={#MyAppName}
; Fixed value, never bumped — releases are versionless (latest only).
AppVersion=1.0.0
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=dist
OutputBaseFilename=ManifestDownloader-Setup-win-x64
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
CloseApplicationsFilter=ManifestDownloader.exe
RestartApplications=no
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "dist\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall
