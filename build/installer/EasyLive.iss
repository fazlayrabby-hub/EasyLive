; EasyLive installer (Inno Setup 6). Built by build\make-installer.ps1.
; Per-user install: no administrator rights, no UAC prompt.
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\..\artifacts\EasyLive-" + AppVersion
#endif

[Setup]
; Fixed AppId: lets a newer Setup upgrade an existing install in place. Never change it.
AppId={{6B0F7C2E-4D3A-4E8B-9C51-3A7E1F5D2B94}
AppName=EasyLive
AppVersion={#AppVersion}
AppVerName=EasyLive {#AppVersion}
AppPublisher=Sohan Roni
VersionInfoVersion={#AppVersion}
VersionInfoCompany=Sohan Roni
VersionInfoDescription=EasyLive Setup
VersionInfoProductName=EasyLive
DefaultDirName={autopf}\EasyLive
DefaultGroupName=EasyLive
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
SetupIconFile=..\..\assets\EasyLive.ico
UninstallDisplayIcon={app}\EasyLive.exe
UninstallDisplayName=EasyLive
InfoBeforeFile=..\..\THIRD-PARTY-NOTICES.txt
OutputDir=..\..\artifacts
OutputBaseFilename=EasyLive-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
CloseApplicationsFilter=EasyLive.exe
UsedUserAreasWarning=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a shortcut on the &desktop"; GroupDescription: "Shortcuts:"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "Read me first.txt"; DestDir: "{app}"; Flags: ignoreversion isreadme

[Icons]
Name: "{autoprograms}\EasyLive"; Filename: "{app}\EasyLive.exe"; IconFilename: "{app}\EasyLive.exe"
Name: "{autoprograms}\EasyLive Read me first"; Filename: "{app}\Read me first.txt"
Name: "{autodesktop}\EasyLive"; Filename: "{app}\EasyLive.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\EasyLive.exe"; Description: "Launch EasyLive"; Flags: nowait postinstall skipifsilent

[Code]
// Saved stream keys and logs live in %LOCALAPPDATA%\EasyLive. They are kept on uninstall unless the user says otherwise
// (or passes /REMOVEDATA=1 to a silent uninstall).
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
  Remove: Boolean;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{localappdata}\EasyLive');
    if not DirExists(DataDir) then Exit;
    Remove := ExpandConstant('{param:REMOVEDATA|0}') = '1';
    if not Remove then
      Remove := SuppressibleMsgBox(
        'Do you also want to remove your saved settings (your saved stream keys)?' + #13#10 + #13#10 +
        'Choose No to keep them in case you install EasyLive again.',
        mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES;
    if Remove then DelTree(DataDir, True, True, True);
  end;
end;


