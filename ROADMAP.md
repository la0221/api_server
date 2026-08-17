# AIVision 主線目標追蹤（ROADMAP）

> **定錨日：2026-07-24**（白板討論拍板）。以下 **3 大主項是必要完成項** —— 每次開新工作前先對照本檔；不屬於任何主項的需求，先確認是否支線，**避免走偏或發散**。
> 標記：✅ 完成｜🔶 部分｜⬜ 未做。完成時打勾補日期。細節見 `.ai/designs/`（本檔只當錨，不寫細節）。

---

## 背景（白板共識）

- UI(edge) 取像 → **HTTP（ip:port）** 送圖 → 推論服務回 JSON。
- 推論服務有兩個：AINAVI 盒子（192.168.1.x，廠商、瑕疵檢測）與 **自建 API server（白板藍字，本案主角）**。
- 自建 API server 定位 = **中央推論 + 模型/版本中樞**（`.ai/designs/2026-07-12_api_server_deployment.md`）。
- **引擎策略（2026-08-04 拍板）**：OCR 的 **CRNN（字元式）效果強於雙 head，將逐步取代之；現階段兩引擎並行**——主控有各自入口（雙head頁／CRNN 測試頁），發布/版控走同一套模型中樞（用途 `ocr_pair` vs `ocr_crnn`）。接入細節：`.ai/designs/2026-07-31_crnn_engine_intake.md`。

---

## 主項 1｜線上版本控管（模型 / 軟體）

**目標**：模型集中放 server、一處更新、多台 edge 拉同步、版本不漂移；（範圍待定）軟體版本亦可線上控管。

- [x] 模型倉庫 API：`GET /api/models`（列版本+md5+_publish.json 溯源+現用/已載入標記；2026-07-31。latest/stable 標記待 R3/R4）
- [x] `GET /api/models/{version}/download?head=mohao|xuehao`（edge 拉檔；回應帶 X-Model-Md5；版本名白名單防路徑跳脫；2026-07-31）
- [x] `POST /api/models/{task}`（上架新版本；2026-07-31 下午）：**模型按用途分家**（ocr_pair／gongmu／defect 各自登錄夾+檔案組成）＋ **UI「模型發布」頁（工程師以上）**：選用途→選 .onnx→版本號→HTTP 上傳（server 對版組成/算 md5/原子落地/_publish.json 溯源；同版本 409 不可覆蓋）——取代 PowerShell 腳本、跨機可發布
- [🔶] edge 拉同步：**手動一鍵下載已通**（API 伺服器設定視窗：清單→下載→.tmp 串流→**md5 複驗（驗不過即丟棄）**→原子落地→_publish.json 溯源+_sync.json 同步紀錄；2026-07-31）。定時檢查/stable 自動拉 + 版本一致性顯示待做
- [ ] 三件套補齊：`pairs/<版本>/` 現缺 `.names.json` / `.report.json`
- [🔶] **版本漂移收斂**：（2026-07-24）已對 v6 全面 md5 溯源——證實 `pairs\*` 大多**非 Content_lens 來源**（早期 OCR_demo 鏡像），**只有 baseline `v671\mohao`(d42bb1b7) 是 Content_lens V6.7.1**；`v671\xuehao` 其實借自 Content_lens **V6.7**。已建 `publish_pair_model.ps1`(md5+原子落地+`_publish.json` 治理)。**待拍板**：定正版來源／是否從 Content_lens 統一重發布。
- [ ] 【**範圍待拍板**】軟體(App)版本控管做到什麼程度？（僅版本檢查？下載？自動更新？）

**現況**：倉庫目錄結構已存在（`D:\AIVisionModels\pairs\v6.7*`），server 端 API 全未做。
**發布全鏈路＋信任鏈設計（2026-07-31）**：`.ai/designs/2026-07-31_model_release_and_trust.md`——版本狀態機（candidate→gate→stable→previous 回滾）、金樣本＋零退步 gate、edge 四時機信任鏈（md5/版本回聲/門檻+工單核對/追溯）；實作順序 R1-R5。
**本地發布路線（2026-07-24）**：辨識/前處理正確性已 harness 實測確認（v6.7.2 conf1.00，與 Content_lens 訓練端逐項一致）；本地發布路線端到端打通（`publish_pair_model.ps1` → 登錄夾 → AIVision載入 → harness驗；實測 Content_lens v6.7.2→`v6.7.2c` conf1.00）。export 工具已建（2026-07-31：`D:\AIVisionModels\export_pt_to_onnx.py`，實測 V6 mohao .pt→.onnx 成功；v9 只差實際跑一次）。設計：`.ai/designs/2026-07-24_model_publish_route.md`。

---

## 主項 2｜線上推論＝隔離試模（不污染本地）

**目標**：新模型可**只放 server** 試跑；edge 不必下載、本地已驗證的模型完全不動。「線上模型尚未下載」或「怕下載污染本地進度」時，都走線上。

- [x] 中央推論端點 `POST /api/infer/pair`（2026-07-15：180/180 讀值正確、Release 191ms/張）
- [x] `GET /api/infer/health` + 回應回填 `modelVersion`（天然隔離：server 與本地版本互不影響）（2026-07-16）
- [x] 「測試中央推論」驗收按鈕（單張、零風險、不碰生產熱迴圈）（2026-07-16）
- [x] server 端**指定版本推論**（2026-07-31：`POST /api/infer/pair` 帶 `modelVersion` → 登錄夾按版本載入，**獨立快取實例不動 baseline**＝真隔離；冷載 ~0.4s 後快取命中；未知版本 404；批量頁可下拉選伺服器版本整批試）
- [x] **線上批量試模**：批量頁加「來源：本機/中央伺服器」，整批走 server 出準確率報告（2026-07-24 實作完成、build 0 錯；**UI 實測待使用者跑一輪**，通過後把主項 3 矩陣該格轉 ✅）

---

## 主項 3｜全模式矩陣：線上/本地 × 離線/實時

**目標**：四格全通。離線模式 = 用拍過的圖跑；實時模式 = 傳輸即時推論。

| | **離線模式**（拍過的圖） | **實時模式**（產線即時） |
|---|---|---|
| **本地模型** | ✅ 雙軸模型管理/批量推論頁 | ✅ 生產熱迴圈（PLC→相機→ONNX→三態→氣吹） |
| **線上模型** | 🔶 批量已實作(07-24)，**UI 實測通過後轉 ✅** | ⬜ 手動開關 → 自動降級 |

- [x] **線上×離線**：批量頁增加來源選擇（本機 ONNX / 中央伺服器），整批走 server（2026-07-24 實作；健檢預檢、連續 3 次傳輸失敗自動中止、報告含 server 推論 p50/p95）
- [ ] **線上×實時（一）**：手動來源開關（`PairInferenceSourceSelector` 包兩個 port）+ Shell 第五顆 SRV 燈（整合書階段 3）
- [ ] **線上×實時（二）**：自動降級——優先 server、逾時切本機、降級事件記錄、半開恢復（階段 4）。**鐵律：server 掛掉不停線**
- [ ] **多站並行**（2026-07-24 白板拍板）：2-3 站同時觸發不排隊——需 GPU(A1000) + 解 server 推論串行鎖 + 並發壓測（`2026-07-24_multi_model_server_architecture.md` §4）
- ⚠ 實時前置未解：edge `TimeBudgetMs=120` vs 單幀實測 191ms 的矛盾（多幀投票恐實際只跑 1 幀）——接實時前必須釐清

---

## AINavi 借鏡五項（2026-08-06 拍板納入；完成即打勾＋補日期）

> 出處：`doc/ainavi逆開發策略/`（03 對照＋05 取代計畫）。都掛在既有主項下，不是新主項。

- [x] **①sidecar 多模型共存／按版本熱切換**（2026-08-06）——實作為**按版本行程池**（每版本一子行程=保留隔離；池上限 MaxProcesses=2、LRU 淘汰閒置；版本檔案由登錄庫解析）：`POST /api/infer/ocr_crnn` 帶 `modelVersion` 即指定任一已發布版本，**換版免改設定免重啟**；CRNN 頁有「查伺服器版本→指定」下拉。E2E：預設版 b3 ✅／指定版熱 41ms ✅／未知版 404 ✅／health 列池狀態 ✅｜服務：主項2＋引擎並行期
- [x] **②per-class 判定門檻進 `_publish.json`**（2026-08-06）——發布頁選填「模號/穴號信心門檻」→ `_publish.json` "judge" 段（0~1 驗證、壞值 400）；CRNN 推論按**版本自帶門檻**算 needsReview 並回傳套用值（無 judge 段=沿用 sidecar 內建）。**產線判定標準異動＝發新版本，不改程式**。E2E：門檻 0.99 觸發複檢 ✅／門檻回聲 ✅／1.5 擋下 ✅｜服務：主項1（治理）＋CRNN 生產語意
- [x] **③前處理參數 JSON 外部化**（2026-08-06，最小子集）——發布頁選填「前處理 JSON」（鍵=WarpPolarParams 欄位；**鍵名打錯發布即 400**）→ `_publish.json` "preprocess" 段；ocr_pair 指定版本推論以**版本自帶參數**建辨識器（無此段=沿用 baseline）。前處理與模型同發布/同 md5/同回滾。E2E：帶段發布+推論 conf 1.0 ✅／錯鍵名 Imgz 擋下 ✅。**範圍註**：涵蓋 server 端 ocr_pair；CRNN 前處理在 sidecar（策略正典不可改常數）不適用；edge 本機辨識器仍走 appsettings（後續）｜服務：主項1（版本漂移收斂）
- [x] **④Port 可用性檢查按鈕**（2026-08-06）——API 伺服器設定視窗「測連接埠」：純 TCP 探測（不打 HTTP 不改設定），區分「服務沒開/防火牆」vs「服務開了但 API 壞」｜服務：主項3（多站並行操作面）
- [x] **⑤PaddleOCR 開源對照試**（2026-08-06，走開源 RapidOCR/PP-OCR，零授權）——M101 1061 對、與 CRNN 完全同源的前處理+detector 裁窗、zero-shot：**模號 97.08%／穴號 39.30%**。結論兩面都拿到：字元式路線再佐證（模號 97%）＋ **CRNN 針對性設計有真實價值**（穴號 39% vs 我們 99.98%，短碼是通用模型的天生弱項）→ 不需為 ocr_2 談 AINavi 授權、被開源即插即用取代的風險低。報告：`experiments/paddleocr_compare/REPORT_三方對照.md`｜服務：引擎策略佐證

## 擋路的已知風險（不解會卡主項）

| 風險 | 卡哪裡 |
|---|---|
| ⚠ `Passes=1` 未大樣本驗證（191ms 可行性押在它上面；僅驗過 M101 單一 session） | 主項 3 實時 |
| ⚠ 節拍餘裕薄：<400ms vs wall p90 289ms（localhost；真網路+TLS 再加） | 主項 3 實時 |
| ⚠ 多線吞吐未知（CPU 單線 ~5 次/秒；幾條線共用一台 server 未拍板） | 主項 3 實時 |
| ⚠ 安全地基：http 明文、`demo-secret`、資料 In-Memory | 上線前全部 |

---

## 非主項（避免發散）

- **AINAVI EdgeHub 瑕疵檢測線（盒子本身）**：機器不在區網上，**不投入開發**；但 2026-07-24 拍板**自建類似能力**（多模型判斷中樞＝主線，見 `2026-07-24_multi_model_server_architecture.md`），瑕疵以自建 `defect` task 回歸主線（模型來源另議）。
- ~~GPU 加速：可選優化~~ → **GPU(A1000) 已升格為主項 3「多站並行」的前提**（2026-07-24 拍板 server 用 A1000），不再屬非主項。
- **gRPC / MQTT**：條件觸發才做（`.ai/designs/2026-07-14_api_transport_protocol.md`）；HTTP 一次來回、**不輪詢**為既定熱路徑（2026-07-24 再確認）。

---

## 文件地圖

| 主題 | 檔案 |
|---|---|
| **使用流程（操作/排錯手冊）** | `使用流程_中央推論.md`（根目錄） |
| **多站×多模型架構（最新定案）** | `.ai/designs/2026-07-24_multi_model_server_architecture.md` |
| CRNN 引擎接入（倉庫+sidecar 已通，2026-07-31） | `.ai/designs/2026-07-31_crnn_engine_intake.md` |
| API 線交接（先讀這） | `.ai/HANDOFF_API.md` |
| 部署大方向（中央推論 §2.5、模型中樞 §2.6） | `.ai/designs/2026-07-12_api_server_deployment.md` |
| 協定選型（HTTP/MQTT/gRPC） | `.ai/designs/2026-07-14_api_transport_protocol.md` |
| 推論 API 契約 | `.ai/designs/2026-07-14_api_infer_pair_contract.md` |
| Edge↔Server 整合（階段 0-4） | `.ai/designs/2026-07-15_edge_server_integration.md` |
| AINAVI 線盤點（非主項） | `.ai/designs/2026-07-16_ainavi_edgehub_line.md` |
| 每日進度 | `.ai/records/`、`.ai/status.json` |

## 維護規則

1. 完成一項 → 打勾＋補日期；狀態變了 → 改矩陣的格子標記。
2. 新需求進來 → 先對照 3 大主項；對不上 → 先問使用者「這是支線嗎？」再動工。
3. 本檔只追蹤主線。細節寫 `.ai/designs/`，每日進度寫 `.ai/records/` 並同步 `.ai/status.json`。
