; Identity Vision Bridge (IDVB) b01.6 installer.
; This script is compiled by Build-Release.ps1 with the three required defines.

#ifndef BuildOutput
  #error BuildOutput must point to the verified dotnet publish directory.
#endif
#ifndef ReleaseOutput
  #error ReleaseOutput must point to artifacts/release.
#endif
#ifndef PublicVersion
  #define PublicVersion "b01.6-00.00.00.0000"
#endif
#ifndef NumericVersion
  #define NumericVersion "1.6.1.0"
#endif

#define AppName "Identity Vision Bridge"
#define AppShortName "IDVB"
#define AppPublisher "Identity Vision Bridge"
#define AppExeName "IDVB.exe"
#define AppId "IDVB"

[Setup]
AppId={#AppId}
AppName={#AppName}
AppVersion={#PublicVersion}
AppVerName={#AppName} {#PublicVersion}
AppPublisher={#AppPublisher}
DefaultDirName={localappdata}\Programs\{#AppShortName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
AllowNoIcons=yes
OutputDir={#ReleaseOutput}
OutputBaseFilename={#AppShortName}-Setup-{#PublicVersion}-x64
SetupIconFile=..\Assets\Icons\IDVB_icon_multisize.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName} {#PublicVersion}
UninstallDisplaySize=500000000
Uninstallable=yes
VersionInfoVersion={#NumericVersion}
VersionInfoProductVersion={#NumericVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppName} 安装程序
VersionInfoProductName={#AppName}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
PrivilegesRequired=lowest
CloseApplications=yes
RestartApplications=no
ChangesAssociations=no
DisableWelcomePage=no
DisableReadyMemo=no
DisableReadyPage=no
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
SetupLogging=yes
UninstallLogging=yes
LicenseFile=LICENSE.txt
InfoBeforeFile=README-安装前须知.txt
InfoAfterFile=README-安装完成后须知.txt

[Languages]
Name: "chinesesimplified"; MessagesFile: "languages\ChineseSimplified.isl"

[Types]
Name: "full"; Description: "完整安装"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："; Flags: checkedonce
Name: "taskbarhelp"; Description: "安装完成后显示固定到任务栏的操作提示"; GroupDescription: "附加任务："; Flags: unchecked

[Files]
Source: "{#BuildOutput}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb,*.xml"
Source: "dependencies\MicrosoftEdgeWebView2RuntimeInstallerX64.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall ignoreversion
Source: "dependencies\vc_redist.x64.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall ignoreversion
Source: "LICENSE.txt"; DestDir: "{app}"; DestName: "LICENSE-IDVB.txt"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\{#AppExeName}"
Name: "{group}\卸载 {#AppName}"; Filename: "{uninstallexe}"; IconFilename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{tmp}\MicrosoftEdgeWebView2RuntimeInstallerX64.exe"; Parameters: "/silent /install"; StatusMsg: "正在安装网页组件（Microsoft Edge WebView2 Runtime）..."; Flags: waituntilterminated runhidden; Check: NeedsWebView2
Filename: "{tmp}\vc_redist.x64.exe"; Parameters: "/install /quiet /norestart"; StatusMsg: "正在安装 Microsoft Visual C++ 运行库..."; Flags: waituntilterminated runhidden; Check: NeedsVCRuntime
Filename: "{app}\{#AppExeName}"; Description: "立即启动 {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
(* Legacy opt-in deletion UI retained as documentation only. IDVB user data is never
   deleted by the uninstaller; users may remove %LocalAppData%\IDVB manually. *)
(*****
var
  KeepPersonalData: Boolean;

function ShouldRemovePersonalData(): Boolean;
begin
  Result := not KeepPersonalData;
end;

function InitializeUninstall(): Boolean;
begin
  Result := True;
  KeepPersonalData := True;

  if UninstallSilent then
    exit;

  if MsgBox(
      '是否保留 Identity Vision Bridge 的本地数据和设置？' + #13#10 + #13#10 +
      '选择“是”（推荐）将保留 %LocalAppData%\\IDVB，方便以后重新安装时继续使用。' + #13#10 +
      '选择“否”将进入永久删除确认。',
      mbConfirmation,
      MB_YESNO or MB_DEFBUTTON1) = IDNO then
  begin
    if MsgBox(
        '此操作会永久删除 %LocalAppData%\\IDVB 中的本地数据和设置，且无法恢复。是否继续？',
        mbConfirmation,
        MB_YESNO or MB_DEFBUTTON2) = IDYES then
      KeepPersonalData := False;
  end;
end;

*****)

function NeedsWebView2: Boolean;
begin
  Result := not DirExists(ExpandConstant('{commonpf32}\Microsoft\EdgeWebView\Application')) and
            not DirExists(ExpandConstant('{localappdata}\Microsoft\EdgeWebView\Application'));
end;

function NeedsVCRuntime: Boolean;
var
  Installed: Cardinal;
begin
  Result := True;
  if RegQueryDWordValue(HKLM64, 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64', 'Installed', Installed) then
    Result := Installed <> 1;
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
  if not IsWin64 then begin
    MsgBox('IDVB {#PublicVersion} only supports 64-bit Windows.', mbError, MB_OK);
    (*
    MsgBox('IDVB b01 仅支持 64 位 Windows。', mbError, MB_OK);
    *)
    Result := False;
  end;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID = wpFinished) and WizardIsTaskSelected('taskbarhelp') then
    WizardForm.FinishedLabel.Caption :=
      'Identity Vision Bridge 已安装完成。' + #13#10 + #13#10 +
      '如需固定到任务栏，请先启动 IDVB，再在任务栏中的 IDVB 图标上右键，选择“固定到任务栏”。';
end;
