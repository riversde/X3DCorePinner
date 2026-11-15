; =============================================================================
;  X3D Core Pinner - Inno Setup 6 script
;  Copyright (c) 2026 PR FRAG - MIT License
;
;  Built by build.ps1:  ISCC /DMyAppVersion=x.y.z /DSourceDir=<publish dir> /DOutputDir=<dist>
;  Manual:              "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\X3DCorePinner.iss
;
;  Security: installs to Program Files (admin-only), which is required for the
;  elevated "Start with Windows" logon task. portable.flag is NOT installed, so
;  settings live in %APPDATA% / %LOCALAPPDATA%\X3DCorePinner.
; =============================================================================

#ifndef MyAppVersion
  #define MyAppVersion "1.1.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\dist\X3DCorePinner"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

#define MyAppName      "X3D Core Pinner"
#define MyAppPublisher "PR FRAG"
#define MyAppURL       "https://github.com/riversde/X3DCorePinner"
#define MyAppExeName   "X3DCorePinner.exe"

[Setup]
; Never change AppId - it identifies the app for upgrades and uninstall.
AppId={{CAD44955-AF1F-43DF-ACBC-FEC0146AAD8D}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
AppCopyright=Copyright (c) 2026 {#MyAppPublisher}
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} Setup
VersionInfoProductName={#MyAppName}

DefaultDirName={autopf}\X3DCorePinner
DisableProgramGroupPage=yes
DisableDirPage=auto
UsePreviousAppDir=yes
UsePreviousTasks=yes
LicenseFile=..\LICENSE

PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0

OutputDir={#OutputDir}
OutputBaseFilename=X3DCorePinner-v{#MyAppVersion}-setup
SetupIconFile=..\src\X3DCorePinner\Assets\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes

; Restart Manager as a fallback; the app is first asked to exit cleanly (see [Code]).
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startup";     Description: "Start with Windows in the tray (recommended - protection is always on)"; GroupDescription: "Options:"
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Options:"; Flags: unchecked

[Files]
; Self-contained publish output. portable.flag and any portable Data folder are excluded.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "portable.flag,Data\*"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Comment: "Keep the X3D V-Cache for your game"
Name: "{autodesktop}\{#MyAppName}";  Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; Start with Windows on/off according to the task checkbox (also applied on upgrades).
Filename: "{app}\{#MyAppExeName}"; Parameters: "--enable-startup";  Flags: runhidden waituntilterminated; Tasks: startup;     StatusMsg: "Enabling Start with Windows..."
Filename: "{app}\{#MyAppExeName}"; Parameters: "--disable-startup"; Flags: runhidden waituntilterminated; Tasks: not startup; StatusMsg: "Updating startup settings..."
; The app requires admin (affinity of other processes). postinstall entries default to the
; non-elevated original user -> CreateProcess error 740. runascurrentuser reuses the
; installer's elevated token, so there is no second UAC prompt.
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent runascurrentuser

[UninstallRun]
; Exit the running app (restores pinned apps), remove the logon task, restore all apps,
; delete the admin-only %ProgramData%\X3DCorePinner folder.
Filename: "{app}\{#MyAppExeName}"; Parameters: "--cleanup"; Flags: runhidden waituntilterminated; RunOnceId: "X3DCleanup"

[UninstallDelete]
Type: filesandordirs; Name: "{commonappdata}\X3DCorePinner"

[Code]
// Ask a running copy to exit cleanly before files are replaced (upgrade / reinstall).
// The app restores every pinned process on exit. Older or missing exe -> nothing to do.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Exe: String;
  ResultCode: Integer;
begin
  Result := '';
  Exe := AddBackslash(WizardDirValue) + '{#MyAppExeName}';
  if FileExists(Exe) then
  begin
    Log('Asking running X3D Core Pinner to exit: ' + Exe + ' --shutdown');
    if not Exec(Exe, '--shutdown', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
      Log('Could not run --shutdown (continuing, Restart Manager will close the app).')
    else
      Log('--shutdown exit code: ' + IntToStr(ResultCode));
  end;
end;

// Offer to remove personal settings and logs after uninstall (skipped when silent).
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usPostUninstall) and (not UninstallSilent) then
  begin
    if MsgBox('Also delete your X3D Core Pinner settings, logs and reports?' + #13#10 + #13#10 +
              'Choose No to keep them for a future reinstall.',
              mbConfirmation, MB_YESNO) = IDYES then
    begin
      DelTree(ExpandConstant('{userappdata}\X3DCorePinner'), True, True, True);
      DelTree(ExpandConstant('{localappdata}\X3DCorePinner'), True, True, True);
    end;
  end;
end;
