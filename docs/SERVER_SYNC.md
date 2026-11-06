# HF811 Collector v1.5 — Server Sync 部署與驗收

> 適用：Windows 10 x64 HF811 Collector（WinForms GUI）與公司內網測試接收端。
> 這是**原始掃描事件同步**，不會自動判定 SMT 工令或 PCB 過站成功。

## 1. 架構與保證

```text
Honeywell HF811 TCP -> Windows Collector -> SQLite scans (durable)
                                           |
                                           | background SyncAgent
                                           | HTTPS POST /api/v1/collector/scans/batch
                                           v
                                   Receiver API (.NET 10)
                                           |
                                           v
                                   SQLite scan_events (UNIQUE event_id)
                                           |
                                           v
                                      server ACK
                                           |
                                           v
                                   local scans -> synced
```

- 先寫本機 SQLite，伺服器斷線時**不刪除、不漏傳已存入資料庫的事件**。
- 升級 v1.4 SQLite 時採**增量 migration**：替既有資料補上永久 `event_id`，保留原有掃描時間與所有欄位，不清空舊資料。
- **重要：首次啟用 Sync 會把舊 SQLite 中所有 Pending 紀錄一起補傳，包括 v1.4 模擬器產生的條碼。** 正式環境上線前請先確認使用的是正確資料庫；如須隔離歷史測試資料，應先備份原資料並改用新 SQLite 路徑，不要刪除尚需追溯的原始紀錄。
- 每筆事件包含固定 UUID、設備標籤、條碼、UTC 時間、原始 HEX、來源、重複標記、NOREAD。
- 上傳最多 100 筆／批（預設 50），正常每 5 秒嘗試；若網路恢復後仍有滿批積壓，則以最短約 150ms 間隔逐批補傳；失敗時指數延後重試最多 300 秒。
- 只有伺服器提交 DB 後回 `acknowledged_event_ids`，Collector 才把對應事件設為 `synced`。
- 伺服器對 `event_id` 設唯一鍵；同 ID、同內容重傳重新回 ACK，但不重複寫入；同 ID、不同內容回 rejected。
- 錯誤或缺少 ACK 時，事件保持 `pending`，下次重傳同個 ID。永久拒收事件設 `rejected`、留下錯誤，需人工處理，不會被靜默刪除。
- 此設計為**at-least-once 傳輸 + server-side idempotency**，不是宣稱網路 exactly-once，也不等於 MES 過站成功。

## 2. 本機一台電腦測試（先不碰正式產線伺服器）

安裝 .NET 10 SDK 的測試電腦上，在 Repository 目錄開 PowerShell。

```powershell
# 此值僅供本機測試，真實環境請使用獨立、足夠強度的隨機金鑰
$env:HF811_SERVER_API_KEY = "local-demo-only-change-to-a-random-key"
$env:HF811_RECEIVER_DB = Join-Path $env:TEMP "hf811-demo-receiver.sqlite3"
dotnet run --project .\server\HF811Receiver.csproj --urls http://127.0.0.1:8090
```

在瀏覽器開 `http://127.0.0.1:8090/health`，應看到 `status: ok`。

安裝 v1.5 Collector（有自包含 .NET Runtime，產線電腦不用安裝 SDK），在 GUI **「伺服器同步」** 分頁填：

| 設定 | 本機示範 |
|---|---|
| 啟用自動批次同步 | 勾選 |
| Collector ID | `SMT-LINE01-PC01`（每台筆電唯一） |
| API URL | `http://127.0.0.1:8090/api/v1/collector/scans/batch` |
| API Key | 與上面環境變數相同（儲存在目前 Windows 使用者的 DPAPI） |
| 同步間隔 | `5` 秒 |
| 每批筆數 | `50` |

點 **儲存設定**，再按 **啟動 Collector**（先用本機模擬器產生事件即可）。觀察待上傳數短暫增加，成功 ACK 後已同步數增加。也可按 **立即同步**。API 金鑰不會寫在 config.json 裡。

**正式部署 URL 一定要改成 HTTPS**。HTTP 僅允許 `localhost`／`127.0.0.1` 本機測試，避免明文傳輸條碼與 API Key。

## 3. 公司內網伺服器 Docker 部署

本 Repository 內含：

- `server/HF811Receiver.csproj` — ASP.NET Core 10 Minimal API
- `server/Dockerfile` — SDK 建置、ASP.NET runtime、非 root 使用者
- `docker-compose.receiver.yml` — 容器資料卷、重啟策略、綁定 host loopback 的 8090 Port

在伺服器上使用核准的 Secret 機制注入 `HF811_SERVER_API_KEY`，再：

```bash
docker compose -f docker-compose.receiver.yml up -d --build
docker compose -f docker-compose.receiver.yml ps
```

Compose 將接收端綁在伺服器本機 `127.0.0.1:8090`，**不直接讓產線筆電透過內網 HTTP 連上它**。請由既有 Caddy/Nginx/公司反向代理，在核准的內網 DNS/FQDN 對此服務提供受信任的 HTTPS，並限制存取來源。

例如 Caddy 的目的服務是 `reverse_proxy 127.0.0.1:8090`，實際網域與內網憑證依公司部署決定。正式客戶端填 `https://<內網網域>/api/v1/collector/scans/batch`。不得關閉 TLS 憑證驗證，也不應公開暴露到 Internet。

接收端 SQLite 預設存在 Docker Volume `hf811_receiver_data`，需做定期備份與磁碟空間告警。正式大量產線與高可用建議改為 MES 管理的 MySQL/PostgreSQL 持久層；此版本接收端是**可運作的單節點參考實作**，不是完整 MES。

## 4. API 範例

```http
POST /api/v1/collector/scans/batch
X-Collector-Key: <shared-key>
Content-Type: application/json
```

```json
{
  "collector_id": "SMT-LINE01-PC01",
  "events": [
    {
      "event_id": "2149c1ae-43ce-431e-bcff-7e52dc3ea638",
      "device_id": "SMT-LINE-01-HF811",
      "barcode": "PCB000001",
      "received_at_utc": "2026-11-06T08:00:00.000+00:00",
      "raw_hex": "504342303030303031",
      "source_address": "192.168.16.50:55256",
      "is_duplicate": false,
      "is_no_read": false
    }
  ]
}
```

成功回應：

```json
{
  "acknowledged_event_ids": ["2149c1ae-43ce-431e-bcff-7e52dc3ea638"],
  "rejected": []
}
```

- `GET /health`：基本服務健康狀態，沒有鑑權；應僅透過受限的內網反向代理暴露。
- `GET /api/v1/collector/stats`：需 `X-Collector-Key`，顯示已收事件數及 Collector 數。
- `POST /api/v1/collector/scans/batch`：需要相同 Key；單次最多 100 個 events。
- API Key 是本版接收端**共用金鑰**。正式多站點應改成每台機器獨立憑證／token、撤銷、稽核與權限管控。

## 5. 可靠性與排錯

| 情況 | 預期行為 |
|---|---|
| MES/Receiver 暫時斷線 | Scanner 仍繼續寫本機 SQLite，Pending 增加 |
| API 回 HTTP 500/401 | 不 ACK；保留 Pending，將錯誤與重試次數寫 SQLite |
| 伺服器已寫入、但 ACK 在網路中遺失 | 原 event_id 重傳，伺服器不重複入庫，重新 ACK |
| 部分事件資料不合法 | 該筆 rejected，其他有效事件可 ACK；拒收資料仍保留本機 |
| Collector 重新啟動 | 從 SQLite 讀取相同 event_id，續傳 Pending |
| 修改 API URL/Key | 儲存後停止並重新啟動 Collector；手動立即同步使用當次啟動設定 |
| Windows 使用者切換 | 設定與 DPAPI 金鑰是**每個 Windows 帳號獨立**，全部人安裝 ≠ 共用同步身分 |

GUI「測試伺服器連線」使用 API Key 呼叫 `/api/v1/collector/stats`，可驗證 HTTPS 連線與認證；但實際事件 ACK 仍需在掃描後檢查。若無法同步，確認 Key、URL、可信 TLS 憑證、網段與防火牆。

此版不會清理本機的 Synced 歷史資料；正式大流量導入前需規畫保留年限、SQLite 備份/歸檔與磁碟告警。

## 6. CI 驗證

GitHub Actions（Windows）執行以下項目：

1. CLI/GUI self-contained win-x64 建置、條碼與 TCP 測試；
2. 從 v1.4 掃描表 Migration + UUID 穩定性／ACK／Rejected／Pending 測試；
3. 啟動真正的 ASP.NET Receiver，讓 Collector 以 HTTP loopback 上傳測試條碼；
4. 同一個 event_id 再次上傳，驗證伺服器只有一筆；未認證要求應被拒絕；
5. Inno Setup 產生 Windows 安裝檔，測試目前使用者／全部使用者兩種安裝及解除安裝。

CI 不是 Windows 10/HF811 實機驗收。請在產線筆電上再測網路故障、電腦重啟、數日連續掃描、資料庫備份及 MES 端工單規則。
