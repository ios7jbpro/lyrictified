#define AppName GetEnv("LYRICTIFIED_APP_NAME")
#define AppVersion GetEnv("LYRICTIFIED_APP_VERSION")
#define AppPublisher GetEnv("LYRICTIFIED_APP_PUBLISHER")
#define SourceDir GetEnv("LYRICTIFIED_SOURCE_DIR")
#define OutputDir GetEnv("LYRICTIFIED_OUTPUT_DIR")
#define Configuration LowerCase(GetEnv("LYRICTIFIED_CONFIGURATION"))

#if AppName == ""
  #define AppName "Lyrictified"
#endif

#if AppVersion == ""
  #define AppVersion "1.0.0"
#endif

#if AppPublisher == ""
  #define AppPublisher "Lyrictified"
#endif

[Setup]
AppId={{6D7809E4-6364-4EF0-AE90-6D2463C59226}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename=Lyrictified-{#Configuration}-setup
SetupIconFile=..\Assets\app.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\Lyrictified.exe

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\Lyrictified.exe"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\Lyrictified.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Lyrictified.exe"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
procedure KillRunningApp();
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM "Lyrictified.exe" /F /T', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function InitializeSetup(): Boolean;
begin
  KillRunningApp();
  Result := True;
end;

function InitializeUninstall(): Boolean;
begin
  KillRunningApp();
  Result := True;
end;
