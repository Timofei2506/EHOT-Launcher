; ---------------------------------------------------------------------------
; EHOT Launcher — установщик для Windows (Inno Setup 6).
; Собирается автоматически в CI: см. .github/workflows/ci.yml, job "installers".
; Локально: iscc.exe packaging\windows\ehot-launcher.iss /DAppVersion=0.1.0
; Пути по умолчанию считаются от расположения этого файла, поэтому запускать можно из любой папки.
; ---------------------------------------------------------------------------

#define AppName        "EHOT Launcher"
#define AppPublisher   "EHOT"
#define AppExeName     "EHOT.Launcher.exe"
#ifndef AppVersion
  #define AppVersion   "0.1.0"
#endif
#ifndef SourceDir
  #define SourceDir    AddBackslash(SourcePath) + "..\..\artifacts\win-x64"
#endif
#ifndef OutputDir
  #define OutputDir    AddBackslash(SourcePath) + "..\..\artifacts\installers"
#endif

[Setup]
AppId={{7E2C1A64-9F6B-4B7E-9E1D-5C3F0A2B8D41}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\EHOT Launcher
DefaultGroupName={#AppName}
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}
DisableProgramGroupPage=yes
LicenseFile={#SourcePath}\..\..\LICENSE.txt
; Установка в Program Files требует прав администратора.
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=EHOT-Launcher-{#AppVersion}-win-x64-setup
SetupIconFile={#SourcePath}\..\..\src\EHOT.Launcher.UI\Assets\ehot.ico
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
; Идентификатор приложения для «Приложения и возможности» Windows
AppMutex=EHOT.Launcher.SingleInstance

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "quicklaunchicon"; Description: "{cm:CreateQuickLaunchIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked; OnlyBelowVersion: 6.1

[Files]
; Вся self-contained сборка (.NET внутри), кроме символов отладки.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb,*.xml"

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Registry]
; Ассоциация ссылок вида ehot://join/XXXX — для быстрого подключения к комнате Game Network.
Root: HKA; Subkey: "Software\Classes\ehot"; ValueType: string; ValueName: ""; ValueData: "URL:EHOT Launcher"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\ehot"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\ehot\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExeName},0"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\ehot\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExeName}"" ""%1"""; Flags: uninsdeletekey

[UninstallDelete]
; Кэш и логи лаунчера лежат в %APPDATA%; настройки пользователя не удаляем.
Type: filesandordirs; Name: "{app}\logs"
