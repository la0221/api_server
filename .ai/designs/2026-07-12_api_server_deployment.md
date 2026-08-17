---
date: 2026-07-12
type: design
project: AIVision（.NET8 WPF 產線檢測 App）
title: API Server 部署規劃
status: proposal（規劃，未實作）
tags: [部署, deployment, API, edge, server, DB, 資料同步, 安全]
---

# 設計書：AIVision API Server 部署規劃

> 目標：預先規劃「未來有 API server 時」的部署架構，界定 edge（產線機台）vs server（中央）的職責切分、要先解的阻礙、資料層、部署方式、分階段落地。
> 本文件只做規劃，不含實作 code。

---

## 0. 一句話

**確定用途（2026-07-12 使用者拍板）：API server = 中央推論伺服器 ＋ 模型發佈/管理中樞。**
即：模型集中放 server → server 用 GPU 跑雙 head ONNX 推論 → 各產線 edge 送影像來推論、並可拉模型版本當**離線後援**。
**鐵律不變：相機/PLC/氣吹決策留 edge；server 掛掉要能自動降級用本機模型，不可停線。**
好消息：程式碼已有 HTTP 推論適配器（`AinaviAiInferencePort`/`SwitchableAiInferencePort`），中央推論≈重用它、指向自建端點。

---

## 1. 核心原則：Edge vs Server 切分

產線檢測系統的鐵律——**推論要低延遲又綁硬體，不能放遠端**。所以切法固定：

| 能力 | 放哪 | 為什麼 |
|---|---|---|
| 相機取像、PLC 握手/吹氣、光源控制 | **Edge（產線機台）** | 綁實體硬體、毫秒級節拍，網路一抖就停線 |
| **即時核對決策（三態 / fail-closed 氣吹）** | **Edge（永遠）** | 安全關鍵，斷網也必須能自主放行/剔除 |
| **雙 head ONNX 推論** | **Server 為主 + Edge 後援** | ★本案重點：正常走中央 GPU 推論；**斷線自動降級用本機 ONNX**，不停線 |
| 本機模型快取（後援用） | **Edge** | server 不可達時的 fallback，來源＝從 server 拉的版本 |
| **模型版本登錄/發佈/下發** | **Server（中樞）** | ★本案重點：一處更新、多台 edge 拉同步 |
| 工單建立/查詢、歷史彙整、良率統計 | **Server**（次要） | 跨多台產線集中、報表 |
| 使用者/權限、稽核紀錄 | **Server** | 集中治理 |

> **關鍵心法**：Edge 必須「斷網也能獨立完成檢測」，Server 只是讓資料集中、管理變方便。**Server 掛掉不可以讓產線停。**

---

## 2. 目標拓撲

```
┌─────────────────── 產線機台 A（Edge） ───────────────────┐
│  WPF App（現況主體）                                      │
│   ├─ 相機(IDS) / PLC(Modbus) / 光源     ← 硬體，永遠在這   │
│   ├─ 取像 → 送影像去 server 推論          ← 正常路徑        │
│   ├─ 【後援】本機 ONNX 雙 head            ← server 斷線時降級│
│   ├─ 三態核對 + fail-closed 氣吹          ← 安全決策，永遠在這│
│   └─ 本機模型快取 + SQLite 離線緩衝                         │
│         │ ①送影像 /infer  ②回讀值+信心   │ ③拉最新模型版本  │
└─────────┼───────────────────────────────┼────────────────┘
          │  HTTPS（低延遲，同網段）         │  HTTPS（拉模型）
          ▼                                ▼
┌─────────────────── 中央 API Server（GPU） ───────────────┐
│  AIVision.Api（ASP.NET Core Kestrel）                    │
│   ├─ ★推論端點 POST /api/infer/pair                       │
│   │     → 跑雙 head warpPolar ONNX（GPU 加速）→ 回 M??/??  │
│   ├─ ★模型登錄/發佈 API（版本、下發、names/report）        │
│   ├─ （次要）工單/歷史彙整、良率報表 + Swagger             │
│   └─ 決策不在這（只回讀值+信心，edge 自己判定）             │
│         │                                                │
│         ▼  模型倉庫（pairs/v6.7.x onnx+names+report）      │
│         ▼  DB（PostgreSQL / SQL Server）                  │
└──────────────────────────────────────────────────────────┘
     ▲              ▲
 產線機台 B      產線機台 C …（多台共用一個中央推論/模型 server）
```

---

## 2.5 ★重點一：中央推論伺服器設計

**核心：edge 送影像 → server 跑雙 head ONNX → 回讀值+信心；edge 自己做三態決策。**

- **重用既有適配器**：`AinaviAiInferencePort`（HTTP multipart 上傳圖片）+ `SwitchableAiInferencePort` 已存在，只要新增一條「雙 head 版」端點 `POST /api/infer/pair`，回傳 `{mohao, xuehao, confMohao, confXuehao}`。Server 端把 `AIVision.MoldCode.Onnx` 的 `WarpPolarTwoHeadRecognizer` 掛進去跑。
- **GPU 加速**：Server 用 `Microsoft.ML.OnnxRuntime.Gpu`（CUDA）取代 CPU 版，吞吐才夠多線共用。需 NVIDIA GPU + CUDA/cuDNN。
- **前處理放哪**：warpPolar/Hough/ROI 建議**在 server 端做**（送原圖上去），確保多台 edge 前處理一致、且好集中調參；但送原圖較耗頻寬。折衷見「多幀策略」。
- **多幀策略（關鍵）**：現況 edge 端多幀投票（最多 7 幀）。網路推論不宜每顆送 7 幀：
  - 方案①：edge 送 **1 幀**，server 推論回值，edge 視信心決定要不要再送（自適應）。
  - 方案②：edge 送 **N 幀一次批次**，server 端投票後回單一結果。
  - → 依頻寬/節拍取捨（見 §10 決策）。
- **⚠️ 斷線降級（不可省）**：server 不可達 → edge **自動 fallback 用本機 ONNX**（現況本機路徑正好可當後援）。需要一個「推論來源選擇器」：優先 server、逾時/失敗即切本機，並記錄降級事件。
- **⚠️ 決策留 edge**：server 只回讀值+信心，**三態判定（`MoldCodePairVerifier`）＋ fail-closed 氣吹（PLC）永遠在 edge**。斷網照樣能自主放行/剔除。
- **延遲預算**：可行性取決於「每顆節拍」vs「網路來回+推論時間」。→ **動工前必須量測**：單張 server 推論延遲（含網路）要遠小於節拍。同網段 + GPU 通常可壓到數十 ms。

## 2.6 ★重點二：模型發佈/管理中樞設計

**核心：模型集中放 server，一處更新，多台 edge 拉同步；並與中央推論共用同一份模型倉庫。**

- **模型倉庫**：server 存 `pairs/<版本>/{mohao,xuehao}.onnx` + `.names.json` + `.report.json`（沿用現有三件套格式，`OnnxModelDiscoveryService` 介面已定義）。
- **發佈 API**：
  - `GET /api/models`（列版本、標記 latest/stable）
  - `GET /api/models/{version}/download?head=mohao|xuehao`（edge 拉檔）
  - `POST /api/models`（上架新版本；工程師/廠商權限）
- **Edge 同步**：edge 啟動/定時檢查 server 有無新 stable 版本 → 拉到本機快取 → 作為「後援模型」與「顯示版本一致性」。中央推論也用同一版本，確保 server/edge fallback 讀值一致。
- **版本治理**：標記 `latest` / `stable` / `deprecated`；記錄每版 report（準確率）；避免 edge 各自版本漂移（現況「兩份 V6.7.1 mohao」正是漂移案例，集中後可解）。
- **與訓練銜接**（未來）：訓練產出 → 上架到此中樞 → 自動下發，串起「訓練→發佈→推論」全鏈（呼應雙 head 訓練另案）。

## 3. ⚠️ 上 server 前要先補的三個地基（現況阻礙）

| # | 現況 | 問題 | 對策 |
|---|------|------|------|
| 1 | `AIVision.Api.csproj` 是 **`net8.0-windows`**；且中央推論要引用 `AIVision.MoldCode.Onnx`（OpenCvSharp4.runtime.**win** + OnnxRuntime）| 若走 GPU 推論，**Windows Server + NVIDIA GPU** 反而是最順路徑（OpenCvSharp win 版可用）；但相機/PLC 原生 DLL 仍不該進 server | 中央推論 server 選 **Windows + GPU**；把硬體 adapter（相機/PLC）拆成 edge-only 模組、不進 server 相依；未來要 Linux 化再評估 OpenCvSharp Linux 版 |
| 2 | API 資料層是 **In-Memory**（重啟即失、不跨進程）；SQLite 只在 WPF、且是 `%LocalAppData%` 每機一份 | Server 需要**持久化 + 多台共享**，SQLite 本機路徑不適合當中央庫 | Server 換 **PostgreSQL 或 SQL Server**；Edge 保留 SQLite 當離線快取；資料存取層已用 Dapper，抽換 DB 方言成本可控 |
| 3 | `appsettings.json` 有明文 `ApiKey: "demo-secret"`；`LogPath` 是相對路徑 | 機密外洩、多實例寫檔衝突 | 機密移到**環境變數/Secrets Manager**；日誌改結構化日誌（Serilog）或集中式，勿寫相對檔 |

---

## 4. API Server 的部署方式（三選項，含 Windows 限制權衡）

因為現況是 `net8.0-windows`，短期最省力是留在 Windows；要 Linux/Docker 需先做 §3#1 的拆分。

| 選項 | 適用 | 優點 | 代價 |
|---|---|---|---|
| **A. Windows Service + Kestrel**（推薦起步） | 先不動框架，快速上線 | 不用改 `net8.0-windows`；`sc create` 或 `WindowsServiceLifetime` 即可；一台 Windows Server 跑 | 只能 Windows；水平擴充較笨重 |
| **B. IIS + ASP.NET Core Module** | 廠內已有 IIS/Windows 維運 | 熟悉的維運模式、反向代理/TLS 現成 | 綁 IIS；仍 Windows |
| **C. Docker（Linux）** | 要雲原生/多副本/K8s | 可攜、易水平擴充、CI/CD 友善 | **必須先做 §3#1**（拆硬體相依、改純 net8.0）；工程量最大 |

> 建議路線：**先 A（Windows Service）快速讓中央資料/管理跑起來 → 需要規模化再演進到 C（容器）**。

---

## 5. 資料層策略（最需要設計的一塊）

- **中央庫**：PostgreSQL（跨平台、免授權）或 SQL Server（廠內若已有）。Schema 沿用現有三表（WorkOrders / Inspections / Defects），加「產線/機台 ID」欄位以區分來源。
- **Edge 快取 + 補傳**：Edge 仍寫本機 SQLite，檢測結果**非同步上拋**到 server；斷線時排隊、復線後補傳（outbox pattern）。→ 保證「斷網不停線、資料不遺失」。
- **時間戳**：全部用 UTC（現況 Inspection 已用 `DateTime.UtcNow`），server 端再轉當地時區顯示。
- **識別碼**：工單/檢測用 GUID（現況已是），天生適合多來源合併、不會撞號。
- **遷移**：現況是啟動時 `CREATE TABLE IF NOT EXISTS` + 手刻 `ALTER`。上 server 建議引入正式 migration 工具（EF Core Migrations 或 FluentMigrator），避免多實例各自建表。

---

## 6. Edge ↔ Server 介面（API 契約要先定）

Server 需要補齊目前缺的 endpoint（現在只有 ainavi/predict、inspection/cycle）：

- `POST /api/inspections`（批次上拋 edge 檢測結果）
- `GET /api/workorders?line=&status=`（edge 拉派工）
- `PATCH /api/workorders/{id}`（狀態回報）
- `GET /api/models/latest?head=mohao|xuehao`（模型版本查詢/下發）
- `GET /api/stats/yield?...`（報表/dashboard）

原則：**Edge 主動 pull 工單 / push 結果**（server 不主動連 edge，避免產線網路被外部打進來）。

---

## 7. 安全

- 全程 **HTTPS/TLS**（廠內自簽 CA 或內部憑證）。
- Edge↔Server 認證：**API Key 或 mTLS**（比目前明文 demo-secret 強）；金鑰進環境變數。
- 網路分段：產線網段與辦公/對外網段隔離；server 放 DMZ，edge 只允許對 server 單向出站。
- 稽核：關鍵動作（換模型、改工單、放行/剔除）留稽核日誌。

---

## 8. 分階段落地

| 階段 | 目標 | 產出 |
|---|---|---|
| **P0 決策+量測** | 拍板 §10（尤其節拍/GPU/降級策略）；**量測單張 server 推論延遲**驗可行性 | 可行性結論 |
| **P1 中央推論端點** | API 加 `POST /api/infer/pair`，掛 `WarpPolarTwoHeadRecognizer`（GPU）；edge 用既有 HTTP 適配器指向它 | 一台 edge 可走中央推論，讀值正確 |
| **P2 斷線降級** | edge「推論來源選擇器」：優先 server、逾時切本機 fallback；記錄降級 | server 掛不停線 |
| **P3 模型中樞** | server 模型倉庫 + 發佈/下載 API；edge 拉 stable 版當後援 | 一處更新、多台同步、版本不漂移 |
| **P4 多線 + 資料集中（次要）** | 多台 edge 接入；檢測結果上拋中央 DB；良率報表 | 集中觀測 |
| **P5 規模化（可選）** | 依負載加 GPU/多副本；CI/CD | 擴充 |

> 每階段仍守既有規則：先測試、有動 UX 寫直觀度評估（見 `EVAL_HANDBOOK.md`）。

---

## 9. 不做什麼（範圍界線）
- **不把即時檢測搬上 server**（延遲/安全/斷網風險）。
- 不動 edge 的相機/PLC/ONNX 既有路徑。
- P1–P3 不需要容器化（Windows Service 即可）。

---

## 10. 待你拍板的關鍵決策

> 用途已定：**中央推論 + 模型發佈中樞**。以下是接著決定可行性/設計的問題。

1. **【可行性・最關鍵】產線節拍是多少？**（每顆鏡片可用的檢測時間）→ 決定「網路推論來得及嗎」，以及多幀策略選①(送1幀)還是②(批次)。單張 server 推論延遲需遠小於節拍。
2. **【硬體】server 有 GPU 嗎？**（NVIDIA + CUDA）→ 沒 GPU 用 CPU 推論，多線共用吞吐可能不夠。
3. **【安全策略】server 斷線時**：edge 自動降級用本機模型繼續跑（推薦），還是寧可停線等 server？
4. **【前處理位置】** warpPolar/ROI 在 server 做（送原圖、集中一致）還是 edge 做（送裁好小圖、省頻寬）？
5. **【規模】** 幾條產線/機台接同一個 server？→ 決定 GPU 數量與是否要多副本。
6. **【部署環境】** 中央推論 server 用 Windows Server + GPU（最順）確認可行？廠內網路能讓 edge 低延遲連到它嗎？

---

## 11. 檔案地標
- API 專案：`AIVision.Api\`（`Program.cs`、`Controllers\{AinaviController,InspectionController}.cs`、`appsettings.json`、`Properties\launchSettings.json`）
- 共用 DI：`AIVision.Infrastructure\DependencyInjection\ServiceCollectionExtensions.cs`
- 資料層：`AIVision.Infrastructure`（Dapper + `SqliteDatabaseConnectionFactory.cs`，三表 schema）
- Edge 主體：`AIVision.Presentation.Wpf\App.xaml.cs`（硬體/ONNX/SQLite 註冊）
- 線上推論轉發：`AIVision.Infrastructure\AiService\AinaviAiInferencePort.cs`
- 本機 ONNX（不上 server）：`AIVision.MoldCode.Onnx\`
- 現有 WPF 發佈設定：`AIVision.Presentation.Wpf\Properties\PublishProfiles\win-x64-portable.pubxml`
