# AIVision.Presentation.Wpf - 展示層

WPF 使用者介面，採用 MVVM 架構（CommunityToolkit.Mvvm）。

```
AIVision.Presentation.Wpf/
│
├── appsettings.json
│   # 系統配置檔 (216 行)
│   # 所有可調整參數的集中配置
│   #
│   # ═══════════════════════════════════════════════════════
│   # 【Authentication】帳號權限設定 (第 2-30 行)
│   # ═══════════════════════════════════════════════════════
│   #   DefaultRole: string - 預設角色 ("Operator")
│   #
│   #   Users: array - 使用者列表
│   #     - Username   : string - 帳號
│   #     - Password   : string - 密碼
│   #     - Role       : string - 角色 (Operator/Engineer/Vendor)
│   #     - DisplayName: string - 顯示名稱
│   #
│   #   現有帳號：
│   #     op1/1234     → 作業員（最低權限）
│   #     eng1/1234    → 工程師
│   #     vendor/admin888 → 廠商（最高權限）
│   #
│   # ═══════════════════════════════════════════════════════
│   # 【Devices:Camera】相機設定 (第 42-55 行)
│   # ═══════════════════════════════════════════════════════
│   #   Type          : string - 驅動類型 ("IdsPeak")
│   #   SdkPath       : string - SDK 路徑
│   #
│   #   Options:
│   #     TriggerMode   : string - 觸發模式 ("Off")
│   #     ExposureTimeUs: double - 曝光時間 µs (8000)
│   #     Gain          : double - 增益 (8.0)
│   #     PixelFormat   : string - 像素格式 ("PixelFormat_BGR8")
│   #
│   # ═══════════════════════════════════════════════════════
│   # 【Devices:Light】光源設定 (第 57-69 行)
│   # ═══════════════════════════════════════════════════════
│   #   Type          : string - 類型 ("LtsAscii")
│   #   ListenIp      : string - 監聽 IP ("0.0.0.0")
│   #   ListenPort    : int    - 監聽埠 (7200)
│   #   ChannelCount  : int    - 通道數 (2)
│   #   DeviceIp      : string - 光源 IP ("192.168.31.0")
│   #   DevicePort    : int    - 光源埠 (8000)
│   #
│   # ═══════════════════════════════════════════════════════
│   # 【Devices:PlcConnection】PLC 連線 (第 91-98 行)
│   # ═══════════════════════════════════════════════════════
│   #   Ip            : string - PLC IP ("192.168.250.1")
│   #   Port          : int    - Modbus 埠 (502)
│   #   UnitId        : byte   - Slave ID (1)
│   #   PollIntervalMs: int    - 輪詢間隔 ms (200)
│   #   ReadTimeoutMs : int    - 讀取超時 ms (2000)
│   #   WriteTimeoutMs: int    - 寫入超時 ms (2000)
│   #
│   # ═══════════════════════════════════════════════════════
│   # 【Devices:PlcSignalMap】PLC 訊號映射 (第 99-108 行)
│   # ═══════════════════════════════════════════════════════
│   #   AddressBaseMode: string - 位址模式 ("OneBased")
│   #
│   #   Signals: array - 訊號定義
│   #     - Name       : string - 訊號名稱
│   #     - Direction  : string - 方向 (PcToPlc/PlcToPc)
│   #     - Area       : string - Modbus 區域 (Coil)
│   #     - Address    : int    - Modbus 位址
│   #     - EdgeMode   : string - 觸發模式 (Rising/Falling/Level)
│   #
│   #   預設訊號：
│   #     CaptureStatus (00001) - PC→PLC 取像中
│   #     InspectDone   (00002) - PC→PLC 檢測完成
│   #     ResultOk      (00003) - PC→PLC 結果 OK
│   #     ResultNg      (00004) - PC→PLC 結果 NG
│   #     TriggerCapture(10001) - PLC→PC 觸發取像
│   #
│   # ═══════════════════════════════════════════════════════
│   # 【Devices:PlcHandshake】握手協議 (第 109-120 行)
│   # ═══════════════════════════════════════════════════════
│   #   TriggerSignal       : string - 觸發訊號名稱
│   #   CaptureStatusSignal : string - 取像狀態訊號
│   #   DoneSignal          : string - 完成訊號
│   #   OkSignal            : string - OK 訊號
│   #   NgSignal            : string - NG 訊號
│   #   CaptureTimeoutMs    : int    - 取像超時 (15000)
│   #   InferenceTimeoutMs  : int    - 推論超時 (10000)
│   #   MinTriggerIntervalMs: int    - 最小觸發間隔 (200)
│   #   AutoClearByPc       : bool   - PC 自動清除訊號 (true)
│   #   ErrorPolicy         : string - 錯誤處理 ("TreatAsNg")
│   #
│   # ═══════════════════════════════════════════════════════
│   # 【Devices:Ai】AI 推論服務 (第 121-131 行)
│   # ═══════════════════════════════════════════════════════
│   #   Type         : string - 類型 ("Http")
│   #   BaseUrl      : string - 服務 URL ("http://localhost:8001")
│   #   PredictPath  : string - 推論路徑 ("/v1/inference")
│   #   TimeoutMs    : int    - 超時 ms (2000)
│   #   RetryCount   : int    - 重試次數 (2)
│   #   ApiKey       : string - API 金鑰
│   #   Model        : string - 預設模型名稱
│   #
│   # ═══════════════════════════════════════════════════════
│   # 【Workflow】多模型串接 (第 146-166 行)
│   # ═══════════════════════════════════════════════════════
│   #   Enabled             : bool   - 是否啟用 (true)
│   #   EdgeHubHost         : string - EdgeHub 主機 ("127.0.0.1")
│   #   EdgeHubPort         : int    - EdgeHub 埠 (5001)
│   #   WorkflowPort        : int    - Workflow 埠 (8100)
│   #   WorkflowSettingPath : string - workflow_setting.json 路徑
│   #   TimeoutSeconds      : int    - 超時秒數 (60)
│   #
│   #   Overlay: 瑕疵輪廓繪製設定
│   #     Enabled   : bool - 是否繪製 (true)
│   #     LineWidth : int  - 線寬 (3)
│   #     ClassColors: object - 各類別顏色
│   #       "TF_crash": "#FF0000"      (紅色)
│   #       "TF_discoloration": "#FFA500" (橙色)
│   #       ...
│   #
│   # ═══════════════════════════════════════════════════════
│   # 【AutoRun】自動運行 (第 167-176 行)
│   # ═══════════════════════════════════════════════════════
│   #   SkipInference       : bool   - 跳過推論 (false，測試用)
│   #   SkipInferenceResult : string - 跳過時的結果 ("Random")
│   #   SaveImages          : bool   - 是否存圖 (true)
│   #   InspectionTimeoutMs : int    - 檢測超時 (30000)
│   #   CaptureTimeoutMs    : int    - 取像超時 (10000)
│   #   InferenceTimeoutMs  : int    - 推論超時 (10000)
│   #   MaxRetryCount       : int    - 最大重試 (3)
│   #   MaxConsecutiveErrors: int    - 最大連續錯誤 (5)
│   #
│   # ═══════════════════════════════════════════════════════
│   # 【DefectFiltering】瑕疵過濾規則 (第 177-185 行)
│   # ═══════════════════════════════════════════════════════
│   #   Enabled        : bool   - 是否啟用 (false)
│   #   PixelAreaMm2   : double - 單像素面積 mm² (0.00000576)
│   #   PixelSizeMm    : double - 單像素邊長 mm (0.0024)
│   #   MinimumAreaMm2 : double - 最小檢出面積 (0.02)
│   #                          - 小於此值的瑕疵忽略
│   #   MediumAreaMm2  : double - 中/大分界 (0.05)
│   #                          - 大於此值直接 NG
│   #   CloseDistanceMm: double - 群聚距離 (50.0)
│   #                          - 中等瑕疵距離小於此值判 NG
│   #   CriticalClasses: array  - 關鍵類別（無論大小都 NG）
│   #
│   # ═══════════════════════════════════════════════════════
│
├── Views/                              # XAML 視圖（UI 畫面）
│   ├── ShellView.xaml
│   │   # 主視窗
│   │   # 左側導航選單、中央內容區、底部狀態列
│   │
│   ├── LoginView.xaml
│   │   # 登入頁面
│   │   # 帳號密碼驗證
│   │
│   ├── CameraView.xaml
│   │   # 相機即時預覽
│   │
│   ├── LineScanView.xaml
│   │   # Line Scan 設定
│   │   # 行數、Line Rate、ROI
│   │
│   ├── IoPanelView.xaml
│   │   # IO 面板 - PLC 訊號監控
│   │
│   ├── LightControlView.xaml
│   │   # 光源亮度控制
│   │
│   ├── HistoryView.xaml
│   │   # 歷史圖庫查詢
│   │
│   ├── ProductionStatsView.xaml
│   │   # 生產統計報表
│   │
│   ├── ModelManagementView.xaml
│   │   # AI 模型管理
│   │
│   └── WorkOrderManagementView.xaml
│       # 工單管理
│
├── ViewModels/                         # 視圖模型（UI 邏輯）
│   │                                   # 使用 CommunityToolkit.Mvvm
│   │
│   ├── ShellViewModel.cs
│   │   # 主視窗邏輯 (第 38-150+ 行)
│   │   #
│   │   # 【權限屬性】
│   │   #   IsEngineerOrAbove : bool - 工程師以上權限
│   │   #   IsVendor          : bool - 廠商權限
│   │   #
│   │   # 【狀態屬性】
│   │   #   CurrentWorkOrder  - 當前工單
│   │   #   IsAutoRunning     - Auto Run 狀態
│   │   #   TotalCount/OkCount/NgCount - 統計
│   │   #
│   │   # 【命令】
│   │   #   StartAutoRunCommand  - 啟動 Auto Run
│   │   #   StopAutoRunCommand   - 停止 Auto Run
│   │   #   NavigateCommand      - 頁面導航
│   │   #   ShowLoginCommand     - 顯示登入視窗
│   │   #   LogoutCommand        - 登出
│   │
│   ├── LoginViewModel.cs
│   │   # 登入邏輯
│   │   # 呼叫 IAuthService.Login()
│   │
│   ├── IoPanelViewModel.cs
│   │   # IO 面板邏輯
│   │   # PLC 訊號輪詢與手動控制
│   │
│   ├── LineScanViewModel.cs
│   │   # Line Scan 控制邏輯
│   │
│   └── ...其他 ViewModel
│
├── Converters/                         # WPF 值轉換器
│   ├── BooleanToVisibilityConverter.cs
│   │   # true → Visible, false → Collapsed
│   │
│   ├── NgToColorConverter.cs
│   │   # NG → 紅色, OK → 綠色
│   │
│   └── ...其他轉換器
│
├── Services/
│   ├── Navigation/NavigationService.cs
│   │   # 頁面導航服務
│   │
│   ├── ModelConfigService.cs
│   │   # 模型配置管理
│   │
│   └── ProductionStats/ProductionStatsExportService.cs
│       # Excel 報表匯出
│
├── Utilities/
│   └── BitmapSourceFactory.cs
│       # ImageData → BitmapSource 轉換
│       # 呼叫 Freeze() 讓影像可跨執行緒使用
│
├── App.xaml.cs
│   # 應用程式進入點
│   # 載入配置、建立 DI 容器、啟動主視窗
│
└── Logging/FileLoggerProvider.cs
    # 檔案日誌：logs/ 資料夾，按日期分檔
```

