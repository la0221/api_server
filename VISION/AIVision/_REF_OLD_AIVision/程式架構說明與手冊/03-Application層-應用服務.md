# Application 層 - 應用服務

## 概述

Application 層定義系統對外介面（Ports）與業務服務，是連接 UI 與硬體/資料庫的橋樑。

---

## 檔案清單與功能說明

### Ports/Devices/ - 設備介面

| 檔案 | 功能說明 |
|------|----------|
| `ICameraPort.cs` | 相機拍攝介面：單次取像、取得影像 |
| `ICameraControlPort.cs` | 相機參數控制：曝光、增益調整 |
| `ICameraDiscoveryPort.cs` | 相機發現：列舉已連接的相機 |
| `IPlcPort.cs` | PLC 基礎通訊：讀寫暫存器 |
| `IPlcCommunicationPort.cs` | PLC 連線管理：連線/斷線 |
| `IPlcHandshakePort.cs` | PLC 握手協議：觸發確認流程 |
| `IPlcSignalMapper.cs` | PLC 訊號映射：訊號名稱 ↔ 位址 |
| `ILightPort.cs` | 光源控制：亮度調整、工作/待機切換 |
| `IAiInferencePort.cs` | AI 推論：送出影像取得預測結果 |
| `IAiModelPort.cs` | AI 模型管理：載入/卸載模型 |
| `IWorkflowService.cs` | Workflow 服務：多模型串接推論 |

### Ports/Persistence/ - 資料庫介面

| 檔案 | 功能說明 |
|------|----------|
| `IInspectionRepository.cs` | 檢測記錄儲存：新增/查詢檢測結果 |
| `IWorkOrderRepository.cs` | 工單儲存：工單 CRUD 操作 |

### Ports/Services/ - 服務介面

| 檔案 | 功能說明 |
|------|----------|
| `ILineScanService.cs` | Line Scan 掃描服務 |
| `ILineScanSimulator.cs` | Line Scan 模擬服務（測試用） |
| `IDefectFilteringService.cs` | 瑕疵過濾：依尺寸/距離規則判定 |
| `IModelConfigProvider.cs` | 模型配置提供：取得模型設定 |
| `IAuthService.cs` | 認證服務：登入/登出/權限檢查 |

### Ports/History/ - 歷史查詢介面

| 檔案 | 功能說明 |
|------|----------|
| `IInspectionHistoryQuery.cs` | 檢測歷史查詢：分頁、日期篩選 |

### Ports/ProductionStats/ - 統計介面

| 檔案 | 功能說明 |
|------|----------|
| `IProductionStatsQuery.cs` | 生產統計查詢：良率、產量計算 |
| `IProductionStatsConfigProvider.cs` | 統計配置提供 |

### Ports/ImageBatch/ - 批量影像介面

| 檔案 | 功能說明 |
|------|----------|
| `IFolderPickerPort.cs` | 資料夾選擇對話框 |
| `IImageEnumeratorPort.cs` | 影像檔案列舉 |
| `IImageLoaderPort.cs` | 影像載入 |
| `IImageWriterPort.cs` | 影像保存 |
| `IOverlayRendererPort.cs` | 標註渲染：繪製瑕疵框 |
| `AiModels.cs` | AI 模型清單 |

### Ports/Models/ - 模型發現介面

| 檔案 | 功能說明 |
|------|----------|
| `DiscoveredModel.cs` | 發現的模型資訊 |
| `IModelDiscoveryService.cs` | 模型發現服務 |

### Services/ - 應用服務實作

| 檔案 | 功能說明 |
|------|----------|
| `IAutoRunService.cs` | 自動運行服務介面 |
| `IInspectionImageService.cs` | 檢測圖片服務介面 |
| `InspectionImageService.cs` | 圖片保存實作：路徑安全驗證、JPEG 編碼 |
| `IWorkOrderManagementService.cs` | 工單管理服務介面 |
| `WorkOrderManagementService.cs` | 工單開立/關閉/查詢實作 |
| `LineScanSimulator.cs` | Line Scan 模擬實作 |
| `LineScanImageBuilder.cs` | Line Scan 圖片組建 |
| `IOfflineInspectionService.cs` | 離線檢測服務介面 |
| `IProjectConfigService.cs` | 專案配置服務介面 |
| `IProjectInitializationService.cs` | 專案初始化服務介面 |
| `IContourOverlayRenderer.cs` | 瑕疵輪廓 Overlay 渲染介面 |

### Inspection/Commands/ - CQRS 命令

| 檔案 | 功能說明 |
|------|----------|
| `StartInspectionCycleCommand.cs` | 啟動單次檢測命令 |
| `StartInspectionCycleCommandHandler.cs` | 檢測流程處理：相機→AI→PLC→儲存 |
| `SwitchModelCommand.cs` | 切換 AI 模型命令 |
| `SwitchModelCommandHandler.cs` | 模型切換處理 |

### Configuration/ - 配置選項

| 檔案 | 功能說明 |
|------|----------|
| `AiServiceOptions.cs` | AI 服務配置：URL、超時 |
| `AinaviOptions.cs` | AINAVI 專用配置 |
| `WorkflowOptions.cs` | Workflow 配置 |
| `HikCameraOptions.cs` | 海康相機配置 |
| `ModelScanOptions.cs` | 模型掃描配置 |
| `InferenceType.cs` | 推論類型列舉 |
| `DefectFilteringOptions.cs` | 瑕疵過濾配置 |
| `ProjectConfig.cs` | 專案配置 |
| `ProductionStatsUiConfig.cs` | 統計 UI 配置 |

### Contracts/ - DTO 定義

| 檔案 | 功能說明 |
|------|----------|
| `InspectionResultDto.cs` | 檢測結果 DTO |
| `DefectDto.cs` | 瑕疵 DTO |
| `BoundingBoxDto.cs` | 邊界框 DTO |
| `ModelLoadRequest.cs` | 模型載入請求 |
| `ModelLoadResult.cs` | 模型載入結果 |
| `Camera/CameraCaptureMessage.cs` | 相機拍攝訊息 |
| `ImageBatch/BatchPreviewMessage.cs` | 批次預覽訊息 |
| `ProductionStats/WorkOrderStatsDto.cs` | 工單統計 DTO |
| `ProductionStats/WorkOrderSummaryDto.cs` | 工單摘要 DTO |
| `WorkOrder/WorkOrderChangedMessage.cs` | 工單變更訊息 |

### Models/ - 應用模型

| 檔案 | 功能說明 |
|------|----------|
| `LineScanSimulatorSettings.cs` | Line Scan 模擬設定 |
| `LineScanRoiSettings.cs` | Line Scan ROI 設定 |

### Interfaces/ - 通用介面

| 檔案 | 功能說明 |
|------|----------|
| `IHealthCheck.cs` | 健康檢查介面 |

### ViewModels/ - 應用層 ViewModel

| 檔案 | 功能說明 |
|------|----------|
| `Camera/CameraDeviceVm.cs` | 相機裝置展示模型 |

---

**最後更新**: 2025-12-26
