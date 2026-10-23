; Advanced Weighbridge – Windows installer (Inno Setup 6). Produces AdvancedWeighbridgeSetup.exe.
; Build: installer\build.ps1 (publishes the self-contained application, then runs ISCC).
;   ISCC /DAppVersion=1.0.0 /DSourceDir=..\out\desktop AdvancedWeighbridge.iss
;
; What it does: installs the application (self-contained .NET, no separate runtime), creates the shared data folder
; C:\ProgramData\AdvancedWeighbridge with modify rights for users, prepares the database, and writes the serial ports it
; finds to the install log. It installs no drivers and no other software; USB-serial adapter drivers come from the
; adapter manufacturer. Upgrades keep all data (the application backs up the database before any schema change).
; Uninstalling keeps the data unless the user explicitly chooses to delete it.

#define AppName "Advanced Weighbridge"
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\out\desktop"
#endif
#ifndef OutputDir
  #define OutputDir "..\out\installer"
#endif
#define AppExe "AdvancedWeighbridge.exe"
#define DataDir "{commonappdata}\AdvancedWeighbridge"

[Setup]
AppId={{6B0E9B3A-4C41-4E0B-9C83-2F5D7B1A9E10}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Advanced Weighbridge
VersionInfoVersion={#AppVersion}
VersionInfoDescription=Advanced Weighbridge Management System setup
DefaultDirName={autopf}\Advanced Weighbridge
DefaultGroupName=Advanced Weighbridge
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename=AdvancedWeighbridgeSetup
SetupIconFile=..\src\Weighbridge.Desktop\Resources\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Windows 10 version 1809 or later
MinVersion=10.0.17763
PrivilegesRequired=admin
SetupLogging=yes
; Detects a running copy (the application holds this mutex) and asks to close it before files are replaced.
AppMutex=Global\AdvancedWeighbridge.Desktop
CloseApplications=yes
RestartApplications=no
InfoAfterFile=after-install.txt
#ifdef SignToolName
SignTool={#SignToolName}
SignedUninstaller=yes
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"
Name: "autostart"; Description: "Start Advanced Weighbridge when Windows starts (recommended for automatic weighing)"; GroupDescription: "Start-up:"; Flags: unchecked

[Dirs]
Name: "{#DataDir}"; Permissions: users-modify
Name: "{#DataDir}\logs"; Permissions: users-modify
Name: "{app}\plugins"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\profiles\*.wbprofile.json"; DestDir: "{app}\profiles"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\samples\*.wbcap"; DestDir: "{app}\samples"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\docs\*.md"; DestDir: "{app}\docs"; Flags: ignoreversion skipifsourcedoesntexist
Source: "plugins-readme.txt"; DestDir: "{app}\plugins"; DestName: "README.txt"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Advanced Weighbridge"; Filename: "{app}\{#AppExe}"
Name: "{autoprograms}\Advanced Weighbridge data folder"; Filename: "{#DataDir}"
Name: "{autoprograms}\Advanced Weighbridge manuals"; Filename: "{app}\docs"
Name: "{autodesktop}\Advanced Weighbridge"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon
Name: "{commonstartup}\Advanced Weighbridge"; Filename: "{app}\{#AppExe}"; Tasks: autostart

[Run]
Filename: "{app}\{#AppExe}"; Parameters: "--init"; StatusMsg: "Preparing the database..."; Flags: runhidden waituntilterminated
Filename: "{app}\{#AppExe}"; Parameters: "--list-ports --out ""{#DataDir}\logs\install-ports.txt"""; StatusMsg: "Looking for serial ports..."; Flags: runhidden waituntilterminated
Filename: "{app}\{#AppExe}"; Description: "Start Advanced Weighbridge now"; Flags: postinstall nowait skipifsilent

[Code]
var
  DeleteData: Boolean;

function InitializeUninstall(): Boolean;
begin
  DeleteData := False;
  if UninstallSilent() then
    { Silent uninstall never deletes data unless /DELETEDATA=1 is given explicitly. }
    DeleteData := ExpandConstant('{param:DELETEDATA|0}') = '1'
  else if MsgBox('Do you also want to delete ALL weighbridge data on this PC?' + #13#10#13#10 +
                 'This includes every transaction, ticket, image, backup, audit record and log in' + #13#10 +
                 ExpandConstant('{commonappdata}\AdvancedWeighbridge') + #13#10#13#10 +
                 'Choose No (recommended) to keep the data for a later re-installation.',
                 mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
    DeleteData := MsgBox('The data cannot be recovered after deletion. Make sure you have a copy of the backups.' + #13#10#13#10 +
                         'Delete all weighbridge data permanently?', mbCriticalError, MB_YESNO or MB_DEFBUTTON2) = IDYES;
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usPostUninstall) and DeleteData then
    DelTree(ExpandConstant('{commonappdata}\AdvancedWeighbridge'), True, True, True);
end;
