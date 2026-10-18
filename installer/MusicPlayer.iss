; MusicPlayer - Inno Setup installer definition
; Usage: scripts\build-installer.ps1
; 複製時は MyAppName / AppId / URL / アイコンパスを新しいアプリ名に変更すること。

#define MyAppName "MusicPlayer"
#define MyAppVersion "2.2.0"
#define MyAppPublisher "kazu-1234"
#define MyAppURL "https://github.com/kazu-1234/MusicPlayer_Windows"
#define MyAppExeName "MusicPlayer.exe"
#define PublishDir "..\dist\folder"

[Setup]
AppId={{8F3C2A71-4D6E-4B9A-A1C5-7E2D9B0F6A34}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE_NOTICE.txt
OutputDir=..\dist\installer
OutputBaseFilename=MusicPlayer-v{#MyAppVersion}-win-x64-setup
; SetupIconFile=..\Assets\AppIcon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
InfoAfterFile=
CloseApplications=force
RestartApplications=no
UsePreviousAppDir=yes
; 設定画面の「既定のアプリに設定」で書いた関連付けを、アンインストール時に消したことをエクスプローラーへ知らせる
ChangesAssociations=yes

[Languages]
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; App leftovers (not run on upgrade)
Type: filesandordirs; Name: "{app}"
; Settings, library cache and artwork cache (uninstall removes everything)
Type: filesandordirs; Name: "{userappdata}\{#MyAppName}"

[Code]
procedure TerminateApp;
var
  ResultCode: Integer;
  ExePath: String;
begin
  ExePath := ExpandConstant('{localappdata}\Programs\{#MyAppName}\{#MyAppExeName}');
  if FileExists(ExePath) then
    Exec(ExePath, '--exit', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#MyAppExeName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(800);
end;

function InitializeSetup(): Boolean;
begin
  TerminateApp;
  Result := True;
end;

function InitializeUninstall(): Boolean;
begin
  TerminateApp;
  Result := True;
end;

// On upgrade: clean {app} but keep %AppData% settings
procedure PreserveUserDataFile(const FileName: String);
var
  Src, DestDir, Dest: String;
begin
  Src := ExpandConstant('{app}\' + FileName);
  DestDir := ExpandConstant('{userappdata}\{#MyAppName}');
  Dest := DestDir + '\' + FileName;
  if FileExists(Src) then
  begin
    ForceDirectories(DestDir);
    if not FileExists(Dest) then
      CopyFile(Src, Dest, False);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
  begin
    if DirExists(ExpandConstant('{app}')) then
    begin
      PreserveUserDataFile('settings.json');
      DelTree(ExpandConstant('{app}'), True, True, True);
    end;
  end;
end;

// アンインストール時: アプリが設定画面から登録した関連付け（HKCU）を消す。値は FileAssociationService.cs と同じ
procedure RemoveOpenWith(const Ext: String);
begin
  RegDeleteValue(HKCU, 'Software\Classes\' + Ext + '\OpenWithProgids', '{#MyAppName}.AudioFile');
end;

procedure RemoveFileAssociation;
begin
  RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\{#MyAppName}.AudioFile');
  RegDeleteKeyIncludingSubkeys(HKCU, 'Software\{#MyAppName}');
  RegDeleteValue(HKCU, 'Software\RegisteredApplications', '{#MyAppName}');
  // 各拡張子の「プログラムから開く」候補からも外す（AppConfig.AudioExtensions と同じ並び）
  RemoveOpenWith('.flac');
  RemoveOpenWith('.mp3');
  RemoveOpenWith('.wav');
  RemoveOpenWith('.m4a');
  RemoveOpenWith('.aac');
  RemoveOpenWith('.ogg');
  RemoveOpenWith('.opus');
  RemoveOpenWith('.wma');
  RemoveOpenWith('.aiff');
  RemoveOpenWith('.aif');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    RemoveFileAssociation;
end;
