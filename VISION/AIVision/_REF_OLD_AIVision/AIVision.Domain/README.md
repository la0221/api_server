# AIVision.Domain - 領域層

核心業務邏輯與資料結構定義，不依賴外部套件。

```
AIVision.Domain/
│
├── Abstractions/
│   └── Entity.cs
│       # 所有領域實體的基底類別
│       # 提供 Id (Guid) 屬性作為唯一識別
│       # 實作相等性判斷 (Equals, GetHashCode)
│
├── Entities/
│   ├── WorkOrder.cs
│   │   # 工單實體 - 代表一個生產批次
│   │   #
│   │   # 【主要屬性】(第 37-51 行)
│   │   #   Code          : string   - 工單編號
│   │   #   ProductName   : string   - 產品名稱
│   │   #   ModelName     : string?  - 使用的 AI 模型名稱
│   │   #   MachineModelName : string? - 機台型號
│   │   #   Status        : string   - 狀態 ("Active"/"Completed"/"Cancelled")
│   │   #   StartAt       : DateTime - 開始時間
│   │   #   EndAt         : DateTime? - 結束時間
│   │   #   IsClosed      : bool     - 是否已關閉
│   │   #
│   │   # 【方法】
│   │   #   Close()            - 關閉工單，設定 EndAt 與 Status="Completed"
│   │   #   UpdateModelName()  - 更新模型名稱
│   │   #   UpdateStatus()     - 更新狀態（只接受 Active/Completed/Cancelled）
│   │
│   ├── Inspection.cs
│   │   # 檢測記錄實體 - 代表單次產品檢測
│   │   #
│   │   # 【主要屬性】(第 44-60 行)
│   │   #   WorkOrderId   : Guid     - 所屬工單 ID
│   │   #   Result        : string   - 檢測結果 ("OK"/"NG")
│   │   #   Confidence    : float?   - AI 信心分數 (0.0~1.0)
│   │   #   ImagePath     : string?  - 原始圖片路徑
│   │   #   AnnotatedImagePath : string? - 標註後圖片路徑
│   │   #   ModelVersion  : string   - 使用的模型版本
│   │   #   InferenceTimeMs : int?   - 推論耗時 (毫秒)
│   │   #   Defects       : IReadOnlyList<Defect>? - 瑕疵列表
│   │   #   At            : DateTime - 檢測時間
│   │
│   └── Defect.cs
│       # 瑕疵實體 - 代表檢測到的單一缺陷
│       # 包含：瑕疵類型名稱、邊界框座標、嚴重程度
│
├── Shared/
│   ├── Prediction.cs
│   │   # AI 預測結果
│   │   #
│   │   # 【Detection 記錄】(第 11 行)
│   │   #   Label       : string     - 瑕疵類別名稱
│   │   #   BoundingBox : RectangleF - 邊界框 (X, Y, Width, Height)
│   │   #   Confidence  : float      - 信心分數
│   │   #
│   │   # 【WorkflowDefect 類別】(第 16-58 行) - Workflow 專用
│   │   #   SourceBlock : string     - 來源 Block 名稱
│   │   #   ClassName   : string     - 瑕疵類別
│   │   #   Areas       : IReadOnlyList<int> - 瑕疵面積列表
│   │   #   Contours    : 輪廓座標點列表
│   │   #   HasDefect   : bool       - 是否有缺陷 (Areas 任一 > 0)
│   │   #   TotalArea   : int        - 總瑕疵面積
│   │   #
│   │   # 【Prediction 記錄】(第 68-97 行)
│   │   #   Label         : string   - 整體標籤
│   │   #   Confidence    : float    - 整體信心
│   │   #   IsOk          : bool     - 是否合格
│   │   #   ModelVersion  : string   - 模型版本
│   │   #   Detections    : IReadOnlyList<Detection> - 偵測框列表
│   │   #   WorkflowDefects : IReadOnlyList<WorkflowDefect>? - Workflow 瑕疵
│   │
│   ├── ImageData.cs
│   │   # 影像資料值物件 (第 11 行)
│   │   #
│   │   # 【參數】
│   │   #   Bytes       : byte[]  - 原始像素資料
│   │   #   Width       : int     - 影像寬度（像素）
│   │   #   Height      : int     - 影像高度（像素）
│   │   #   PixelFormat : string  - 像素格式 ("Mono8", "Bgr24", "Rgb24")
│   │   #   Stride      : int     - 每行位元組數 (0=自動計算)
│   │   #
│   │   # 【常用 PixelFormat】
│   │   #   "Mono8"  - 灰階 8bit，每像素 1 byte
│   │   #   "Bgr24"  - 彩色 24bit，每像素 3 bytes (藍綠紅)
│   │   #   "Rgb24"  - 彩色 24bit，每像素 3 bytes (紅綠藍)
│   │
│   ├── IoCommand.cs
│   │   # PLC 寫入指令封裝
│   │
│   └── IoSnapshot.cs
│       # PLC 狀態快照
│
├── Plc/
│   ├── PlcHandshakeState.cs
│   │   # PLC 握手狀態列舉
│   │   # Idle / WaitingAck / Completed / Error
│   │
│   ├── PlcAddressBaseMode.cs
│   │   # Modbus 位址基底模式
│   │   # ZeroBased(0起始) / OneBased(1起始)
│   │
│   ├── PlcSignalDefinition.cs
│   │   # PLC 訊號定義
│   │
│   ├── PlcSignalEnums.cs
│   │   # PLC 訊號類型列舉 (第 1-61 行)
│   │   #
│   │   # 【PlcSignalDirection】訊號方向
│   │   #   PlcToPc       - PLC→PC (PC 讀取)
│   │   #   PcToPlc       - PC→PLC (PC 寫入)
│   │   #   Bidirectional - 雙向讀寫
│   │   #
│   │   # 【PlcSignalArea】Modbus 區域類型
│   │   #   Coil            - 線圈 (FC01/05/15)，可讀寫位元
│   │   #   DiscreteInput   - 離散輸入 (FC02)，只讀位元
│   │   #   HoldingRegister - 保持暫存器 (FC03/06/16)，可讀寫
│   │   #   InputRegister   - 輸入暫存器 (FC04)，只讀
│   │   #
│   │   # 【PlcEdgeMode】觸發模式
│   │   #   Level   - 位準觸發（看當前值）
│   │   #   Rising  - 上升緣 (0→1)
│   │   #   Falling - 下降緣 (1→0)
│   │   #
│   │   # 【PlcActiveLevel】有效位準
│   │   #   High - 高位準有效 (1=有效)
│   │   #   Low  - 低位準有效 (0=有效)
│   │
│   └── ModbusAddressConverter.cs
│       # Modbus 位址轉換器
│       # HMI 位址 ↔ Modbus 協議位址
│
├── AutoRun/
│   ├── AutoRunState.cs
│   │   # 自動運行狀態列舉 (第 6-37 行)
│   │   #
│   │   # 【狀態值】
│   │   #   Idle           - 閒置，等待啟動
│   │   #   Initializing   - 初始化中（連接相機、PLC）
│   │   #   WaitingTrigger - 等待 PLC 觸發訊號
│   │   #   Capturing      - Line Scan 取像中
│   │   #   Inferring      - AI 推論中
│   │   #   Reporting      - 回報 PLC 結果中
│   │   #   Paused         - 暫停
│   │   #   Stopping       - 停止中
│   │   #   Stopped        - 已停止
│   │   #   Error          - 錯誤狀態
│   │   #
│   │   # 【狀態流程】
│   │   #   Idle → Initializing → WaitingTrigger → Capturing
│   │   #        → Inferring → Reporting → WaitingTrigger (循環)
│   │
│   ├── AutoRunStatistics.cs
│   │   # 自動運行統計資料 (第 6-111 行)
│   │   #
│   │   # 【統計屬性】
│   │   #   TotalCount      : int    - 總檢測數
│   │   #   OkCount         : int    - 合格數
│   │   #   NgCount         : int    - 不合格數
│   │   #   ErrorCount      : int    - 錯誤數
│   │   #   ConsecutiveErrorCount : int - 連續錯誤數
│   │   #
│   │   # 【計算屬性】
│   │   #   YieldRate       : double - 良率 % (OkCount/TotalCount*100)
│   │   #   UnitsPerHour    : double - 每小時產量 UPH
│   │   #   RunningDuration : TimeSpan - 運行時長
│   │   #
│   │   # 【時間統計】
│   │   #   AverageCaptureTimeMs   : double - 平均取像時間 (ms)
│   │   #   AverageInferenceTimeMs : double - 平均推論時間 (ms)
│   │   #   AverageTotalTimeMs     : double - 平均總耗時 (ms)
│   │   #
│   │   # 【方法】
│   │   #   Reset()  - 重置所有統計
│   │   #   Update(isOk, captureTimeMs, inferenceTimeMs, totalTimeMs) - 更新統計
│   │   #   RecordError() - 記錄錯誤
│   │
│   ├── AutoRunOptions.cs
│   │   # 自動運行配置選項
│   │
│   └── AutoRunEvents.cs
│       # 自動運行事件定義
│
└── User/
    └── UserRole.cs
        # 使用者權限角色列舉 (第 6-16 行)
        #
        # 【角色定義】
        #   Operator = 3  - 作業員（最低權限）
        #   Engineer = 2  - 工程師（中等權限）
        #   Vendor   = 1  - 廠商（最高權限）
        #
        # 【權限比較邏輯】
        #   數值越小權限越高
        #   判斷方式：(int)currentRole <= (int)requiredRole
        #   例如：Engineer(2) <= Engineer(2) → true，有權限
        #         Operator(3) <= Engineer(2) → false，無權限
```
