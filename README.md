# HF811 C# Collector — Windows 10 x64 專用安裝包建置專案（v1.1）

**交付的是可編譯、可建置 Inno Setup 安裝程式的原始碼，不是已編譯的 Setup.exe。**
本製作環境沒有 .NET SDK / Windows / Inno Setup，無法現場編譯或驗證 Windows 10 上的實際執行情況；請在 Windows 開發機或 GitHub Actions 編譯、自動測試，然後在產線一台舊筆電試裝。

## 1. 目標環境與限制

| 項目 | 設定 |
|---|---|
| 產線電腦 | Windows 10 **64 位元 x64** |
| 核心程式 | C# / .NET 10、單檔自包含執行檔 (`win-x64`) |
| 安裝包 | `HF811Collector-win-x64-Setup.exe` (Inno Setup 6.3+) |
| 目標電腦的額外依賴 | **無 Python、無 .NET Runtime、無 Visual Studio／SDK** |
| 安裝權限 | **當前 Windows 使用者安裝**，一般不需管理員權限 |
| 開機啟動 | **使用者登入後自動啟動**，不是 Windows Service；如未登入，不能收碼 |
| Scanner | HF811 TCP Server 時 Collector 主動連線；預設 port 55256 |
| SQLite | `%LOCALAPPDATA%\HF811Collector\data\scans.sqlite3` |
| 日誌 | `%LOCALAPPDATA%\HF811Collector\logs\collector-YYYYMMDD.log`，保留最近約 14 天 |
| 查詢 API | **只監聽** `127.0.0.1:8088`，不對外網開啟 |

> 微軟目前對 .NET 10 + Windows 10 的官方支援限於仍受支援的 Enterprise／LTSC 版本。一般 Windows 10 Home/Pro 已於 2025-10-14 結束一般支援。它們可能可以執行，但**不能保證官方支援或安全更新**。正式生產環境須依 OS Edition / Version / ESU 政策評估，並在實機試驗。Windows 10 32-bit / Windows 7 / 8 不適用。

## 2. 開發電腦產生安裝檔（只需做一次）

在**Windows x64 開發電腦**安裝 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) 和 [Inno Setup 6](https://jrsoftware.org/isinfo.php)（開發電腦才需要）。開 PowerShell：

```powershell
cd C:\path\to\hf811_csharp_installer
.\build.ps1
```

腳本會執行 `dotnet publish` → 本機 C# `selftest`（TCP framing / SQLite / 去重 / NOREAD）→ Inno Setup，成功後輸出：

```text
dist\installer\HF811Collector-win-x64-Setup.exe
dist\installer\SHA256SUMS.txt
```

自包含發佈與 SQL/Native 相依套件都要放進安裝檔，**不可將原始的 `.cs` 程式碼或只有 DLL 的資料夾複製到產線電腦，就以為能執行**。

只要 `.exe`、暫時不打包：

```powershell
.\build.ps1 -SkipInstaller
```

### 沒有安裝 SDK / Inno Setup 的電腦

將專案放在自己的**私有** GitHub repository，切到 `Actions → Build HF811 Windows 10 x64 Installer → Run workflow`，下載 `HF811Collector-win-x64-Setup` artifact。原始碼不包含公司機密，真實 IP、員工資料、實際產線條碼禁止提交。

> GitHub Actions workflow 已包含 x64 自動編譯及 self-test；它仍不是 HF811 **實體設備**驗證。

## 3. 舊 Windows 10 筆電實機安裝

1. 確認 `winver` 顯示 Windows 10，且 `設定 → 系統 → 關於` 是 64 位元；記下 **Home/Pro/Enterprise/LTSC** 及組建號碼。
2. 在開發電腦比對安裝包的 SHA256（USB 複製後可再比對一次）：

   ```powershell
   Get-FileHash .\HF811Collector-win-x64-Setup.exe -Algorithm SHA256
   ```

3. 在產線筆電執行 `HF811Collector-win-x64-Setup.exe`。預設安裝位置 `%LOCALAPPDATA%\Programs\HF811Collector`，通常無需管理員權限；若被 AppLocker、WDAC 或公司政策封鎖，須找 IT 授權，不應繞過安全控管。
4. 安裝精靈可勾選「**Windows 登入後自動啟動 Collector**」。建議專用產線帳號使用；使用不同 Windows 帳號時，設定與 SQLite **不是同一份**。安裝完成時先不要啟動，先改設定。
5. 開始選單 → `HF811 Collector` → `03 編輯 HF811 設定`，修改 `%LOCALAPPDATA%\HF811Collector\config.json`。範例如下：

   ```json
   {
     "Mode": "connect",
     "Host": "192.168.16.50",
     "Port": 55256,
     "DeviceId": "SMT-LINE-01-HF811",
     "Framing": "lf",
     "IdleGapMs": 100,
     "ReconnectMs": 1000,
     "DedupWindowMs": 2000,
     "NoReadValue": "NOREAD",
     "ApiPort": 8088,
     "DatabasePath": ""
   }
   ```

   `192.168.16.50` 是**示範 IP**，一定要填實際 HF811 位置，不要直接照抄。`DatabasePath` 留空會存放在上述 `%LOCALAPPDATA%`。

6. **先確認** HF811 的實際 IP、TCP Server mode、port，以及掃描輸出尾碼。務必在 DataMax Plus 設定 `LF`，或按現場設定調整 `"Framing": "crlf"`。原廠若沒輸出分隔符，TCP 不會幫你區分兩筆條碼；`idle` 時間切分只能當近似測試。
7. 開啟 PowerShell，到專案附的 `preflight.ps1` 所在資料夾執行：

   ```powershell
   .\preflight.ps1 -CheckScanner
   ```

   此步驟檢查 Windows 架構、讀取設定、測試 HF811 TCP Port 及本機 API。第一次執行 API 尚未啟動顯示警告是正常的。若公司 PowerShell 執行原則阻擋，請依 IT 規範核准；勿擅自永久降低執行政策。
8. 開始選單 → `01 啟動 Collector`。可查看主控台出現 `Scanner connected`，然後打開：

   - `http://127.0.0.1:8088/health`
   - `http://127.0.0.1:8088/stats`
   - `http://127.0.0.1:8088/events?limit=20`

9. 掃 3–5 張實體條碼，比對事件時間、條碼內容、`is_duplicate`、`is_no_read`。再拔掉掃描器網路線／關閉交換器埠，確認 `ReconnectMs` 間隔的重連，再恢復確認持續接收。

## 4. 無 HF811 實體設備的模擬測試

預設樣板 `Host=127.0.0.1`、`Port=55256`、`Framing=lf`。在同一台 Windows PC：

1. `02 啟動本機模擬掃描器`（TCP Server 每 200ms 輸出 1 碼）。
2. `01 啟動 Collector`。
3. 開啟 `http://127.0.0.1:8088/stats`，確認 total 上升。
4. 停止任意一個程式後重新啟動，觀察重連與事件連續性。

兩種 TCP 模式可測試：

```powershell
# TCP Server：模擬 HF811 原始配置
$exe = "$env:LOCALAPPDATA\Programs\HF811Collector\HF811Collector.exe"
& $exe simulator --mode server --host 127.0.0.1 --interval-ms 200 --count 1000 --disconnect-every 30 --delimiter lf

# TCP Client 模式須修改 Collector Mode=listen，Host=127.0.0.1，並設定相同 port
& $exe simulator --mode client --host 127.0.0.1 --interval-ms 200 --count 1000 --delimiter lf
```

## 5. 維運、安全與已知限制

- **不需入站防火牆開孔**（預設 `Mode=connect`，Collector 主動連 HF811）。若改用 `Mode=listen`（Scanner 主動連筆電），才須依 IT 核可新增入站防火牆規則及限制掃描器 IP。
- 日誌位置：`%LOCALAPPDATA%\HF811Collector\logs`；SQLite 的所有掃描（包括重複碼與 NOREAD）都保留，`is_duplicate` 僅代表時間窗標記，不會直接丟棄。
- **程式只執行一份**：同一 Windows 使用者工作階段的第二個 Collector 會退出。更新／解除安裝時，若 Collector 正在執行，安裝程式會要求先關閉。
- **更新不覆寫 config.json**，解除安裝後設定、SQLite、日誌預設保留；若要清除，需自行備份後手動刪除 `%LOCALAPPDATA%\HF811Collector`。
- 如果選擇 Windows 登入自動啟動，捷徑會以最小化模式啟動。**電腦沒登入、登出或使用者工作階段中斷時，不能保證持續收碼**。要真正無人值守，後續版本應改 Windows Service + ProgramData + 管理員受控部署。
- **尚未**支援 SMT API 自動上傳／佇列確認、設備回寫、完整 Honeywell 專用命令、硬體驅動、簽章、角色控管、遠端 API；本版適合 TCP Collector pilot，不等於可直接用來做工單過站判定。
- 產線環境不宜對網際網路直接開放掃描器或 Collector。資料庫需定期備份（建議使用 SQLite 備份 API 或安全關閉程式後複製），並在正式上線前做停電、斷網、磁碟滿與資料量耐久測試。

## 6. 檔案清單

```text
src/Program.cs                     C# Collector / Simulator / SQLite / Loopback API / selftest
src/config.json                    預設設定樣板
src/HF811Collector.csproj          .NET 10 self-contained
installer/HF811Collector.iss       x64 Inno Setup，登入自動啟動，保留設定與資料
build.ps1                          唯一 x64 build / self-test / SHA256
preflight.ps1                      產線端 Windows、TCP、API 啟動前檢查
.github/workflows/build-windows.yml  Windows runner 一鍵建置
README.md                          本說明
```

## 官方參考

- [.NET 10 的 Windows 支援範圍](https://learn.microsoft.com/en-us/dotnet/core/install/windows)
- [.NET single-file self-contained 發佈](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/)
- [Windows 10 結束支援公告](https://support.microsoft.com/en-us/windows/deployment/updates-lifecycle/windows-10-support-has-ended-on-october-14-2025)
- [Inno Setup](https://jrsoftware.org/isinfo.php)


## v1.1.1 修正（2026-11-06）

修正安裝時 `IPersistFile::Save failed; code 0x80070003`：原本 `[Icons]` 的捷徑名稱 `04 掃描資料 / 日誌資料夾` 包含斜線，被 Windows 視作資料夾路徑，且父目錄不存在。已改為 `04 掃描資料與日誌資料夾`。這不是中文 Unicode 的問題。

建置腳本同時增加搜尋使用者 AppData 中的 Inno Setup 6/7 安裝位置。

若你已經成功建置 `dist\win-x64`，可直接用 Inno Setup Compiler 開啟 `installer\HF811Collector.iss`，點 Build > Compile；或使用 `& '實際ISCC.exe路徑' .\installer\HF811Collector.iss`，不必重新編譯 C#。
