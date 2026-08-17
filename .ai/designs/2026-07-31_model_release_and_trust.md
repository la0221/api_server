---
date: 2026-07-31
type: design
project: AIVision（.NET8 WPF 產線檢測 App）
title: 模型發布全鏈路 ＋ Edge 信任鏈（嚴謹版）
status: proposal（設計；本地段已實測、server 段未動工）
tags: [模型發布, 版本治理, 信任鏈, golden set, 零退步, md5, 主項1, 多路線]
roadmap: 主項1（線上版本控管）＋ 主項2（隔離試模＝發布 gate）
---

# 設計書：模型發布全鏈路 ＋ Edge 信任鏈

> 回答使用者兩問（2026-07-31，多路線架構圖確立後）：
> **Q1 模型要如何發布？** → §1 全鏈路管線（本地段已建，server 段為主項 1 落地）。
> **Q2 edge 要怎麼知道模型做的是對的？** → §2 信任鏈：**沒有單一機制能「知道模型是對的」——靠四個時機各設關卡，把「信任」拆成可驗證的小塊**。
> 承接：`2026-07-24_model_publish_route.md`（本地發布，已實測）、`2026-07-12_api_server_deployment.md` §2.6、`2026-07-24_multi_model_server_architecture.md`。

---

## 0. 兩問的最短答案

**Q1**：訓練產出 → 本地發布腳本（md5＋原子落地＋`_publish.json`，**已建**）→ 上架 server（`POST /api/models`，待建）→ 成為 **candidate** → **server 隔離試模當發布 gate**（金樣本批量、零退步比較，工具**已建**＝批量試模頁/EdgeSimulator）→ 晉升 **stable** → 各路線 edge 拉同步（下載後 **md5 複驗**）→ 有問題一鍵回滾到前一個 stable。

**Q2**：edge **永遠不「相信」模型——它驗證五件事**：①拿到的檔案對不對（md5）②現在跑的是哪一版（每筆回應都帶 `modelVersion` 回聲）③這一版上線前證明過什麼（金樣本準確率＋零退步 gate）④這一筆判斷可不可信（信心門檻＋fail-closed＋**工單預期碼核對**——執行期最強的對錯訊號）⑤事後可追溯（stationId＋版本＋讀值全記錄）。

---

## 1. Q1：發布全鏈路（版本的一生）

### 1.1 管線總圖

```
訓練端 (Content_lens OCR repo)
  │ ①(僅.pt) export → onnx（內嵌 names、imgsz=640）
  ▼
本地發布【已建✅】 publish_pair_model.ps1
  │ md5 + 來源記錄 + 原子落地 pairs\<版本>\ + _publish.json
  │ + harness paircycle 驗讀值（gate 前置）
  ▼
② 上架 server：POST /api/models（multipart onnx×2 + _publish.json）【待建】
  │ server 複驗 md5、可載入性、names/類別數 → 狀態=candidate
  ▼
③ 發布 gate：server 隔離試模【工具已建✅＝主項2】
  │ 金樣本批量（M101 180 張 + M83 夾…）走 candidate 版本
  │ 判準：準確率 ≥ 門檻 且 對現任 stable「零退步」
  ▼
④ 晉升：POST /api/models/{ver}/promote → 狀態=stable（舊 stable → previous）【待建】
  ▼
⑤ 分發：路線1..n 的 edge 定時查 GET /api/models?state=stable
  │ 新版本 → 下載 → 本地 md5 複驗 → 進本機登錄夾（後援用）
  │ server 中央推論同步用同版本 → server/edge 讀值一致
  ▼
⑥ 監控/回滾：出問題 → demote 回 previous（一鍵）；每筆判斷都可追溯到版本
```

### 1.2 版本狀態機（治理核心）

| 狀態 | 意義 | 進入條件 | 可做什麼 |
|---|---|---|---|
| `candidate` | 已上架、未驗證 | 上架＋server 複驗（md5/可載入/類別數）通過 | 只能被**隔離試模**呼叫（`modelVersion` 指定），**不可**成為預設 |
| `stable` | 現役 | 通過 §1.3 gate ＋ 人工確認晉升 | 中央推論預設版本；edge 拉同步的目標 |
| `previous` | 前一個 stable | 被新 stable 取代時自動降 | 回滾目標（保留至少一版不刪） |
| `deprecated` | 退役 | 人工標記 | 不可載入；保檔可查 |

**回滾**＝把 `previous` 重新標回 `stable`（一個 API 呼叫），edge 下一次同步自動跟上。**任何時刻 server 至少保有兩個可用版本**。

### 1.3 發布 gate（晉升 stable 的硬條件，逐項可機器檢查）

1. **完整性**：mohao+xuehao onnx md5 與 `_publish.json` 一致；OnnxRuntime 可載入。
2. **身分**：內嵌 `names` 可讀、類別數符合宣告（模號 20 含 NG；穴號 18/19 依版本——**v9 的 19 類含 NG 是已知坑，必檢 NG index**）。
3. **金樣本準確率**：走 server 隔離試模跑金樣本集（§3.4），雙軸命中率 ≥ 設定門檻（建議：不低於 99%，依集而定）。
4. **零退步**：與現任 stable 在**同一金樣本集**上逐張比對——新版本錯的張數不得多於現任（借用 Content_lens「v9 全量重訓零退步」的成功先例）。
5. **延遲**：金樣本批量的 server 推論 p95 不高於現任 stable 的 120%（防「準了但慢到超節拍」）。
6. **記錄**：gate 結果寫進 `pairs\<版本>\_gate_report.json`（跑了哪個集、命中率、對照版本、日期）→ 三件套從「onnx×2＋publish」補齊為「＋gate report」。

### 1.4 Server API 契約草案（主項 1 落地面）

| API | 作用 | 備註 |
|---|---|---|
| `GET /api/models?task=ocr_pair` | 列版本＋狀態＋md5＋gate 摘要 | edge 同步輪詢用（低頻，如每 10 分鐘） |
| `POST /api/models`（multipart） | 上架 → candidate | 帶 `_publish.json`；server 複驗 |
| `GET /api/models/{ver}/download?head=` | 下載檔案 | 回應含 md5，edge 下載後**必複驗** |
| `POST /api/models/{ver}/promote` | candidate→stable（附 gate report） | 工程師以上；審計記錄 |
| `POST /api/models/{ver}/demote` | 回滾 stable→previous 對調 | 同上 |
| `POST /api/infer/pair` 的 `modelVersion` | **指定 candidate 試模**（隔離試模的鑰匙） | server 端按版本載入＝主項 2 既列缺項，在此一併實作 |

### 1.5 多路線分發（對應架構圖 路線1..n）

- 各路線 edge 各自輪詢 `stable` → 下載 → md5 複驗 → 本機登錄夾（斷線後援模型）。
- **一致性顯示**：edge UI 顯示「本機後援版本 vs server 現役版本」，不一致亮黃（同步中）——別做成靜默。
- 中央推論回應的 `modelVersion` 讓 edge **每一筆都知道**是哪版判的——多路線期間若 server 剛換版，邊界請求也可追溯。

---

## 2. Q2：Edge 信任鏈——四個時機、五道驗證

> 原則：**「模型是對的」不是一個可直接知道的事實，是一串可驗證的證據。** 每道關卡擋一類錯誤。

### 時機 A｜發布前（這一版值不值得上）

| 驗證 | 擋什麼錯 | 現況 |
|---|---|---|
| 來源 md5＋`_publish.json` 溯源 | 拿錯檔/版本漂移（「兩份 V6.7.1」重演） | ✅ 已建 |
| harness paircycle 讀值驗證 | 前處理不對齊、export 壞掉 | ✅ 已建 |
| 金樣本批量＋**零退步** gate（§1.3） | 「新版本整體變差」矇混上線 | 🔶 工具已建（批量試模），gate 化待做 |
| 延遲 gate | 準但慢、超節拍 | 🔶 數字都有，門檻化待做 |

### 時機 B｜edge 取得模型時（拿到的東西對不對）

| 驗證 | 擋什麼錯 | 現況 |
|---|---|---|
| 下載後 md5 複驗 | 傳輸損壞、拿錯檔 | ⬜ 隨分發 API 做 |
| 類別數/names 對版（health 回 20/18） | 載到類別錯位的模型（讀值全錯的最壞情境） | ✅ health 已回；edge 比對邏輯待加 |
| 版本一致性顯示（本機 vs server） | 「以為在用新版其實是舊版」 | ⬜ 隨分發做 |

### 時機 C｜每一筆判斷（這一筆答案可不可信）——**執行期核心**

| 驗證 | 擋什麼錯 | 現況 |
|---|---|---|
| **信心門檻**（模號 0.60／穴號 0.85）低於即不採信 | 模型「猜」的答案被當真（實例：M83 讀成 M58 conf 0.596 → 被 0.60 門檻擋下 ✅） | ✅ 生產已有 |
| **fail-closed**：無物件/讀不到/逾時/連不上 → 一律不放行 | 「沒答案」被誤當「沒問題」 | ✅ 全鏈已貫徹 |
| **工單預期碼核對**：讀值 vs 工單預期 → 不符=混料警報 | **模型有信心地讀錯**（唯一能擋這種錯的執行期機制——因為工單就是現場的正解） | ✅ 生產已有（MixedAlarm） |
| 多幀投票（共識才採信） | 單幀偶發誤讀 | ✅ 有（⚠ TimeBudgetMs=120 矛盾未解，效果存疑） |
| 回應帶 `modelVersion`＋`stationId` | 出事不知道「哪版在哪站判的」 | ✅ 已上線（07-31） |

### 時機 D｜事後（持續發現「模型悄悄變得不對」）

| 驗證 | 擋什麼錯 | 現況 |
|---|---|---|
| 每筆判斷記錄（版本/站點/讀值/信心/時間） | 無法回溯定位問題批次 | 🔶 本機歷史有；集中化=部署書 P4 |
| 信心分布/NG 率漂移監測 | 光源老化、鏡頭髒污等造成的緩慢劣化 | ⬜ 未來（先靠人看統計頁） |
| 金樣本定期複測（同一批圖定期重跑現役版本） | 環境/依賴變動造成的靜默退化 | ⬜ 可先手動月跑 |

### 一句話總結信任鏈

> **上線前用金樣本證明它夠好（A），拿到手驗它沒變質（B），每一筆都用門檻＋工單正解防它出錯（C），事後留痕讓錯誤無所遁形（D）。** Edge 從頭到尾不需要「相信」模型——它只需要執行這些驗證。

---

## 3. 嚴謹細節（易漏清單）

1. **原子性**：落地/下載一律「暫存檔→改名」；swap 期間辨識器鎖已保證請求不撞半成品（現有 `SwitchableTwoHeadRecognizer.Swap` 機制）。
2. **併發試模 vs 現役推論**：candidate 試模與 stable 推論同時發生 → 按版本載入需**獨立 session**（勿動現役實例）；VRAM/RAM 佔用要算（雙 head ~40MB/版，可接受）。
3. **金樣本集治理（§1.3 的根基）**：金樣本本身要版本化（放 `D:\AIVisionModels\golden\<集名>\`＋清單 md5）——**gate 的可信度取決於金樣本的可信度**；正解由資料夾結構承載（M101/01 慣例）。起始集：M101 180 張＋M83 整夾（跨模號）。
4. **v9 的 19 類穴號（含 NG）**：gate 第 2 項必須比對 NG index；edge 門檻邏輯對 NG 類的處理要先想（讀出 NG ≠ 讀不到）。
5. **時間**：所有記錄用 UTC（既有慣例）。
6. **權限與審計**：上架/晉升/回滾=工程師以上；記誰按的（現有登入體系可掛）。
7. **不重造**：本地發布腳本、隔離試模、harness、md5 治理**全部沿用**——server 段是把這些「串成 API 化流程」，不是新發明。

---

## 4. 實作順序建議（等拍板再動工）

| 步 | 內容 | 依賴 |
|---|---|---|
| R1 | server 端「按版本載入」（`modelVersion` 實作）＋ candidate 試模打通 | 無（主項 2 既列缺項） |
| R2 | `GET /api/models`＋`POST /api/models`（上架→candidate；複驗 md5/names） | R1 |
| R3 | gate 自動化：金樣本批量＋零退步比較＋`_gate_report.json` | R1、金樣本集定版 |
| R4 | promote/demote＋審計 | R2、R3 |
| R5 | edge 分發：輪詢/下載/md5 複驗/一致性顯示 | R4 |

## 5. 檔案地標
- 本地發布（已實測）：`2026-07-24_model_publish_route.md`、`publish_pair_model.ps1`、`pairs\<版本>\_publish.json`
- 隔離試模（gate 工具）：批量頁「中央伺服器」來源、EdgeSimulator 資料夾模式
- server 模型中樞原始構想：`2026-07-12_api_server_deployment.md` §2.6
- 多站/多模型架構：`2026-07-24_multi_model_server_architecture.md`
- 執行期防線 code：`MoldCodePairVerifier`（三態）、`MoldCodePairCycleOptions`（門檻）、`InferController`（版本/站點回聲）
