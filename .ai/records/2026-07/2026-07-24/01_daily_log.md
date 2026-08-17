---
date: 2026-07-24
type: daily_log
project: AIVision（.NET8 WPF 產線檢測 App）— 主線目標定錨（ROADMAP）
tags: [AIVision, ROADMAP, 主線, 版本控管, 隔離試模, 模式矩陣, AINAVI]
status: draft
---

# Daily Log - 2026-07-24

## 1. 今日主題

上半：使用者以白板討論**拍板 3 大必要主項**，產出 `ROADMAP.md` + CLAUDE.md 指引（定錨、防走偏）。
下半：使用者拍板繼續推進＋納儀表板白名單 → 實作**「線上×離線批量試模」**（主項 2/3 交集：批量頁加來源選擇，整批走 server 出準確率報告），build 0 錯、API/App 已起供 UI 實測。

## 2. 進度

- **釐清白板架構**：UI(edge) 送圖 → HTTP(ip:port) → 推論服務回 JSON；「api server」= 白板藍字 = 我們自建的 `AIVision.Api`（與 AINAVI 盒子是兩個並存的推論服務）。
- **3 大主項拍板**（使用者原話歸納）：
  1. **線上版本控管**：模型 or 軟體版本可線上控管（模型部分 = 部署書 §2.6 模型中樞；軟體部分**範圍待拍板**）。
  2. **線上推論＝隔離試模**：線上模型未下載、或怕下載後污染本地模型進度 → 走線上。（我們已建的 `/api/infer/pair` 天然支援：server 版本獨立、回應回填 modelVersion）
  3. **全模式矩陣**：線上/本地模型 × 離線（拍過的圖）/實時（傳輸即時）四格全通。現況：本地兩格 ✅、線上×離線 🔶（單張驗收已通、批量待做）、線上×實時 ⬜（整合書階段 3/4）。
- **產出 `ROADMAP.md`（根目錄）**：3 大主項 + 勾選清單 + 2×2 矩陣現況 + 擋路風險 + 非主項清單 + 文件地圖 + 維護規則。
- **CLAUDE.md 加「主線目標」指引**：每個未來 session 開場即見錨點，開新工作前先對照。
- **AINAVI 定調**：使用者確認瑕疵模型「跟我們現在要做的不一樣」→ ROADMAP 列為**非主項、不投入開發**；去留仍待 ainavi 設計書 §7 拍板。

## 2.5 下半進度：線上×離線批量試模（實作完成）

- **VM（`MoldCodePairBatchViewModel`）**：
  - 新增 `UseRemoteSource`（預設 false=本機）；`RunBatchAsync` 依來源分支。
  - **中央來源不需本地模型**（隔離試模精神：`線上模型尚未下載也能試`）——只有本機來源才要求先載入版本。
  - 中央來源**先健檢**（連不上/沒模型 → 不開跑、訊息指路驗收按鈕）；批量中**連續 3 次傳輸失敗自動中止**（避免整批 350ms 逾時慢磨）；「無鏡片/讀不到」是有效觀測照常記錄、不算故障（沿用 fail-closed 語意）。
  - 新增 `RecognizeOneRemoteAsync`：讀圖 → `RemotePairRecognizer.RecognizeAsync` → 圓圖標註仍本機產生（同組前處理參數、僅目視用），讀值以 server 為準。
  - **準確率報告**：`來源=中央伺服器(版本)　張數/比對/模號/穴號/雙軸命中率　|　單張來回 p50/p95（server 推論 p50/p95）`——來回 vs server 純推論並列，網路開銷一眼可見。
- **XAML（`MoldCodePairBatchView`）**：①-5 區塊改「推論來源」RadioButton（本機 ONNX / 中央伺服器，執行中鎖定）；「套用相機ROI」勾選在中央來源時停用（server 收已裁圖 Roi=0，勾了也沒意義）。
- **儀表板**：發現 `projects.json` 白名單已含 aivision（使用者先加了）→ 跑 `build_dashboard.py` 上板成功。
- build 0 錯；API（Release, `ready/baseline/20/18`）與 App 已啟動，**待使用者 UI 實測一輪**（通過後 ROADMAP 矩陣「線上×離線」格轉 ✅）。

## 2.6 實測回饋修正（逾時）＋使用流程文件

- **使用者實測驗收按鈕**：健檢 8ms ✅、推論 ❌ 逾時(>350ms)。根因＝**自己造成的設定矛盾**：TimeoutMs=350 照 Passes=1(191ms) 訂，但 server 保守維持 Passes=2（實測 317-326ms、來回 387-463ms）→ 必逾時。修正：試模逾時調 **2000ms**（appsettings+options+設計書同步），並言明「試模逾時 vs 生產逾時（<節拍）是兩回事、階段3要分開設」。用同張 M83 圖 HTTP 重現 3 次皆過。詳見 02_bug_notes 坑 1。
- **試模新發現**：v671 baseline 把 M83 讀成 M58（conf 0.596；穴號 01 正確）。查 ONNX metadata 確認 **M83 在 20 類字集內** → 是模型弱點非字集問題；0.596 < 門檻 0.60，生產端 fail-closed 會擋。n=1 不下結論 → 待批量跑整個 M83 資料夾（正好是跨模號驗證實例）。
- **產出《使用流程_中央推論.md》**（根目錄，使用者要求）：啟動 server、驗收按鈕解讀表、批量試模步驟（正解資料夾結構）、報告怎麼讀、疑難排解表（含本日逾時案例）、設定速查。ROADMAP 文件地圖已連結。

## 2.7 面板收斂評估（doc/ 三件套，未動 code）

使用者指出面板 12 項太亂、功能重複 → 產出三份評估文件（**本輪不動 code**）：
- `doc/2026-07-24_面板功能評估.md`：12 項逐一盤點（選單→視窗對應已核實）。三個亂源：**離線測試 ×3**（模號離線辨識/雙head頁/Offline測試模式）、**模型管理 ×3**（雙head上半/離線自建/AINAVI線上）、**選單自身重複**（系統選單重列 3 項）＋ 3 個零綁定死視窗（CameraTest/LightControl/LightDeviceScan）。
- `doc/2026-07-24_面板收斂計畫.md`：12→8（生產/模型與測試/硬體/開發 四群）。關閉入口：線上模型管理(AINAVI)、Offline 測試模式；降級到開發群：模號離線辨識、離線模型管理（**單 head 仍被 AreaRunService/單head週期引用，降級不刪**）；批量推論保留+重命名（唯一寫記錄的批量）。**兩階段**：Phase 1 只動選單（可回退）→ 兩輪復測全綠 → Phase 2 才刪檔（附完整檔案清單與「不動」清單）。⚠ 專案無 git，刪檔前必須 git init。
- `doc/2026-07-24_面板收斂測試計畫.md`：**Agent 自動測 A1-A7**（build/選單靜態驗證/單元/啟動smoke/API回歸/中央批量回歸/FlaUI走查(建議)）＋ **人工兩輪復測制 M1-M8**（R1 全項→修→R2 全項重跑；含三帳號權限矩陣、熱迴圈不受影響、回退驗證）＋ 基準數據表。

## 2.8 面板收斂 Phase 1 執行 ＋ Agent 測試第 1 輪（全綠）

使用者核准後執行 **Phase 1（只動選單，未刪檔）**：
- `ShellView.xaml`：移除「線上模型管理(API)」「Offline 測試模式」入口；系統選單去重（只剩離開）；面板分組=生產3/模型與測試2/硬體2＋**「開發」子選單**收 3 項（模號離線辨識（單head）/離線模型管理（單head）/新增測試檢測記錄）；批量推論→批量推論（工單核對）。
- **權限語意零變動**：沿用 IsEngineerOrAbove/IsVendor 既有綁定；ShellViewModel 一行未改。
- **回退備份**：`doc/test/phase1_backup/ShellView.xaml.bak`（git 未 init 前 M8 依此）。
- **Agent 測試第 1 輪 A1-A6 全綠**：build 0 錯×2、選單靜態驗證過（教訓：A2 必須只比對 Header 屬性，全文 grep 會被 XAML 註解誤報——已回寫測試計畫）、單元 52/52、App smoke 存活、API 契約 5 項全符、**A6 批量回歸 179/180=基準持平**（server p50 288ms）。結果已記入測試計畫 §E。
- 測試計畫同步 3 處：A2 檢法修正、M8 改備份還原法、§E 執行紀錄。

## 2.9 人工測試執行單（補路徑選項）

使用者指出人工測試缺實際路徑無法執行 → 產出 `doc/2026-07-24_人工測試執行單.md`：
- **所有路徑都先核實過才寫**：App exe 啟動法（含「必以 exe 目錄為工作目錄，否則登入清單空」）、API 啟動指令與 health URL、三帳號密碼表（op1/eng1/vendor）、測試資料夾（M101 全夾 18 穴、M101\01 小跑、M83 夾 **12 穴**且無 06 屬正常）、pairs 三版本、M8 回退備份。
- M1 直接畫出**預期選單結構全文**供比對；M2 給三帳號各自預期可見項；M3 給參考基準（179/180、server p50≈288-320ms）；M6 標明需站機、辦公室可 N/A。
- M8 改為**雙向備份純複製**自助操作（舊版 `ShellView.xaml.bak` ↔ 新版 `ShellView.xaml.phase1_new`，都在 doc/test/phase1_backup/）——不依賴 agent 在場。
- 測試計畫 §D 已指向執行單（計畫=規格、執行單=實操）。

## 2.10 測試輔助 UI（依使用者實測回饋兩則）

- **「路徑選項應在 UI 內」**（前一則「給路徑」是筆誤）→ 雙head/單head 批量頁「測試資料夾」改**可編輯 ComboBox**：預設項來自 appsettings 新區段 `TestImageFolders`（M101 全夾/M101\01/M83 三項，正斜線路徑），仍可貼任意路徑、瀏覽按鈕保留。新增 `Models/TestImageFolderOptions.cs`。
- **「系統選單不能只剩離開，要能選 API server」**→ 新增 **系統→API 伺服器設定**（工程師以上）：`ServerSettingsView/VM`——KnownServers 下拉（appsettings `InferenceServer:KnownServers`）＋手填、「套用並測試連線」直接改寫 DI 內共用 `InferenceServerOptions` 實例 → **執行期全域生效**（驗收按鈕/中央批量立即改打新位址），重啟恢復 appsettings；視窗註明永久修改方式。`InferenceServerOptions` 加 `KnownServers`。
- 坑：python heredoc 寫 JSON 中文路徑反斜線逸出寫壞檔 → 改用**正斜線路徑**（JSON 合法、.NET 接受）並以 Read+Edit 精確修復。
- Agent 第 2 輪：A1 ✅、A2 ✅（含新選單項）、A3 52/52、A4 ✅（設定到位）；A5/A6 未重跑（server 零改動）。執行單新增 **M3b（API 伺服器設定）**、M1 樹/M3 步驟同步；收斂計畫補 Phase 1 補充節。

## 2.11 API 伺服器設定：自建接口 + 持久化（依使用者回饋「AINAVI 當初有這功能」）

- **加入清單 / 從清單移除**兩鈕（自建接口）；清單與**最後套用位址**持久化於 `%LocalAppData%\AIVision\inference_servers.json`（新 `InferenceServerListStore`）。
- **刻意不寫 bin appsettings** —— 直接套用 AINAVI 線 07-16 記過的坑（models.online.json 寫 bin、rebuild 被蓋掉）；appsettings 的 KnownServers 降為首次種子。
- App 啟動時（Host start 後）還原最後套用位址 → **重啟仍記得**（原本「重啟恢復 appsettings」的行為已升級，視窗註記同步改）。
- **遺留連線盤點**寫入收斂計畫 §2.5：AINAVI 三埠（5001/8001/9080@192.168.1.95）、舊 Devices:Ai localhost:8001、硬體（光源/PLC）各自處置；**AINAVI 端點刻意不匯入新清單**（不是 /api/infer 接口，混入必誤導）。
- Agent 第 3 輪：A1 ✅、A3 52/52、A4 ✅；執行單 M3b 擴為 8 步（自建/重啟持久化/移除）。

## 2.12 白板架構定案：多站點 × 多模型判斷中樞（討論輪，未動工）

使用者出白板圖 + 四點拍板，產出 `.ai/designs/2026-07-24_multi_model_server_architecture.md`：
- **A**：server 只回 JSON+狀態，edge 依設定執行動作 → **鐵律不變**（觀測在 server、決策/動作在 edge、逾時 fail-closed）。
- **B**：HTTP、**不輪詢** → 圖上③圖像通知+⑤判斷通知＝**一次 request/response**，無推播無 MQTT。
- **C**：做成 **AINAVI 類似的多模型中樞** → task 化：`ocr_pair`(已通)→`gongmu`(跨專案引入，97.3% 分類器)→`defect`(自建，AINAVI 角色自家版)；補模型管理面（=主項1 API，銜接 publish md5 治理）。
- **D**：GPU server = **A1000**；2-3 站**並行不排隊** → ⚠ 點出現況真瓶頸：server 辨識器對 InferenceSession **上鎖 → 並發會排隊**（第2站 +385ms、第3站 +770ms，正是使用者怕的 lag）。解法排序：GPU 壓單張 → 解串行（ORT Run 官方執行緒安全，可並行 Run 或 session 池）→ 2-3 並發壓測。
- 契約演進（相容式）：`POST /api/infer/{task}` + `stationId` + 共通信封；`/api/infer/pair` 保留為別名。分階段 P-A~P-F。
- ROADMAP 同步：GPU 從「可選優化」升格「多站並行前提」；主項3 加多站並行項；AINAVI 節補「自建類似能力=主線」；文件地圖收錄新架構文件。

## 2.13 架構 §8 待確認回覆（三 task 狀態；其餘未知→全配預設不擋路）

使用者回覆：`ocr_pair` 幾乎完成｜`gongmu` 本人進行中｜`defect` 未開工；其餘五項未知。設計書 §5/§8 已更新：A1000 未知→RTX 3050 先代量；站頻未知→先用 <400ms×3 站假設；gongmu export 使用者自己弄→**我方出「接入規格」**（onnx+names+前處理參數+md5 治理）等對接；defect 只佔 task 名；stationId 先自由字串。**結論：五個未知全部不擋 P-A~P-C。**

## 2.14 明日開工計畫（使用者宣告，2026-07-25 執行）

使用者三目標 vs 現況：①本地起 server+HTTP 自呼＝**已就緒**（使用流程 §1）②**獨立 UI 模擬 edge＝明日主菜**：新蓋 `EdgeSimulator`——獨立小視窗、**零依賴主 App**（純 HttpClient，證明第三方上位機只靠 HTTP 可接=契約試金石）；欄位=Server/stationId/選圖/選資料夾，顯示**原始 JSON+狀態碼+延遲**＋解析示意（對齊拍板 A：edge 靠 JSON+狀態做動作）。加碼：開 2-3 實例同時送=多站並行模擬，直接實測排隊 lag（驗架構書 §4）③線下模式手動選資料夾＝**已就緒**（批量頁 來源=中央伺服器，=執行單 M3）。
動線：起server→主App批量線下→蓋Simulator→單張+資料夾重驗→2實例並行加碼。

## 3. 待辦 / 未決

- **【使用者實測】**批量頁選「中央伺服器」→ 選 M83 或 M101 資料夾 → 執行批量 → 看準確率報告（通過後 ROADMAP「線上×離線」轉 ✅；M83 整夾命中率順帶回答跨模號疑問）。
- 【使用者拍板】主項 1 的「**軟體**版本控管」範圍：僅檢查？下載？自動更新？
- 下一步：**階段 3 手動開關 + SRV 燈**（主項 3 線上×實時前半）——需先定狀態模型（週期健檢 + 專屬 event）。
- 沿用未解風險：Passes=1 大樣本驗證、TimeBudgetMs=120 矛盾、多線吞吐、安全地基。

## 4. 產出

- **新增** `ROADMAP.md`（根目錄錨點）＋ `CLAUDE.md` 指引段。
- Presentation.Wpf：`ViewModels/MoldCodePairBatchViewModel.cs`（+UseRemoteSource、遠端批量分支、RecognizeOneRemoteAsync、來源化報告）；`Views/MoldCodePairBatchView.xaml`（+來源 RadioButton、ROI 勾選停用條件）。
- 儀表板：aivision 上板（快照 + index 重生）。
- 本日 records + `status.json` 同步。

## 5. 今日一句話總結

3 大主項定錨 `ROADMAP.md` 後立即補上第一格：批量頁新增「本機/中央伺服器」來源選擇（中央=隔離試模、不需本地模型、健檢預檢、連續失敗自動中止、報告含 server p50/p95），build 0 錯、API+App 已起，待 UI 實測一輪即可把「線上×離線」轉 ✅；aivision 已納入專案管理儀表板。
