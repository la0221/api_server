# 父子節點 POC（edge ↔ server HTTP 通訊）

> **平行於 VISION 的獨立實驗資料夾**——先把父子節點跑通，通過後再議是否合併進主程式。
> 定位對照：`../doc/強化策略/多站架構_PLC.html`、`.ai/designs/2026-08-10_multi_station_plc_architecture.md`。

## 這是什麼

模擬我方 **edge → server → edge** 的最小可跑版本：

- **子節點（child_edge.py）＝ edge/站**：**送圖**給父端 → 收回**驗證結果 JSON** → 顯示。
- **父節點（parent_server.py）＝ server**：收圖 → 推論 → 回**驗證結果 JSON**。
- 協定對齊我方既有契約（`POST /api/infer/pair` + 信封 JSON），**日後要合併最省事**。

```
子(edge)  ──POST /api/infer/pair (圖 bytes + X-Station-Id/X-Task)──▶  父(server)
   ▲                                                                    │
   └──────────── JSON 信封 {stationId,task,elapsedMs,status,result} ◀────┘
```

## 怎麼跑

需要 conda `lens-gpu` 環境（torch/ultralytics/opencv）。以下 `PY` = `C:\Users\User\anaconda3\envs\lens-gpu\python.exe`。

### 1) 啟父端（server）
```
# 純傳輸測試（不載模型，回假結果，可設模擬推論耗時）
PY parent_server.py --engine stub --port 8770 --proc-ms 40

# 真 CRNN 模號穴號
PY parent_server.py --engine ocr  --port 8770

# 真 母模鏡片偵測
PY parent_server.py --engine lens --port 8770
```
啟動後用瀏覽器開 **http://127.0.0.1:8770/** 看即時狀態頁（父端收到的每筆結果，2 秒刷新）＝父端的簡易 UI。

### 2) 子端（edge）
```
# GUI（簡易）：填父端 host:port/站號 → 選圖/資料夾 → 送出 → 看回傳 JSON + 來回 ms
PY child_edge.py

# 壓測（量端到端延遲，含 HTTP）：
PY child_edge.py --bench --host 127.0.0.1 --port 8770 --dir <圖資料夾> \
     --n 150 --concurrency 1,2,4 --task ocr_pair [--sim-net-ms 20]
```
`--sim-net-ms`：人為在每次來回加模擬網路延遲，用來估「真網路下大概怎樣」。

## Route A 資料流（route_a_edge.py）★定案流程，先本機後跨機

前面 child_edge 只證「傳輸」；**Route A** 才是設計書 §11 定案的完整 edge 資料流：

```
相機圖(raw) ─▶ A(edge)  ① 本機存原圖(非同步)   ② 前處理成小圖+版本標籤(無損PNG)
                        ③ 只送「小圖」給 server ────────────▶ server 只做推論
              ◀─ 回結果(原樣帶回 rawId) ─ ④ 原圖綁結果寫 manifest(可追溯)；server掛→本機fallback不停線
```

**先在本機 127.0.0.1 跑通，再把 `--host` 換父機 IP 就是真跨機**（協定/程式一字不改）。
用 **Python 3.13**（子端 Route A 只需 numpy+cv2、**免 torch/lens-gpu**，因為 server 用 stub、前處理在 edge）。
> ⚠ 乾淨子機不會自帶 numpy/cv2——**先裝 `dist/環境配置/子`**（`安裝_子_離線.bat` 或 `安裝_子_線上.bat`）。本開發機剛好裝過才有。

**一鍵 bat 已依用途分資料夾**（2026-08-13 重整）：通用（開防火牆）在 `dist/` 最外層，其餘分
`dist/連線測試/`（傳圖/假推論，exe 免安裝）、`dist/真實測試/`（真前處理+真 GPU）、`dist/環境配置/`（父/子安裝包）。
本機先預演 Route A（同一台機跑通再上機）：
```
# 視窗A：起父端(stub)   雙擊 dist/真實測試/本機預演_1_父_stub.bat   （127.0.0.1:8770，免 GPU）
# 視窗B：跑 Route A      雙擊 dist/真實測試/本機預演_2_子_RouteA.bat  （Enter=127.0.0.1；真前處理送出）
# 跨機真測              父端 dist/真實測試/1_父_真GPU推論_存收圖.bat ＋ 子端 dist/真實測試/2_子_RouteA真前處理送出_跨機.bat
```
> 子端真前處理走 python（需 numpy+cv2，先裝 `dist/環境配置/子`）；`連線測試/` 的 exe 則免安裝。
> 子資料夾的 bat 以 `%~dp0..\` 取用最外層的 `route_a_edge.py`／`parent_server.py`／`sample_images` 等。

### 前處理模式 `--preproc`（★2026-08-13 修正：接真分類器要用 real）

| 模式 | 送什麼 | 讀值 | payload | 需求 |
|---|---|---|---|---|
| **real（預設）** | **真 CRNN `to_strip` → 640×640 strip**，server 用 `is_strip=True` 只辨識 | **正確**（實測 10/10 與完整 pipeline 一致；lh-dmz 無 torch 機也 5/5 對） | 原圖約 **1/3**（~31KB） | **只需 numpy+cv2**（用自足 `edge_preproc.py`，**免 torch**；前處理不做辨識） |
| none | 原圖直送，server 自己做完整前處理 | 正確 | ＝原圖 | 無（edge_preproc/cv2 缺才會退回 none） |
| repr | 灰階縮 H48 小圖 | **❌ 會亂判** | 極小（毀圖才那麼小） | 只能配 **stub** 做「傳輸縮減」示意，**不可接真分類器** |

> ⚠ **舊版預設是 repr（灰階H48），接真 CRNN 會全部亂判**——因為那不是模型要的輸入。已改為預設 **real**：邊緣做真 `to_strip`（find_circle→裁圓→annulus_polar 極座標展開），server `is_strip=True` 只做辨識，讀值正確。
> **要看正確讀值＝父端須是真 CRNN**（`真實測試/1_父_真GPU推論_存收圖` / `--engine ocr`）＋子端用 `real`。

**看得到的東西**：
- **子端**產物在 `_routeA_out/`：`raw/`＝原始圖、`preprocessed/`＝**真 strip**、`json/`＝每張一個 JSON、`index.html`＝**檢視頁（一列：原始圖 | 前處理 strip | 讀值 | JSON）**、`manifest.jsonl`＝溯源總表。
- **父端**狀態頁 `http://父IP:8770/`：**Route A 面板**顯示「父端收到的前處理圖（真 strip）」＋「子端原圖位置」（`X-Raw-Path`），表格多一欄原圖位置。
- **父端存圖（`--save-recv`）**：父端預設**不存圖**（只留記憶體 30 筆）。加 `--save-recv [資料夾]` 才會把**實際收到的圖**寫到硬碟：`<夾>/recv/<rawId>.png`（收到的 strip）+ `<夾>/json/<rawId>.json`（讀值+子端原圖位置等）。不帶資料夾＝存到 `parent_server.py 旁/_recv_out`。瀏覽器開 `http://父IP:8770/recv` 有收圖檢視頁。`真實測試/1_父_真GPU推論_存收圖` bat 已預設存到 `父子節點POC/_recv_out`。
- 演示 Route A 精神：**原圖留子端、只有前處理 strip 上網、server 只做 GPU 辨識、父端知道原圖在子端哪可回溯**。

## 協定（common.py）

- 端點：`POST /api/infer/pair`
- 請求：body = 影像原始 bytes（jpg/png/bmp）；headers `X-Station-Id` / `X-Model-Version` / `X-Task`
- 回應信封（同 `2026-07-24_multi_model_server_architecture` §3.1）：
  ```json
  { "stationId":"ST-01", "task":"ocr_pair", "modelVersion":"poc",
    "elapsedMs": 37.2, "status":"ok",
    "result": { "mohao":"M101", "confMohao":0.95, "xuehao":"02", "confXuehao":0.93,
                "hasReading":true, "needsReview":false } }
  ```
  （lens task 的 result = `{verdict, conf}`。）
- HTTP 注意事項已落實：**keep-alive 連線重用、關 Nagle、逾時**。
  - 子端：`common.Client` 設 `TCP_NODELAY`。
  - 父端：`parent_server.Handler` 設 `disable_nagle_algorithm = True`。
  - ⚠ **父端這行是 2026-08-13 跨機測試才補上的**：原本只有子端關 Nagle、父端漏了，導致跨機每筆回應被 delayed-ACK 卡 ~40ms（loopback 測不出、躲過本機 acceptance）。詳見 `doc/2026-08-13_跨機測試報告_lh-dmz.md`。

> POC 用「raw body + headers」簡化；正式契約用 multipart（欄位語意相同）。

## 已驗證（2026-08-11）

- 父子通訊打通：子送圖、父回 JSON 信封、子量到端到端來回。
- 三種 engine 皆可跑（stub / ocr / lens）。
- 網路延遲影響已量（見 `網路延遲測試結果.md`）。
- **未合併進主程式**——依使用者指示，先在此平行資料夾通過為止。

## 檔案

| 檔 | 角色 |
|---|---|
| `parent_server.py` | 父（server）：HTTP 收圖→推論→回 JSON；GET / 狀態頁；回拋 rawId 溯源 |
| `child_edge.py` | 子（edge）：GUI + `--bench` 壓測 + `--demo` 一張一張 |
| `route_a_edge.py` | **Route A edge**：存原圖(非同步)+前處理+送+溯源 manifest+fallback+index.html |
| `edge_preproc.py` | **edge 免 torch 前處理**：純 cv2 的 `to_strip`(find_circle/white_pad_square/annulus_polar)，任何有 cv2 的機器都能做真 strip |
| `common.py` | 協定 + HTTP 客戶端（keep-alive/NoDelay；`infer` 可帶 extra_headers）+ **中央事件 log helper `event_log`** |

> 📒 **中央事件 log（每個節點一份、append-only，供事後回填驗證紀錄）**：父端寫 `_logs/parent_events.jsonl`、子端（`route_a_edge`＋`child_edge` 共用）寫 `_logs/child_events.jsonl`，預設在**程式旁 `_logs/`**（走 dist bat＝`dist/_logs/`），啟動時印路徑，可 `--log <路徑>` 改、`--log off` 關。任何操作（啟動/請求/推論/送圖/存圖/health/警告/錯誤）都記一行 JSON（含 ts/pid）。⚠ 目前 `parent_server.exe`/`child_edge.exe` 是加 log 前打的舊版，**exe 路徑要記 log 需重建**；真實測試走 `.py`（父 `parent_server.py`、子 `route_a_edge.py`）已即時生效。
| `dist/` 一鍵檔（依用途分夾） | 通用 `父_開防火牆`(最外層)；`連線測試/`(1_父假推論、2_子壓測、3_子DEMO、4_子GUI，exe 免安裝)；`真實測試/`(1_父真GPU存收圖、2_子RouteA真前處理跨機、本機預演_父/子)；`環境配置/`(子=離線+線上包、父=真GPU線上包) |
| `網路延遲測試結果.md` | Q1 網路延遲量測與分析 |
