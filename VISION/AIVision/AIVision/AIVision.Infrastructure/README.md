# AIVision.Infrastructure - 基礎設施層

實作 Application 層介面，處理硬體通訊、資料庫、AI 服務連接。

```
AIVision.Infrastructure/
│
├── Devices/                            # 硬體設備驅動實作
│   │
│   ├── Camera/
│   │   ├── Ids/                        # IDS Peak 工業相機驅動
│   │   │   ├── IdsCameraPort.cs
│   │   │   │   # IDS 相機拍攝實作
│   │   │   │   # 實作 ICameraPort 介面
│   │   │   │   # 支援單次拍照 (Area Scan) 與連續掃描 (Line Scan)
│   │   │   │
│   │   │   ├── IdsCameraControlPort.cs
│   │   │   │   # IDS 相機參數控制實作 (第 19-559 行)
│   │   │   │   # 實作 ICameraControlPort 介面
│   │   │   │
│   │   │   │   # 【可調整參數 CameraParameterKind】(第 213-276 行)
│   │   │   │   #   ExposureTime        - 曝光時間 (µs)
│   │   │   │   #   Gain                - 增益 (dB)
│   │   │   │   #   Height              - 影像高度 (px)
│   │   │   │   #   AcquisitionLineRate - 行頻 (Hz) [Line Scan 專用]
│   │   │   │   #   OffsetX/OffsetY     - ROI 起點
│   │   │   │   #   Width               - ROI 寬度
│   │   │   │
│   │   │   │   # 【參數範圍 FloatRange】(第 525-528 行)
│   │   │   │   #   ExposureTime: 10 ~ 200,000 µs (預設 step=50)
│   │   │   │   #   Gain: 0 ~ 24 dB (預設 step=0.1)
│   │   │   │   #   LineRate: 100 ~ 250,000 Hz (預設 step=100)
│   │   │   │
│   │   │   ├── IdsCameraOptions.cs
│   │   │   │   # IDS 相機配置選項 (第 5-53 行)
│   │   │   │   # appsettings.json 區段名稱: "Devices:Camera"
│   │   │   │   #
│   │   │   │   # 【連線設定】
│   │   │   │   #   Type          : string - 驅動類型 (預設 "IdsPeak")
│   │   │   │   #   Preferred     : List<string> - 優先開啟的相機序號
│   │   │   │   #   SdkPath       : string - SDK 路徑 (預設 "libs/cameras/IDS_PEAK")
│   │   │   │   #   ConfigPath    : string - 設定檔路徑 (預設 "configs/camera-ids.json")
│   │   │   │   #
│   │   │   │   # 【預設參數】
│   │   │   │   #   Height        : long   - 影像高度 (預設 1024)
│   │   │   │   #   ExposureTimeUs: double - 曝光時間 µs (預設 15000)
│   │   │   │   #   GainSelector  : string - 增益選擇器 (預設 "AnalogAll")
│   │   │   │   #   Gain          : double - 增益值 (預設 2.0)
│   │   │   │   #   AcquisitionLineRate: double? - 行頻 Hz (Line Scan 專用)
│   │   │   │
│   │   │   ├── LineScanService.cs
│   │   │   │   # Line Scan 掃描服務實作
│   │   │   │   # 實作 ILineScanService 介面
│   │   │   │   # 逐行接收相機資料，累積後組合成完整影像
│   │   │   │
│   │   │   └── IdsPeakLibrary.cs
│   │   │       # IDS Peak SDK 封裝
│   │   │       # 處理 SDK 初始化/關閉
│   │   │
│   │   └── Hik/                        # 海康威視相機驅動
│   │       ├── HikCameraAdapter.cs
│   │       │   # 海康相機適配器，實作 ICameraPort
│   │       │
│   │       └── HikDiscoveryAdapter.cs
│   │           # 海康相機發現
│   │
│   ├── Plc/                            # PLC 通訊驅動
│   │   ├── Communication/
│   │   │   ├── ModbusTcpPlcAdapter.cs
│   │   │   │   # Modbus TCP PLC 適配器
│   │   │   │   # 實作 IPlcPort、IPlcCommunicationPort 介面
│   │   │   │
│   │   │   └── PlcConnectionOptions.cs
│   │   │       # PLC 連線選項 (第 6-42 行)
│   │   │       # appsettings.json 區段名稱: "Devices:Plc:Connection"
│   │   │       #
│   │   │       # 【連線設定】
│   │   │       #   Ip            : string - PLC IP 位址 (預設 "127.0.0.1")
│   │   │       #   Port          : int    - Modbus 埠號 (預設 502)
│   │   │       #   UnitId        : byte   - Slave ID (預設 1)
│   │   │       #
│   │   │       # 【輪詢設定】
│   │   │       #   PollIntervalMs : int   - 輪詢間隔 (預設 50 ms)
│   │   │       #
│   │   │       # 【超時設定】
│   │   │       #   ReadTimeoutMs  : int   - 讀取超時 (預設 3000 ms)
│   │   │       #   WriteTimeoutMs : int   - 寫入超時 (預設 3000 ms)
│   │   │       #
│   │   │       # 【重連設定】
│   │   │       #   ReconnectIntervalMs    : int - 重連間隔 (預設 5000 ms)
│   │   │       #   InitialReconnectDelayMs: int - 首次重連等待 (預設 2000 ms)
│   │   │       #   MaxReconnectDelayMs    : int - 最大重連等待 (預設 10000 ms)
│   │   │       #   ErrorThresholdForReconnect: int - 觸發重連的錯誤次數 (預設 3)
│   │   │       #
│   │   │       # 【心跳設定】
│   │   │       #   HeartbeatIntervalMs: int - 心跳間隔 (預設 3000 ms，0=停用)
│   │   │
│   │   ├── Handshake/
│   │   │   ├── PlcHandshakeService.cs
│   │   │   │   # PLC 握手協議實作
│   │   │   │   # 實作 IPlcHandshakePort 介面
│   │   │   │   #
│   │   │   │   # 【握手流程】
│   │   │   │   #   1. PLC 送出觸發訊號 (10001=1)
│   │   │   │   #   2. Vision 回應已收到 (10002=1)
│   │   │   │   #   3. Vision 回報結果 (10003=OK/NG)
│   │   │   │   #   4. PLC 清除觸發 (10001=0)
│   │   │   │
│   │   │   └── PlcHandshakeOptions.cs
│   │   │       # 握手選項
│   │   │       # 包含：等待超時、重試次數
│   │   │
│   │   └── SignalMapping/
│   │       ├── PlcSignalMapper.cs
│   │       │   # PLC 訊號映射實作
│   │       │   # 訊號名稱 → Modbus 位址
│   │       │
│   │       └── PlcSignalMapOptions.cs
│   │           # 訊號映射配置
│   │
│   └── Light/                          # 光源控制驅動
│       ├── LtsAsciiLightPort.cs
│       │   # LTS 光源控制器驅動
│       │   # 實作 ILightPort 介面
│       │   # 支援 TCP ASCII / Serial 通訊
│       │
│       └── LightDeviceOptions.cs
│           # 光源配置選項 (第 6-32 行)
│           # appsettings.json 區段名稱: "Devices:Light"
│           #
│           # 【連線設定】
│           #   ListenIp      : string - 監聽 IP (預設 "0.0.0.0")
│           #   ListenPort    : int    - 監聽埠號 (預設 8000)
│           #   ChannelCount  : int?   - 通道數 (預設 2)
│           #
│           # 【超時設定】
│           #   TimeoutMs     : int?   - 通訊超時 (預設 1000 ms)
│           #   PollIntervalMs: int?   - 輪詢間隔 (預設 500 ms)
│           #
│           # 【設備端設定（可選）】
│           #   DeviceIp      : string? - 設備 IP
│           #   DevicePort    : int?    - 設備埠號
│           #   UnitId        : byte?   - Modbus Unit ID
│
├── Services/                           # 基礎設施服務
│   ├── AutoRunService.cs
│   │   # 自動運行服務實作 (第 22-100+ 行)
│   │   # 實作 IAutoRunService 介面
│   │   #
│   │   # 【核心功能】
│   │   #   - 管理狀態機 (Idle→WaitingTrigger→Capturing...)
│   │   #   - 監聽 PLC 觸發訊號
│   │   #   - 協調相機、AI、PLC 的檢測流程
│   │   #
│   │   # 【空閒檢測常數】(第 46-47 行)
│   │   #   IdleWarningSeconds   = 60   - 空閒警告秒數
│   │   #   IdleCheckIntervalMs  = 5000 - 檢查間隔 ms
│   ├── DefectFilteringService.cs
│   │   # 瑕疵過濾服務實作 (第 22-331 行)
│   │   # 實作 IDefectFilteringService 介面
│   │   #
│   │   # 【過濾規則說明】(第 13-21 行)
│   │   #   1. 關鍵瑕疵（CriticalClasses）：無論大小，直接判 NG
│   │   #   2. 大瑕疵（≥ MediumAreaMm2）：直接判 NG
│   │   #   3. 中等瑕疵（MinimumAreaMm2 ~ MediumAreaMm2）：
│   │   #      - 單一個：OK
│   │   #      - 多個且距離 < CloseDistanceMm：NG
│   │   #      - 多個且距離 ≥ CloseDistanceMm：OK
│   │   #   4. 小瑕疵（≤ MinimumAreaMm2）：忽略
│   │   #
│   │   # 【主要方法】
│   │   #   FilterDefects(defects)    - 過濾瑕疵，回傳 DefectFilteringResult
│   │   #   UpdateOptions(options)    - 動態更新配置
│   │   #   CalculateAreaMm2(px)      - 像素 → mm² 換算
│   │   #   CalculateDistanceMm(d1,d2)- 計算兩瑕疵距離
│   │
│   ├── ContourOverlayRenderer.cs
│   │   # 瑕疵輪廓渲染實作
│   │   # 實作 IContourOverlayRenderer 介面
│   │   # 在影像上繪製瑕疵輪廓線
│   │
│   └── ConfigAuthService.cs
│       # 認證服務實作 (第 11-127 行)
│       # 實作 IAuthService 介面
│       #
│       # 【appsettings.json 配置】
│       #   區段名稱: "Authentication"
│       #
│       #   DefaultRole: string - 預設角色 (預設 "Operator")
│       #
│       #   Users: array - 使用者列表
│       #     - Username   : string - 帳號
│       #     - Password   : string - 密碼
│       #     - Role       : string - 角色 (Operator/Engineer/Vendor)
│       #     - DisplayName: string? - 顯示名稱
│       #
│       # 【appsettings.json 範例】
│       #   "Authentication": {
│       #     "DefaultRole": "Operator",
│       #     "Users": [
│       #       { "Username": "admin", "Password": "1234", "Role": "Vendor" },
│       #       { "Username": "engineer", "Password": "eng123", "Role": "Engineer" }
│       #     ]
│       #   }
│       #
│       # 【權限判斷邏輯】(第 103-107 行)
│       #   數值越小權限越高：Vendor(1) > Engineer(2) > Operator(3)
│       #   HasPermission: (int)currentRole <= (int)requiredRole
│
├── Persistence/SQLite/                 # SQLite 資料庫實作
│   ├── SqliteInspectionRepository.cs
│   │   # 檢測記錄儲存庫
│   │   # 實作 IInspectionRepository 介面
│   │   # 使用 Dapper ORM
│   │
│   ├── SqliteWorkOrderRepository.cs
│   │   # 工單儲存庫
│   │   # 實作 IWorkOrderRepository 介面
│   │
│   ├── SqliteInspectionHistoryQuery.cs
│   │   # 歷史查詢：日期篩選、OK/NG篩選、分頁
│   │
│   └── SqliteProductionStatsQuery.cs
│       # 生產統計：總數、良率計算
│
├── AiService/                          # AI 推論服務實作
│   ├── AinaviAiInferencePort.cs
│   │   # AINAVI 推論實作
│   │   # 實作 IAiInferencePort 介面
│   │   # HTTP Multipart 上傳影像
│   │
│   └── AinaviAiModelPort.cs
│       # AINAVI 模型管理
│       # 實作 IAiModelPort 介面
│
└── DependencyInjection/
    └── ServiceCollectionExtensions.cs
        # DI 容器擴充方法
        # services.AddInfrastructure(config)
        # services.AddAuthService()
```

