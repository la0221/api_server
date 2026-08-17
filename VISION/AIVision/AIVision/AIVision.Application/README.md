# AIVision.Application - 應用層

定義系統介面（Ports）與業務服務，連接 UI 與硬體/資料庫。

```
AIVision.Application/
│
├── Ports/                              # 介面定義（六邊形架構的「端口」）
│   │
│   ├── Devices/                        # 硬體設備介面
│   │   ├── ICameraPort.cs
│   │   │   # 相機拍攝介面 (第 5-18 行)
│   │   │   #
│   │   │   # 【介面方法】
│   │   │   #   OpenAsync(deviceId)    - 開啟指定相機
│   │   │   #   StartPreviewAsync()    - 開始即時預覽
│   │   │   #   StopPreviewAsync()     - 停止預覽
│   │   │   #   CaptureOnceAsync()     - 單次拍照，回傳 ImageData
│   │   │   #
│   │   │   # 【事件】
│   │   │   #   FrameReceived          - 收到影像幀時觸發
│   │   │
│   │   ├── IPlcPort.cs
│   │   │   # PLC 基礎讀寫介面 (第 5-10 行)
│   │   │   #
│   │   │   # 【介面方法】
│   │   │   #   ReadAsync()            - 讀取 PLC 狀態，回傳 IoSnapshot
│   │   │   #   WriteAsync(command)    - 寫入 PLC，參數為 IoCommand
│   │   │
│   │   └── IAiInferencePort.cs
│   │       # AI 推論介面 (第 5-23 行)
│   │       #
│   │       # 【屬性】
│   │       #   IsConnected : bool     - AI 服務是否已連線
│   │       #
│   │       # 【介面方法】
│   │       #   PredictAsync(image)    - 執行推論，回傳 Prediction
│   │       #   HealthCheckAsync()     - 健康檢查，回傳 bool
│   │
│   └── Services/                       # 業務服務介面
│       ├── ILineScanService.cs
│       │   # Line Scan 掃描服務介面 (第 12-134 行)
│       │   #
│       │   # 【狀態屬性】
│       │   #   IsCameraConnected : bool - 相機是否已連接
│       │   #   IsScanning        : bool - 是否正在掃描
│       │   #   CurrentLineIndex  : int  - 當前已掃描行數
│       │   #   LatestImage       : ImageData? - 最近完成的影像
│       │   #
│       │   # 【設定屬性】
│       │   #   CurrentSettings   : LineScanRoiSettings? - 當前 ROI 設定
│       │   #   OriginalSettings  : LineScanRoiSettings? - UI 原始設定
│       │   #   Bounds            : LineScanRoiBounds?   - ROI 參數邊界
│       │   #
│       │   # 【主要方法】
│       │   #   ConnectCameraAsync()       - 連接相機
│       │   #   ConfigureAndStartAsync(settings) - 設定 ROI 並開始掃描
│       │   #   CaptureOnceAsync()         - 單次完整取像
│       │   #   WaitForNextImageAsync()    - 等待下一張影像（被動模式）
│       │   #   GetLatestImageIfFresh(maxAgeMs=5000) - 取得有效期內的影像
│       │   #
│       │   # 【事件】
│       │   #   LineReceived    - 收到一行數據時觸發
│       │   #   ImageCompleted  - 完成一張完整圖像時觸發
│       │   #   ScanError       - 掃描錯誤時觸發
│       │
│       ├── IDefectFilteringService.cs
│       │   # 瑕疵過濾服務介面 (第 10-124 行)
│       │   #
│       │   # 【介面方法】
│       │   #   FilterDefects(defects)     - 過濾瑕疵並判定結果
│       │   #   UpdateOptions(options)     - 更新過濾配置
│       │   #   GetCurrentOptions()        - 取得目前配置
│       │   #   CalculateAreaMm2(pixelArea) - 計算瑕疵面積 (mm²)
│       │   #
│       │   # 【回傳結果 DefectFilteringResult】(第 50-98 行)
│       │   #   IsOk            : bool   - 最終判定 (true=OK)
│       │   #   ValidDefects    : list   - 有效瑕疵（判 NG 的）
│       │   #   FilteredDefects : list   - 被過濾的小瑕疵
│       │   #   LargeDefectCount   : int - 大瑕疵數量
│       │   #   MediumDefectCount  : int - 中等瑕疵數量
│       │   #   SmallDefectCount   : int - 被過濾的小瑕疵數量
│       │   #   CriticalDefectCount: int - 關鍵瑕疵數量
│       │   #
│       │   # 【瑕疵尺寸分類 DefectSizeCategory】(第 103-124 行)
│       │   #   Small    - 小於 MinimumAreaMm2，忽略
│       │   #   Medium   - 介於最小與中等之間，條件判定
│       │   #   Large    - 大於 MediumAreaMm2，直接 NG
│       │   #   Critical - 屬於 CriticalClasses，直接 NG
│       │
│       └── IAuthService.cs
│           # 認證服務介面 (第 8-49 行)
│           #
│           # 【屬性】
│           #   CurrentUsername   : string? - 當前帳號
│           #   CurrentRole       : UserRole - 當前角色
│           #   CurrentDisplayName: string  - 顯示名稱
│           #
│           # 【方法】
│           #   Login(username, password) - 登入驗證，回傳 bool
│           #   Logout()                  - 登出
│           #   HasPermission(requiredRole) - 檢查權限，回傳 bool
│           #
│           # 【事件】
│           #   LoginStateChanged - 登入狀態變更時觸發
│
├── Services/                           # 應用服務實作
│   ├── IAutoRunService.cs
│   │   # Auto Run 服務介面 (第 8-76 行)
│   │   #
│   │   # 【狀態屬性】
│   │   #   State         : AutoRunState       - 當前狀態
│   │   #   IsRunning     : bool               - 是否正在運行
│   │   #   Statistics    : AutoRunStatistics  - 統計資料
│   │   #   CurrentOptions: AutoRunOptions?    - 當前設定
│   │   #
│   │   # 【控制方法】
│   │   #   StartAsync(options) - 啟動 Auto Run
│   │   #   StopAsync()         - 停止 Auto Run
│   │   #   PauseAsync()        - 暫停
│   │   #   ResumeAsync()       - 恢復
│   │   #   ResetStatistics()   - 重置統計
│   │   #
│   │   # 【事件】
│   │   #   StateChanged        - 狀態變更
│   │   #   InspectionCompleted - 檢測完成
│   │   #   ErrorOccurred       - 錯誤發生
│   │   #   TriggerReceived     - 收到觸發訊號
│   │   #   CaptureCompleted    - 取像完成
│   │
│   ├── InspectionImageService.cs
│   │   # 檢測圖片保存服務 (第 14-285 行)
│   │   #
│   │   # 【保存位置】(第 27-30 行)
│   │   #   根目錄: %LocalAppData%/AIVision/Images
│   │   #   結構: Images/{工單號}/{OK|NG}/時間戳.jpg
│   │   #
│   │   # 【檔名格式】(第 64 行)
│   │   #   時間戳: yyyyMMdd_HHmmss_fff.jpg
│   │   #   標註圖: yyyyMMdd_HHmmss_fff_annotated.jpg
│   │   #
│   │   # 【JPEG 品質】(第 104 行)
│   │   #   QualityLevel = 95 (0-100)
│   │   #
│   │   # 【工單代碼驗證】(第 236-264 行)
│   │   #   - 只允許: 字母、數字、底線、連字號
│   │   #   - 長度限制: 100 字符以內
│   │   #   - 禁止: .. / \ 等路徑遍歷字符
│   │
│   └── WorkOrderManagementService.cs
│       # 工單管理服務 (第 10-193 行)
│       #
│       # 【主要方法】
│       #   GetCurrentWorkOrderAsync()       - 取得當前工單
│       #   CreateWorkOrderAsync(...)        - 建立新工單
│       #   EndCurrentWorkOrderAsync()       - 結束當前工單
│       #   SwitchToWorkOrderAsync(code)     - 切換到指定工單
│       #
│       # 【工單號格式】(第 179 行)
│       #   自動生成: WO-yyyyMMdd-HHmmss-fff
│       #   例如: WO-20251226-143021-123
│
├── Configuration/                      # 配置選項類別
│   ├── AiServiceOptions.cs
│   │   # AI 服務配置 (第 6-69 行)
│   │   #
│   │   # 【連線設定】
│   │   #   Type          : string  - 類型 ("Http" 或其他)
│   │   #   BaseUrl       : string  - 服務 URL (如 http://192.168.1.95:8009)
│   │   #   PredictPath   : string  - 推論路徑 (預設 "/v1/inference")
│   │   #
│   │   # 【認證設定】
│   │   #   ApiKey        : string? - API 金鑰
│   │   #   ApiKeyHeader  : string  - Header 名稱 (預設 "Authorization")
│   │   #   ApiKeyPrefix  : string  - 金鑰前綴 (預設 "Bearer ")
│   │   #
│   │   # 【效能設定】
│   │   #   TimeoutMs     : int     - HTTP 超時 (預設 2000ms，範圍 100-60000)
│   │   #   RetryCount    : int     - 重試次數 (預設 2，範圍 0-5)
│   │   #   HealthCheckPath: string? - 健康檢查路徑
│   │
│   ├── AinaviOptions.cs
│   │   # AINAVI EdgeHub 配置 (第 8-57 行)
│   │   #
│   │   # 【連線設定】
│   │   #   Host          : string  - EdgeHub 主機 (預設 "http://192.168.1.95")
│   │   #   EdgeHubPort   : int     - EdgeHub 埠號 (預設 5001)
│   │   #   ModelBasePath : string  - 模型路徑 (預設 "/home/smasoft/...")
│   │   #
│   │   # 【模型設定】
│   │   #   DefaultModel     : string - 預設模型 (預設 "WPC_top")
│   │   #   DefaultModelPort : int    - 模型埠號 (預設 8009)
│   │   #
│   │   # 【日誌設定】
│   │   #   LogPath       : string  - 推論日誌路徑 (預設 "logs/inference_log.json")
│   │
│   ├── WorkflowOptions.cs
│   │   # Workflow 多模型串接配置 (第 8-70 行)
│   │   #
│   │   # 【啟用設定】
│   │   #   Enabled       : bool    - 是否啟用 Workflow (預設 false)
│   │   #
│   │   # 【連線設定】
│   │   #   EdgeHubHost   : string  - EdgeHub 主機 (預設 "127.0.0.1")
│   │   #   EdgeHubPort   : int     - EdgeHub 管理埠 (預設 5001)
│   │   #   WorkflowPort  : int     - Workflow 推論埠 (預設 11000)
│   │   #   TimeoutSeconds: int     - HTTP 超時 (預設 60，範圍 1-300)
│   │   #
│   │   # 【Workflow 設定】
│   │   #   WorkflowSettingPath: string - workflow_setting.json 路徑
│   │   #
│   │   # 【API 路徑】
│   │   #   GetWorkflowRunUrl() → http://{host}:{WorkflowPort}/workflow/run
│   │   #   GetEdgeHubUrl()     → http://{host}:{EdgeHubPort}
│   │
│   └── DefectFilteringOptions.cs
│       # 瑕疵過濾配置 (第 7-79 行)
│       # appsettings.json 區段名稱: "DefectFiltering"
│       #
│       # 【啟用設定】
│       #   Enabled       : bool    - 是否啟用過濾 (預設 false)
│       #
│       # 【像素換算】
│       #   PixelAreaMm2  : double  - 單像素面積 mm² (預設 0.00000576，對應 2.4µm)
│       #   PixelSizeMm   : double  - 單像素邊長 mm (預設 0.0024)
│       #
│       # 【尺寸閾值】
│       #   MinimumAreaMm2: double  - 最小檢出面積 (預設 0.02 mm²)
│       #                           - 小於此值的瑕疵忽略不計
│       #   MediumAreaMm2 : double  - 中/大瑕疵分界 (預設 0.05 mm²)
│       #                           - 大於此值直接判 NG
│       #
│       # 【距離閾值】
│       #   CloseDistanceMm: double - 群聚距離 (預設 50.0 mm)
│       #                          - 中等瑕疵距離小於此值時判 NG
│       #
│       # 【關鍵類別】
│       #   CriticalClasses: List<string> - 關鍵瑕疵類別清單
│       #                                 - 這些類別無論大小都判 NG
│       #                                 - 例如: ["crack", "hole"]
│
├── Models/
│   └── LineScanRoiSettings.cs
│       # Line Scan ROI 設定 (第 6-90 行)
│       #
│       # 【LineScanRoiSettings 記錄】(第 6-49 行)
│       #   OffsetX      : long   - ROI 起始 X 座標 (pixel)
│       #   OffsetY      : long   - ROI 起始 Y 座標 (pixel)
│       #   Width        : long   - ROI 寬度 (pixel)
│       #   TargetHeight : int    - 目標圖像高度（掃描行數）
│       #   LineRate     : double - 行頻 (Hz)
│       #   ExposureTimeUs: double? - 曝光時間 (µs)
│       #   Gain         : double? - 增益
│       #   UserSetName  : string - 相機 UserSet (預設 "Linescan")
│       #
│       # 【LineScanRoiBounds 記錄】(第 54-90 行)
│       #   OffsetXMin/Max/Step  - X 座標範圍與步進
│       #   OffsetYMin/Max/Step  - Y 座標範圍與步進
│       #   WidthMin/Max/Step    - 寬度範圍與步進
│       #   LineRateMin/Max      - 行頻範圍
│       #   SensorWidth/Height   - 感測器尺寸
│
└── Contracts/                          # DTO（資料傳輸物件）
    ├── InspectionResultDto.cs
    │   # 檢測結果 DTO
    │   # 包含：IsNg、Confidence、Defects
    │
    └── ProductionStats/
        ├── WorkOrderStatsDto.cs
        │   # 工單統計 DTO
        │   # 包含：TotalCount、OkCount、NgCount、YieldRate
        │
        └── WorkOrderSummaryDto.cs
            # 工單摘要 DTO
            # 包含：WorkOrderNumber、ProductModel、Status
```

