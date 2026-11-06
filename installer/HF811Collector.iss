; Windows 10 x64 (single package). Compile with Inno Setup 6.3+ / ISCC.exe.
#define AppName "HF811 Collector Lab"
#define AppVersion "1.1.1"

[Setup]
AppId={{E28955B5-B36E-4F4B-B73D-5128A25D87C9}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Internal SMT Engineering
DefaultDirName={localappdata}\Programs\HF811Collector
DefaultGroupName=HF811 Collector
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
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

[Dirs]
Name: "{localappdata}\HF811Collector"
Name: "{localappdata}\HF811Collector\data"
Name: "{localappdata}\HF811Collector\logs"

[Files]
Source: "..\dist\win-x64\HF811Collector.exe"; DestDir: "{app}"; Flags: ignoreversion
; Never overwrite config on reinstall. Uninstall retains user data/config by design.
Source: "..\src\config.json"; DestDir: "{localappdata}\HF811Collector"; Flags: onlyifdoesntexist uninsneveruninstall

[Icons]
Name: "{group}\01 啟動 Collector"; Filename: "{app}\HF811Collector.exe"; Parameters: "collector --config ""{localappdata}\HF811Collector\config.json"""; WorkingDir: "{app}"
Name: "{group}\02 啟動本機模擬掃描器"; Filename: "{app}\HF811Collector.exe"; Parameters: "simulator --mode server --interval-ms 200 --repeat-every 5 --no-read-every 10"; WorkingDir: "{app}"
Name: "{group}\03 編輯 HF811 設定"; Filename: "{sys}\notepad.exe"; Parameters: """{localappdata}\HF811Collector\config.json"""
Name: "{group}\04 掃描資料與日誌資料夾"; Filename: "{localappdata}\HF811Collector"
Name: "{group}\05 Collector 狀態 API"; Filename: "http://127.0.0.1:8088/stats"
Name: "{userstartup}\HF811 Collector"; Filename: "{app}\HF811Collector.exe"; Parameters: "collector --config ""{localappdata}\HF811Collector\config.json"""; Tasks: startup; Flags: runminimized

[Run]
Filename: "{app}\HF811Collector.exe"; Parameters: "collector --config ""{localappdata}\HF811Collector\config.json"""; Description: "立刻啟動 Collector（首次設定 IP 後再勾選）"; Flags: nowait postinstall skipifsilent unchecked
