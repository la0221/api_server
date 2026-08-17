# Infrastructure 層 - 外部依賴實作

## 概述

Infrastructure 層實作 Application 層定義的介面，處理硬體設備通訊、資料庫存取、AI 推論服務連接。

---

## 檔案清單與功能說明

### Devices/Camera/Ids/ - IDS 相機驅動

| 檔案 | 功能說明 |
|------|----------|
| `IdsCameraPort.cs` | IDS 相機拍攝實作：支援單次與 Line Scan 模式 |
| `IdsCameraControlPort.cs` | IDS 相機控制：曝光、增益、Line Rate 調整 |
| `IdsCameraDiscoveryAdapter.cs` | IDS 相機發現：列舉已連接設備 |
| `IdsCameraOptions.cs` | IDS 相機配置選項 |
| `IdsCameraSettings.cs` | IDS 設定管理：EEPROM 讀寫 |
| `IdsPeakLibrary.cs` | IDS Peak SDK 封裝：SDK 初始化/關閉 |
| `LineScanService.cs` | Line Scan 掃描實作：逐行擷取組成影像 |

### Devices/Camera/Hik/ - 海康相機驅動

| 檔案 | 功能說明 |
|------|----------|
| `HikCameraAdapter.cs` | 海康相機適配：標準 Area Scan 模式 |
| `HikDiscoveryAdapter.cs` | 海康相機發現 |

### Devices/Plc/Communication/ - PLC 通訊

| 檔案 | 功能說明 |
|------|----------|
| `ModbusTcpPlcAdapter.cs` | Modbus TCP PLC 適配器：實作 IPlcPort |
| `PlcConnectionOptions.cs` | PLC 連線選項：IP、Port、超時 |

### Devices/Plc/Modbus/ - Modbus 客戶端

| 檔案 | 功能說明 |
|------|----------|
| `PlcModbusTcpClient.cs` | Modbus TCP 客戶端：低階暫存器讀寫 |

### Devices/Plc/Handshake/ - PLC 握手協議

| 檔案 | 功能說明 |
|------|----------|
| `PlcHandshakeService.cs` | 握手協議實作：觸發→回應→確認 |
| `PlcHandshakeOptions.cs` | 握手選項：超時、重試次數 |

### Devices/Plc/SignalMapping/ - PLC 訊號映射

| 檔案 | 功能說明 |
|------|----------|
| `PlcSignalMapper.cs` | 訊號映射：訊號名稱 ↔ Modbus 位址 |
| `PlcSignalMapOptions.cs` | 映射配置 |

### Devices/Light/ - 光源控制

| 檔案 | 功能說明 |
|------|----------|
| `LtsAsciiLightPort.cs` | LTS 光源控制：Serial/TCP ASCII 指令 |
| `LtsModbusTcpServer.cs` | LTS Modbus TCP 伺服器 |
| `Modbus/ModbusTcpClient.cs` | 通用 Modbus TCP 客戶端 |
| `LightDeviceOptions.cs` | 光源配置選項 |

### Devices/ - 模擬設備（測試用）

| 檔案 | 功能說明 |
|------|----------|
| `FakeCameraPort.cs` | 模擬相機：回傳假影像 |
| `FakePlcPort.cs` | 模擬 PLC：不實際通訊 |
| `FakeLightPort.cs` | 模擬光源 |
| `FakeCameraDiscovery.cs` | 模擬相機發現 |
| `FakeAiInferencePort.cs` | 模擬 AI 推論：回傳隨機結果 |
| `NullCameraControlPort.cs` | 空相機控制：不做任何事 |

### Persistence/SQLite/ - SQLite 資料庫

| 檔案 | 功能說明 |
|------|----------|
| `SqliteInspectionRepository.cs` | 檢測結果 SQLite 儲存：Dapper CRUD |
| `SqliteWorkOrderRepository.cs` | 工單 SQLite 儲存 |
| `SqliteInspectionHistoryQuery.cs` | 檢測歷史查詢：分頁、日期篩選 |
| `SqliteProductionStatsQuery.cs` | 生產統計查詢：良率計算 |
| `SqliteDatabaseConnectionFactory.cs` | 資料庫連線工廠 |
| `IDatabaseConnectionFactory.cs` | 連線工廠介面 |

### Persistence/ - 記憶體儲存（測試用）

| 檔案 | 功能說明 |
|------|----------|
| `InMemoryInspectionRepository.cs` | 記憶體檢測儲存 |
| `InMemoryWorkOrderRepository.cs` | 記憶體工單儲存 |

### AiService/ - AI 推論服務

| 檔案 | 功能說明 |
|------|----------|
| `AinaviAiInferencePort.cs` | AINAVI 推論實作：Multipart 上傳取得預測 |
| `AinaviAiModelPort.cs` | AINAVI 模型管理：載入/卸載 |
| `HttpAiInferencePort.cs` | HTTP 推論基類 |
| `AiInferenceRequestDto.cs` | 推論請求 DTO |
| `AiInferenceResponseDto.cs` | 推論回應 DTO |
| `JsonFileInferenceLogService.cs` | 推論日誌記錄 |
| `PredictResult.cs` | 預測結果類 |

### Adapters/AiInference/ - 推論適配器

| 檔案 | 功能說明 |
|------|----------|
| `HttpClassificationInferencePort.cs` | 分類推論：回傳單一標籤 |
| `HttpSegmentationInferencePort.cs` | 分割推論：回傳像素遮罩 |
| `HttpBatchInferencePort.cs` | 批量推論：一次處理多張 |

### Services/ - 基礎設施服務

| 檔案 | 功能說明 |
|------|----------|
| `AutoRunService.cs` | 自動運行實作：協調相機、PLC、AI 的循環 |
| `DefectFilteringService.cs` | 瑕疵過濾實作：尺寸/距離規則判定 |
| `ContourOverlayRenderer.cs` | 瑕疵輪廓 Overlay 渲染 |
| `ConfigAuthService.cs` | 認證服務實作：從 appsettings 讀取帳號 |

### ConfigurationValidators/ - 配置驗證

| 檔案 | 功能說明 |
|------|----------|
| `AiServiceOptionsValidator.cs` | AI 服務配置驗證 |
| `PlcConnectionOptionsValidator.cs` | PLC 連線配置驗證 |
| `LightDeviceOptionsValidator.cs` | 光源配置驗證 |

### DependencyInjection/ - DI 註冊

| 檔案 | 功能說明 |
|------|----------|
| `ServiceCollectionExtensions.cs` | DI 容器擴充方法 |
| `Plc/PlcServiceExtensions.cs` | PLC 服務註冊 |

### Common/ - 共用工具

| 檔案 | 功能說明 |
|------|----------|
| `ExponentialBackoff.cs` | 指數退避重試策略 |

---

**最後更新**: 2025-12-26
