# Presentation 層 - WPF 介面

## 概述

Presentation 層是使用者介面，採用 WPF + MVVM 架構，透過 CommunityToolkit.Mvvm 實現資料綁定。

---

## 檔案清單與功能說明

### ViewModels/ - 視圖模型

#### 主要視圖

| 檔案 | 功能說明 |
|------|----------|
| `ShellViewModel.cs` | 主視窗邏輯：統合所有功能、全局狀態管理 |
| `LoginViewModel.cs` | 登入驗證：帳號密碼驗證、權限載入 |
| `IoPanelViewModel.cs` | IO 面板：PLC 訊號監控、手動控制 |

#### 相機控制

| 檔案 | 功能說明 |
|------|----------|
| `CameraViewModel.cs` | 相機控制：即時預覽 |
| `CameraTestViewModel.cs` | 相機測試：拍照儲存 |
| `CameraParameterViewModel.cs` | 相機參數：曝光、增益調整 |
| `LineScanViewModel.cs` | Line Scan 控制：掃描參數設定 |

#### 歷史與統計

| 檔案 | 功能說明 |
|------|----------|
| `HistoryViewModel.cs` | 歷史圖庫：檢測記錄查詢展示 |
| `ProductionStatsViewModel.cs` | 生產統計：良率圖表、產量分析 |
| `DefectStatViewModel.cs` | 瑕疵統計分析 |

#### 批量處理

| 檔案 | 功能說明 |
|------|----------|
| `ImageBatchViewModel.cs` | 批量影像處理 |
| `BatchInferenceViewModel.cs` | 批量 AI 推論 |

#### 模型管理

| 檔案 | 功能說明 |
|------|----------|
| `ModelManagementViewModel.cs` | AI 模型管理：列表、載入、切換 |
| `ModelSelectorViewModel.cs` | 模型選擇器 |
| `ModelSelectViewModel.cs` | 模型選擇視圖 |
| `ModelEditViewModel.cs` | 模型編輯 |

#### 設備控制

| 檔案 | 功能說明 |
|------|----------|
| `LightControlViewModel.cs` | 光源亮度控制 |
| `LightDeviceScanViewModel.cs` | 光源設備掃描 |

#### 工單管理

| 檔案 | 功能說明 |
|------|----------|
| `WorkOrderInputViewModel.cs` | 工單輸入表單 |
| `WorkOrderManagementViewModel.cs` | 工單管理：開立、關閉、查詢 |

#### 其他

| 檔案 | 功能說明 |
|------|----------|
| `DefectItemViewModel.cs` | 瑕疵項目展示 |
| `DefectRowViewModel.cs` | 瑕疵列表行 |
| `ResultTypeItemViewModel.cs` | 結果類型項目 |
| `SummaryFieldViewModel.cs` | 摘要欄位 |

### Views/ - XAML 視圖

| 檔案 | 功能說明 |
|------|----------|
| `ShellView.xaml` | 主視窗：導航區、內容區、狀態列 |
| `LoginView.xaml` | 登入視圖：帳號密碼輸入 |
| `CameraView.xaml` | 相機視圖：即時預覽 |
| `CameraTestView.xaml` | 相機測試：拍照儲存 |
| `HistoryView.xaml` | 歷史記錄：分頁列表、影像預覽 |
| `ImageBatchView.xaml` | 批量影像：資料夾選擇、批次處理 |
| `IoPanelView.xaml` | IO 面板：訊號燈、手動控制按鈕 |
| `LightControlView.xaml` | 光源控制：亮度滑桿 |
| `LightDeviceScanView.xaml` | 光源掃描：設備發現 |
| `LineScanView.xaml` | Line Scan：參數設定、預覽 |
| `ModelEditView.xaml` | 模型編輯 |
| `ModelManagementView.xaml` | 模型管理 |
| `ModelSelectorView.xaml` | 模型選擇器 |
| `ModelSelectView.xaml` | 模型選擇 |
| `ProductionStatsView.xaml` | 統計視圖：圖表、數據表格 |
| `WorkOrderInputView.xaml` | 工單輸入 |
| `WorkOrderManagementView.xaml` | 工單管理 |
| `BatchInferenceView.xaml` | 批量推論 |

### Converters/ - WPF 值轉換器

| 檔案 | 功能說明 |
|------|----------|
| `BooleanToVisibilityConverter.cs` | bool → Visibility：控制顯示/隱藏 |
| `BooleanToTitleConverter.cs` | bool → 標題文字 |
| `InverseBooleanConverter.cs` | 反向 bool |
| `NgToColorConverter.cs` | IsNg → 顏色：OK=綠、NG=紅 |
| `NullToVisibilityConverter.cs` | null → Collapsed |
| `ModelTypeConverter.cs` | 模型類型 → 顯示文字 |
| `PageIndexToBoolConverter.cs` | 頁碼 → bool |
| `PageIndexToDisplayConverter.cs` | 頁碼 → 顯示文字 |
| `CanGoNextPageConverter.cs` | 是否可翻頁 |
| `LightControlConverters.cs` | 光源相關轉換 |

### Adapters/ - UI 適配器

| 檔案 | 功能說明 |
|------|----------|
| `Camera/AForgeCameraPort.cs` | AForge 相機適配 |
| `Camera/AForgeCameraDiscovery.cs` | AForge 相機發現 |
| `ImageBatch/WpfImageLoader.cs` | WPF 圖片載入 |
| `ImageBatch/WpfImageWriter.cs` | WPF 圖片保存 |
| `ImageBatch/SegOverlayRenderer.cs` | 分割標註渲染 |
| `ImageBatch/FileSystemImageEnumerator.cs` | 檔案系統影像列舉 |
| `ImageBatch/FolderPickerPort.cs` | 資料夾選擇對話框 |
| `ImageBatch/NullOverlayRenderer.cs` | 空標註渲染 |
| `ProductionStats/ProductionStatsConfigProvider.cs` | 統計配置提供者 |
| `ProductionStats/FakeProductionStatsQuery.cs` | 模擬統計查詢 |

### Services/ - UI 服務

| 檔案 | 功能說明 |
|------|----------|
| `Navigation/INavigationService.cs` | 視窗導航介面 |
| `Navigation/NavigationService.cs` | 視窗導航實作 |
| `ProductionStats/IProductionStatsExportService.cs` | 統計匯出介面 |
| `ProductionStats/ProductionStatsExportService.cs` | 統計 Excel 匯出 |
| `ModelConfigService.cs` | 模型配置管理：models.json 讀寫 |
| `AinaviApiClient.cs` | AINAVI API 客戶端 |

### Models/ - UI 模型

| 檔案 | 功能說明 |
|------|----------|
| `ModelsConfiguration.cs` | AI 模型配置載入 |
| `ModelConfig.cs` | 單個模型配置 |
| `ModelType.cs` | 模型類型列舉 |
| `LineScanSettings.cs` | Line Scan 設定 |

### Utilities/ - 工具類

| 檔案 | 功能說明 |
|------|----------|
| `BitmapSourceFactory.cs` | 圖片轉換：byte[] → BitmapSource |
| `ObjectPathResolver.cs` | 物件路徑解析 |

### Logging/ - 日誌

| 檔案 | 功能說明 |
|------|----------|
| `FileLoggerProvider.cs` | 檔案日誌提供器 |

### 根目錄

| 檔案 | 功能說明 |
|------|----------|
| `App.xaml.cs` | 應用程式進入點：配置載入、DI 設定、主視窗啟動 |
| `appsettings.json` | 系統配置檔：硬體連線、AI 服務、帳號設定 |

---

**最後更新**: 2025-12-26
