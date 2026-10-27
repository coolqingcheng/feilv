#ifndef AppVersion
  #error AppVersion must be supplied
#endif
#ifndef SourceDirectory
  #error SourceDirectory must be supplied
#endif
#ifndef OutputDirectory
  #error OutputDirectory must be supplied
#endif

[Setup]
AppId={{AFBE1865-4656-49BA-9B10-BCFD5767B137}
AppName=飞驴
AppVersion={#AppVersion}
AppPublisher=飞驴
AppPublisherURL=https://github.com/coolqingcheng/feilv
AppSupportURL=https://github.com/coolqingcheng/feilv/issues
AppUpdatesURL=https://github.com/coolqingcheng/feilv/releases
DefaultDirName={localappdata}\Programs\Feilv
DefaultGroupName=飞驴
DisableProgramGroupPage=yes
DisableWelcomePage=no
DisableDirPage=no
UsePreviousAppDir=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutputDirectory}
OutputBaseFilename=feilv-v{#AppVersion}-win-x64-setup
SetupIconFile=..\..\src\LanChat.Desktop\Assets\lanchat.ico
UninstallDisplayIcon={app}\飞驴.exe
UninstallDisplayName=飞驴
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
WizardSizePercent=110
SetupLogging=yes
AppMutex=Local\Feilv.Application
CloseApplications=no
RestartApplications=no

[Languages]
Name: "chinesesimplified"; MessagesFile: "Languages\ChineseSimplified.isl"

[Messages]
WelcomeLabel2=这将在您的电脑上安装飞驴 {#AppVersion}。%n%n飞驴用于局域网文件传输、文件夹共享和二维码分享。%n%n继续前，请先从系统托盘退出正在运行的旧版飞驴。
SelectDirDesc=选择飞驴的安装位置。
SelectDirLabel3=程序和本地数据保存在此目录。配置、历史记录及收发文件在升级和卸载时均保留。
FinishedLabelNoIcons=飞驴已经安装完成。点击“完成”退出安装向导。

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："; Flags: unchecked

[Files]
Source: "{#SourceDirectory}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\飞驴"; Filename: "{app}\飞驴.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\飞驴"; Filename: "{app}\飞驴.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\飞驴.exe"; Description: "启动飞驴"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Command: String;
begin
  if CurUninstallStep = usUninstall then
    if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'FeiLv', Command) then
      if CompareText(Command, '"' + ExpandConstant('{app}\飞驴.exe') + '"') = 0 then
        RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'FeiLv');
end;
