# P4 — 重用舊案 AIVision（WPF 殼＋裝置層）整合計畫

> **狀態**：評估完成，**核心決策已定（2026-06-06，見 §8）** → 待實機進 P4
> **建立**：2026-06-06
> **前置**：P1-P3 已完成（辨識核心 / 重訓 / 定位，見 [PROJECT-CHARTER](PROJECT-CHARTER.md)、[PHASE-1-2-RESULT](PHASE-1-2-RESULT.md)）
> **本檔對應 charter §9 的 P4「設備整合」**：接 IDS（面相機）+ TCP→PLC 氣吹 + DI 註冊 + 把舊案 WPF 殼接進來
> **參考來源**：`AIVision.Presentation.Wpf.7z`（舊案完整方案，2025-12，已解壓到 `VISION/AIVision/_REF_OLD_AIVision/`）

---

## 0. 一句話結論

舊案是一套 **Clean Architecture / Ports-&-Adapters 的瑕疵檢測 AOI**；你們新案的「腦」（模號辨識＋多幀投票＋三態分料）**已經寫好、且已接在舊案同一組 `ICameraPort` / `IPlcPort` port 上**。所以正確策略**不是**「把 YOLO 塞回舊的推論 API 介面」，而是：

> **保留新案的腦（`IMoldCodeRecognizerPort` + `VerifyMoldCodeCycleCommandHandler`），把舊案的「殼＋裝置層」嫁接進來**，並對三條軸做改動：**相機 線掃→面掃**、**PLC 沿用＋氣吹點位**、**推論 打 API→本地 ONNX（已完成，只差 DI 接線）**。

「大多功能通用」成立的範圍 = **WPF 殼 + 裝置層 + 工單/歷史/統計**，不是推論核心。

---

## 進度紀錄（Progress Log）

> 決策：**家目錄就地用 `VISION/AIVision/AIVision`**，以**舊案完整版為主體** + 模號 graft。

- **2026-06-06 地基勘查**：現有三層 = 舊案核心 + 模號 graft（Domain/Application 為超集；Infrastructure 原少 19 檔=要砍的瑕疵/線掃）。現有專案原無 `libs/`→Infra 本機編不過；**舊案 7z 帶了完整相機 SDK（18 DLL）**。
- **2026-06-06 P4.0 ✅**：補入 `libs/` + 完整 Infrastructure(67) + WPF + InterfaceAdapters + Api + Tests，MoldCode 兩專案加進 sln → **9 專案單一 `AIVision.sln` 全綠（0 error）**，產出 `AIVision.dll`。
- **2026-06-06 P4.1 進行**：WPF 加 `MoldCode.Onnx` 參照（帶 OnnxRuntime/OpenCvSharp 原生 DLL）+ 模型隨輸出複製；`App.xaml.cs` DI 註冊 `IMoldCodeRecognizerPort→OnnxMoldCodeRecognizer` + `MoldCodeOnnxOptions`/`MoldCodeCycleOptions`（ClassSet 由 onnx 衍生）；appsettings 加 `MoldCodeOnnx`/`MoldCodeCycle`。**先加不減**，舊推論線暫留。
- **修正（使用者）**：舊案 WPF **已內建完整 Area Scan 路徑**（`ShellViewModel.StartAreaScanModeAsync`、`CameraView` 預覽面板），只是被 `IsLineScanMode=true`（註解「專案只使用 Line Scan」）**隱藏**。→ 面相機 UI 是「**取消隱藏 + 設預設**」，非新建。
- **2026-06-06 P4.2 ✅**：`ShellViewModel` `IsLineScanMode=false`(面掃唯一)、移除自動切線掃/線掃相機檢查/`OpenLineScan`；刪 `LineScanView`(.xaml/.cs)+`LineScanViewModel`；`App.xaml.cs`+`AddIdsPeakCamera` 取消註冊 `ILineScanService`/`Simulator`/`AutoRunService`；`ShellView.xaml` 移除線掃選單。**9 專案全 sln 綠**。`StartAreaScanModeAsync` 確認自給自足(只用 `ICameraPort`+`IPlcHandshakePort`，不依賴 AutoRun/LineScan)。深層線掃源碼(`LineScanService`/`AutoRunService`/`LineScanImageBuilder`/模擬器/`IdsCameraPort` 線掃方法/ShellViewModel `StartLineScanAutoRunAsync`+AutoRun 事件區) **不可達但仍編譯**，留待 P4.3 由 `AreaRunService` 取代時一併刪。
- **新資訊（使用者）**：新站 PLC 改為**三菱 MELSEC（同走 TCP）**→ P4.3 的 PLC adapter 可能需 MC Protocol 實作（舊案是 Modbus TCP），但 transport 同為 TCP、整合容易；**PLC IP/氣吹點位不急，後面確認**。
- **2026-06-06 P4.3 離線 ✅**：新增 `AIVision.Application/MoldCode/AreaRunService.cs`（取代 AutoRunService 角色；依賴 `IRequestHandler<VerifyMoldCodeCycleCommand,...>` 免容器可測；`RunOnceAsync`/`Start`/`Stop`+`CycleCompleted` 事件；實機觸發訂閱 `IPlcHandshakePort.CaptureRequested`=暫行待三菱協定收斂），DI 註冊。新增 `AreaRunServiceTests`（FakePlc+FakeCamera+樁辨識器）**3/3 綠**：Match 放行不氣吹、高信心混料→MixedAlarm 氣吹、空預期碼拋例外。刪 3 個線掃 impl（`LineScanService`/`LineScanSimulator`/`LineScanImageBuilder`）。全 sln 綠。
- **延後到 P4.4 的深層清除**：`AutoRunService`+`Domain/AutoRun/*`+`ShellViewModel` 的 AutoRun 區（事件處理/狀態屬性與 UI 狀態顯示 plumbing 交織，**改造模號運轉 UI 時順勢重用/取代**，避免刪了又重建）+ `ILineScanService`/`ILineScanSimulator` 介面 + `LineScanRoiSettings`/`LineScanSimulatorSettings`/`LineScanSettings` 模型 + `IdsCameraPort` 線掃方法 + `ProjectInit` 線掃初始化。
- **2026-06-06 P4.4a 離線批量頁 ✅**：新增 `MoldCodeImageLoader`(MoldCode.Onnx,Cv2.ImDecode→Bgr24,中文路徑安全) + `MoldCodeBatchViewModel`/`MoldCodeBatchView`(選資料夾→本地 ONNX 批量辨識,子資料夾名當正解算準確率,DataGrid+摘要,純離線免相機/PLC) + ShellView 選單「模號離線辨識」+ DI 註冊。WPF 編綠。**Harness 對真實 271 張 M101 離線批量實證:100%(此資料集偏樂觀)、p50 26.6ms/p95 37.6ms CPU;MODE2 週期 Match 放行/MixedAlarm 氣吹正確**。
- **2026-06-06 P4.4b 深層清除 ✅**：刪除 11 個源碼檔——`AutoRunService`/`IAutoRunService`/`AIVision.Domain/AutoRun/*`(4,含 CameraMode enum)/`ILineScanService`/`ILineScanSimulator`/`LineScanRoiSettings`/`LineScanSimulatorSettings`/`LineScanSettings`;gut `ShellViewModel`(移除雙服務欄位/ctor、7 個 AutoRun* 屬性+IsLineScanMode、10 個 AutoRun/線掃方法、Start/StopPlcModeAsync 收斂為面掃、CleanupAsync/Dispose 清理)、`ProjectInitializationService`(移除線掃初始化)、`ServiceCollectionExtensions`(刪 AddAutoRunService)、`IdsCameraPort`(刪線掃公開方法,保留 ApplyLineRate/LoadDefaultUserSet/ApplyOffset*)。**全 sln 0 error、測試 7/7 綠**。保留:CameraParameterKind enum、IoPanelViewModel guard、面掃相機參數路徑。(殘餘僅 3 處註解提及舊名,無程式引用。)
- **2026-06-06 P4.4b-B 工單帶預期碼 ✅**（決策:完整碼 M101/07）：`WorkOrder.ExpectedMoldCode`(可選) + WorkOrders 表 additive migration + `CreateWorkOrderAsync` 新多載(舊多載委派 null,具名引數) + `WorkOrderInputViewModel`/View 輸入框(regex 驗證,空=不核對) + `ShellViewModel` 注入 `AreaRunService` 並於載入工單時設 `ExpectedCode`/`WorkOrderId`。
- **2026-06-06 P4.4b-C History/Stats 改模號三態 ✅**（決策:退役瑕疵顯示）：Inspections 表加 4 欄 `ExpectedCode/ReadCode/Outcome/AirBlown`(additive migration,`EnsureColumnExistsAsync` 守衛) + `Inspection` entity 加欄 + **寫入路徑**(`AreaRunService` 注入 `IInspectionRepository`,每週期持久化一筆,WorkOrderId 具備才寫,失敗不中斷) + Repository/Query/DTO 改 outcome 語意(Good=Match+TrustInput,Reject=MixedAlarm,排除 Skip) + `OutcomeToColorConverter` + History/Stats VM/View 改三態顯示、移除瑕疵 + `WorkOrderStatsDto` 保留 Ok/Ng/Yield 名(reflection)改語意 + FakeProductionStatsQuery/設定更新。**全 sln 0 error、測試 9/9 綠**(+2 持久化測試)。
- **小遺留(無害)**：ProductionStats/History VM 的 `_modelConfigService` 變未用但保留避免 DI 簽章變動;History SQL 的 DefectType 過濾欄位保留為 inert。
- **剩餘=純實機(P4.3)**：三菱 MELSEC TCP adapter(可能 MC Protocol) + 氣吹 Modbus 點位(安全簽核) + 即時運轉/結果頁綁 `AreaRunService.StartAsync`(訂閱 PLC 握手觸發)。

- **2026-06-06 模型選擇 + 推論全面本地化 ✅**(全綠 15 測試 + 2 輪對抗式 review + runtime 驗證)：拿掉 Spingence logo;本地 YOLO ONNX 模型選擇(`OnnxModelDiscoveryService` 掃 `D:\AIVisionModels` + `SwitchableMoldCodeRecognizer`/`IMoldCodeModelSwitch`)接進 模型管理/批量推論/離線/工單;**根因修正**——批量錯誤/慢是舊建置未部署 + Offline/圖片點檢/主流程仍走 AINAVI HTTP(無伺服器逐張逾時)。修法:`LocalMoldCodeInferencePort`/`LocalMoldCodeBatchInferencePort` 包本地辨識器,WPF DI 兩個 IAiInferencePort 重指本地 → 全站零遠端 HTTP(HTTP 類別留給 Api);效能加 SessionOptions+warmup+快取前處理;FromImageData 解碼編碼位元組(修 offline);LoadModel 空類別讀 .names.json(修 bh_v1 字集);discovery 跳過無 names.json(crop_v2)。詳見記憶 [[project-moldcode-p4-reuse]]。

---

## 1. 兩案關係（已查證）

| | 舊案（參考 `_REF_OLD_AIVision`） | 新案（現 `VISION/AIVision/AIVision`） |
|---|---|---|
| 業務 | **瑕疵檢測 AOI**（OK/NG 品質閘） | **模號/穴號核對分料**（讀碼→核對→混料氣吹） |
| 推論 | `IAiInferencePort.PredictAsync → Prediction/DefectDto`，後端 **AINAVI EdgeHub HTTP API** | `IMoldCodeRecognizerPort.Recognize → MarkingObservation`，**本地 ONNX YOLO**（blackhat 前處理，fail-closed） |
| 週期 | `StartInspectionCycleCommandHandler`（capture→AI→OK/NG） | `VerifyMoldCodeCycleCommandHandler`（**多幀投票→三態→氣吹**） |
| 相機 | IDS **線掃** + Hik 面掃 + AForge | 只需 IDS **面掃** |
| 完整度 | 完整方案：Domain/Application/Infrastructure/InterfaceAdapters/**Api**/**Presentation.Wpf**/Tests + 相機 SDK + 架構手冊 | 只有 3 核心層 + `AIVision.MoldCode.Onnx`（引擎）+ `AIVision.MoldCode.Harness`（離線測試）；**無 Presentation.Wpf / Api 實體資料夾** |

**關鍵事實**：
- 新案核心 ports（`IPlcPort` / `ICameraPort` / `IAiModelPort`）與舊案**逐位元組相同** → 新案是舊案核心的子集 + 模號擴充。
- 新案的 `FolderBurstCamera : ICameraPort`、`FakePlcPort : IPlcPort` 證明**新腦只依賴舊案這兩個 port**；`IoCommand` 已內建 `AirBlow`（混料氣吹點位）。
- 換句話說：**接縫已經對好了**，P4 = 把舊殼接上 + 換真實裝置。

---

## 2. 三軸改動總表

| 軸 | 舊案 | 新案要的 | 動作 | 工作量 |
|---|---|---|---|---|
| **相機** | IDS **線掃**（line 累積成 frame）+ Hik 面掃 | IDS **面掃**（一觸發一張全圖） | 重用 grab loop，**砍 LineScan 整套**，IDS 改硬體觸發面掃 | 中 |
| **PLC** | Modbus TCP + 握手 + 訊號映射 + 光源 | 一樣 + **氣吹剔料點位** | **整套照用**，只改 config + 加氣吹訊號（`IoCommand.AirBlow` 已做） | 小（安全件需簽核） |
| **推論** | HTTP AINAVI / Workflow（遠端 API） | **本地 ONNX YOLO**（已完成） | **不走 `IAiInferencePort`**；DI 註冊 `OnnxMoldCodeRecognizer`，退役舊 HTTP 推論線 | 小（接線）|

---

## 3. 軸一：相機 線掃 → 面掃

### 3.1 直接重用（零/近零改動）
- `ICameraPort`（open/preview/`CaptureOnceAsync`/`FrameReceived`）— 已是「一觸發一張」面向，面掃天生適用。
- `ICameraDiscoveryPort` + `IdsCameraDiscoveryAdapter` — IDS 列舉，面掃相同。
- `IdsPeakLibrary` — 原生 SDK init/shutdown/DLL preload，面掃相機相同。
- `IdsCameraPort` 的 **grab pipeline**：`OpenAsync` / `AllocateBuffers` / `AcquisitionLoop` / `ExtractImage` / `WaitForSingleImageAsync` / buffer 管理 — 全是通用 GenICam，面掃可用。
- `ImageData`、曝光/增益套用（`ApplyExposure`/`ApplyGain`）。
- DI 樣式 `AddIdsPeakCamera()`；`HikCameraAdapter` 可當「面掃 + 硬體觸發」的最佳範本參考。

### 3.2 必須砍掉（純線掃，面掃無用）
- `IdsCameraPort.ApplyLineScanRoi / ApplyLineRate / LoadLineScanUserSet / LoadUserSet("Linescan")` — 線掃語意（OffsetY=掃哪一列、Height=累積幾條線、LineRate、載入相機 Linescan UserSet）。
- `AIVision.Application/Services/LineScanImageBuilder.cs`、`LineScanSimulator.cs`、`Models/LineScanRoiSettings.cs`、`LineScanSimulatorSettings.cs`、`Ports/Services/ILineScanService.cs`、`ILineScanSimulator.cs` — **整套 N-line 組幀 / 模擬**。
- `LineScanView.xaml(.cs)` + `LineScanViewModel` — 線掃 ROI/LineRate UI。
- `CameraParameterKind.AcquisitionLineRate`（+ `IdsCameraControlPort` 對應的 LineRate bound 讀取與「Line Scan 用」描述）。

### 3.3 必須改/新增
- **IDS 改面掃硬體觸發**：`ConfigureAcquisitionMode` 設 `TriggerSelector=FrameStart`、`TriggerMode=On`、`TriggerSource=<硬體線, 走 config>`；`AcquisitionMode=Continuous` 但靠硬體觸發 gate。`CaptureOnceAsync` 已是「等一張完成的 buffer = 一張觸發影像」。**移除** `ApplyLineRate` 用的 `LineStart` 觸發路徑。
- **以 `ILineScanService` 換成輕量面掃服務**（或直接讓 orchestrator 用 `ICameraPort`）：保留 `LineScanService` 的 `CaptureAreaPreviewAsync` / `GetLatestImageIfFresh` / `WaitForNextImageAsync` 被動等待邏輯，砍 `ConfigureAndStartAsync(LineScanRoiSettings)` 與向下轉型 `IdsCameraPort` 的 cast。
- **config**：`Devices:Camera:Options:TriggerMode` 由 `Off`→`On` + 加 `TriggerSource`；移除 `AcquisitionLineRate` 及 `camera-ids.json` 內 OffsetY/Width/LineRate 線掃欄位；保留 Exposure/Gain/Height/PixelFormat。

> ⚠️ 風險：IDS 現行 `ConfigureAcquisitionMode` 強制 `TriggerMode=Off`（free-run），與硬體觸發**相反**；若沿用 preview 路徑會 free-run 忽略 PLC 觸發。觸發設定要在 `OpenAsync/BeginAcquisition` 套用，並調 `AcquisitionLoop` 5000ms timeout 對齊觸發延遲。實機 IDS **取像時間**是 charter 待確認#2、也是 150ms 預算最大未知。

---

## 4. 軸二：PLC（沿用）＋ 氣吹

### 4.1 整套照用（零改動，camera-agnostic）
`IPlcPort` / `IPlcCommunicationPort` / `IPlcHandshakePort` / `IPlcSignalMapper` / `ILightPort` 介面；`PlcHandshakeService`（狀態機 WaitForTrigger→StartCapture→RunningInspect→SendResult）、`ModbusTcpPlcAdapter`（重連 backoff＋heartbeat）、`PlcSignalMapper`、`Domain/Plc/*`、`LtsAscii/LtsSerialLightPort`、`PlcServiceExtensions.AddPlcServices`。

握手把「相機/推論」接縫**完全外露為事件/方法**：raise `CaptureRequested` → 等 `NotifyCaptureCompleteAsync` → 等 `ReportResultAsync(Ok|Ng)`。介面內**無任何相機/影像/推論型別**。全部 async/await + CancellationToken + timeout，無 `.Result`/`.Wait`。

### 4.2 要改
- **唯一耦合點 = `AutoRunService`（舊 orchestrator，不在重用範圍）**：它依賴 `ILineScanService` + `IAiInferencePort`，且對 `CameraMode.Area` **直接 throw NotSupported**。→ 不要去擴充它。
- **二選一的編排策略**（見 §6）：
  - (a) 用舊 `IPlcHandshakePort` 握手 + 寫新 `AreaRunService` 接三步接縫（對齊舊站 OK/NG 風格）；或
  - (b) **直接用新案已寫好的 `VerifyMoldCodeCycleCommandHandler`**（它自己用 `IPlcPort` 寫 `CaptureStart`/`AirBlow`/`Result`），不經 `PlcHandshakeService`。
- **config 重定向**：`Devices:PlcConnection`（IP/Port/UnitId）、`Devices:PlcSignalMap`（加氣吹點位）、`Devices:PlcHandshake`（面掃把 `CaptureTimeoutMs` 從 15000 調小）、`Devices:Light`。

> ⚠️ **安全件**：PLC↔PC 握手協議與 Modbus 位址屬 safety-critical（見 `safety-critical.md`）。訊號表/位址/握手序列**照舊重用**；任何位址或氣吹點位變更需**人工/安全工程師簽核**，不可靜默改。charter 待確認 #3（氣吹點位 + TCP/Modbus 細節）要先定。
> ⚠️ config section 名稱陷阱：Options 的 `SectionName` 常數（`Devices:Plc:Connection`）與實際 appsettings/App.xaml.cs 綁定（`Devices:PlcConnection`）**不一致**；要照 App.xaml.cs 的顯式 `.Bind()` 複製，否則會靜默 fallback 到 127.0.0.1:502 + 預設訊號（誤打到錯的 PLC）。

---

## 5. 軸三：推論 打 API → 本地 ONNX YOLO

### 5.1 重要：不要塞回 `IAiInferencePort`
舊 `IAiInferencePort` 回 `Prediction`（Label/IsOk/Detections，瑕疵/分割形狀）；新引擎回 `MarkingObservation`（碼字串 + 雙信心，無框無 mask）。**阻抗不匹配**：`IsOk` 對 OCR 沒有自然語意（讀到一個碼不等於 OK/NG），硬塞會丟掉多幀投票與三態（Match/MixedAlarm/TrustInput）邏輯。

charter §2 早期寫「擴充/沿用 `IAiInferencePort`」，但 **P1 實作已演進**成獨立的 `IMoldCodeRecognizerPort` + `VerifyMoldCodeCycleCommandHandler`（PHASE-1-2 已驗證 C# 99.26% / CPU 20-32ms）。**這是對的，採用之，本檔以實作為準。**

### 5.2 已完成（重用）
`OnnxMoldCodeRecognizer`（`IMoldCodeRecognizerPort`，blackhat + ONNX，fail-closed）、`MoldCodePreprocessor`、`MoldCodeOnnxOptions`、`MultiFrameVoter`、`MarkingVerifier`（三態純函式）、`VerifyMoldCodeCycleCommandHandler`、離線 harness。`ImageData` 兩邊同型，影像邊界零轉換。

### 5.3 要做
- **DI 註冊**：`services.Configure<MoldCodeOnnxOptions>(config.GetSection("MoldCodeOnnx"))` + 註冊 `IMoldCodeRecognizerPort → OnnxMoldCodeRecognizer`（**singleton**；`InferenceSession` 非執行緒安全，序列化呼叫或開小 pool）。把 `AIVision.MoldCode.Onnx` 從 Infrastructure/host 加 ProjectReference，讓 OnnxRuntime + OpenCvSharp 原生 DLL 流到輸出（缺則 runtime `DllNotFound`）。
- **退役舊推論線**：`AinaviAiInferencePort` / `HttpAiInferencePort` / `WorkflowAiInferencePort` / `SwitchableAiInferencePort` / `EdgeHubWorkflowService` 及 `DefectFiltering` / `ContourOverlay` / Batch HTTP ports — 視 §8 決策保留為休眠或刪除。
- **config 全走檔**：`MoldCodeOnnx` { ModelPath, Imgsz, UseBlackhat, UseLocator, OuterFactor, CodePrefix, ClassNames }；`MoldCodeCycle` { ClassSet, MixedAlarmConfThreshold(T_alarm), MaxFrames, TimeBudgetMs, MinConsensusVotes, MinConsensusMargin }。**移除 Harness/Program.cs 的 hardcode 路徑**。
- **GPU 可選**：CPU 20-32ms 已達標（面掃單件/觸發），GPU（`OnnxRuntime.Gpu` 吃 4090）非必要；要上 GPU 才換套件。

---

## 6. 把舊 WPF 殼接進來（落地動作）

把 `_REF_OLD_AIVision` 的 `AIVision.Presentation.Wpf`（+ 視需要 `AIVision.Api` / `InterfaceAdapters`）+ Infrastructure 的**相機/PLC/光源 adapter** 併入現有方案，剝掉瑕疵/線掃，接上模號週期。

### 6.1 WPF View 重用矩陣（舊案 25 個 View）
| 類別 | View | 動作 |
|---|---|---|
| **殼/通用 — 照用**（約 16/25） | ShellView, SplashWindow, LoginView, ProjectSelect/Loading/EditWindow, IoPanelView, LightControlView, LightDeviceScanView, LightSerialControlView, HistoryView, ProductionStatsView, WorkOrderInputView, WorkOrderManagementView, ImageViewerWindow | 直接用（換品牌/欄位）。IoPanel 加氣吹點顯示；History/Stats 結果結構由「瑕疵」改「讀碼/預期/三態/氣吹數」 |
| **相機 — 改** | CameraView, CameraTestView | 留，剝線掃參數，改面掃即時預覽 |
| **相機 — 砍** | LineScanView | 移除 |
| **推論/模型 — 改造或退役** | ModelManagementView, ModelEditView, ModelSelectView, ModelSelectorView | 由「AINAVI 模型/結果類別管理」改為「ONNX 模型路徑/版本 + 封閉字集 M101/01..18 + T_alarm」或退役走 config |
| **批次/離線 — 改造或退役** | BatchInferenceView, ImageBatchView, OfflineTestView | 重指向本地 ONNX 做離線驗證（可併現有 harness 混淆矩陣），或退役 |
| **新增** | （模號核對運轉/結果頁） | 新：預期碼 vs 讀碼、信心、票數、三態結果、氣吹累計、節拍 |

> **工單是重點重用**：操作員「預期模號」很可能就從 `WorkOrder` 帶入 → `VerifyMoldCodeCycleCommand.ExpectedCode`。

### 6.2 編排（取代 `AutoRunService`）
新 orchestrator 訂閱握手 `CaptureRequested` → 面相機 `CaptureOnceAsync`（多幀）→ `VerifyMoldCodeCycleCommandHandler`（投票+三態）→ 混料則 `IPlcPort.AirBlow` 否則放行 → `NotifyCaptureComplete`/`ReportResult`。**不要改 `PlcHandshakeService`**。

---

## 7. 分階段路線（P4 細化）

| 步 | 內容 | 產出 |
|---|---|---|
| **P4.0** | 決策拍板（§8）＋把 `Presentation.Wpf`/Api/Infra 相機&PLC adapter 併入現有 sln，先能編譯（fake 裝置） | 可跑的空殼 |
| **P4.1 推論接線** | DI 註冊 `OnnxMoldCodeRecognizer` + `MoldCodeOnnx`/`MoldCodeCycle` config；退役舊 HTTP 推論線 | 模號週期跑通（FakeCamera/FakePlc） |
| **P4.2 相機面掃** | IDS 改面掃硬體觸發；砍 LineScan 整套；面掃預覽 UI；實機量取像時間 | 真 IDS 一觸發一張 |
| **P4.3 PLC＋氣吹** | config 重定向；氣吹點位（**簽核**）；握手 vs 週期 handler 編排定案 | 真 PLC 觸發→氣吹 |
| **P4.4 UI 收斂** | View 重用矩陣落地；模號運轉/結果頁；工單帶預期碼；History/Stats 改結果結構 | 操作員可用站 |
| **P4.5 現場校** | T_alarm 校準、漏報趨近 0、節拍驗證、實體零件樣本補訓 | 量產就緒 |

---

## 8. 決策（已定 2026-06-06）

1. ✅ **本站範圍 = 純模號核對分料**。舊瑕疵檢測 AOI 整組（`IAiInferencePort` + `Switchable/Http/Ainavi/Workflow` 推論 + `DefectFiltering` + `ContourOverlay` + AINAVI `ModelManagement` + `BatchInference`）**刪除**（不留休眠），殼最精簡。對齊 charter 範圍邊界「不做缺陷檢測」。
2. ✅ **線掃完全移除**（LineScanService/Builder/Simulator/View/LineRate/ROI-row 整套）。7z 與 `_REF_OLD_AIVision/` 留底可回溯。
3. ✅ **編排 = 舊 `PlcHandshakeService` 握手狀態機 + 新 `AreaRunService`**（production 穩；保留 trigger/timeout/ErrorPolicy）。`AreaRunService` 在 `CaptureRequested` 內呼叫面相機多幀取像 → `VerifyMoldCodeCycleCommandHandler`（投票+三態）→ `NotifyCaptureComplete`/`ReportResult`（混料 → `IPlcPort.AirBlow`）。**不改 `PlcHandshakeService`**。
4. ✅ **推論 = 本地 in-process ONNX（CPU 已達標 20-32ms）定案**。遠端 `yolo_service`（4090）**暫不納入**；未來要時，新增一個 `IMoldCodeRecognizerPort` 的遠端實作即可（charter §5 辨識器抽換），不影響本計畫。
5. ⏳ **待實機/現場定**：charter 待確認 #2 IDS 取像時間、#3 氣吹點位 + TCP/Modbus 細節（**安全件，需簽核**）、#4 `T_alarm`（預設 0.85）。

---

## Related
- [PROJECT-CHARTER.md](PROJECT-CHARTER.md)（決策 A：以 AIVision 當殼；重用對照表）
- [PHASE-1-2-RESULT.md](PHASE-1-2-RESULT.md)（C# 99.26% / 20-32ms / IoCommand.AirBlow）
- 參考舊案解壓：`VISION/AIVision/_REF_OLD_AIVision/`（評估後可保留為殼來源或刪）
- 舊案架構手冊：`_REF_OLD_AIVision/程式架構說明與手冊/01-架構總覽.md`
