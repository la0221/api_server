# 2026-08-22 每日流水

> 承 `.ai/HANDOFF_主程式整併_2026-08-17.md`。
> ⚠ 日期說明：需求書與現場實測是 **8/19**，但**這一整天的實作都發生在 8/22**（本檔）。
> 本日主題：**把 8/19 跨機實測踩到的洞補起來**（需求書 `doc/需求/2026-08-19_站端事件記錄檔與跨機部署改善.md`）
> ＋**父端監控三項現場反映** ＋**移植 `模號檢驗/相機版` 的吹氣與自我強化訓練**。

---

## 1. 起點：現場反映的三件事（使用者當面指出）

| # | 反映 | 本質 |
|---|---|---|
| ③ | 「中央處理，**就算得到圖也是這樣子，根本無法確認是否收到**」 | 父端只答得出「服務活著／模型載入了」，收件流水完全看不到 |
| ④ | 「父端的模型**根本沒得選擇**，哪裡改模型都沒有」 | 父端沒有任何切版入口，只能改 appsettings 重啟 |
| ⑤ | 「**模號穴號就會需要到自己的池去，公母模到自己的地方，瑕疵等**」 | 模型要**按用途分池**；登錄庫早就 task 化，但沒有端點/畫面把它講清楚 |

加上需求書的 5 條（站端事件記錄檔 / 父端視窗自適應 / ScanFolder 硬路徑 / 模型硬路徑 / API 綁定）。

---

## 2. 做了什麼

### ① 站端送檢事件記錄檔（需求 1，最優先）
- 新增 `AIVision.Presentation.Wpf/Services/RouteAEventLog.cs`：append-only JSONL，
  `<程式目錄>\logs\routeA_events_YYYYMMDD.jsonl`（與既有 `AIVision_*.log` 同層）。
- 三種事件 `batch_start` / `item`（每張一筆，主體）/ `batch_end`（統計卡數字直接落地），
  另加 `item_failed`（讀檔/解碼/前處理就掛掉的張數——不能讓檔案裡憑空少幾張）。
- **鐵律照做**：每行寫完即落地（`AppendAllText` 開關檔）；寫檔失敗只記一次 warning，
  **絕不打斷送檢**；`source` 固定 `central`/`local` 不寫中文；不寫影像 bytes 只寫大小。
- ⚠ JSON 一律用 `JsonSerializer` 產生**不自己拼字串**——Windows 路徑反斜線沒跳脫就是壞 JSON，
  事後解析不了＝等於沒記（POC 的 `_log.bat` 踩過）。
- 附帶：站端狀態列顯示記錄檔位置（開頁就顯示今天會寫到哪，不用猜）。

### ② 驗收彙總腳本（需求 1 的驗收條件）
- 新增 `doc/包一包/彙總站端事件log.py`：吃 jsonl → 直接吐驗收表要填的數字。
- **完全不看畫面**算出：送達中央數、本機備援數、**讀值正確率**（檔名帶正解自動比對）、
  **傳輸量縮減%**、p50/p90、沒送出的張數。
- 額外把**中央 vs 本機接管的正確率分開算**——本機備援定位是「不停線」不是「同等準確」，
  混在一起會看不出真實情況（正好對到 8/17 留下的「本機 61%」待決策項）。

### ③ 父端：最近辨識紀錄（反映③）
- API 新增 `RecentInferenceStore`（記憶體環狀緩衝 300 筆）+ `GET /api/infer/recent`、
  `POST /api/infer/recent/clear`。每筆推論（**含失敗**）都留痕。
- **刻意只放記憶體**：這是「現在在收什麼」的監看，不是稽核帳；要留存的在站端 jsonl（原圖也在站端）。
  重啟即清空是預期行為，換來零磁碟寫入、不影響推論節拍。
- 父端畫面接上：時間／來源站／讀值／收到大小／**前處理在哪**／模型版本／ms／**站端原圖位置**，
  讀不到與需複檢的列會變色。狀態卡再加一個 **「本機累計收到 N 張」**——
  「到底有沒有收到」最直接的答案，不用再翻 console。

### ④ 溯源（順手把 8/17 的 D2 補掉）
- 站端送檢時多帶一個 `rawPath` 表單欄位（原圖留站端，只把「它在哪」告訴父端）。
- ⚠ **走 form 欄位不走 HTTP header**——header 只吃 latin-1，中文路徑會讓請求根本送不出去
  （POC 階段為此卡了一整天，症狀是封包從沒離開子機）。實測中文站號＋中文路徑往返無損。

### ⑤ 父端：模型可選 + 按用途分池（反映④⑤）
- API 新增 `ModelPoolsController`：
  - `GET /api/models/pools`——每個用途一張卡：版本清單／現用版本／已載入行程／能不能推論／說明。
  - `POST /api/models/{task}/current`——執行期切現用版本，**免改設定檔免重啟**。
    CRNN 只換預設版本（下一筆自然冷啟）；雙 head 直接載 ONNX（~1s）；
    公母模／瑕疵回 400 並講明「目前只有倉庫能力，還沒有推論端點」。
- `CrnnSidecarService` 加 `SetDefaultVersion()`（執行期覆寫，含版本存在性檢查）。
- 父端畫面「模型池（依用途）」：**模號穴號(CRNN)／模號穴號(雙head)／公母模／瑕疵各一張卡**，
  各自下拉選版本 +「設為現用」，結果（成功/失敗原因）顯示在底部狀態列。
- 修掉一個自打臉顯示：ocr_pair 從畫面切版後「現用 v6.7.2、已載入（尚未載入）」——
  registry 的快取不涵蓋 SwitchableTwoHeadRecognizer，要併進來。

### ⑥ 跨機部署四修（需求 2–5）
| 需求 | 修法 |
|---|---|
| 5 API 綁定 | appsettings 加 `"Urls": "http://0.0.0.0:5030"`＋啟動印 **`[Bind] 實際綁定位址`**，只綁 loopback 再警告一次並講怎麼改 |
| 4 模型硬路徑 | `MoldCodeWarpPolarOptions.ResolveModelPath()`：設定值不存在→相對程式目錄→`<exe>\models\<版本夾>\`→`<exe>\models\`。**跨機不必手改 appsettings** |
| 3 ScanFolder | 找不到資料夾改記 Information（不是 WARN，那不是故障）；支援相對程式目錄 |
| 2 父端視窗 | 把站端的 `WindowSizeAdapter` 複製進 Server 專案並註冊（兩專案刻意零依賴，共用不了，**兩份要一起改**） |

> ⚠ **踩到一個新坑並修掉**：一開始用 `Kestrel:Endpoints:Http:Url` 綁 0.0.0.0，
> 結果它**反過來蓋掉命令列**，連文件教的 `--urls` 都失效（實測 `--urls 5031` 被忽略、仍搶 5030）。
> 改用 `"Urls"` 鍵才是正確優先序：**appsettings < 環境變數 < 命令列**。

---

## 3. 實測（本機，API 另起一份在 5031 不動使用者正在跑的那份）

| 項目 | 結果 |
|---|---|
| Route A 迴歸（8 張 strip） | ✅ 讀值 **7/7 正確 + 1 刻意異常樣本正確**、縮減 **68.5%**、中位數 **46ms** |
| `GET /api/models/pools` | ✅ 四個池分開：ocr_crnn(現用 b3)／ocr_pair(7 個版本)／gongmu／defect，後兩者標「只有倉庫能力」 |
| `POST .../ocr_pair/current` | ✅ 切 v6.7.2 → 200「已載入 v6.7.2，即刻生效」，pools 現用與已載入同步更新 |
| 切到不存在的版本 | ✅ 400「登錄庫（ocr_crnn）找不到版本 'nope99'——請先從發布頁上架」（**不是無聲失敗**） |
| 切 gongmu | ✅ 400「目前只有倉庫能力…還沒有推論端點可切換現用版本」 |
| `GET /api/infer/recent` | ✅ 8 筆全到位：站號／讀值／31KB／isStrip=true／b3／44–46ms |
| 溯源（中文） | ✅ 站號 `ST-測試站`、路徑 `D:\新增資料夾\父子節點POC\...` 往返**完全無損** |
| 綁定自我揭露 | ✅ `--urls 0.0.0.0:5031` → 印出實際位址；`--urls localhost:5032` → 額外跳 loopback 警告 |
| 事件記錄檔格式 | ✅ 用**真正的 `RouteAEventLog` 類別**跑一輪合成資料：5 行全部合法 JSON、中文路徑正確跳脫 |
| 彙總腳本 | ✅ 從那份 jsonl 算出送達/備援/縮減 −68.5%/p50/p90/讀值 2/2，並分開列中央與本機正確率 |

---

## 4. 還沒做 / 待辦

- **實機 GUI 一輪**（站端真的按「開始送檢」→ 產出真的 jsonl → 父端畫面逐筆出現）：
  被「使用者以系統管理員身分執行的舊 `AIVision.Api`(16712) 與 `AIVisionServerConsole`(12940) 鎖住 bin」擋住，
  **無法從這裡終止（存取被拒）**，需使用者手動關掉這兩個視窗。
- E3（logs 唯讀時不打斷送檢）、E9（模型丟 `<exe>\models\` 免改設定）、E10（1024x768 父端視窗）尚未實測。
- 8/17 留下的 **本機接管準確率 ~61%** 仍待決策（彙總腳本現在會自動把這個數字算出來）。
- 公母模／瑕疵**推論端點**仍未開（目前只有倉庫能力）——要開需先定模型與前處理契約。

---

## 4b. 第二輪：使用者看過畫面後的三點回饋

| # | 回饋 | 做法 |
|---|---|---|
| ① | 「那我們**會收到圖片嗎**？還是讓我有選項可以選擇父是否要收圖片」 | 做成**選項，預設關**（Route A 本來就是原圖留站端、父端只收前處理小圖）。新增 `ReceivedImageStore` + appsettings `ReceivedImages`，父端站點細節頁可**即時開關**；開了之後每張落地 `received\yyyyMMdd\HHmmss_fff_站號_序號.png`，超過 `MaxFiles` 自動刪最舊；單筆詳細頁可直接看那張圖。寫檔失敗**不影響推論**（同事件 log 鐵律）。 |
| ② | 「模號穴號不管是 CRNN 還是雙 head，**都是模號穴號站點，所以應該只有一個**」＋「要可以**點進去查看細節**…請用**條列式**…想更詳細可再點進去單獨查看。這個風格三個站點都要有」 | pools API 加 `groupKey/groupName/engineName` → 畫面改成 **3 張站點卡**（模號穴號／公母模／瑕疵檢查），引擎收進卡裡。點卡 → **站點細節（條列式）**：這個站點在做什麼／每個引擎的現用版本·已載入·檔案組成·登錄夾（可切版）／收到的圖要不要留·**存放點**·已存幾張／本站點最近辨識。再雙擊單筆 → **單筆詳細**（全欄位條列 + 父端實際收到的影像）。 |
| ③ | 「子端影像預覽為何是空的？已接上 IDS 相機」 | **暫緩**——使用者回「晚點我等等補給你，第 3 項先 pass」。已先查清：`libs/ids_peak` 在輸出目錄**存在**（我一度誤判成路徑錯，已更正），SDK 路徑不是原因；真正待釐清的是**指哪個畫面**（主頁 Area Scan 才訂閱影格／相機面板有自己的預覽／站端送檢頁根本沒有相機概念只吃資料夾）。 |

**第二輪實測（一樣另起 API 在 5031，不動使用者那份）**

| 項目 | 結果 |
|---|---|
| 站點分組 | ✅ `[moldcode] 模號穴號 — 2 個引擎`（CRNN 字元式/ocr_crnn 現用 b3、雙 head 分類/ocr_pair 現用 baseline）、`[gongmu] 公母模`、`[defect] 瑕疵檢查` |
| 收圖開關 | ✅ 預設 false → 開啟 → 送 3 張 → **3 張全部落地**，統計「已存 3 張 / 94 KB / 上限 5000」→ 關閉回 false |
| 取單筆 / 取圖 | ✅ `GET /api/infer/recent/{seq}` 200；`GET .../image` **32026 bytes，PNG magic 正確** |
| 錯誤路徑 | ✅ 不存在的 seq → 404「找不到流水號 99999」；沒留存 → 講明「父端的留存預設是關閉的」 |
| 迴歸 | ✅ Route A 讀值全對、縮減 68.1%、中位 53ms（改動沒有動到主資料流） |

**沿路修掉**：`Application.Current` 又撞到 `AIVision.Application` 命名空間（CS0234）→ 寫完整名稱；
`RecentInferenceItem` 少 `Timestamp` 欄位（單筆詳細要顯示年月日）→ 補上。

---

## 4c. 第三輪：主頁影像預覽一片黑（IDS 相機）

使用者給了畫面（`VISION/AIVision/AIVision/image.png`）指認是**主頁面板**中央那塊「影像預覽」。
查下去是**兩個 bug 疊在一起**，而且兩個都會造成「全黑且畫面上毫無線索」：

| # | 根因 | 修法 |
|---|---|---|
| **①** | **`appsettings.Development.json` 被無條件載入**，而它把 `Devices:Camera:Type` 蓋成 `Fake`。這台明明接了 IDS 相機，DI 解到的卻永遠是 `FakeCameraPort`（log 實證）。該檔自己的註解就寫著「正式部署刪除或改回」，但沒有任何機制保證 | 改成**要明確指定環境才載入**：`AIVISION_ENVIRONMENT=Development`（或 `DOTNET_ENVIRONMENT`）。預設＝不載入＝走真實裝置。另外 `SetBasePath(AppContext.BaseDirectory)`，免得從捷徑啟動讀不到設定 |
| **②** | 相機**開得起來卻在套參數時整個拋出**：本機這顆 onsemi NOIP1SN1300A 的 `GainSelector` 回報 `IsWriteable()==true`，`SetCurrentEntry` 卻丟 `PEAK_RETURN_CODE_BAD_ACCESS`。例外一路往上炸掉 `OpenAsync` | (a) `ApplyGain` 的 selector 設定單獨包 try/catch——這種相機本來就只有單一 gain，沒 selector 也能設值；(b) **開相機時的「一次套用全部參數」改成逐項 best-effort**：單項失敗只記 warning 不中斷（不同機種支援的節點本來就不一樣）。⚠ 使用者在相機面板**手動調單一參數**時仍照舊拋，UI 才報得出失敗原因 |

**③ 順帶補的體驗問題**：主頁預覽原本**只有按「開始」跑 Area Scan（要工單／PLC）才會訂閱影格**。
改成**一進面板就自動接**（`ShellViewModel.StartLivePreviewAsync`，含防重複訂閱旗標）；
`StopAreaScanModeAsync` **不再順手關掉預覽**（以前一按停止整片變黑，看起來像壞了）。
預覽區中央那行字改綁 `LivePreviewHint`：沒影像時**講出原因**（模擬相機／相機未連線＋錯誤訊息＋去哪查），不再是一片黑。

**實測**：重開站端 App → log 依序出現
`✓ IDS 相機已成功開啟 (AccessType=Exclusive)` → `[LivePreview] ✓ 主頁即時預覽已啟動（IdsCameraPort）` →
`▶ 影像擷取迴圈開始` → `已成功擷取 30/60/…/660 幀影像` **持續累加（約 20 fps）**；
Gain 那項只留下一行 info「此相機不接受 GainSelector='AnalogAll'…略過選擇器、直接套用 Gain 值」，不再中斷。

> ⚠ **副作用要知道**：`appsettings.Development.json` 同時也把 **PLC** 蓋成 `Fake`。
> 現在該檔預設不載入，PLC 會改走 `appsettings.json` 的 `Modbus`（僅註冊，不會自動連線）。
> 要恢復舊行為：設環境變數 `AIVISION_ENVIRONMENT=Development`。

**④ 過曝——第三個 bug（參數根本沒被讀到）**

使用者回報影像出來了但過曝，要我去 `D:\模號檢驗` 查當初調好的值。
查到 `OCR_demo/app/config.py`：**`IDS_EXPOSURE_US = 1300`、`IDS_GAIN = 1.0`**
（沿革就寫在註解：1500µs 過曝→1300µs mean~89/overexp4.7%；6/26 相機變亮，1300→mean107/overexp7.1%，
曾試 900µs 但使用者要求維持 1300）。

對照 AIVision 才發現**參數來源整條是斷的**：

| 位置 | 值 | 真的被讀嗎 |
|---|---|---|
| `appsettings.json` → `Devices:Camera:Options:{ExposureTimeUs:8000, Gain:8.0}` | 8000 / 8.0 | ❌ **完全沒人讀**——`IdsCameraOptions` 根本沒有 `Options` 子物件 |
| `configs/camera-ids.json` | 15000 / 2.0 | ✅ **這份才是真的**（`IdsCameraControlPort` 讀寫，相機面板存檔也寫這裡） |

log 印的是 `ExposureTime = 14999.67µs`——正是 `IdsCameraSettings` 的**類別預設 15000**，
證明兩邊 appsettings 的值都沒生效。**比實測調校值大一個數量級，當然整片白。**

**修法**：`configs/camera-ids.json` 改成 **1300µs / Gain 1.0**（專案內與輸出目錄都寫），
檔頭用 `//` 欄位寫清楚「這份才是真正被讀的」「值取自模號檢驗實測」「沿革」；
並把 `appsettings.json` 那塊死設定標上 `//Options` 警告「沒有任何程式在讀，別在這裡調」。

**實測**：`✓ ExposureTime = 1299.6666µs (調整後)` → 預覽啟動 → 影格持續累加。

## 4d. 第四輪：把 `模號檢驗/相機版` 的兩項新功能包進來

使用者指定移植兩項：**① 自我強化訓練 ② mismatch 觸發吹氣**。
`模號檢驗/.ai/PR/2026-08-22_main-vs-dev比較與移植計畫.md` 已把兩項的檔案清單列得很清楚，直接照著讀。

### 先確認：AIVision **已經有**混料氣吹，但少了現場要的東西

| | AIVision 原況 | 相機版 |
|---|---|---|
| 觸發判定 | ✅ `MoldCodePairVerifier.Decide` → `ShouldReject` | ✅ server 回 `status` |
| 輸出 | **PLC(Modbus)** `IoCommand.Blow()` 立即寫 | **TCP 送一行 JSON 到另一台 IO 電腦** |
| 延遲吹氣 | ❌ | ✅ `DelayMs`（等工件走到吹嘴） |
| 混料/NG 分別開關 | ❌ | ✅ |
| 佇列 + 去重 | ❌ | ✅ |
| 設定視窗 + 測試吹氣 | ❌ | ✅ |

相機版走 TCP 的原因是**現場 IO 卡在另一台電腦上**（妍華那台），PLC 那條吹不到它。
→ 使用者裁示：**只加 TCP**（PLC 那條完全不動）、**訓練跑父端**。

### ✅ 已完成：吹氣（TCP）

| 檔案 | 作用 |
|---|---|
| `Application/Ports/Devices/IBlowOutputPort.cs` | 輸出 port + `BlowRequest`（含預期/實際/信心，讓 IO 端能單獨對帳） |
| `Application/Ports/Devices/IBlowDispatcherPort.cs` | 派送 port：**排隊+延遲+去重**，`Enqueue` 立即返回 |
| `Infrastructure/Devices/Blow/BlowOptions.cs` | 設定（Enabled/DelayMs/兩個原因開關/Host/Port/Channel/Output） |
| `…/TcpBlowOutput.cs` | 送 JSON 到 IO 監聽程式，**協定與相機版完全一致**（現場那支監聽程式不必改） |
| `…/LogBlowOutput.cs` | 只寫 log（開發機／驗收用） |
| `…/BlowDispatcher.cs` | 背景 worker：延遲→送出；去重表有上限（產線整天上萬片，無上限＝記憶體洩漏） |
| `ViewModels/BlowSettingsViewModel.cs` + `Views/BlowSettingsView.xaml` | 設定視窗 + **測試吹氣** |

**接線點**：`VerifyMoldCodePairCycleCommandHandler` 寫完 PLC IO 之後多排一筆。
⚠ 只排隊不等待——熱迴圈不能為了吹氣多花任何時間。

**設定存 `configs/blow.json`**（不是 appsettings）：`DelayMs` 要在現場邊試邊調，
不該逼人改部署設定還重啟。該檔以 `reloadOnChange:true` 掛進設定系統 → **存檔即生效**。

**實測（起一個假的 IO 監聽程式在 127.0.0.1:5001）**

```
[    1ms] 排入 T1 MISMATCH  -> True
[   35ms] 排入 T1 重複      -> False   ← 去重生效
[   35ms] 排入 T2 NG        -> False   ← NG 開關關閉生效
[   35ms] 排入 T3 MISMATCH  -> True
[   35ms] ★ Enqueue 全部返回 = 沒有阻塞熱迴圈

IO 端收到（只有 2 發，延遲 800ms）：
[18:41:57.799] ch=0 reason=MISMATCH id=T000001 expect=M101/03 got=M17/03 conf=0.95/0.9
[18:41:58.617] ch=0 reason=MISMATCH id=T000003 expect=M101/03 got=M17/03 conf=0.95/0.9
```

另驗設定熱重載：改檔後 `IOptionsMonitor` 讀到 Delay 850→**1234**，免重啟 ✓

### ✅ 自我強化訓練（跑父端，已完成並實測）

**前置缺口先補**：`AIVision.MoldCode.Onnx/MismatchArchive.cs` + `IMismatchArchivePort`。
抓到混料的當下，**正解（工單預期）與模型答錯的內容同時在手上** →
存 `_MISMATCH\yyyyMMdd\exp_M108-14_got_M17-14_202608221912_WO-xxx_1.jpg`——
**檔名本身就是標註**，不需要另一份標註檔（多一份就有不同步風險）。只存 MixedAlarm（NG 沒有正解可寫）。
預設**開啟**：抓到卻沒存等於白抓。實測含惡意輸入：`../../etc` → `....etc`、`0/3` → `03`、`WO<>|x` → `WOx`，路徑跳脫被清掉；沒工單則整段跳過。

**API（中央推論機）**

| 檔案 | 作用 |
|---|---|
| `Services/TrainingOptions.cs` | python 入口／權重／閘門／逾時；`CrnnRehearsalPath` **必填** |
| `Services/TrainingRun.cs` | run 狀態機（Queued/Running/Passed/Failed/Error/Cancelled）+ 滾動 log |
| `Services/TrainingService.cs` | 寫 manifest → driving python → 解析進度/結果 → 套閘門；**同時只准一個 run**（訓練吃滿 GPU） |
| `Controllers/TrainingController.cs` | 資料集上傳/列表、開始/查詢/取消、**上架** |

**與 python 的契約完全沿用相機版**，現有腳本不必改：
`training_request.json`(schema_version=1) → stdout `[PROGRESS] n msg` → `training_result.json` → exit 0=過/2=沒過。

**兩道人為關卡（照抄相機版，這是整套的價值所在）**
1. 訓練**永不覆蓋** production 權重，一律開新 run 夾
2. 過閘門只代表「可以考慮」→ **使用者按上架**才進登錄庫 → **還要**到模型池按「設為現用」才真的生效

⚠ 中央端刻意**不讓 python 自己上架**（manifest 一律送 `register_catalog=false`）：
候選一律回到 API，走既有 `/api/models` 發布流程（有 md5／溯源／版本不可變），比相機版的 CSV 清單完整。

**父端 UI**：`TrainingViewModel` + `TrainingWindow`——選用途/head/資料集 → 開始訓練 →
run 清單（狀態/進度/量測，可上架的列標綠）+ 右側即時執行紀錄 → 取消／上架。

**實測（用一支照契約寫的假訓練腳本 + 模號檢驗真實的混料圖）**

| 驗的事 | 結果 |
|---|---|
| 上傳資料集 | ✅ 5 張真實混料圖 → `training_datasets/mismatch-0819/M108/` |
| 參數驗證 | ✅ 資料集不存在＋run 名稱不合法**一次講完兩個原因** |
| CRNN 排練集閘門 | ✅ 未設定 → 400「少了它，一批修正資料就可能把舊標籤原本會的能力洗掉」 |
| 訓練驅動 | ✅ Queued→Running→Passed，`[PROGRESS] 10/40/88/100` 進度與 stage 都吃到 |
| 過閘門 | ✅ accuracy 0.964 ≥ 0.90 → `Passed`、`canPublish=true` |
| **沒過閘門** | ✅ 門檻拉到 0.99 → `Failed`、`canPublish=false`、上架回 400 並附未通過原因；**權重仍留著供查** |
| 上架 | ✅ 進 `D:\AIVisionModels\pairs\`，模型池立刻看得到；`_publish.json` 的 `publishedVia=self-training` 且含 run/資料集/張數/metrics 溯源 |
| 版本不可變 | ✅ 重複上架 → 409 |

> ⚠ 測試時我把假 ONNX 上架到**真實的**登錄庫，事後已刪除 `pairs/v671-st0819a`，
> Release 的 `Training:Enabled` 與 `ReceivedImages:Save` 也已還原為 false。

## 4e. 自測：F 類（吹氣）+ G 類（訓練）全部跑完

使用者要求「你自己測試完成」——不留給人工驗。做了兩層：

**① 端到端（最有價值的一層）**：寫假 PLC／相機／辨識器，但**吹氣與歸檔都用正式實作**，
直接驅動真正的 `VerifyMoldCodePairCycleCommandHandler`。這才證明得了「接線點對不對」。

```
判定：Outcome=MixedAlarm  AirBlown=True  讀值=M17/14
✔ F1a 混料判定成立
✔ F1b PLC 仍照舊下 Blow（原有那條沒被動到）
✔ G1  混料圖已歸檔  exp_M108-14_got_M17-14_202608221921_1.jpg
✔ G1b 檔名自帶正解
✔ F5  熱迴圈沒被吹氣拖累 → 整個週期 62 ms
✔ G2  歸檔失敗不打斷判定與吹氣
IO 端收到：ch=0 reason=MISMATCH id=T000001 expect=M108/14 got=M17/14 conf=0.97/0.96
```

**F5 是這輪最關鍵的一項**：關掉 IO 監聽程式後重跑，判定／PLC 氣吹／歸檔全部照常，
而且**整個週期只花 62 ms**——TCP 連線逾時設 800ms，若是同步等待一定會露餡。
這就是「只排隊不等待」的證據，不是靠讀程式碼自我保證。

**② API 層 G3–G9**：資料集用 `D:\模號檢驗\_MISMATCH` 的**真實混料圖**，
後端用照契約寫的假腳本。16 項全過（含反面：未達門檻 → Failed／不可上架／權重仍留著／硬上架被擋）。

### ⚠ 自測抓到的兩件事

1. **G9 一度顯示 FAIL**。追時間戳發現 `g-run-a` 在 40.126 就跑完，而 `g-run-x` 41.166 才送出
   ——**是我的測試腳本太慢**（每次 urllib POST 約 2 秒），守衛根本沒被觸發。
   把假腳本每階段拉長到 3 秒後重測 → 確實回 400「已有訓練正在執行」。
   **不是產品 bug，但原本那個「通過」是假的**，所以重測到真的觸發為止才算數。
2. 歸檔沒工單時檔名多一段 `_NA` → 改成整段跳過（與相機版行為一致）。

### 測試污染清除

測試會把假 ONNX 上架到**真實的**登錄庫，所以特別確認清乾淨：
- 已刪 `D:\AIVisionModels\pairs\g-ver-a`、`pairs671-st0819a`（其餘 7 個版本原封不動）
- Release 的 `appsettings.json` 已還原：`Training:Enabled=false`、`YoloEntry=""`、門檻 0.90、`ReceivedImages:Save=false`
- `training_runs` / `training_datasets` / `received` 測試資料夾、scratchpad 測試專案全部刪除

## 4f. 實際走 UI 自測（不只打 API）

使用者要求「自己走我們設定好的 UI 然後作假的訓練」。用 **UI Automation 實際操作父端視窗**
（等同人手點擊）＋螢幕截圖存證，截圖放 `doc/包一包/screens/`。

走完整條：開父端 → 點「自我強化訓練」→ 填 run 名稱／備註 → 開始訓練 → 看進度與 log →
切換選取的 run → 上架 → 回頭查登錄庫。**每一步都通過**，含最重要的那句：
狀態列顯示「✔ 已上架 ui-run-M108b。⚠ 上架不等於啟用——要到站點細節的模型池按『設為現用』才會真的用它」，
而登錄庫的**現用版本確實仍是 `baseline` 沒被動**。

### ⚠ 走 UI 才抓到的兩個問題（打 API 測不出來，已修）

| # | 問題 | 修法 |
|---|---|---|
| **1** | **執行紀錄不會即時更新**——只在「換選 run」時載入一次。第一次截圖就抓到：狀態欄都跑到 `通過驗證` 了，log 卻還卡在最前面 3 行。真的跑 30 分鐘的訓練會完全看不到進展 | 新增 `RefreshSelectedLogAsync()`：只在「還在跑」或「狀態剛變了」時重載（跑完的 run 不必每 3 秒重抓） |
| 2 | DataGrid 列的無障礙名稱是型別名 `...ViewModels.TrainingRunRow`，螢幕閱讀器與 UI 自動化都讀不出是哪一筆 | `TrainingRunRow` / `RecentInferenceRow` 加 `ToString()` |

> **這就是為什麼要真的走 UI**：兩個問題都在「API 全綠」的情況下存在——
> API 每次都回完整 log，是畫面沒去重抓。只打 API 永遠測不到。

**測試污染已清除**：`pairs/ui-run-M108b` 已刪（其餘 7 版原封不動）；
API Release `appsettings.json` 還原（`Training:Enabled=false`、`YoloEntry=""`）；
父端 Release 位址還原 5030；`training_runs`／`training_datasets` 已刪；scratchpad 清空。

## 4g. 吹氣也實際走一次 UI

同樣用 UI Automation 操作站端 App（另起假 IO 監聽在 127.0.0.1:5001）。
帳密由腳本從 appsettings 讀取，**沒有寫進任何指令或紀錄**。

| 步驟 | 結果 |
|---|---|
| 登入前／後看系統選單 | ☑ **登入前沒有「吹氣觸發設定」，登入 Engineer 後才出現**（權限控制生效） |
| 還沒啟用就按「測試吹氣」 | ☑ 擋下「⚠ 目前是停用狀態：請先勾『啟用』並按儲存，再測試。」 |
| 勾啟用＋延遲 1200ms＋儲存 | ☑ 「已儲存並即刻生效（免重啟）」；`configs/blow.json` 內容正確（巢狀 Devices:Blow） |
| 按「測試吹氣」 | ☑ `21:21:04.554` 按下 → IO 端 `21:21:05.929` 收到＝**1375 ms**（設定 1200＋開銷） |
| **熱重載**：改 300ms → 儲存 → 再測 | ☑ `55.062` → `55.424`＝**362 ms**，**沒重開程式** |
| **斷線**：關掉 IO 監聽 → 按測試 | ☑ 按鈕 **73 ms 就返回**（逾時設 1500ms，同步等待會 >1500）；4 秒後視窗仍可操作；log 出現 `[WARN] 送出失敗…（辨識流程不受影響）` |

> 這輪**沒有抓到新 bug**——與訓練那輪不同，吹氣的 UI 很薄（設定＋兩顆按鈕），
> 邏輯都在已經端到端測過的 `BlowDispatcher`。但「登入前選單不顯示」「未啟用時擋下測試」
> 這兩個只有走 UI 才驗得到。

**測試污染已清除**：測試寫出的 `configs/blow.json` 已刪除（回到 appsettings 預設 `Enabled=false`，
免得接手的人莫名其妙在吹氣）。截圖：`doc/包一包/screens/吹氣_設定視窗.png`、`吹氣_測試送出.png`。

---

## 5. 文件

- `doc/包一包/01_測試計畫.md`：新增 **E 類 E1–E10**（全部來自現場踩到的問題）；
  D1/D2/D3 標記為**已實作**並改導到 E 類；新增 D5（父端紀錄只在記憶體，刻意）。
- `doc/包一包/03_操作SOP與故障排除.md`：父端 3 步加「看 `[Bind]` 綁定位址」「本機累計收到」「怎麼換模型」；
  故障排除表換掉「最近紀錄永遠空白＝未實作」那列，改成**「累計收到是 0＝圖根本沒到這台，別往模型方向查」**；
  速記加綁定與收工彙總指令；已知限制改寫。
- `doc/包一包/彙總站端事件log.py`：新增。
- `doc/包一包/screens/`：**走 UI 的截圖存證**（父端站點三張卡／訓練開啟·進行中·完成含log／吹氣設定視窗·測試送出）。
- `doc/包一包/01_測試計畫.md`：再加 **E11–E14**（站點卡合併／站點細節條列式／單筆詳細／父端要不要收圖）＋ D6（公母模‧瑕疵推論端點未開）。
- `doc/包一包/02_驗證與記錄.md`：新增 **E 類實測結果**整段（E1/E2/E4–E8/E11–E14 全過；E3/E9/E10 未測）。
- `doc/包一包/03_操作SOP與故障排除.md`：父端操作補「點卡看細節／雙擊看單筆／要不要留圖」；
  故障排除加「單筆詳細看不到圖＝預設不留存」「留存的圖會自動汰舊」「父端重啟紀錄全空是刻意的」。
- `doc/包一包/01_測試計畫.md`：再加 **E15/E16**（主頁影像預覽會動／沒相機要講原因）、**F1–F6**（吹氣）、**G1–G9**（訓練）。

---

## 6. 交接

📄 **`.ai/HANDOFF_吹氣與自我強化訓練_2026-08-22.md`** —— 下個 session 先讀那份。
含核心定案（吹氣只加 TCP／訓練兩道人為關卡）、影像預覽三個 bug、測試結果、
**待做與待決策**（真實 GPU 訓練、吹氣現場對延遲、本機接管 61%、ROI 疊圖）、踩坑紀律。
