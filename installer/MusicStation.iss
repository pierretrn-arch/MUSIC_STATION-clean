; Music Station setup (Inno Setup 6).
; Built by .github/workflows/release.yml, which first publishes the app self-contained into
; ..\publish and passes the version:  ISCC /DAppVersion=0.1.0 installer\MusicStation.iss
; The setup .exe lands in ..\dist.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#define AppName "Music Station"
#define AppExe "MusicStation.exe"

[Setup]
; Keep this AppId the same for every release, so new versions upgrade the old install.
AppId={{188D65F5-2910-4314-B4B5-1DD52BE228B2}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Caramasta
AppPublisherURL=https://github.com/pierretrn-arch/MUSIC_STATION-clean
AppSupportURL=https://github.com/pierretrn-arch/MUSIC_STATION-clean/issues
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#AppExe}
OutputDir=..\dist
OutputBaseFilename=MusicStation-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; 64-bit Windows 10 or later only (the app and its VST3 host are x64).
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
; Installs for all users by default; the wizard lets people choose "only for me" (no admin rights).
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
