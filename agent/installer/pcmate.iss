; PC MATE — установщик агента (Inno Setup 6.1+)
;
; Собирается скриптом agent/build.ps1 после dotnet publish:
;   iscc /DSourceDir="..\publish" /DAppVersion="1.0.0" pcmate.iss
;
; Программа распространяется через GitHub Releases, поэтому установщик
; сам разбирается с зависимостями: проверяет .NET 8 Desktop Runtime и, если
; его нет, скачивает с сайта Microsoft. Пользователю не нужно ничего искать.

#define AppName "PC MATE"
#define AppPublisher "PC MATE"
#define AppUrl "https://github.com/marakaybo/pc-mate"
#define ServiceName "PcMateAgent"
#define ServiceExe "PcMate.Agent.Service.exe"
#define TrayExe "PcMate.Tray.exe"
#define LanPort "8760"

; Ссылка Microsoft всегда указывает на свежий патч .NET 8 Desktop Runtime.
#define DotNetUrl "https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe"

#ifndef SourceDir
  #define SourceDir "..\publish"
#endif

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

; Самодостаточная сборка тащит рантайм с собой — проверка не нужна.
#ifndef SelfContained
  #define SelfContained "0"
#endif

[Setup]
AppId={{8F3C5A62-5B21-4E0C-9E1B-4D7A1C0E2F31}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#AppVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription=Удалённое управление компьютером с телефона
VersionInfoProductName={#AppName}

DefaultDirName={autopf}\PC MATE
DefaultGroupName=PC MATE
DisableProgramGroupPage=yes
DisableDirPage=auto
OutputDir=..\dist
OutputBaseFilename=PcMate-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
LZMANumBlockThreads=4

WizardStyle=modern
WizardImageFile=wizard-image.bmp,wizard-image@2x.bmp
WizardSmallImageFile=wizard-small.bmp,wizard-small@2x.bmp
WizardImageStretch=yes
SetupIconFile=..\src\PcMate.Agent.Tray\pcmate.ico
UninstallDisplayIcon={app}\{#TrayExe}
UninstallDisplayName={#AppName}

PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
MinVersion=10.0.17763
CloseApplications=yes
RestartApplications=no
AllowNoIcons=yes

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
russian.ServiceInstalling=Устанавливаем службу PC MATE…
russian.FirewallRule=Добавляем правило брандмауэра для домашней сети…
russian.PowerPrep=Готовим питание: гибернация и таймеры пробуждения…
russian.LaunchTray=Запустить PC MATE и показать QR-код для телефона
russian.AutoStartTray=Запускать помощник PC MATE при входе в Windows
russian.DotNetTitle=Требуется компонент .NET 8
russian.DotNetMessage=PC MATE работает на .NET 8 Desktop Runtime, а на этом компьютере его нет.%n%nУстановщик скачает его с сайта Microsoft (около 55 МБ) и установит автоматически.
russian.DotNetInstalling=Устанавливаем .NET 8 Desktop Runtime…
russian.DotNetFailed=Не удалось скачать .NET 8 Desktop Runtime.%n%nПроверьте подключение к интернету или установите его вручную:%nhttps://dotnet.microsoft.com/download/dotnet/8.0%n%nОшибка: %1
russian.NoInternet=Нет подключения к интернету. Скачайте .NET 8 Desktop Runtime вручную и запустите установку заново.
russian.ReadyInfo=После установки откроется QR-код для подключения телефона и мастер проверки готовности компьютера.

english.ServiceInstalling=Installing the PC MATE service...
english.FirewallRule=Adding a firewall rule for the home network...
english.PowerPrep=Preparing power settings: hibernation and wake timers...
english.LaunchTray=Start PC MATE and show the pairing QR code
english.AutoStartTray=Start the PC MATE helper when Windows starts
english.DotNetTitle=.NET 8 is required
english.DotNetMessage=PC MATE runs on the .NET 8 Desktop Runtime, which is not installed.%n%nSetup will download it from Microsoft (about 55 MB) and install it automatically.
english.DotNetInstalling=Installing the .NET 8 Desktop Runtime...
english.DotNetFailed=Could not download the .NET 8 Desktop Runtime.%n%nCheck your internet connection or install it manually:%nhttps://dotnet.microsoft.com/download/dotnet/8.0%n%nError: %1
english.NoInternet=No internet connection. Download the .NET 8 Desktop Runtime manually and run setup again.
english.ReadyInfo=After installation the pairing QR code and the readiness wizard will open.

[Tasks]
Name: "autostart"; Description: "{cm:AutoStartTray}"; GroupDescription: "Автозапуск"; Flags: checkedonce

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; \
  Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\PC MATE"; Filename: "{app}\{#TrayExe}"; Comment: "Значок PC MATE в области уведомлений"
Name: "{group}\Проверка готовности компьютера"; Filename: "{app}\{#TrayExe}"; Parameters: "--wizard"
Name: "{group}\Папка с журналами"; Filename: "{commonappdata}\PcMate\logs"
Name: "{group}\Удалить PC MATE"; Filename: "{uninstallexe}"

[Registry]
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
  ValueName: "PC MATE"; ValueData: """{app}\{#TrayExe}"""; Flags: uninsdeletevalue; Tasks: autostart

[Run]
; 0. Зависимость: .NET 8 Desktop Runtime, если его нет
Filename: "{tmp}\windowsdesktop-runtime.exe"; Parameters: "/install /quiet /norestart"; \
  StatusMsg: "{cm:DotNetInstalling}"; Flags: waituntilterminated; Check: NeedsDotNet

; 1. Служба: автозапуск, перезапуск при сбое (ТЗ §6.2)
Filename: "{sys}\sc.exe"; Parameters: "create {#ServiceName} binPath= ""{app}\{#ServiceExe}"" start= auto DisplayName= ""PC MATE Agent"""; \
  Flags: runhidden; StatusMsg: "{cm:ServiceInstalling}"
Filename: "{sys}\sc.exe"; Parameters: "description {#ServiceName} ""Удалённое управление компьютером с телефона: питание, сценарии, расписания."""; \
  Flags: runhidden
Filename: "{sys}\sc.exe"; Parameters: "failure {#ServiceName} reset= 86400 actions= restart/5000/restart/10000/restart/30000"; \
  Flags: runhidden
Filename: "{sys}\sc.exe"; Parameters: "start {#ServiceName}"; Flags: runhidden

; 2. Брандмауэр: локальный сервер агента, только частные сети
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""PC MATE (локальная сеть)"" dir=in action=allow protocol=TCP localport={#LanPort} profile=private,domain program=""{app}\{#ServiceExe}"""; \
  Flags: runhidden; StatusMsg: "{cm:FirewallRule}"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""PC MATE (Wake-on-LAN)"" dir=in action=allow protocol=UDP localport=9 profile=private,domain"; \
  Flags: runhidden

; 3. Базовая подготовка питания — то же, что делает мастер готовности
Filename: "{sys}\powercfg.exe"; Parameters: "/hibernate on"; Flags: runhidden; StatusMsg: "{cm:PowerPrep}"
Filename: "{sys}\powercfg.exe"; Parameters: "/setacvalueindex scheme_current 238c9fa8-0aad-41ed-83f4-97be242c8f20 bd3b718a-0680-4d9d-8ab2-e1d2b4ac806d 1"; Flags: runhidden
Filename: "{sys}\powercfg.exe"; Parameters: "/setactive scheme_current"; Flags: runhidden

; 4. Помощник в трее: QR-код и мастер готовности
Filename: "{app}\{#TrayExe}"; Parameters: "--first-run"; Description: "{cm:LaunchTray}"; \
  Flags: postinstall nowait skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/f /im {#TrayExe}"; Flags: runhidden; RunOnceId: "StopTray"
Filename: "{sys}\sc.exe"; Parameters: "stop {#ServiceName}"; Flags: runhidden; RunOnceId: "StopService"
Filename: "{sys}\sc.exe"; Parameters: "delete {#ServiceName}"; Flags: runhidden; RunOnceId: "DeleteService"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""PC MATE (локальная сеть)"""; Flags: runhidden; RunOnceId: "DelFwTcp"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""PC MATE (Wake-on-LAN)"""; Flags: runhidden; RunOnceId: "DelFwUdp"
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""\PC MATE"" /F"; Flags: runhidden; RunOnceId: "DelTasks"

[UninstallDelete]
Type: filesandordirs; Name: "{commonappdata}\PcMate\logs"
Type: filesandordirs; Name: "{commonappdata}\PcMate\triggers"

[Code]
var
  DownloadPage: TDownloadWizardPage;
  DotNetNeeded: Boolean;
  DotNetChecked: Boolean;

{ Проверяем не «какой-нибудь .NET», а именно Desktop Runtime 8:
  WinForms-помощнику нужен именно он, консольного Microsoft.NETCore.App мало. }
function HasDotNet8Desktop(): Boolean;
var
  FindRec: TFindRec;
  Path: String;
begin
  Result := False;
  Path := ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App');

  if FindFirst(Path + '\8.*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
        begin
          Result := True;
          Break;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

function NeedsDotNet(): Boolean;
begin
  if not DotNetChecked then
  begin
#if SelfContained == "1"
    DotNetNeeded := False;
#else
    DotNetNeeded := not HasDotNet8Desktop();
#endif
    DotNetChecked := True;
  end;
  Result := DotNetNeeded;
end;

function OnDownloadProgress(const Url, FileName: String; const Progress, ProgressMax: Int64): Boolean;
begin
  Result := True;
end;

procedure InitializeWizard();
begin
  DownloadPage := CreateDownloadPage(
    SetupMessage(msgWizardPreparing), SetupMessage(msgPreparingDesc), @OnDownloadProgress);
end;

function InitializeSetup(): Boolean;
var
  ResultCode: Integer;
begin
  { Обновление поверх работающей установки: снимаем службу и помощник заранее,
    иначе файлы окажутся занятыми. }
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im {#TrayExe}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := True;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID <> wpReady then
    Exit;

  if not NeedsDotNet() then
    Exit;

  if SuppressibleMsgBox(CustomMessage('DotNetMessage'), mbConfirmation, MB_OKCANCEL, IDOK) <> IDOK then
  begin
    Result := False;
    Exit;
  end;

  DownloadPage.Clear;
  DownloadPage.Add('{#DotNetUrl}', 'windowsdesktop-runtime.exe', '');
  DownloadPage.Show;
  try
    try
      DownloadPage.Download;
      Result := True;
    except
      SuppressibleMsgBox(
        FmtMessage(CustomMessage('DotNetFailed'), [GetExceptionMessage]), mbCriticalError, MB_OK, IDOK);
      Result := False;
    end;
  finally
    DownloadPage.Hide;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssInstall then
  begin
    { Старую службу удаляем до копирования файлов, чтобы sc create в [Run]
      отработал на чистом месте. }
    Exec(ExpandConstant('{sys}\sc.exe'), 'delete {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(1000);
  end;
end;

function UpdateReadyMemo(const Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo,
  MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result := MemoDirInfo + NewLine + NewLine;

  if MemoTasksInfo <> '' then
    Result := Result + MemoTasksInfo + NewLine + NewLine;

  Result := Result + 'Что будет сделано:' + NewLine;
  Result := Result + Space + '• служба PC MATE с автозапуском и перезапуском при сбое' + NewLine;
  Result := Result + Space + '• правило брандмауэра для домашней сети (порт {#LanPort})' + NewLine;
  Result := Result + Space + '• включение гибернации и таймеров пробуждения' + NewLine;

  if NeedsDotNet() then
    Result := Result + Space + '• загрузка и установка .NET 8 Desktop Runtime' + NewLine;

  Result := Result + NewLine + CustomMessage('ReadyInfo');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    if SuppressibleMsgBox(
         'Удалить настройки, сопряжённые телефоны, сценарии и расписания?' + #13#10 +
         'Нажмите «Нет», если планируете установить PC MATE заново.',
         mbConfirmation, MB_YESNO, IDNO) = IDYES then
      DelTree(ExpandConstant('{commonappdata}\PcMate'), True, True, True);
  end;
end;
