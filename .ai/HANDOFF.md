# AIVision 交接文件（HANDOFF）

> 最後更新：2026-07-14
> 用途：讓下一個對話/session 無縫接手。先讀本檔，再看 `.ai/records/2026-07/{01,02,06,14}/` 逐日細節。
> **API server 這條線另有專檔：先讀 `.ai/HANDOFF_API.md`。**

---

## 0. 一分鐘摘要

把「模號(mohao) + 穴號(xuehao) **雙 head** warpPolar 辨識」整合進 **AIVision**（.NET8 WPF 產線檢測 App）。

- **線下模型流程**（載入版本→離線逐張測試/視覺化→批量核對→寫歷史）已可用且體感良好。
- **工單**已補齊（預期碼連動模型、目前工單持久化+同步、可編輯）達 8/8/8。
- **比例佈局**：活躍流程頁（工單管理/歷史/辨識過程）已修（07-06）；周邊頁待掃。
- **API server（新線，07-14）**：用途定為**中央推論 + 模型發佈中樞**；`POST /api/infer/pair` 雙 head 中央推論已實作、DI 崩潰已解、端到端跑通。待配真模型量延遲。詳見 `HANDOFF_API.md`。

**目前狀態：WPF build 0 錯、App 可正常執行；API build 0 錯、Development 可啟動。**

---

## 1. 專案 / 建置 / 執行

- 方案：`d:\新增資料夾\VISION\AIVision\AIVision\AIVision.sln`（clean architecture 多專案）。
- 目標框架：`net8.0-windows`；本機 SDK 為 .NET 10（可建 net8）。
- **建置**（PowerShell，先 cd 到方案夾）：
  ```
  cd "d:\新增資料夾\VISION\AIVision\AIVision"
  dotnet build "AIVision.Presentation.Wpf\AIVision.Presentation.Wpf.csproj" -c Debug
  ```
- **執行檔**：`...\AIVision.Presentation.Wpf\bin\Debug\net8.0-windows\win-x64\AIVision.exe`
  - ⚠️ **必須用 exe 所在目錄當工作目錄啟動**，否則 appsettings 讀不到 → 登入清單為空：
    ```
    $dir="...\win-x64"; Start-Process "$dir\AIVision.exe" -WorkingDirectory $dir
    ```
- **登入帳密**（appsettings.json）：`vendor`/`admin888`（廠商，最高權限）、`eng1`/`1234`（工程師）、`op1`|`op2`/`1234`（作業員）。雙 head 相關功能多需**工程師以上**。

---

## 2. 模型與資料

- **雙 head 版本登錄**：`D:\AIVisionModels\pairs\<版本>\{mohao,xuehao}.onnx`，目前有 `v6.7 / v6.7.1 / v6.7.2`。
- **baseline**：`D:\AIVisionModels\v671\{mohao,xuehao}.onnx`（appsettings `MoldCodeWarpPolar` 指向）。
- 類別：mohao = `M101,M15…M96,NG`（19–20 類）；xuehao = `01…18`。
- 來源 `.pt`：`D:\OCR_demo\Contact_Lens_DRI_System\yolo_a_V6.7.x\runs\{mohao,xuehao}\weights\best.pt`（缺 onnx 用 ultralytics `export(format="onnx", imgsz=640)`）。
- 測試資料集：`d:\新增資料夾\VISION\AIVision\2026_06_05_yolo模號穴號\2026-06-05`（M101/01..18）。
- ⚠️ **v6.7.1 的 xuehao 是借用 v6.7.2**（v6.7.1 從未訓練 xuehao）——待確認可否接受。
- ⚠️ **有兩份不同的 V6.7.1 mohao**：`v671`(md5 d42bb1b7，來自 `D:\Content_lens_OCR`) vs `pairs\v6.7.1`(515a8271，來自 `D:\OCR_demo`)——待確認正版。

---

## 3. 關鍵架構 / 接線

- **雙 head 辨識**：`IMoldCodePairRecognizerPort` → `SwitchableTwoHeadRecognizer`（Singleton，包 `WarpPolarTwoHeadRecognizer`）。同時實作 `IMoldCodePairModelSwitch`（`LoadVersion` / `CurrentVersionName` / `CurrentMohaoNames` / `CurrentXuehaoNames`）。
- **單 head 辨識（舊）**：`IMoldCodeRecognizerPort` → `SwitchableMoldCodeRecognizer`（baseline `moldcode_v3_18cls.onnx`，**該檔不存在**，屬 legacy）。
- **主檢測 `IAiInferencePort` = `LocalMoldCodeInferencePort`（單 head）**；雙 head 的 pair-cycle（`VerifyMoldCodePairCycleCommand`）**尚未接到即時/PLC**（延後項）。
- **頁面**（皆為獨立彈窗，`NavigationService.ShowWindow`）：
  - 模號穴號模型管理(雙head)：`MoldCodePairBatchView` — 版本載入 + 離線逐張測試/播放 + 雙擊看辨識過程。
  - 批量推論：`BatchInferenceView` — 承接目前版本 + 依工單預期碼核對 → 寫歷史。
  - 工單管理：`WorkOrderManagementView`；工單建立/編輯：`WorkOrderInputView`。
  - 歷史圖庫：`HistoryView`。
- **跨頁狀態**：`PairWorkflowState`（上次影像資料夾，Singleton）；目前版本由切換器保存；**目前工單由 `WorkOrderManagementService`（Singleton）保存，且已做 DB 啟動還原**。
- **導引**：模型管理 →「下一步：批量推論 →」→ 批量推論 →「查看歷史 →」（保留各頁、用按鈕串接）。

---

## 4. ⚠️ 必知陷阱（踩過的坑）

1. **相機 ROI 誤判（最重要）**：appsettings `MoldCodeWarpPolar.Preprocess` 有 `RoiX=240,RoiY=0,RoiW=700,RoiH=680`，這是**即時全幅相機**用的裁切框。**離線/已裁圖絕不可套**，否則 Hough 找錯圓 → warpPolar 壞 → **模號 M101 誤判成 M60（高信心）**。離線測試/批量推論都用 `new WarpPolarParams()`（RoiW=0）重建辨識器；模型管理頁有「套用相機ROI(全幅原圖才勾)」開關，**預設關**。對齊 Python `engine.predict(apply_roi=False)`。（與 2026-06-04 Bug 3 同型；細節見 07-01 bug_notes Bug 1）
2. **啟動工作目錄**必須是 exe 目錄（否則登入清單空）。
3. **前處理必與訓練一致**（RInner 0.6 / Imgsz 640 / Hough 參數）；除錯「同權重不同結果」用三方交叉比對：先驗權重(md5+names) → 換正確前處理(Python) → 換預設參數路徑(harness) → 比對差異（見 07-01 reusable_flow Flow 1）。
4. **佈局**：勿用「固定寬度 + 水平 StackPanel」（視窗變窄按鈕被裁）→ 用 `WrapPanel` / Grid 星號(*) / DockPanel 比例佈局。

---

## 5. 驗證方式（每次改完必做）

- `dotnet build ...`（見 §1），確認 0 錯。
- 辨識驗證用 harness（不必開 UI）：
  ```
  AIVision.MoldCode.Harness.exe paircycle "D:\AIVisionModels\pairs\v6.7.2\mohao.onnx" "D:\AIVisionModels\pairs\v6.7.2\xuehao.onnx" "d:\新增資料夾\VISION\AIVision\2026_06_05_yolo模號穴號\2026-06-05"
  ```
  （資料夾結構 root/M101/<穴號>/*.jpg；預期讀 M101/08 conf≈1.00）
- UI：以正確工作目錄啟動、登入後操作。

---

## 6. 已完成（近幾日重點）

- 雙 head 模型整合（pairs 登錄）+ 可切換辨識器 + 模型管理頁（版本載入/逐張播放/辨識過程視覺化）。
- 批量推論改雙 head + 依工單預期碼核對（Match/混料）+ 寫歷史（Inspection 帶 Expected/Read/Outcome/OK-NG/AirBlown）。
- 修掉相機 ROI 誤判、批量頁殘留單 head 下拉。
- 工單：預期碼改**模號/穴號下拉**（來自載入模型類別）；**目前工單持久化+啟動還原**；管理清單 **★目前/自動選取/雙擊設為目前**；**編輯工單**；建工單 AI 模型改選填。
- 比例佈局修正（起手）：`MoldCodePairBatchView`、`BatchInferenceView` 工具列改 WrapPanel。
- 導引式「下一步」串接；警示字改浮層。

---

## 7. 待辦 / 接續（建議優先序）

1. **比例佈局逐頁掃**：✅ 活躍流程頁已修（07-06：`WorkOrderManagementView` 操作欄裁切、`HistoryView`、`RecognitionProcessView`）。**待掃周邊頁**：`ProjectEditWindow`、`OfflineTestView`、`ModelSelectView`/`Online*`/`Offline*ManagementView`、`IoPanelView`、`Light*View`。每頁改完 build+啟動驗證。
2. 工單其餘強化：批量頁 **inline 建工單**；管理頁**現代深色風** + 切換工單的**模型不一致 MessageBox 改非阻斷**（該警告是單 head 遺留、雙 head 下多為雜訊）。
3. ~~歷史圖庫「只看混料」篩選鈕~~ ✅ **已存在**（`HistoryView` 結果下拉含 Match/TrustInput/MixedAlarm/Skip）。
4. （延後/大改）把雙 head pair-cycle **接進生產/PLC 即時流程**（目前只在離線/批量用）。
5. 確認 v6.7.1 xuehao 借用、兩份 V6.7.1 mohao 正版。

---

## 8. 待使用者回覆的體感（見 `04_ux_intuitiveness.md`）

- 「下一步」開新視窗會**疊視窗**——要不要「開下一步時自動關前一個」？
- **雙擊列語意**：目前雙擊=設為目前工單、編輯走按鈕；使用者若覺得雙擊該是編輯可對調。
- 工單預期碼下拉需**先載入模型**才有選項（否則空+提示）——可接受嗎？

---

## 9. 使用者定下的工作規則（務必遵守）

1. **每次動手前先測試**（build + harness/啟動），並重視「操作直觀性」。
2. **有動 UX 就寫「直觀度評估」**進當日 `.ai/records/YYYY-MM/YYYY-MM-DD/04_ux_intuitiveness.md`，列出自評 + 待使用者回覆的問題；使用者下個 session 回體感分數（滿分10）。
3. **`.ai` 紀錄結構**：`records/YYYY-MM/YYYY-MM-DD/{01_daily_log,02_bug_notes,03_reusable_flow,04_ux_intuitiveness}.md`，含 frontmatter + 固定區塊（參 2026-07-01 各檔）。
4. 順序：**先把工單類做到平均 8 分再談進階（線上模型等）**——目前工單已達 8/8/8。

---

## 10. 檔案地標（快速定位）

- 雙 head 辨識：`AIVision.MoldCode.Onnx/{SwitchableTwoHeadRecognizer,WarpPolarTwoHeadRecognizer,WarpPolarPreprocessor,WarpPolarVisualizer}.cs`
- 切換 port：`AIVision.Application/Ports/MoldCode/IMoldCodePairModelSwitch.cs`
- 模型管理/測試頁：`AIVision.Presentation.Wpf/{ViewModels,Views}/MoldCodePairBatchView*`、`RecognitionProcessView*`
- 批量推論：`.../BatchInferenceView*`（VM 含雙 head + 驗證 + 寫歷史）
- 工單：`.../WorkOrderInputView*`（建立/編輯）、`WorkOrderManagementView*`；`AIVision.Application/Services/WorkOrderManagementService.cs`（含啟動還原）；`AIVision.Domain/Entities/WorkOrder.cs`（`UpdateDetails`）
- DI 組裝根：`AIVision.Presentation.Wpf/App.xaml.cs`
- 設定：`AIVision.Presentation.Wpf/appsettings.json`（`MoldCodeWarpPolar`、`Models`、`Authentication`）
- DB：`%LocalAppData%\AIVision\aivision.db`（WorkOrders / Inspections / Defects）
- 每日紀錄：`d:\新增資料夾\.ai\records\2026-07\{2026-07-01,2026-07-02,2026-07-06}\`
- **評分手冊（固定重用）**：`d:\新增資料夾\.ai\EVAL_HANDBOOK.md`（直觀度四維度評分標準＋流程＋模板）
- **API server 交接**：`d:\新增資料夾\.ai\HANDOFF_API.md`（API 這條線；用途＝中央推論＋模型中樞；現況啟動即崩根因）
- 設計書：`d:\新增資料夾\.ai\designs\2026-07-06_dualhead_camera_wiring.md`（雙 head 接相機/PLC）、`2026-07-12_api_server_deployment.md`（部署大方向）、`2026-07-12_api_local_run_guide.md`（本地啟動）
