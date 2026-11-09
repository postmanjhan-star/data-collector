> ⚠️ 歷史 README（v1.1–v1.5）僅供追溯，部分舊版 Console 指令及限制已過時。最新操作請看 [README](../README.md)。

---

# HF811 Collector — Windows 10 x64 GUI／Server Sync（v1.5）

> **公司使用 PHP 或 Python 伺服器？** C# Collector v1.5 不需要更改。接收端已提供 [FastAPI](server-python/) 與 [PHP PDO](server-php/) 兩種相同 ACK 協定的實作；請看 [PHP／Python 部署指南](docs/RECEIVER_PHP_PYTHON.md)。ASP.NET 版本只是保留的參考接收端。


**GitHub Actions 會編譯並提供可下載的 Setup.exe Artifact；Repository 本身保存原始碼，不追蹤編譯成品。**
本製作環境沒有 .NET SDK / Windows / Inno Setup，無法現場編譯或驗證 Windows 10 上的實際執行情況；請在 Windows 開發機或 GitHub Actions 編譯、自動測試，然後在產線一台舊筆電試裝。

## 1. 目標環境與限制

| 項目 | 設定 |
|---|---|
| 產線電腦 | Windows 10 **64 位元 x64** |
| 核心程式 | C# / .NET 10、單檔自包含 CLI + WinForms GUI (`win-x64`) |
| 安裝包 | `HF811Collector-win-x64-Setup.exe` (Inno Setup 6.3+) |
| 目標電腦的額外依賴 | **無 Python、無 .NET Runtime、無 Visual Studio／SDK** |
| 安裝權限 | 安裝精靈可選 **僅目前使用者（免管理員）** 或 **所有使用者（需要 UAC 管理員授權）** |
| 開機啟動 | 可選擇登入後啟動 WinForms 並縮至系統匣；沒有登入仍不會收碼，不是 Windows Service |
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

3. 在產線筆電執行 `HF811Collector-win-x64-Setup.exe`，選擇「僅為我安裝」或「所有使用者安裝」。前者在 `%LOCALAPPDATA%\Programs\HF811Collector`，後者在 `C:\Program Files\HF811Collector`，會由 Windows UAC 要求管理員權限；若被 AppLocker、WDAC 或公司政策封鎖，須找 IT 授權。
4. 安裝精靈可勾選「**Windows 登入後自動啟動 Collector**」。建議專用產線帳號使用；使用不同 Windows 帳號時，設定與 SQLite **不是同一份**。安裝後開啟 WinForms 主視窗，先改設定，再按「啟動 Collector」；無需開啟 Console。
5. 開始選單 → `HF811 Collector` → `03 編輯 HF811 設定`。這個捷徑會先為目前登入的 Windows 使用者建立 `%LOCALAPPDATA%\HF811Collector\config.json`（若不存在），再開啟記事本。範例如下：

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
$exe = "$env:LOCALAPPDATA\Programs\HF811Collector\HF811Collector.exe"  # 所有使用者安裝請改成 "$env:ProgramFiles\HF811Collector\HF811Collector.exe"
& $exe simulator --mode server --host 127.0.0.1 --interval-ms 200 --count 1000 --disconnect-every 30 --delimiter lf

# TCP Client 模式須修改 Collector Mode=listen，Host=127.0.0.1，並設定相同 port
& $exe simulator --mode client --host 127.0.0.1 --interval-ms 200 --count 1000 --delimiter lf
```

## 5. 維運、安全與已知限制

- **不需入站防火牆開孔**（預設 `Mode=connect`，Collector 主動連 HF811）。若改用 `Mode=listen`（Scanner 主動連筆電），才須依 IT 核可新增入站防火牆規則及限制掃描器 IP。
- 日誌位置：`%LOCALAPPDATA%\HF811Collector\logs`；SQLite 的所有掃描（包括重複碼與 NOREAD）都保留，`is_duplicate` 僅代表時間窗標記，不會直接丟棄。
- **單一使用者工作階段僅執行一份**：同一 Windows 使用者工作階段的第二個 Collector 會退出。不同 Windows 帳號可能各自啟動，若共用一台 HF811 則可能互相競爭連線；正式產線建議指定一個專用登入帳號，或後續改 Windows Service。更新／解除安裝前請先關閉 Collector。
- **更新不覆寫 config.json**，解除安裝後設定、SQLite、日誌預設保留；若要清除，需自行備份後手動刪除 `%LOCALAPPDATA%\HF811Collector`。
- 如果選擇 Windows 登入自動啟動，捷徑會以最小化模式啟動。**電腦沒登入、登出或使用者工作階段中斷時，不能保證持續收碼**。要真正無人值守，後續版本應改 Windows Service + ProgramData + 管理員受控部署。
- **尚未**支援 SMT API 自動上傳／佇列確認、設備回寫、完整 Honeywell 專用命令、硬體驅動、簽章、角色控管、遠端 API；本版適合 TCP Collector pilot，不等於可直接用來做工單過站判定。
- 產線環境不宜對網際網路直接開放掃描器或 Collector。資料庫需定期備份（建議使用 SQLite 備份 API 或安全關閉程式後複製），並在正式上線前做停電、斷網、磁碟滿與資料量耐久測試。

## 6. 檔案清單

```text
src/Program.cs                     C# Collector / Simulator / SQLite / Loopback API / selftest
src/config.json                    預設設定樣板
src/HF811Collector.csproj          .NET 10 self-contained
installer/HF811Collector.iss       x64 Inno Setup，兩種安裝模式，登入自動啟動，保留設定與資料
build.ps1                          唯一 x64 build / self-test / SHA256
preflight.ps1                      產線端 Windows、TCP、API 啟動前檢查
.github/workflows/build-windows.yml  Windows runner 一鍵建置與兩種安裝範圍實測
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

## v1.2 安裝範圍與快速體驗（2026-11-06）

安裝精靈的兩個選項：

| 安裝模式 | 安裝檔案所在位置 | 權限要求 | 開始選單 |
|---|---|---|---|
| 僅為我安裝 | `%LOCALAPPDATA%\\Programs\\HF811Collector` | 一般使用者即可 | 目前使用者 |
| 所有使用者安裝 | `%ProgramFiles%\\HF811Collector` | Windows UAC 管理員確認／帳密 | 全部使用者 |

不論安裝哪種模式，**每個 Windows 登入帳號的設定、SQLite、日誌仍各自位於 `%LOCALAPPDATA%\\HF811Collector`**，不會因為共享程式檔就共享掃描資料。第一次點 `01 啟動 Collector` 或 `03 編輯 HF811 設定` 時，程式從安裝目錄的 `config.default.json` 建立該帳號的 `config.json`，且不覆蓋既有設定。若需要所有 Windows 使用者共享**同一份**掃描資料，應另外設計受控的 Windows Service + ProgramData 或中央 DB，不能只選「所有使用者安裝」。

**從先前 v1.1.x 轉換安裝範圍時，建議先停止 Collector、備份 `%LOCALAPPDATA%\\HF811Collector`，解除安裝舊版後再安裝新版。** 程式資料預設保留；請勿直接刪除資料夾或 SQLite。

無實機測試：

1. 開始選單先開 `02 啟動本機模擬掃描器`（預設監聽 `127.0.0.1:55256`）。
2. 開 `01 啟動 Collector`，畫面應出現 `Scanner connected`，持續收到 `PCB...` 資料。
3. 在本機瀏覽 `http://127.0.0.1:8088/stats` 與 `http://127.0.0.1:8088/events?limit=20`。
4. 開 `03 編輯 HF811 設定`，把 `Host` 改成實際設備 IP 後重新啟動 Collector 以測實機，確保不與模擬器同時占用連線。

CI 透過 `/CURRENTUSER` 和 `/ALLUSERS` 兩種參數執行靜默安裝測試，並在全新的 GitHub Windows Runner 驗證檔案、捷徑、首次設定、內建 self-test 和解除安裝。正式產線仍須在 Windows 10 x64 與 HF811 實機上驗證；CI Runner 並非 Windows 10。

## v1.3 GUI / 系統匣操作（2026-11-06）

安裝後使用開始選單的 **HF811 Collector**，會打開 WinForms 主視窗，不再需要先打開黑色主控台。主視窗包含：

- **設定**：IP/Host、Port、Mode、Device ID、LF/CRLF/CR/idle、去重時間、重連時間、NOREAD、API Port、SQLite 路徑。
- **操作**：「儲存設定」「啟動 Collector」「停止 Collector」「開始/停止模擬掃描」「資料夾」「狀態 API」。
- **監控**：連線狀態、掃碼總數／有效／重複／NOREAD、最近條碼、運作日誌。
- **系統匣**：按 X 僅隱藏視窗，背景持續收碼；左鍵圖示還原，右鍵有啟停／資料夾／API／結束程式。只有選「結束程式」才停止所有背景工作。
- **自動啟動**：安裝時可選「登入 Windows 後啟動並縮到系統匣」（預設不勾）；啟動參數為 `--background`。無登入時不會執行，非 Windows Service。
- **外觀**：WinForms 輕量 GUI 與自訂 HF811 Scanner 圖示，適用 Windows 10 x64。

**第一次模擬測試**：在 GUI 確認 Host = `127.0.0.1`、Mode = `connect`、Port = `55256`、Framing = `lf`；按「開始模擬掃描」，再按「啟動 Collector」。總筆數應開始上升。按「停止模擬」確認 Collector 顯示重連，再恢復模擬確認自動回復。

**真正接 HF811**：停止模擬與 Collector，修改 Host 為實際設備 IP 並儲存，再按「啟動 Collector」。HF811 預設無分隔符，正式收碼前必須配置 LF/CRLF 或依實機設定修改 Framing；使用 idle framing 僅適合啟發式測試，不能保證正確分包。

**設定生效時機**：儲存設定不會直接改變已連線的 Collector；修改 IP、Port 等參數後請先停止並重新啟動。SQLite 與日誌仍保存在目前登入使用者的 `%LOCALAPPDATA%\\HF811Collector`，不隨「所有使用者安裝」自動共享。

**重要限制**：WinForms Tray 仍需登入 Windows 才能長期運作；正式無人值守應改成 Windows Service。多位不同 Windows 使用者同時執行 GUI 並不代表能共享同一台 HF811；建議固定單一產線帳號使用。此版未具備 SMT 過站 API 的確認重送佇列。

### GUI 原始碼

```text
desktop/HF811Collector.Desktop.csproj   .NET 10 WinForms GUI，self-contained WinExe
desktop/Program.cs                       Tray GUI 啟動、單實例控制、自我檢查
desktop/MainForm.cs                      設定、即時統計、系統匣、背景收碼與模擬
desktop/ConfigFile.cs                    設定檔驗證與安全寫入
desktop/assets/hf811.ico                 自訂 Windows App / Tray 圖示
src/Program.cs                           原始 CLI／核心（保留）
```

GitHub Actions 現在對 CLI、GUI 分別編譯並執行 self-test，再建立 Setup.exe，使用 Windows Runner 測試「目前使用者」和「所有使用者」的安裝與卸載。CI 的 Windows Runner 並非 Windows 10，也無法取代現場 HF811 實體掃描器的測試。

## v1.4.0 — UI 重構與區網搜尋（2026-11-06）

### GUI 重構

- 概覽 / 設備設定 / 運作日誌三個頁面，避免控制列與標題重疊。
- 固定高度的標題與操作列，概覽頁獨立顯示連線資訊、四個統計數字、最後條碼、事件摘要。
- 設定頁採左右雙欄、可捲動；滑鼠滾輪不再意外改變數值欄位。
- 保留「關閉 X → 最小化至系統匣」、「真正結束 → 停止 Collector」、登入後背景啟動等既有行為。

### 搜尋 HF811 IP（目前是有界 TCP 探測，不是原廠識別）

1. 先停止 Collector 和本機模擬器，以免占用同一個 TCP 連線。
2. 點 **「搜尋掃描器」**，選擇 Windows 網卡（例如乙太網路）與搜尋網段前三段，例如 `192.168.16`。
3. 程式只在你按下「開始搜尋」之後探測**此 /24 網段**的主機 `.1` 至 `.254`。預設 TCP Port 是 `55256`，每次最多 12 個並行連線、短逾時，允許取消。
4. 結果標示 **TCP Port 開啟 / 設備型號未驗證**。請和 DataMax Plus 中的設備 IP 及序號核對，選中候選後按「套用 IP / Port」；最後在「設備設定」手動儲存。
5. 如果需要判斷 LF / CR / CRLF 尾碼，可選中候選後按「取樣判斷尾碼」，在 8 秒內讓掃描器讀取一張測試條碼。顯示收到的資料 HEX 和「推測」結果，需另外勾選才會套用尾碼；未收到資料或無明確控制字元時不推測。

**掃描範圍限制**：只接受 RFC1918 私有網段和 `169.254.x.x` 直連網段，單次最多 254 個 IP。請只在取得網管／設備管理者許可的現場網段使用；不會自動掃描整個公司網路，也不會發出 HF811 設定變更或 Trigger 指令。

**不可自動讀出的部分**：一般 TCP 條碼輸出只可確定候選 IP、Port 可連通，以及觀察到的條碼位元組。**TCP 連得上不等於一定是 HF811**，也無法直接從此連線得知真實設備序號、韌體、DHCP、觸發模式、影像曝光、所有格式設定。設備代號 `DeviceId` 是 Collector 的本機追蹤標籤，不是掃描器的真實序號；去重窗、重連時間、SQLite 路徑和 API Port 也是 Collector 自己的設定，不是掃描器參數。

**未來可再整合原廠 SDK**：HF81x User Guide Chapter 13 描述 DataMax SDK `SDK_searchDevices`、發現設備序號和 IP 的 callback，以及查詢部分掃描器參數的函式。要完成真正的設備識別和全套設定讀取，需取得原廠 Windows SDK（DLL、header、版本與授權），再設計明確的唯讀介面；此版**尚未整合 DataMax SDK**，不假裝能自動抓取所有硬體設定。

官方文件：https://prod-edam.honeywell.com/content/dam/honeywell-edam/sps/ppr/en-us/public/products/barcode-scanners/fixed-mount/hf81x/documents/sps-ppr-hf81x-en-ug.pdf

### 驗收

GitHub Actions 執行 x64 self-contained CLI + GUI 編譯，GUI self-test（包含網段有效性與 mock TCP CRLF 訊號），再以 Windows Runner 測試目前使用者與所有使用者安裝、捷徑、自我測試、解除安裝。仍須在真正的 Windows 10 舊筆電與 HF811 設備上實際核對 IP、網段、Port 和連續掃碼。

## v1.5 — Server Sync（2026-11-06）

**完整部署、API、測試與風險說明：[docs/SERVER_SYNC.md](docs/SERVER_SYNC.md)**

- HF811 每筆條碼先落地 SQLite，再由背景 `SyncAgent` 以 HTTPS 批次送到 Receiver。
- Migration 會替既有掃描資料補 `event_id`、同步狀態和重試欄位，不刪除 v1.4 資料。
- 伺服器 ACK 後才轉 `synced`，失敗留 `pending`；同 UUID 重傳由伺服器去重。
- GUI 新增「伺服器同步」設定頁：Collector ID、完整 API URL、同步間隔、批次數、加密 API Key、Pending／Synced／Rejected、立即同步、授權驗證。
- 使用者憑證透過 Windows DPAPI 加密保存在 `%LOCALAPPDATA%\\HF811Collector\\server-key.dpapi`，**不放進 Git 或 config.json**；所有使用者安裝仍各自設定。
- 接收端 `server/` 為 .NET 10 ASP.NET Core 最小可用伺服器，單節點 SQLite 去重，需自行部署 HTTPS 反向代理與定期備份。
- 接收端 `docker-compose.receiver.yml` 預設只綁伺服器 loopback `127.0.0.1:8090`，不得直接對外公開未加密 TCP 服務。
- 此版本**只保存原始掃描事件**，不會自動核銷工單或認定 PCB 過站成功。正式 SMT 驗證須另接入 MES 業務 API。

### 開發電腦快速啟動接收端

```powershell
$env:HF811_SERVER_API_KEY = "local-demo-only-change-to-a-random-key"
dotnet run --project .\\server\\HF811Receiver.csproj --urls http://127.0.0.1:8090
```

Collector GUI →「伺服器同步」→ 啟用、填 `http://127.0.0.1:8090/api/v1/collector/scans/batch`、填相同 API Key → 儲存 → 啟動 Collector（可搭配本機模擬器）。正式伺服器請使用受信任的 HTTPS 網域。

新測試：`tests/receiver-e2e.ps1` 在一次性的 GitHub Windows Runner 上啟動 Receiver，執行 Collector HTTP 實際上傳、ACK、重送去重、憑證拒絕，之後再驗證單一使用者／所有使用者的 Inno Setup。
