# HF811 Server Sync：公司使用 Python FastAPI 或 PHP

這份文件是 **v1.5 C# Collector** 的接收端部署指南。**Windows Collector 不需要改動，也不需要在產線電腦安裝 Python 或 PHP**。

```text
Honeywell HF811 --TCP--> Windows 10 C# Collector --SQLite (offline queue)
                                               |
                                    HTTPS + X-Collector-Key
                                    POST /api/v1/collector/scans/batch
                                               |
                          +--------------------+--------------------+
                          |                                         |
                Python FastAPI                            PHP 8.1+ PDO
            (SQLAlchemy / MySQL)                       (PDO / MySQL)
                          |                                         |
                          +--------------------+--------------------+
                                               |
                                    scan_events (event_id PK)
                                               |
                                       durable ACK to C#
```

兩個接收端皆具備：

- `GET /health`：健康檢查。
- `GET /api/v1/collector/stats`：需 `X-Collector-Key`，回 `{"total":0,"collectors":0}`。
- `POST /api/v1/collector/scans/batch`：需 `X-Collector-Key`，每批 1–100 筆，提交 DB 後回 `acknowledged_event_ids` 與 `rejected`。
- 對 `event_id` 建立唯一主鍵。同 ID、相同內容重送會重新 ACK；同 ID、不同內容會拒收，不會重複插入。
- 以上事件只是**設備原始掃碼事件**，不是工單／PCB 過站成功的判定。

> API Key 是這版的單一共用金鑰，正式大量部署時應改成每台 Collector 的獨立憑證、撤銷與稽核策略。伺服器需透過公司核准的 HTTPS 反向代理提供連線，**切勿直接在產線網路以明文 HTTP 傳送 API Key 與條碼**。

## 方案 A：Python FastAPI（推薦）

目錄：[`server-python/`](../server-python/)。

需求：Python 3.10+；正式部署建議搭配現有 Docker、Nginx/Caddy 和公司 MySQL。

### 用 SQLite 做本機測試

在伺服器測試環境／開發電腦：

```bash
python -m venv .venv
source .venv/bin/activate
pip install -r server-python/requirements.txt
export HF811_SERVER_API_KEY="$(openssl rand -hex 32)"
export HF811_DATABASE_URL="sqlite:///./data/receiver.sqlite3"
python -m uvicorn app:app --app-dir server-python --host 127.0.0.1 --port 8090
```

在 Windows PowerShell，請改用 `.venv\\Scripts\\Activate.ps1` 和 `$env:...` 設定環境變數；上面是 Linux 伺服器的指令。請保存產生的 API Key，讓測試 Collector 輸入**完全相同的金鑰**。正式不要使用文件內的範例金鑰。

健康檢查：`http://127.0.0.1:8090/health`。

### 接公司 MySQL

先由 DB 管理人員建立專用資料庫及受限服務帳號。將環境變數設為：

```bash
export HF811_DATABASE_URL='mysql+pymysql://hf811_user:URL_ENCODED_PASSWORD@mysql.internal:3306/hf811_scans?charset=utf8mb4'
```

SQLAlchemy 將初始化 `scan_events` 表；正式環境應審核 DDL、備份與授權。密碼含 `@`、`:`、`/`、`#` 等字元需 URL-encode。不要把帳密或 API Key 放進 Git。

### Docker 版本

```bash
export HF811_SERVER_API_KEY="$(openssl rand -hex 32)"
docker compose -f docker-compose.receiver-python.yml up -d --build
```

預設使用獨立 SQLite Docker Volume，僅將本機 `127.0.0.1:8090` 暴露給反向代理。需要 MySQL 時，應按公司部署方式改注入 `HF811_DATABASE_URL` 並使用可連線的 MySQL 主機；如果 Caddy 在另一個容器，兩者需要共用受控 Docker 網路，不能把 Caddy 容器內的 localhost 當作 Receiver。

## 方案 B：PHP 8.1+（現有 PHP / Apache / Nginx 系統）

目錄：[`server-php/`](../server-php/)。不需要 Composer；只用 PHP 標準函式與 PDO MySQL 或 PDO SQLite。

### 本機測試

```bash
export HF811_SERVER_API_KEY="$(openssl rand -hex 32)"
export HF811_PDO_DSN="sqlite:$(pwd)/server-php/data/receiver.sqlite3"
php -S 127.0.0.1:8090 -t server-php/public server-php/router.php
```

`php -S` 僅用於開發測試；**不要**用 PHP 內建開發伺服器作為正式產線服務。

### 正式接 MySQL

伺服器必須啟用 `pdo_mysql`，並為服務帳號設定：

```bash
export HF811_PDO_DSN='mysql:host=mysql.internal;port=3306;dbname=hf811_scans;charset=utf8mb4'
export HF811_DB_USER='hf811_user'
export HF811_DB_PASSWORD='來自公司秘密管理的密碼'
export HF811_SERVER_API_KEY='來自公司秘密管理的金鑰'
```

PHP 首次處理請求會透過 `CREATE TABLE IF NOT EXISTS` 建立 `scan_events`（需適當資料表建立權限）。正式環境建議先由 DBA 審核並初始化表格，再按公司政策設定最小權限；請特別檢查你們既有 MySQL 的角色權限。此接收端不會修改 MES 現有 production 表。

### Apache / Nginx 路由

Apache：將此接收端的 DocumentRoot（或獨立虛擬主機）指向 `server-php/public`，啟用 mod_rewrite 並允許該目錄的 rewrite 規則，Repository 內的 `server-php/public/.htaccess` 會導向 `index.php`。

Nginx + PHP-FPM：以受控的 location 把下列路徑轉給 `server-php/public/index.php`，保留原始 `REQUEST_URI`：

```text
/health
/api/v1/collector/stats
/api/v1/collector/scans/batch
```

請使用正式站點的 PHP-FPM、TLS 憑證及認證設定；不要讓整個 Repository 成為公開的 Web Root。如果要嵌入既有 Laravel／Symfony／原生 PHP 專案，可沿用 `index.php` 的驗證與寫入邏輯，將三個 Endpoint 註冊到原框架的 Router。

## Windows Collector 設定（兩種伺服器完全相同）

安裝原本 **HF811 Collector v1.5** 後，開 GUI → **伺服器同步**：

| 欄位 | 範例 |
|---|---|
| 啟用背景批次同步 | 勾選 |
| Collector ID | `SMT-LINE01-PC01`（每台電腦要不同） |
| Server URL | `https://hf811.internal.example/api/v1/collector/scans/batch` |
| API Key | 和後端 `HF811_SERVER_API_KEY` 相同 |
| 同步間隔 | 5 秒 |
| 批次最大筆數 | 50 |

以上網域只是示例，必須換成公司實際的 **HTTPS FQDN**。只有與 Collector 在**同一台電腦**做測試，才可使用 `http://127.0.0.1:8090/api/v1/collector/scans/batch`。Windows 端不允許對內網 IP 使用明文 HTTP，以免洩漏金鑰。

依序「儲存設定」→「測試伺服器連線」→「啟動 Collector」→ 使用模擬器或實體 HF811 掃碼。觀察：

1. 新事件先出現在 SQLite、Pending 增加。
2. Receiver 成功寫入後回 ACK，Synced 增加、Pending 減少。
3. 關閉 Receiver 時仍可存入本機 SQLite；恢復時能以相同 `event_id` 補傳。
4. 測試過的 SQLite 可能已有很多舊版模擬條碼：首次啟用同步前先**備份並隔離測試資料**，避免送進正式資料庫。

## 選擇建議與驗收

若現場已有 Python FastAPI + MySQL 服務，優先採用 `server-python`；你可以把 Endpoint 合併進現有 FastAPI App，而不是再部署第二台服務。若既有伺服器由 PHP/Nginx 管理、不方便新增 Python 容器，則使用 `server-php`，由現有 PHP-FPM 提供同樣功能。

CI 以真實 C# `SyncAgent` 對兩個後端執行：離線 Pending、成功 ACK、相同 UUID 重傳不重複入庫，以及無憑證 401；Python 另測部分資料拒收、與重啟後持久化。參考 [GitHub Actions 驗證紀錄](https://github.com/postmanjhan-star/data-collector/actions/workflows/receiver-compat.yml)。

**限制**：尚未接你們公司的實際 PHP/Python 專案、MySQL 生產資料庫、網域憑證或 SMT 過站判定，也尚未完成 Windows 10 + HF811 現場壓力測試。請先部署到測試環境，確認備份、容量、權限、網路及資料保留策略。
