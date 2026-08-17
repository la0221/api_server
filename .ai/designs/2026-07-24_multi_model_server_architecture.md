---
date: 2026-07-24
type: design
project: AIVision（.NET8 WPF 產線檢測 App）
title: 多站點 × 多模型判斷中樞架構（白板定案版）
status: approved-direction（方向已拍板，未動工；細項見 §8 待確認）
tags: [架構, api server, 多站點, 多模型, 並行, GPU, A1000, stationId, 契約演進]
---

# 架構設計：多站點 × 多模型判斷中樞

> 來源：2026-07-24 白板圖 + 使用者四點拍板（A: server 回 JSON 邊緣執行動作；B: HTTP、不輪詢；C: 做成 AINAVI 類似的多模型中樞；D: GPU server = **A1000**、要求 2-3 站**並行**不排隊）。
> 本文件是「目前最新的 api server 架構」正式版；**只設計、未動工**。

---

## 0. 一句話

1..n 號上位機（PLC 觸發＋相機）各自把圖用 **HTTP 一次來回**送到中央 **GPU(A1000) server**；server 依 `task`（OCR／公母模／瑕疵…）選模型**並行**推論，回 **JSON＋狀態**；上位機依收到的 JSON 執行**我們設定的動作**——收不到回覆＝fail-closed/本機後援，不停線。

---

## 1. 拍板結論（四點，逐條落成設計約束）

| # | 拍板 | 設計約束 |
|---|---|---|
| A | **server 只回 JSON＋狀態；edge 依 JSON 執行設定好的動作** | 維持既有鐵律：server 回**觀測**（讀值/類別＋信心，可附建議判定欄位），**動作決策與執行永遠在 edge**（動作映射是 edge 的設定）。逾時/連不上 → edge 預設動作（fail-closed）或本機後援 |
| B | **HTTP、不要輪詢** | 圖上的 ③圖像通知＋⑤判斷通知 ＝ **一個 HTTP request/response**（送圖的回應就是判斷）。無輪詢、無推播、無 MQTT——熱路徑最簡形。伺服器慢任務未來才考慮 202+回呼，現階段不做 |
| C | **做成與 AINAVI 類似的多模型中樞** | server 從「單一 OCR」擴成**多 task 判斷中樞**：`ocr_pair`（已有）→ `gongmu`（公母模，另專案現成模型）→ `defect`（瑕疵，自建）。並補 **AINAVI 對等的管理面**（模型列表/上架/載入切換 = 主項 1 的模型中樞 API）。AINAVI 盒子本身仍照先前拍板不投入（機器不在）；若日後回歸，以相容 task 接入 |
| D | **GPU server：A1000；2-3 站同時觸發要並行、不可排隊 lag** | 兩件事：①server 端 ORT 換 CUDA EP（**套件切分**：`MoldCode.Onnx` 被 edge 共用，GPU 依賴只能進 server 側）②**解掉推論串行鎖**（見 §4——現況並發會排隊，正是使用者怕的 lag） |

## 2. 目標拓撲

```
 1號上位機──┐        ┌──────────── 主機 api server（Windows + A1000 GPU）─────────────┐
 (PLC+相機) │ HTTP   │  POST /api/infer/{task}   ← 圖 + stationId (+modelVersion)     │
 2號上位機──┼───────>│    task 路由 → 模型註冊表 → 並行推論(CUDA) → JSON+狀態          │
 3號上位機──┘ 1來回  │  GET  /api/infer/health   ← 各 task 模型狀態                    │
   …n號              │  GET/POST /api/models…    ← 模型中樞（主項1；AINAVI 對等管理面）│
     ▲               └──────────────────────────────────────────────────────────────┘
     └─ ⑥ 依回覆 JSON 執行動作（放行/剔除/氣吹）；逾時→fail-closed/本機後援
```

單站循環：①物件到站(PLC IO) → ②取像 → ③HTTP 送圖+stationId → ④server 選 task 模型推論 → ⑤回 JSON → ⑥edge 依設定動作。n 站各自獨立跑，server 並行處理。

## 3. 契約演進（相容式擴充，不破壞既有）

### 3.1 推論端點
- 現有 `POST /api/infer/pair` **不動**（= `task=ocr_pair` 的別名，已驗證 180/180）。
- 新增規劃：`POST /api/infer/{task}`，`task ∈ { ocr_pair, gongmu, defect, … }`。
- 請求新增欄位：**`stationId`**（站點識別，圖上的「站點通知」）＋既有 `modelVersion`（指定版本，server 端載入邏輯未做，列主項 2 缺項）。
- 回應新增欄位：`stationId`（回聲）、`task`、（可選）`suggestedOutcome`——**僅建議**，動作仍由 edge 設定決定。
- 各 task 回應主體不同（OCR=PairObservation 形；gongmu=四態分類+信心；defect=類別/框/面積），共通信封：`{ stationId, task, modelVersion, elapsedMs, status, result:{...} }`。

### 3.2 健康檢查
`GET /api/infer/health` 擴成 per-task：`{ status, tasks: { ocr_pair: {loaded, version, classes}, gongmu: {...}, defect: {...} } }`（現有欄位保留相容）。

### 3.3 模型中樞（主項 1 落地 = AINAVI 管理面對等）
`GET /api/models?task=`／`GET /api/models/{version}/download`／`POST /api/models`（上架）＋「載入為現用」（對應 AINAVI open-model 語意）。與本地發布路線（`publish_pair_model.ps1`、`_publish.json` md5 治理）銜接：**上架走同一套 md5+三件套規範**，杜絕漂移。

## 4. ⚠ 並行：使用者最擔心的 lag，現況真的會發生

**現況瓶頸**：Kestrel 天生可並發收請求，但 server 端辨識器對 `InferenceSession` **上了鎖**（「InferenceSession 非執行緒安全 → 內部上鎖」）→ **2-3 站同時打，推論會排隊**：CPU Passes=2 下第 2 站多等 ~385ms、第 3 站 ~770ms——正是「排序 lag」。

**實測證實（2026-07-31，CPU Passes=2 Release）**：單發 317-340ms；**3 站同時 → 356 / 619 / 864ms**——清楚的 1x/1.9x/2.6x 排隊階梯，串行鎖瓶頸屬實。P-B/P-C 的必要性有數字背書。

**解法（依序）**：
1. **GPU（A1000）先把單張壓下來**：目標數十 ms/張——即使短暫排隊，疊加也小於節拍。**量測是第一步**（P0'：A1000 單張/並發 2-3 的 p95）。
2. **解串行**：ONNX Runtime 官方文件 `InferenceSession.Run()` 本身**執行緒安全**（專案上鎖屬保守作法，並含模型抽換一致性考量）。方案：(a) 允許並行 Run（鎖只保護 swap）或 (b) 每 task 開 session 池（2-3 個實例輪用）。VRAM 需核算（雙 head ONNX 各 ~20MB，A1000 4-8GB 綽綽有餘；瑕疵/公母模另計）。
3. **並發壓測驗收**：模擬 2-3 站同時觸發（同時 POST），驗 p95(含排隊) < 各站節拍。

## 5. 各 task 現況與引入順序

| task | 模型 | 現況（2026-07-24 使用者拍板） | 順序 |
|---|---|---|---|
| `ocr_pair` | 雙 head warpPolar | ✅ **幾乎完成**（server 已通、180/180 驗證） | 已有 |
| `gongmu` | 公模四態分類（另專案 gongmu，CHS 97.3%、~110ms/張 CPU） | 🔶 **使用者本人進行中**——我方出接入規格等對接（見 §8-3） | 第二 |
| `defect` | 瑕疵（自建，AINAVI 角色的自家版） | ⬜ **未開工**——先佔 task 名，不排程 | 最後 |

## 6. 分階段（未動工，只排序；與整合書階段 3/4 正交並行）

| 階段 | 內容 | 前置 |
|---|---|---|
| **P-A** | 契約擴充：`stationId` + task 信封（先支援 ocr_pair 別名） | 無（相容改） |
| **P-B** | GPU 化：套件切分（server 側 CUDA EP，edge 不動）+ A1000 量測（單張/並發 p95） | A1000 到位 |
| **P-C** | 並行化：解串行（並行 Run 或 session 池）+ 2-3 站並發壓測 | P-B |
| **P-D** | `gongmu` task 上架（export onnx + 前處理移植 + harness 對版） | 跨專案協調 |
| **P-E** | 模型中樞 API（主項 1；銜接 publish 路線 md5 治理） | — |
| **P-F** | `defect` task（模型來源另議） | 資料/模型 |

## 7. 對 ROADMAP 的影響（已同步）

- **GPU 從「可選優化」改為「多站並行的前提」**（D 拍板 A1000）。
- 主項 3 補「多站並行」項；主項 1 的模型中樞 API 即本文件 §3.3。
- AINAVI 節維持「盒子不投入」，補注「自建類似能力＝本文件」。

## 8. 待確認 →（2026-07-24 使用者回覆：僅知三 task 狀態，其餘未知）——**已全部配預設，不擋任何階段**

**三 task 狀態（使用者拍板）**：`ocr_pair` **幾乎完成**｜`gongmu` **使用者本人進行中**｜`defect` **未開工**。

| # | 原問題 | 回覆 | 不擋路的處理 |
|---|---|---|---|
| 1 | A1000 到位時間/裝哪台 | 未知 | P-B 的**套件切分與 CUDA 化先用開發機 RTX 3050 代量**（機制相同）；A1000 到位只是換卡重量測 |
| 2 | 2-3 站並行實際頻率 | 未知 | P-C 壓測門檻先用既有假設：**節拍 <400ms、尖峰同時 3 站**；拿到真數字再校準 |
| 3 | gongmu export 由誰做 | **使用者在弄** | 我方先出「**gongmu 接入規格**」（server 要收的東西：`.onnx` + `.names.json` + 前處理參數 + md5/`_publish.json`，沿用發布路線治理）→ 他弄好即插即驗 |
| 4 | defect 模型來源 | 未開工 | 契約**先佔 `defect` task 名**，不排程、不投入 |
| 5 | stationId 編碼 | 未知 | P-A 先用**自由字串**（edge 設定檔填，如 `"ST-01"`），日後定編碼規則不破壞契約 |

## 9. 檔案地標
- 白板前身（多 edge 拓撲）：`2026-07-12_api_server_deployment.md` §2
- 契約現行版：`2026-07-14_api_infer_pair_contract.md`
- 整合階段 0-4：`2026-07-15_edge_server_integration.md`
- 本地模型發布路線（md5 治理，P-E 銜接）：`2026-07-24_model_publish_route.md`
- AINAVI 管理面語意參考：`2026-07-16_ainavi_edgehub_line.md` §2-3
