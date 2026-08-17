# Domain 層 - 核心業務邏輯

## 概述

Domain 層定義 AOI 檢測系統的核心業務概念，包含工單、檢測、瑕疵、PLC 通訊等資料結構。

---

## 檔案清單與功能說明

### Abstractions/ - 基礎抽象

| 檔案 | 功能說明 |
|------|----------|
| `Entity.cs` | 所有業務實體的基底類，提供 Id 與相等性判斷 |

### Entities/ - 業務實體

| 檔案 | 功能說明 |
|------|----------|
| `WorkOrder.cs` | 工單實體：工單編號、產品型號、開始/結束時間、狀態 |
| `Inspection.cs` | 檢測記錄：檢測時間、OK/NG 結果、信心分數、關聯工單 |
| `Defect.cs` | 瑕疵資料：瑕疵類型、邊界框座標、嚴重程度 |

### Shared/ - 共用資料結構

| 檔案 | 功能說明 |
|------|----------|
| `Prediction.cs` | AI 預測結果：標籤、信心分數、邊界框座標 |
| `ImageData.cs` | 影像資料：原始位元組、寬度、高度、像素格式 |
| `IoCommand.cs` | IO 寫入指令：PLC 寫入動作封裝 |
| `IoSnapshot.cs` | IO 狀態快照：PLC 當前狀態讀取 |

### Plc/ - PLC 通訊模型

| 檔案 | 功能說明 |
|------|----------|
| `PlcHandshakeState.cs` | 握手狀態：Idle、WaitingAck、Completed、Error |
| `PlcAddressBaseMode.cs` | 位址模式：0-based 或 1-based |
| `PlcSignalDefinition.cs` | 訊號定義：訊號名稱與 Modbus 位址對應 |
| `ModbusAddressConverter.cs` | 位址轉換：HMI 位址 ↔ Modbus 協議位址 |
| `PlcSignalEnums.cs` | 訊號類型列舉：觸發、結果、狀態等訊號定義 |

### AutoRun/ - 自動運行狀態機

| 檔案 | 功能說明 |
|------|----------|
| `AutoRunState.cs` | 運行狀態：Idle、Initializing、WaitingTrigger、Capturing、Inferring、Reporting |
| `AutoRunStatistics.cs` | 運行統計：累計檢測數、OK 數、NG 數、良率 |
| `AutoRunOptions.cs` | 運行配置：觸發模式、超時設定、重試次數 |
| `AutoRunEvents.cs` | 事件定義：狀態變更、檢測完成、錯誤發生 |

### User/ - 使用者權限

| 檔案 | 功能說明 |
|------|----------|
| `UserRole.cs` | 權限角色：Operator（作業員）、Engineer（工程師）、Vendor（廠商） |

---

**最後更新**: 2025-12-26
