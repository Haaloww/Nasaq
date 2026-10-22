; ============================================================================
;  Nasaq - Inno Setup 6 installer script
;
;  Build it with:  build\build.cmd   (recommended, publishes first)
;  Or in the Inno Setup IDE: open this file and press F9.
;
;  The [Files] section expects ..\build\publish to exist. build.ps1 creates it.
; ============================================================================

#define MyAppName        "Nasaq"
#define MyAppPublisher   "Abdullah Abdulmanea"
#define MyAppExeName     "Nasaq.exe"
#define MyAppTagline     "Fast, quiet file conversion for Windows."

#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif

#define PublishDir AddBackslash(SourcePath) + "publish"

#if !FileExists(PublishDir + "\" + MyAppExeName)
  #error "The published app is missing. Run build\build.cmd before compiling this script."
#endif

#define MyAppVersion4 GetVersionNumbersString(PublishDir + "\" + MyAppExeName)

[Setup]
; A stable AppId lets a newer installer upgrade an older install and lets
; Windows list the app once in Settings > Apps. Do not change it.
AppId={{7E2B9C4D-3A61-4F58-9B0E-2D6C8A1F5E73}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppCopyright=Copyright (C) 2026 {#MyAppPublisher}
VersionInfoVersion={#MyAppVersion4}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} Setup
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}
AppComments={#MyAppTagline}

; Per-user install by default: no UAC prompt, no admin password.
; {autopf} becomes %LocalAppData%\Programs for a per-user install.
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

; Output
OutputDir=..\dist
OutputBaseFilename={#MyAppName}-Setup-{#MyAppVersion}
SetupIconFile=..\Assets\nasaq.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
DisableWelcomePage=no
ShowLanguageDialog=no

; Platform
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763

; Let Setup close a running copy of Nasaq before replacing files.
; AppMutex matches Local\Nasaq.SingleInstance created by App.xaml.cs.
CloseApplications=yes
RestartApplications=no
AppMutex=Nasaq.SingleInstance

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; The whole self-contained publish output, including tools\ffmpeg.exe when bundled.
Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Comment: "{#MyAppTagline}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent

[Registry]
; Nasaq can add itself to HKCU\...\Run when "Launch at sign-in" is enabled in
; Settings. Remove that entry on uninstall so Windows does not try to start a
; deleted executable. ValueType=none means setup never creates the value.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "Nasaq"; Flags: uninsdeletevalue

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    DataDir := ExpandConstant('{userappdata}\Nasaq');
    if DirExists(DataDir) then
    begin
      { A silent uninstall must never block, so keep the data in that case. }
      if (not UninstallSilent) and
         (MsgBox('Do you also want to remove your Nasaq settings, logs and downloaded tools?' + #13#10 + #13#10 +
                 'Choose No to keep them for a future reinstall.',
                 mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES) then
      begin
        DelTree(DataDir, True, True, True);
      end;
    end;
  end;
end;
