#ifndef EmbeddedSetup
  #error EmbeddedSetup must point to the Velopack Setup.exe.
#endif
#ifndef ReleaseOutput
  #error ReleaseOutput must point to the channel output directory.
#endif
#ifndef PublicVersion
  #error PublicVersion is required.
#endif

#define AppName "Identity Vision Bridge"
#define AppId "IdentityVisionBridge.VelopackBootstrap"

[Setup]
AppId={#AppId}
AppName={#AppName}
AppVersion={#PublicVersion}
AppVerName={#AppName} {#PublicVersion}
AppPublisher={#AppName}
DefaultDirName={localappdata}\IdentityVisionBridge
DisableDirPage=no
DisableProgramGroupPage=yes
DisableReadyPage=no
CreateAppDir=yes
UsePreviousAppDir=no
PrivilegesRequired=lowest
Uninstallable=no
CreateUninstallRegKey=no
OutputDir={#ReleaseOutput}
OutputBaseFilename=IDVB-Setup-{#PublicVersion}-x64
SetupIconFile=..\Assets\Icons\IDVB_icon_multisize.ico
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes

[Languages]
Name: "chinesesimplified"; MessagesFile: "languages\ChineseSimplified.isl"

[Files]
Source: "{#EmbeddedSetup}"; DestDir: "{tmp}"; DestName: "IDVB-Velopack-Setup.exe"; Flags: deleteafterinstall ignoreversion

[Run]
Filename: "{tmp}\IDVB-Velopack-Setup.exe"; Parameters: "--silent --installto ""{app}"""; StatusMsg: "正在安装 {#AppName}…"; Flags: waituntilterminated
Filename: "{app}\IDVB.exe"; Description: "立即运行 {#AppName}"; Flags: nowait postinstall skipifsilent
