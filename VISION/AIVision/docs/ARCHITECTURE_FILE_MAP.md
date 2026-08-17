# AIVision 檔案層級對照表（每個檔案 → 層 / 區 / 建議）

> 搭配 [ARCHITECTURE_ZONES_LAYERS.md](./ARCHITECTURE_ZONES_LAYERS.md) 看。
> 本表把方案內所有「有效原始碼」（排除 obj/bin、_REF_OLD、資料集圖片）逐檔歸位。
> **不改 code**：「建議」欄只是提案。圖例：✅保留　🔀搬遷　🆕新建　⚠️滲漏待修

## 區（Zone）代號
- **A** 模型　**B** 裝置連接　**C** 資料與設定　**D** 應用流程　**E** 控制(權限+畫面控制)　**F** 畫面(版面+風格)　**G** 對外 API

---

## L0 — 核心模型層　`AIVision.Domain`

| 檔案 | 區 | 建議 |
|---|---|---|
| Abstractions/Entity.cs | A | ✅ |
| Entities/Defect.cs / Inspection.cs / WorkOrder.cs | A | ✅ |
| MoldCode/MarkingDecision · MarkingObservation · MarkingVerifier · MarkingVerifyOutcome | A | ✅ |
| MoldCode/MoldCodePairVerifier · MoldCodePairVoter · MultiFrameVoter · PairDecision · PairObservation · PairVerifyOutcome | A | ✅ |
| Plc/ModbusAddressConverter · PlcAddressBaseMode · PlcHandshakeState · PlcSignalDefinition · PlcSignalEnums | A | ✅ |
| Shared/ImageData · IoCommand · IoSnapshot · Prediction | A | ✅ |
| User/UserRole.cs | A→E | ✅（被控制區引用，留在 Domain 正確）|

> 可選：本專案 `net8.0-windows` → 降 `net8.0` 以保純度。

---

## L1 — 應用層　`AIVision.Application`

| 檔案 | 區 | 建議 |
|---|---|---|
| Configuration/AiServiceOptions · AinaviOptions · DefectFilteringOptions · HikCameraOptions · InferenceType · ModelScanOptions · ProductionStatsUiConfig · ProjectConfig · WorkflowOptions | C | ✅ |
| Contracts/BoundingBoxDto · DefectDto · InspectionResultDto · ModelLoadRequest · ModelLoadResult | D | ✅ |
| Contracts/Camera/CameraCaptureMessage · ImageBatch/BatchPreviewMessage · ProductionStats/* · WorkOrder/WorkOrderChangedMessage | C/D | ✅ |
| Inspection/Commands/StartInspectionCycleCommand(+Handler) · SwitchModelCommand(+Handler) | D | ✅ |
| MoldCode/AreaRunService · MoldCodeCycle* · MoldCodePairCycle* · VerifyMoldCodeCycleCommand(+Handler) · VerifyMoldCodePairCycleCommand(+Handler) | D | ✅ |
| Ports/Devices/IAiInferencePort · IAiModelPort · ICameraControlPort · ICameraDiscoveryPort · ICameraPort · ILightPort · IPlcCommunicationPort · IPlcHandshakePort · IPlcPort · IPlcSignalMapper · IWorkflowService | B | ✅（契約）|
| Ports/History/IInspectionHistoryQuery · Persistence/IInspectionRepository · IWorkOrderRepository · ProductionStats/* | C | ✅（契約）|
| Ports/ImageBatch/* · Models/* · MoldCode/IMoldCode* | B/D | ✅（契約）|
| Ports/Services/IAuthService.cs | E | ✅（權限契約）|
| Ports/Services/IDefectFilteringService · IModelConfigProvider | C/D | ✅ |
| Interfaces/IHealthCheck.cs | C | ✅ |
| Services/IContourOverlayRenderer · IInspectionImageService · IOfflineInspectionService · IProjectConfigService · IProjectInitializationService · IWorkOrderManagementService | D | ✅ |
| Services/InspectionImageService.cs · WorkOrderManagementService.cs | D | ✅ |
| **ViewModels/Camera/CameraDeviceVm.cs** | F | ⚠️🔀 搬到 Presentation.Wpf/ViewModels（或改名 DTO）— UI 概念不該在應用層 |

---

## L2 — 轉接層　`AIVision.InterfaceAdapters`

| 檔案 | 區 | 建議 |
|---|---|---|
| Inspection/InspectionResultMapper.cs | D | ✅ |

---

## L3 — 基礎設施層　`AIVision.Infrastructure`

| 檔案 | 區 | 建議 |
|---|---|---|
| Adapters/AiInference/HttpBatch · HttpClassification · HttpSegmentation InferencePort | B | ✅ |
| AiService/AiInferenceRequestDto · ResponseDto · AinaviAiInferencePort · AinaviAiModelPort · EdgeHubWorkflowService · HttpAiInferencePort · IInferenceLogService · JsonFileInferenceLogService · PredictResult · SwitchableAiInferencePort · WorkflowAiInferencePort | B | ✅ |
| Common/ExponentialBackoff.cs | B | ✅ |
| Configs/AiSettings.cs | C | ✅ |
| ConfigurationValidators/AiServiceOptionsValidator · LightDeviceOptionsValidator · PlcConnectionOptionsValidator | C | ✅ |
| DependencyInjection/ServiceCollectionExtensions.cs | E | ✅（組裝根的一部分）|
| Devices/Camera/Hik/HikCameraAdapter · HikDiscoveryAdapter | B | ✅ |
| Devices/Camera/Ids/IdsCamera* · IdsPeakLibrary | B | ✅ |
| Devices/Fake*（AiInference/Camera/Light/Plc/Discovery）· NullCameraControlPort | B | ✅（測試替身）|
| Devices/Light/Lts* · LightDeviceOptions · Modbus/ModbusTcpClient | B | ✅ |
| Devices/Plc/Communication/ModbusTcpPlcAdapter · PlcConnectionOptions · Handshake/* · Modbus/PlcModbusTcpClient · ModbusPlcPort · SignalMapping/* · DependencyInjection/PlcServiceExtensions | B | ✅ |
| Persistence/InMemory*Repository · SQLite/* | C | ✅ |
| Services/ConfigAuthService.cs | E | ✅（權限實作）|
| Services/ContourOverlayRenderer · DefectFilteringService · LocalModelDiscoveryService · OfflineInspectionService · OnnxModelDiscoveryService · ProjectConfigService · ProjectInitializationService | C/D | ✅ |

---

## L4 — 模型推論層　`AIVision.MoldCode.Onnx`

| 檔案 | 區 | 建議 |
|---|---|---|
| LocalMoldCodeBatchInferencePort · LocalMoldCodeInferencePort · MoldCodeImageLoader · MoldCodeModelConfig · MoldCodeOnnxOptions · MoldCodePreprocessor · MoldCodeWarpPolarOptions · OnnxMoldCodeRecognizer · SwitchableMoldCodeRecognizer · WarpPolarPreprocessor · WarpPolarTwoHeadRecognizer | A | ✅ |

---

## L5 — 表現層 (WPF)　`AIVision.Presentation.Wpf`

### 控制區（E）—— 建議收攏到新的 `Shell/`
| 檔案 | 現位置 | 建議 |
|---|---|---|
| App.xaml.cs | 根 | ✅（組裝根，留根目錄）|
| AssemblyInfo.cs | 根 | ✅ |
| Services/Navigation/INavigationService · NavigationService | Services/Navigation | 🔀→ Shell/Navigation/ |
| ViewModels/ShellViewModel.cs | ViewModels | 🔀→ Shell/ |
| ViewModels/LoginViewModel.cs + Views/LoginView.xaml(.cs) | ViewModels/Views | 🔀→ Shell/Auth/ |
| Views/SplashWindow · ProjectLoadingWindow + ViewModels/ProjectLoadingViewModel | Views/ViewModels | 🔀→ Shell/（啟動/載入流程）|
| Logging/FileLoggerProvider.cs | Logging | ✅（橫切，留著或併 Shell）|

### 畫面區（F）—— 版面 + 對應 VM（保留原結構）
| 群組 | 檔案 | 建議 |
|---|---|---|
| 相機 | Views/CameraView · CameraTestView；ViewModels/CameraViewModel · CameraTestViewModel · Camera/CameraParameterViewModel | ✅ |
| 批次/離線推論 | Views/BatchInferenceView · ImageBatchView · MoldCodeBatchView · OfflineTestView；對應 ViewModels | ✅ |
| 模型管理 | Views/ModelEditView · ModelSelectView · ModelSelectorView · OfflineModelEditView · OfflineModelManagementView · OnlineModelManagementView；對應 ViewModels | ✅ |
| 專案 | Views/ProjectEditWindow · ProjectSelectWindow；ViewModels/ProjectEditViewModel · ProjectSelectViewModel | ✅ |
| 工單/歷史/統計 | Views/WorkOrderInputView · WorkOrderManagementView · HistoryView · ProductionStatsView；對應 ViewModels | ✅ |
| IO/光源 | Views/IoPanelView · LightControlView · LightDeviceScanView · LightSerialControlView；對應 ViewModels | ✅ |
| 影像檢視 | Views/ImageViewerWindow | ✅ |
| 結果項 VM | ViewModels/DefectItemViewModel · DefectRowViewModel · DefectStatViewModel · ResultTypeItemViewModel · SummaryFieldViewModel · WorkOrderInputViewModel | ✅ |

### 風格區（F）—— 建議從畫面區內析出
| 檔案 | 建議 |
|---|---|
| Converters/*（11 個：Boolean/Inverse/Null/Ng/Outcome/PageIndex/ModelType/LightControl…） | ✅ 留 Converters/ |
| Utilities/BitmapSourceFactory · ObjectPathResolver | ✅ |
| **(無檔案)** Themes/ 色票·字體·控件樣式 ResourceDictionary | 🆕 新建風格層 |

### 裝置連接區（B）在 WPF 內的部分
| 檔案 | 建議 |
|---|---|
| Adapters/Camera/AForgeCameraDiscovery · AForgeCameraPort | ⚠️🔀 可併入 Infrastructure/Devices/Camera（或標註 WPF 專用）|
| Adapters/ImageBatch/FileSystemImageEnumerator · FolderPickerPort · NullOverlayRenderer · SegOverlayRenderer · WpfImageLoader · WpfImageWriter | ✅（多依賴 WPF 影像 API，留 WPF 合理）|
| Adapters/ProductionStats/FakeProductionStatsQuery · ProductionStatsConfigProvider | ✅ |

### 其他 WPF 內服務 / 設定模型
| 檔案 | 區 | 建議 |
|---|---|---|
| Services/AinaviApiClient.cs | G | ✅ |
| Services/ModelConfigProviderAdapter · ModelConfigService · ProductionStats/* | C/D | ✅ |
| Models/InferenceType · ModelConfig · ModelType · ModelsConfiguration | C | ✅（WPF 端設定模型）|

---

## L5 — 表現層 (HTTP)　`AIVision.Api`

| 檔案 | 區 | 建議 |
|---|---|---|
| Controllers/AinaviController.cs · InspectionController.cs | G | ✅ |
| Program.cs | G/E | ✅（API 組裝根）|

---

## 測試 / 工具（不分區）

| 專案 | 檔案 | 建議 |
|---|---|---|
| AIVision.Application.Tests | AiService/* · Inspection/* · MoldCode/*（8 個測試類）| ✅ |
| AIVision.MoldCode.Harness | FakePlcPort · FolderBurstCamera · GoldenDump · ImageLoad · PairCycleDemo · Program | ✅（命令列驗證台）|

---

## 搬遷總清單（只有這幾個要動，其餘全保留）

| # | 檔案 | 從 | 到 | 理由 |
|---|---|---|---|---|
| 1 | CameraDeviceVm.cs | Application/ViewModels/Camera | Presentation.Wpf/ViewModels（或改 DTO）| ⚠️ UI 概念滲漏進應用層 |
| 2 | INavigationService · NavigationService | Wpf/Services/Navigation | Wpf/Shell/Navigation | 收攏控制區 |
| 3 | ShellViewModel | Wpf/ViewModels | Wpf/Shell | 收攏控制區 |
| 4 | LoginViewModel · LoginView | Wpf/ViewModels · Wpf/Views | Wpf/Shell/Auth | 收攏控制區 |
| 5 | SplashWindow · ProjectLoadingWindow · ProjectLoadingViewModel | Wpf/Views · Wpf/ViewModels | Wpf/Shell | 啟動流程屬控制 |
| 6 | AForgeCameraDiscovery · AForgeCameraPort | Wpf/Adapters/Camera | Infrastructure/Devices/Camera | 統一裝置連接區（可選）|
| 7 | （新建）Themes/ | — | Wpf/Themes | 建立風格層 |

> 全方案 ~200 個原始碼檔，實際需移動者 **≦ 12 個**，其餘只是「在心智上歸區」即可。架構本體不需大改。
