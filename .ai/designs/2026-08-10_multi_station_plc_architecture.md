---
date: 2026-08-10
type: design
project: AIVision（商品化・戰線一 主場）
title: 多站架構：PLC 觸發 × 完美影像 × 低延遲 × 靈活換權重
status: 設計（待拍板細項 §9，未動工）
roadmap: 商品化戰線一（產線即時引擎）；承接 2026-07-24_multi_model_server_architecture.md、2026-07-12_api_server_deployment.md
tags: [多站, PLC, 觸發, 前處理, 低延遲, session池, 站台設定檔, 商品化]
---

# 設計書：多站架構（PLC 觸發 × 完美影像 × 低延遲 × 靈活換權重）

> 承接使用者需求：多站→不同處理換不同權重（權重不是問題）；真正問題＝送 server 的前處理影像要**完美**且**低延遲**；拍照是 **PLC 觸發**。
> 先回答「PLC 能做什麼」（§0），再給正確分工（§1）與完整設計。

---

## 0. PLC 三問直答（地基，搞錯全歪）

| 問 | 答 | 原因 |
|---|---|---|
| PLC 可以做前處理嗎？ | **不行** | 標準 PLC 無影像資料型別/OpenCV/浮點矩陣；掃描週期要維持毫秒級確定性。影像前處理屬 edge/server |
| PLC 可以跑程式嗎？ | **只能跑 PLC 邏輯**（IEC 61131-3 梯形圖/ST/FBD），跑不了我方 C#/Python/ML | 例外：工業 PC/PAC（Beckhoff TwinCAT 等）同機跑 Windows+即時核，但那是「工業電腦跑我方 edge 軟體」，非「PLC 跑我們程式」 |
| PLC 只能觸發拍照傳圖嗎？ | **PLC 負責觸發+致動，但不搬圖片** | 圖是 相機→edge。「PLC 通知 server 收到圖」的正解＝edge 收到相機圖後 HTTP 送 server，那 request 就是通知。**PLC 不碰影像位元組** |

**一句話**：PLC ＝ 產線的「神經反射」（感測到位→觸發→致動），不是「大腦」（影像判斷）也不是「電腦」（跑我們的程式）。

---

## 1. 正確分工（誰做什麼——這是整個設計的核心）

```
┌ PLC ────────────────────────────────────────────────┐
│ • 感測件到位(sensor/encoder)                          │
│ • 發硬體觸發訊號 → 相機(數位輸出線)                    │
│ • 與 edge 交握(Modbus): 觸發/busy/結果就緒/OK-NG      │
│ • 依 edge 回的 OK/NG 驅動氣吹/剔除閘(數位輸出)         │
│ ✗ 不碰影像、不做前處理、不跑我方程式                  │
└──────────┬──────────────────────────▲────────────────┘
   硬體觸發線│                         │ Modbus 結果(OK/NG)
           ▼                          │
┌ 相機(IDS) ┐                   ┌ Edge PC(WPF) ─────────────────┐
│硬體觸發→  │──GigE/USB3 原始幀→│ • 收幀 → [前處理(版本化profile)]│
│曝光+讀出  │                   │ • HTTP 一次來回送 server       │
└───────────┘                   │ • 收讀值+信心 → 三態+工單核對   │
                                │ • fail-closed → Modbus 回 PLC  │
                                │ • server 掛→本機 ONNX 後援不停線│
                                └──────────┬─────────────────────┘
                                     HTTP 一次來回(PNG/raw+stationId+task+version)
                                           ▼
                                ┌ 中央 Server(GPU A1000) ────────┐
                                │ 只做推論(按 task/version 暖session)│
                                │ P-C: session 池並行、不排隊       │
                                └───────────────────────────────────┘
```

**鐵律**：PLC↔edge、edge↔server；**PLC 不直連 server**（相機在 edge 觸發線最短；server 掛產線網被外部戳＝安全風險）。

---

## 2. 觸發與交握（低延遲的起點）

### 2.1 觸發方式（選硬體觸發）
| 方式 | 延遲/抖動 | 判定 |
|---|---|---|
| **硬體觸發**（PLC 數位輸出 → 相機 Trigger IN 腳） | **最低、確定性**（µs~ms 級） | ✅ 用這個 |
| 軟體觸發（PLC→Modbus→edge→SDK 觸發相機） | 多一層軟體排程抖動（數 ms~數十 ms） | 只在無硬體觸發線時退而求其次 |

### 2.2 交握協定（我方已用 Modbus）
- 低階數位 I/O 交握：PLC 設 `trigger` bit → vision 設 `busy`/`done`/`result`(OK/NG/reject) bit。最確定性。
- 或 Modbus TCP 結構化：帶 `partId`/`stationId`/結果碼（資料較richer，略多延遲）。
- **時序關鍵**：從觸發到「edge 回 OK/NG 給 PLC」必須在**剔除閘到達前**完成——這是硬即時約束，也是為什麼**決策留 edge + fail-closed**（server 慢/掛，edge 用本機後援照回 OK/NG，剔除閘照動、不停線）。

---

## 3. 前處理落點（決定多站能不能擴的關鍵決策）

「前處理放哪」不是畫質問題，是**吞吐問題**——因為 server 是多站共享的瓶頸。

| 落點 | server 負擔 | payload | 一致性(完美) | 多站吞吐 |
|---|---|---|---|---|
| **edge 前處理 + 版本化 profile（推薦）** | server **只做推論**(最省) | 小(640 strip PNG ~100-300KB) | ✅ 靠版本化 profile 保證 | ✅ **最佳**——server 只做只有它能做的 GPU 推論 |
| server 前處理 | server 多做幾何運算 | 大(送原圖) | ✅ 集中一致 | ✗ 加重共享瓶頸 |

### 3.1 ⭐ 推薦：前處理放 edge，但吃「版本化 profile」
- 前處理參數已外部化成 JSON（借鏡五項③）＝**profile**。
- **把 profile 跟模型版本綁在一起發布、分發**：edge 拉模型時一起拉 profile（md5 複驗）。
- edge 用該版本 profile 做 crop/Hough/annulus_polar → 送 640 strip + **帶版本標籤**。
- **server 驗版本標籤相符**（不符＝拒絕/告警）→ 保證「這張圖的前處理，和訓練時、和其他站，用的是同一組確定性參數」。
- **這樣同時拿到**：小 payload（低延遲）＋ 確定性一致（完美）＋ server 純推論（多站吞吐最大）。
- **為什麼比「server 前處理」好**：多站時 server 是共享資源，**讓它只做 GPU 推論這件只有它能做好的事**；CPU 幾何前處理分散到各 edge 平行做，天生水平擴充。

> 若某些站是「智慧相機（相機內建前處理）」，也可把 profile 燒進相機，但**難像 JSON profile 那樣版本化/回滾**——一般站優先走 edge+profile。

---

## 4. 「完美影像」＝ 確定性 + 版本化（不是畫質玄學）

「完美」不是更漂亮，是**每張送進模型的圖，前處理都一模一樣、且和訓練時一致**：
1. **無損**：只用 PNG 無損 / raw，禁 JPEG（我方契約已強制）。
2. **確定性**：前處理參數是版本化 profile，不是每台 edge 手寫（消滅漂移）。
3. **可驗證**：request 帶前處理版本標籤，server 比對相符才算數。
4. **train/infer 一致**：profile 就是訓練當時那組——結構性保證（AINavi 沒有）。

---

## 5. 「零延遲」的實話 + 延遲預算

**零延遲不存在；目標＝延遲 < 節拍餘裕，且 server 慢/掛不停線。** 粗估預算（GPU 後）：

| 階段 | 概估 | 壓法 |
|---|---|---|
| PLC 觸發→相機曝光 | µs~數 ms | 硬體觸發 |
| 曝光+讀出 | 數~數十 ms | 相機設定/光源 |
| edge 前處理(Hough/warpPolar) | ~數十 ms(CPU) | 各 edge 平行、不占 server |
| 網路(local GigE, 640 PNG) | <10 ms | 小 payload |
| **server 推論** | **CPU 現況 191ms → GPU 目標數十 ms** | **P-B GPU(A1000)** |
| 回程+edge 判定+交握 | 數 ms | — |
| **多站疊加** | 串行鎖現況 3站 356/619/864ms | **P-C session 池並行** |

- **瓶頸是推論**：P-B（GPU）把單張壓到數十 ms 是關鍵；P-C（session 池）讓多站不排隊。
- ⚠ **必先解**：edge `TimeBudgetMs=120` vs 單幀 191ms 矛盾——接實時前釐清（多幀投票恐只跑 1 幀）。GPU 後重新量。
- **兜底**：edge 本機 ONNX 後援 + fail-closed＝server 慢/斷也回得出 OK/NG、產線不停。**延遲的下限風險被這條保險兜住。**

---

## 6. 靈活換權重＝站台設定檔（解掉「笨重」）

「笨重」是因為現在多站靠手動接線。解法＝**站台設定檔驅動**：

```jsonc
// stations.json（每站一份 profile）
{
  "ST-01": { "task":"ocr_pair",  "modelVersion":"v6.7.2c", "preprocessProfile":"pp_lens_v3",
             "camera":{...}, "plc":{"triggerBit":100,"resultReg":200} },
  "ST-02": { "task":"ocr_crnn",  "modelVersion":"v4",       "preprocessProfile":"pp_lens_v3", ... },
  "ST-03": { "task":"gongmu",    "modelVersion":"g-cur",    "preprocessProfile":"pp_gong_v1", ... }
}
```

- **加一站＝加一份設定，不改程式。** 換用途/換權重＝改 `task/modelVersion`，沿用**熱切換**（已實作：行程池+LRU，免重啟）。
- `stationId` 已在契約裡（server 回聲）→ 天生可識別、可追溯「哪版在哪站判的」。
- 這就是[強化策略]的「模組化」落地：站台＝一組方塊(profile)+權重(version)，設定即組裝。

---

## 7. 多站拓撲選項（要拍板）

| 拓撲 | 說明 | 適合 | 取捨 |
|---|---|---|---|
| **A. 每站一 edge PC** | 每站：WPF+相機+PLC I/O，各自送 server | 站數少、要最強隔離 | N 台 PC；隔離最好、故障不連坐 |
| **B. 一 edge 多相機** | 一台 edge 接多站相機 | 站密集、省 PC | 該 PC 要並發處理多觸發/多相機 |
| **C. 智慧相機+瘦 edge** | 相機內建前處理，直推 server | 極省空間 | 前處理難版本化/回滾 |

- **server 端一律走 P-B（GPU）+ P-C（session 池並行）**：不論哪種拓撲，多站併發打 server 都要靠 session 池解串行鎖。
- **建議起步**：A（每站一 edge，隔離清楚、對齊現況 WPF），server 先 P-B 量 A1000、再 P-C 解串行壓測。

---

## 8. 端到端時序（單站一次循環）

```
PLC: 件到位 ──觸發線──▶ 相機曝光讀出 ──▶ edge 收幀
edge: 依站 profile 前處理 ──HTTP(strip+stationId+task+version)──▶ server
server: 按 version 暖 session 推論(GPU) ──▶ 回讀值+信心+version回聲
edge: 三態 + 工單預期碼核對 ──▶ Modbus 回 OK/NG ──▶ PLC 驅動氣吹/剔除
     (若 server 逾時/掛 → edge 本機 ONNX 後援 → 照回 OK/NG，不停線)
```

**多站**：N 站各自跑此循環、各自 edge 平行前處理；server 用 session 池並行推論、不排隊。

---

## 9. 待拍板 / 待量測

1. **拓撲**：A（每站一 edge）/ B（一 edge 多相機）/ C（智慧相機）？→ 建議 A 起步。
2. **前處理落點**：確認走「edge + 版本化 profile」（推薦，多站吞吐最佳）？
3. **觸發**：確認相機有硬體觸發腳、PLC 有數位輸出接？（決定延遲下限）
4. **交握**：Modbus 數位 bit 交握 vs 結構化暫存器？結果碼定義。
5. **量測（P-B 前置）**：A1000 單張推論 p50/p95；2-3 站並發 p95（解串行前後對比）。
6. **必解**：`TimeBudgetMs=120` vs 單幀延遲矛盾（GPU 後重量）。
7. **節拍**：實際產線每站節拍多少 ms？→ 反推延遲預算是否夠。

---

## 11. ★定案（2026-08-12，父子節點 POC 實測後拍板）

### 11.1 傳輸：**push（A 主動送前處理圖），不用 pull**
- **`A ──POST 前處理圖──▶ B`，一次來回，回 JSON 結果。**（POC 已實測：有線區網傳輸 ~1ms、端到端≈純推論。）
- 不走「A 通知、B 回頭撈（pull/共享資料夾）」：那做的事更多（多一次來回＋圖先落地磁碟＋B 反向連進 A），**更慢、更複雜、更不安全**。pull 只在「離線解耦／超大圖」才用，即時檢測不用。

### 11.2 Route A（邊緣職責分工，定案）
1. **A 本地存原圖**（追溯/重訓/除錯用）。
2. **A 做前處理 → 只送「前處理後的小圖」給 server B**（server 只做 GPU 推論＝多站共享瓶頸下吞吐最佳）。
- **四項補強（從「能跑」到「產線級穩」）**：
  1. **存原圖＝非同步＋選擇性/輪替**：背景寫、不卡熱路徑；全存要 N 天自動清，或只留 NG/需複檢/低信心＋OK 抽樣（防塞爆硬碟）。
  2. **送的前處理圖＝帶前處理版本標籤＋無損**：server 核對版本相符才算數→每站每張與訓練同一組確定性參數＝完美一致；只送 PNG/raw，禁 JPEG。
  3. **原圖↔結果綁一起**：存原圖一併記 `{時間,站號,讀值,信心,模型版本,前處理版本,OK/NG}`＝可追溯（信任鏈時機 D）。
  4. **server 掛→用手上前處理圖跑本機 ONNX**：edge 前處理圖本來就在手上，fail-closed 後援天生支援＝不停線。
- **反例（何時才改送原圖給 server 前處理）**：edge 變很弱（智慧相機無 CPU）或站數很少想集中管理。**我方目標多站→前處理留 edge、送小圖，不反過來加重 server。**

### 11.3 PLC 角色 vs A 電腦（釐清，定案）
- **PLC ＝ 純觸發 ＋ 致動**：發「拍照」觸發訊號給相機、收 OK/NG 驅動氣吹/剔除。**不碰圖、不跑我方程式、不主導流程。**
- **A 電腦（edge／POC 的「子端」）＝ 代理／orchestrator**：收相機圖 → 前處理 → 存原圖 → push 給 server B → 收結果 → Modbus 回 OK/NG 給 PLC。**整條流程由 A 主導。**
- 相機、PLC 都接到 A 電腦（相機走 GigE/USB、PLC 走 Modbus/IO）；A 對外用 HTTP 跟 server 講話。
- **一句話**：PLC 是「反射神經」（觸發＋動作），A 電腦是「大腦＋手」（拿圖、前處理、判、回報）；**圖的路徑永遠是「相機→A 電腦→server」，PLC 全程不碰影像。**

> POC 對應：`父子節點POC/`＝子端(A/edge push 圖)＋父端(B/server 回 JSON)已實測通;PLC＋相機是接真硬體時才在 A 上補(Modbus/相機 SDK),與現行網路/通訊測試無關。

---

## 10. 檔案地標
- 本設計：`.ai/designs/2026-08-10_multi_station_plc_architecture.md`
- 多模型中樞（server 並行/串行鎖）：`2026-07-24_multi_model_server_architecture.md` §4
- 推論契約（stationId/modelVersion/format）：`2026-07-14_api_infer_pair_contract.md`
- 前處理 JSON 外部化（profile 基礎）：借鏡五項③、`2026-08-06_借鏡五項_驗證清單.md`
- 商品化全景（本設計＝戰線一 P1）：`doc/強化策略/商品化戰場全景.md`
