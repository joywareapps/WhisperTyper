; ── WhisperTyper Inno Setup Installer Script ──────────────────────────
; Build:  iscc WhisperTyper.iss
; Expects publish output in ..\publish\  (or override with /DSourceDir=...)

#ifndef SourceDir
  #define SourceDir "..\publish"
#endif

#define AppName       "WhisperTyper"
#define AppPublisher  "Joyware Apps"
#define AppURL        "https://github.com/joywareapps/WhisperTyper"
#define AppExeName    "WhisperTyper.exe"

; Read version from the assembly — fallback to 1.0.0
#if FileExists(SourceDir + "\WhisperTyper.exe")
  #define _undef
#else
  #define _undef
#endif

[Setup]
AppId={{B7E3F2A1-4D5C-4B8E-9F1A-3C2D5E6F7A89}
AppName={#AppName}
AppVersion=1.0.0
AppVerName={#AppName} 1.0.0
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}
DefaultDirName={localappdata}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputBaseFilename=WhisperTyper-setup
SetupIconFile=..\App.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayName={#AppName}
CloseApplications=force

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional icons:"
Name: "startup";    Description: "Start with &Windows";          GroupDescription: "Options:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}";            Filename: "{app}\{#AppExeName}"
Name: "{group}\&Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}";      Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Registry]
; "Start with Windows" via HKCU Run key (user-level, no admin needed)
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
    ValueType: string; ValueName: "{#AppName}"; ValueData: """{app}\{#AppExeName}"""; \
    Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\{#AppExeName}"; Description: "&Launch {#AppName}"; Flags: nowait postinstall skipifsilent