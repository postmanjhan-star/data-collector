# HF811 Collector — 使用手冊（v1.5）

Windows 10 x64 產線工具：C# / .NET 10 GUI + 系統匣常駐，透過 TCP 接收 Honeywell HF811 的條碼，**先存本機 SQLite**，可再經 HTTPS 自動同步到公司的 **Python FastAPI 或 PHP** 伺服器。**產線電腦不需要安裝 Python、PHP、.NET Runtime 或 Visual Studio。**

> 本專案目前已完成 CI 自動測試，**不代表已完成 HF811 實機驗收**。掃描事件的成功上傳也不等於 MES/PCB 過站成功。

## 1. 五分鐘試玩：先用模擬器

1. 從 [GitHub Actions 安裝檔](https://github.com/postmanjhan-star/data-collector/actions/workflows/build-windows.yml) 找到成功的 Windows Build，下載 HF811Collector-win-x64-Setup Artifact，解壓縮後執行 `HF811Collector-win-x64-Setup.exe`。
2. 選擇 **僅目前使用者**（通常不需管理員）或 **所有使用者**（需要 UAC 管理員權限）；從開始選單開啟 **HF811 Collector**。
3. 在「設備設定」確認：`Mode=connect`、`Host=127.0.0.1`、`Port=55256`、`Framing=lf`，然後按 **儲存設定**。
4. 先按 **模擬掃描**，再按 **啟動收集**。概覽頁的總筆數、有效、重複、NOREAD 應開始變動；可切換「運作日誌」查看詳情。
5. 點視窗右上角 **X** 只會隱藏至系統匣，背景繼續收碼；系統匣右鍵選 **結束程式** 才會退出。

**注意：** `127.0.0.1` 是目前筆電自己，不是 HF811 真實 IP。若未啟動模擬器就啟動 Collector，看到 `127.0.0.1:55256 Connection refused`，代表本機此 Port 沒有模擬服務在監聽。

## 2. 設定真實 HF811

1. 停止模擬器與 Collector，先確認 HF811 的網路線、IP、TCP Server/Client 模式、Port 和實際條碼結尾符。
2. 點 **搜尋掃描器** → 選自己的乙太網路介面 → 在有權限的內網網段尋找 Port 55256 開放的候選主機。**Port 開啟不代表一定是 HF811**；請與設備商 DataMax Plus 的結果核對。這個功能不會更改掃描器參數。
3. 設定 `Mode=connect`、`Host=HF811 真實 IP`、`Port=已核對的 Port`；選好 Framing 後按 **儲存設定**、**啟動收集**。
4. 掃描 3–5 個實際條碼，確認內容沒有截斷、黏包及重複誤判。拔線／復線測試重連。

若設備未輸出 LF、CRLF 或 CR，Collector 可能無法按筆分包。應優先在 Honeywell 設定工具上啟用明確尾碼；`idle` 模式僅可用於近似測試。

### 設備連線參數

| 欄位 | 預設 | 用途 |
|---|---|---|
| 連線模式 Mode | `connect` | Collector 主動連到 HF811 TCP Server；`listen` 則等待 HF811 TCP Client 連入，需另外考慮防火牆 |
| Scanner IP / Host | `127.0.0.1` | 本機模擬專用，接實機要換成實際 HF811 IP |
| TCP Port | `55256` | 雙方要設定成相同的 TCP Port |
| 設備代號 DeviceId | `SMT-LINE-01-HF811` | Collector 的自訂來源標籤，**不是硬體序號** |

## 3. 「資料格式與儲存」參數（你畫面上的七項）

**這些是 Collector 本身的設定，不是 Honeywell 設備內部設定。**

| GUI 欄位 | 預設值 | 作用／建議 |
|---|---|---|
| **資料分隔符 Framing** | `lf` | 每收到 LF（0A）視為一筆結束。還可選 `crlf`（0D 0A）、`cr`（0D）、`idle`（依空閒時間推測）。**以實際 HF811 輸出為準** |
| **重連間隔 ReconnectMs** | `1000 ms` | 連線失敗後約每秒重試一次；一般保留預設 |
| **去重時間 DedupWindowMs** | `2000 ms` | 同條碼在時間窗內重複掃描，標記 `is_duplicate`；仍會保存到 SQLite。這**不是** MES 過站防重 |
| **Idle Gap (IdleGapMs)** | `100 ms` | **僅 Framing=idle 時有效**；接收停止一段時間後推測分筆，不保證每筆一定正確 |
| **NOREAD 值 NoReadValue** | `NOREAD` | 當收到這個文字時標記 `is_no_read`；設備若未輸出資料，Collector 不能自行判斷讀碼失敗 |
| **本機 API Port ApiPort** | `8088` | 提供本機查詢，**不是公司伺服器的接收 Port** |
| **SQLite 路徑 DatabasePath** | 空白 | 留空使用預設資料庫 `%LOCALAPPDATA%\HF811Collector\data\scans.sqlite3`；除非有明確維運需求，建議留白 |

Collector 啟動後，在**同一台電腦**可開啟：

- `http://127.0.0.1:8088/health` — 健康檢查
- `http://127.0.0.1:8088/stats` — 掃描統計
- `http://127.0.0.1:8088/events?limit=20` — 最近 20 筆事件

日誌在 `%LOCALAPPDATA%\HF811Collector\logs`。即使選擇「所有使用者安裝」，每個登入 Windows 的帳號仍有獨立的設定、SQLite 和 API Key，不會自動共享掃描資料。

**儲存設定後，請停止並重新啟動 Collector，新的 TCP／同步參數才會生效。**

## 4. 「伺服器同步」：PHP／Python 都可以

資料流程：

`HF811 → C# Collector → 本機 SQLite Pending → HTTPS POST → FastAPI / PHP → MySQL / SQLite → ACK → 本機 Synced`

| 公司技術 | 接收端 | 文件 |
|---|---|---|
| Python | [FastAPI + SQLAlchemy](server-python/) | [PHP／Python 部署指南](docs/RECEIVER_PHP_PYTHON.md) |
| PHP | [PHP 8.1+ PDO](server-php/) | [PHP／Python 部署指南](docs/RECEIVER_PHP_PYTHON.md) |
| .NET 參考實作 | [ASP.NET Core](server/) | [同步協定與驗收](docs/SERVER_SYNC.md) |

在 GUI「伺服器同步」設定：

| 欄位 | 說明 |
|---|---|
| 啟用背景批次同步 | **先部署測試用接收端，再開啟** |
| Collector ID | 每台產線筆電唯一，例如 `SMT-LINE01-PC01` |
| 接收端 API URL | `https://公司內網網域/api/v1/collector/scans/batch`，換成公司正式 HTTPS FQDN |
| API Key | 與 Receiver 一致；Windows DPAPI 加密保存，不寫入 `config.json` |
| 同步間隔 | 預設 5 秒 |
| 每批最大筆數 | 預設 50 筆（最多 100） |

依序 **儲存設定 → 測試伺服器連線 → 啟動 Collector**。新資料先寫本機 SQLite；上傳後只有伺服器寫入成功並回 ACK 才標記 Synced。斷網時先保留 Pending，恢復後使用同一 event_id 補傳。

**首次啟用時會連先前的 Pending 模擬掃描紀錄一起補傳。** 切到正式環境前請備份／隔離測試 SQLite，絕對不要直接對正式 MES 進行盲目補傳。跨電腦同步必須使用受信任的 HTTPS，不可明文傳送 API Key 和條碼。

## 5. 常見問題

| 現象 | 先檢查什麼 |
|---|---|
| `127.0.0.1:55256` 拒絕連線 | 本機模擬器有沒有啟動；連實機是否已改 Host |
| 掃描器搜尋不到 | 網卡 IP、網段、設備的 TCP 模式／Port、有無網管許可 |
| 已連線卻沒事件 | 條碼尾碼是否與 Framing 相符、設備是否真的輸出 |
| 標記 DUPLICATE | 是短時間重複掃描標記，原始紀錄仍保留 |
| `127.0.0.1:8088` 無法查詢 | Collector 是否啟動、ApiPort 是否被占用 |
| Pending 不斷增加 | 檢查伺服器 URL、TLS 憑證、API Key、Receiver 的日誌及 ACK |
| 關閉視窗後程式還在 | 預期行為：已隱藏到系統匣 |
| 沒有登入 Windows 就不收碼 | GUI Tray App **不是 Windows Service**，需要登入使用者 |

## 6. 開發／建置／部署

開發機安裝 **.NET 10 SDK + Inno Setup 6/7**，產線筆電不需這些工具。

```powershell
git clone https://github.com/postmanjhan-star/data-collector.git
cd data-collector
.\build.ps1
```

成功後在 `dist\installer` 找到 `HF811Collector-win-x64-Setup.exe` 與 SHA-256 校驗檔，也可以直接透過 [GitHub Actions](https://github.com/postmanjhan-star/data-collector/actions/workflows/build-windows.yml) 下載編譯 Artifact。

上線前：先停止 Collector、備份 `%LOCALAPPDATA%\HF811Collector`，再更新。需由 IT 確認 Windows 10 版本／權限／公司安全政策與防火牆。完成實體 HF811 收碼、分包、斷網重連、SQLite 備份、HTTP ACK 和過站業務規則驗收後再投入正式使用。若需要沒有使用者登入也持續收碼，後續應改 Windows Service。

更多：[FastAPI／PHP Server Sync 部署](docs/RECEIVER_PHP_PYTHON.md) · [同步協定與驗收](docs/SERVER_SYNC.md) · [舊版 README 備份](docs/README_HISTORY.md)

## 參考

- [.NET 10 Windows 支援範圍](https://learn.microsoft.com/dotnet/core/install/windows)
- [Inno Setup](https://jrsoftware.org/isinfo.php)
