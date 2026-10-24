; Hentai Reader installer (specs/004-windows-installer). Compiled by windows/build/Build-Installer.ps1:
;   ISCC HentaiReader.iss /DAppVersion=<x.y.z> /DSourceDir=<verified publish folder> /O<output>
; Never compile it by hand from a Debug build: only the verified publish folder may be packaged.

#ifndef AppVersion
  #error AppVersion is not defined (pass /DAppVersion=x.y.z)
#endif
#ifndef SourceDir
  #error SourceDir is not defined (pass /DSourceDir=<publish folder>)
#endif

#define AppName "Hentai Reader"
#define AppExe "HentaiReader.exe"
; The app's data folder (preferences, source data, encrypted sign-ins). The installer never writes it.
#define DataFolderName "HentaiReader"

[Setup]
; AppId identifies the installation for upgrades and uninstall. It must never change.
AppId={{6E0B7C2A-4F1D-4C8B-9A35-2D7E91B4F6C3}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=kolod
AppPublisherURL=https://github.com/kolod/hentai-reader
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}
SetupIconFile=..\src\HentaiReader.App\Assets\AppIcon.ico

; Scope: current user by default (no elevation) or all users; upgrades keep the previous scope (FR-002).
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline
UsePreviousPrivileges=yes

; {autopf} is %LOCALAPPDATA%\Programs for the current user and Program Files for all users. The program
; folder is never the data folder %LOCALAPPDATA%\HentaiReader (FR-016).
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
; The welcome page carries the adult content notice (FR-024); Inno hides it by default.
DisableWelcomePage=no

; Install and uninstall wait until the app is closed (FR-019); the app holds these mutexes.
AppMutex=HentaiReader.Running,Global\HentaiReader.Running

; Windows 10 1903 or later, x64; x64 also runs emulated on ARM64 Windows 11 (FR-021).
MinVersion=10.0.18362
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; Languages: follow the Windows display language and let the user change it (FR-025).
ShowLanguageDialog=yes
LanguageDetectionMethod=uilanguage
WizardStyle=modern

OutputBaseFilename=HentaiReader-Setup-{#AppVersion}-x64
OutputManifestFile=manifest-x64.txt
Compression=lzma2/ultra64
SolidCompression=yes

; Optional code signing (FR-014): Build-Installer.ps1 passes /DSign and /Ssign=<command> when
; HENTAIREADER_SIGN_COMMAND is set.
#ifdef Sign
SignTool=sign
SignedUninstaller=yes
#endif

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl,Messages\en.isl"
Name: "de"; MessagesFile: "compiler:Languages\German.isl,Messages\de.isl"
Name: "fr"; MessagesFile: "compiler:Languages\French.isl,Messages\fr.isl"
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl,Messages\es.isl"
Name: "ja"; MessagesFile: "compiler:Languages\Japanese.isl,Messages\ja.isl"
Name: "uk"; MessagesFile: "compiler:Languages\Ukrainian.isl,Messages\uk.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIcon}"; Flags: unchecked

[InstallDelete]
; The app loads every Sources\<id>\<id>.js. The folder belongs to the installer and is rewritten on each
; install, so a source removed in a newer version cannot keep running from a leftover file (research R5).
Type: filesandordirs; Name: "{app}\Sources"

[Files]
; The whole verified publish folder, nothing else and nothing excluded (research R3).
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; Comment: "{cm:AppDescription}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Comment: "{cm:AppDescription}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent

[Code]
const
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{6E0B7C2A-4F1D-4C8B-9A35-2D7E91B4F6C3}_is1';

{ Version of an existing installation in either scope, or '' when not installed. }
function InstalledVersion(): String;
begin
  if not RegQueryStringValue(HKCU, UninstallKey, 'DisplayVersion', Result) then
    if not RegQueryStringValue(HKLM64, UninstallKey, 'DisplayVersion', Result) then
      if not RegQueryStringValue(HKLM32, UninstallKey, 'DisplayVersion', Result) then
        Result := '';
end;

{ True when Installed is newer than This; versions that cannot be parsed never block. }
function IsNewer(Installed, This: String): Boolean;
var
  InstalledPacked, ThisPacked: Int64;
begin
  Result := StrToVersion(Installed, InstalledPacked) and StrToVersion(This, ThisPacked)
    and (ComparePackedVersion(InstalledPacked, ThisPacked) > 0);
end;

function InitializeSetup(): Boolean;
var
  Installed: String;
begin
  Result := True;
  { No silent downgrade (FR-020); the same version repairs, a newer one upgrades. }
  Installed := InstalledVersion();
  if IsNewer(Installed, '{#AppVersion}') then
  begin
    SuppressibleMsgBox(FmtMessage(CustomMessage('NewerVersionInstalled'), [Installed, '{#AppVersion}']), mbError, MB_OK, IDOK);
    Result := False;
  end;
end;

{ The adult content notice is part of the welcome page, before anything else (FR-024). }
procedure InitializeWizard();
begin
  WizardForm.WelcomeLabel2.Caption := WizardForm.WelcomeLabel2.Caption + #13#10#13#10 + CustomMessage('AdultNotice');
end;

{ Uninstall removes only installed files (no [UninstallDelete] for data, FR-018). Tell interactive users
  where their data stays and how to remove it. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if (CurUninstallStep = usPostUninstall) and not UninstallSilent then
  begin
    DataDir := ExpandConstant('{localappdata}\{#DataFolderName}');
    if DirExists(DataDir) then
      MsgBox(FmtMessage(CustomMessage('DataKeptNotice'), [DataDir]), mbInformation, MB_OK);
  end;
end;
