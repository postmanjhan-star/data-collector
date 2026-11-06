; Windows 10 x64, dual install scope (current user or all users).
; All users requires UAC elevation. Data stays per logged-in Windows user.
#define AppName "HF811 Collector Lab"
#define AppVersion "1.2.0"

[Setup]
AppId={{E28955B5-B36E-4F4B-B73D-5128A25D87C9}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Internal SMT Engineering
DefaultDirName={autopf}\HF811Collector
DefaultGroupName=HF811 Collector
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
UsePreviousPrivileges=no
UsePreviousAppDir=no
MinVersion=10.0
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
AppMutex=HF811Collector-Collector
OutputDir=..\dist\installer
OutputBaseFilename=HF811Collector-win-x64-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName=HF811 Collector Lab
UninstallDisplayIcon={app}\HF811Collector.exe

[Tasks]
Name: "startup"; Description: "Windows 登入後自動啟動 Collector（需使用者登入，非 Windows Service）"; Flags: checkedonce

    ; Personal data/config are initialized by Collector for the actual signed-in Windows user.
    ; Do not create them from elevated setup: UAC may run under a different admin account.

[Files]
Source: "..\dist\win-x64\HF811Collector.exe"; DestDir: "{app}"; Flags: ignoreversion
; Bundle a read-only default template beside the executable. Each logged-in user gets
; their own %LOCALAPPDATA%\HF811Collector\config.json on first launch.
Source: "..\src\config.json"; DestDir: "{app}"; DestName: "config.default.json"; Flags: ignoreversion

[Icons]
Name: "{group}\01 啟動 Collector"; Filename: "{app}\HF811Collector.exe"; Parameters: "collector"; WorkingDir: "{app}"
Name: "{group}\02 啟動本機模擬掃描器"; Filename: "{app}\HF811Collector.exe"; Parameters: "simulator --mode server --interval-ms 200 --repeat-every 5 --no-read-every 10"; WorkingDir: "{app}"
Name: "{group}\03 編輯 HF811 設定"; Filename: "{app}\HF811Collector.exe"; Parameters: "edit-config"; WorkingDir: "{app}"
Name: "{group}\04 掃描資料與日誌資料夾"; Filename: "{app}\HF811Collector.exe"; Parameters: "open-data"; WorkingDir: "{app}"
Name: "{group}\05 Collector 狀態 API"; Filename: "http://127.0.0.1:8088/stats"
Name: "{autostartup}\HF811 Collector"; Filename: "{app}\HF811Collector.exe"; Parameters: "collector"; Tasks: startup; Flags: runminimized

[Run]
Filename: "{app}\HF811Collector.exe"; Parameters: "collector"; Description: "立刻啟動 Collector（首次設定 IP 後再勾選）"; Flags: nowait postinstall skipifsilent unchecked runasoriginaluser
