#ifndef AppVersion
  #error AppVersion must be supplied
#endif
#ifndef FilesInclude
  #error FilesInclude must be supplied
#endif
#ifndef OutputDirectory
  #error OutputDirectory must be supplied
#endif
#if Ver < EncodeVer(6, 7, 1)
  #error Inno Setup 6.7.1 or newer is required for the modern Windows folder picker
#endif

[Setup]
AppId={{AFBE1865-4656-49BA-9B10-BCFD5767B137}
AppName=飞驴 / Feilv
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
AppendDefaultDirName=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutputDirectory}
OutputBaseFilename=feilv-v{#AppVersion}-win-x64-setup
SetupIconFile=..\..\src\LanChat.Desktop\Assets\lanchat.ico
UninstallDisplayIcon={app}\飞驴.exe
UninstallDisplayName=飞驴 / Feilv
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
WizardSizePercent=110
SetupLogging=yes
AppMutex=Local\Feilv.Application
CloseApplications=no
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimplified"; MessagesFile: "Languages\ChineseSimplified.isl"

[Messages]
chinesesimplified.WelcomeLabel2=这将在您的电脑上安装飞驴 {#AppVersion}。%n%n飞驴用于局域网文件传输、文件夹共享和二维码分享。%n%n继续前，请先从系统托盘退出正在运行的旧版飞驴。
chinesesimplified.SelectDirDesc=选择飞驴的安装位置。
chinesesimplified.SelectDirLabel3=程序和本地数据保存在此目录。配置、历史记录及收发文件在升级和卸载时均保留。
chinesesimplified.SelectDirBrowseLabel=可直接输入完整路径，或点击“选择目录”浏览、新建文件夹。
chinesesimplified.FinishedLabelNoIcons=飞驴已经安装完成。点击“完成”退出安装向导。
english.WelcomeLabel2=This will install Feilv {#AppVersion} on your computer.%n%nTransfer files over your LAN, share folders, and exchange files with your phone using a QR code.%n%nBefore continuing, quit any running copy of Feilv from the system tray.
english.SelectDirDesc=Choose where to install Feilv.
english.SelectDirLabel3=The application and local data are stored here. Settings, history and transferred files are kept during updates and uninstallation.
english.SelectDirBrowseLabel=Enter a full path, or click Browse to choose or create a folder.
english.FinishedLabelNoIcons=Feilv has been installed. Click Finish to close Setup.

[CustomMessages]
chinesesimplified.DesktopShortcut=创建桌面快捷方式
english.DesktopShortcut=Create a desktop shortcut
chinesesimplified.Shortcuts=快捷方式：
english.Shortcuts=Shortcuts:
chinesesimplified.LaunchApp=启动飞驴
english.LaunchApp=Launch Feilv
chinesesimplified.ChooseDirectory=选择飞驴安装目录
english.ChooseDirectory=Choose the Feilv installation folder
chinesesimplified.BrowseDirectory=选择目录…
english.BrowseDirectory=Browse…

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopShortcut}"; GroupDescription: "{cm:Shortcuts}"; Flags: unchecked

[Files]
#include FilesInclude

[Icons]
Name: "{group}\飞驴"; Filename: "{app}\飞驴.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\飞驴"; Filename: "{app}\飞驴.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\飞驴.exe"; Description: "{cm:LaunchApp}"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

[Code]
function NeedsFileUpdate(RelativePath, ExpectedHash: String): Boolean;
var
  Target: String;
begin
  Result := True;
  Target := AddBackslash(ExpandConstant('{app}')) + RelativePath;
  if FileExists(Target) then
    try
      if CompareText(GetSHA256OfFile(Target), ExpectedHash) = 0 then
      begin
        Log('Reusing unchanged file: ' + RelativePath);
        Result := False;
      end;
    except
      Log('Cannot verify existing file, replacing: ' + RelativePath);
    end;
end;

procedure SelectInstallDirectory(Sender: TObject);
var
  Directory, ParentDirectory: String;
begin
  Directory := Trim(WizardForm.DirEdit.Text);
  while not DirExists(Directory) do
  begin
    ParentDirectory := ExtractFileDir(RemoveBackslashUnlessRoot(Directory));
    if (ParentDirectory = '') or (CompareText(ParentDirectory, Directory) = 0) then
    begin
      Directory := ExpandConstant('{localappdata}');
      Break;
    end;
    Directory := ParentDirectory;
  end;
  { The script BrowseForFolder uses IFileDialog; True enables New Folder. }
  if BrowseForFolder(CustomMessage('ChooseDirectory'), Directory, True) then
    WizardForm.DirEdit.Text := Directory;
end;

procedure InitializeWizard();
begin
  WizardForm.DirBrowseButton.Caption := CustomMessage('BrowseDirectory');
  WizardForm.DirBrowseButton.OnClick := @SelectInstallDirectory;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Command: String;
begin
  if CurUninstallStep = usUninstall then
    if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'FeiLv', Command) then
      if CompareText(Command, '"' + ExpandConstant('{app}\飞驴.exe') + '"') = 0 then
        RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'FeiLv');
end;
