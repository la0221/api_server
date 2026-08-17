# Target Architecture – Assembly/Test/AGV/MES/AI 專案

## 1. 系統場景

- 組裝站（Assembly Station）
- 測試站（Test Station）
- AGV（含 Roller + PLC base）
- 中控台 Orchestrator（含派車邏輯、工單／流程管控）
- MES / ERP / Web HMI / Dashboard
- 未來會接 AI Model（視覺、判定…）

## 2. 分層原則

### 2.1 每個服務內部：用 Clean Architecture

每一個後端服務（.NET 或 Python）內部都遵守：

- **Presentation / Adapters**
  - Web API Controller（如果需要對外 HTTP）
  - ROS2 Node Adapters（Publish/Subscribe/Service/Action）
- **Application**
  - UseCases / Services
- **Domain**
  - Entities / Aggregates / Value Objects / State Machines
- **Infrastructure**
  - PLC / IO / Modbus / EtherCAT / Serial / MQTT / DB / File / Logger

### 2.2 設備 ↔ 設備 之間：用 ROS2

- AGV、組裝站、測試站、各種 PLC Gateway、Camera、AI Service 之間的溝通，目標是改成 ROS2：
  - 使用 **Topic** 傳狀態與資料
  - 使用 **Service** 提供一次性命令
  - 使用 **Action** 處理長任務（如：AGV 移動、Docking、整站流程）
- ROS2 節點規劃方向：
  - `assembly_station_node`
  - `test_station_node`
  - `agv_controller_node`
  - `agv_roller_node`
  - `station_io_node`（各站 PLC/IO gateway）
  - `orchestrator_node`（中控主腦）
  - 視需要擴充 camera_node、ai_node 等

### 2.3 對 MES / ERP / HMI：保留 HTTP API

- 對人類與企業系統的邊界仍用 HTTP / WebSocket：
  - Web HMI / React 前端
  - MES / ERP
  - 報表、Dashboard
- 這些 API 集中由「中控 Orchestrator」或專門的 Web API 專案提供，例如：
  - `GET /api/system/status`
  - `GET /api/orders`
  - `POST /api/orders/{id}/start`
  - `GET /api/agvs`
  - `GET /api/stations`

### 2.4 MCP 出現的位置

- MCP 不進到每個設備服務裡面，而是：
  - MCP Server 站在系統外圍，提供工具：
    - 例如：`create_order`, `query_station_status`, `move_agv`, `start_test_cycle`
  - 這些工具的實作，只是呼叫：
    - 中控 Orchestrator 的 HTTP API，或
    - 一個 `Ros2Bridge`（再去 call ROS2 Service/Action）
- 目標：**現有服務只需要知道「API」與「ROS2」，不用感知 MCP。**

## 3. 這個 repo 希望達成的事情

1. 釐清目前專案中：
   - 哪些是「站別服務」（Assembly/Test/AGV/Station IO）
   - 哪些是「中控／派車／Orchestrator」
   - 哪些是「前端 / MES / 外部 API」
2. 幫忙設計一個對應到上面 Target 的：
   - 服務清單（Service List）
   - ROS2 Node 清單與 Topic/Service/Action 定義
   - API 邊界（哪些功能仍由 HTTP 暴露）
   - MCP 工具清單（將來 AI 要能呼叫哪些高層功能）
3. 給出一份「遷移／重構計畫」，說明：
   - 目前程式哪些部分可以直接包成 ROS2 Node Adapter
   - 哪些服務需要拆分或重新命名
   - 建議的資料夾結構與專案切分方式

你是一個工廠自動化與機器人系統的軟體架構師。

這個 repo 是一個實際專案，包含：
- 組裝站（Assembly Station）
- 測試站（Test Station）
- AGV（含 Roller / PLC base）
- 中控台 Orchestrator
- 對接 MES / ERP / Web HMI 的 API
- 之後還會有 AI Model（視覺判定等）

請你閱讀專案中的程式碼與 docs，特別是：
- docs/TARGET_ARCHITECTURE.md（我已經寫好的目標架構說明）

接著依照這個目標，幫我做下面幾件事，並用多個文件輸出：

1. 產生 `docs/CURRENT_ARCHITECTURE.md`
   - 描述目前整個系統有哪些服務 / 專案
   - 它們彼此之間現在的通訊方式是什麼（HTTP, MQTT, DB, 其他）
   - 用簡單的架構圖或列表說明資料流與控制流

2. 產生 `docs/SERVICE_MAPPING.md`
   - 依照 TARGET_ARCHITECTURE 裡的角色，把現有專案對應到：
     - 組裝站服務（AssemblyStation.Service）
     - 測試站服務（TestStation.Service）
     - AGV 控制（AgvController.Service）
     - AGV 上 Roller / PLCbase（AgvRoller.Service）
     - 站別 IO Gateway（StationIoGateway.Service）
     - 中控 Orchestrator（Orchestrator.Service）
   - 如果有不確定的服務，也請標註出來，寫上你推測的角色

3. 產生 `docs/ROS2_INTERFACE_DESIGN.md`
   - 幫我設計這個系統的 ROS2 介面：
     - 每個服務對應的 ROS2 Node 名稱
     - Topics：名稱、發佈方、訂閱方、建議的欄位結構
     - Services：名稱、誰 call 誰、Request / Response 欄位
     - Actions：例如 AGV 移動、Docking、整站流程等，設計 Goal/Feedback/Result
   - 記得設備 ↔ 設備之間都走 ROS2，不走 HTTP

4. 產生 `docs/API_BOUNDARIES.md`
   - 分析現有 Web API / Controller：
     - 哪些是未來應該保留給 MES / ERP / Web HMI 使用的 HTTP API
     - 哪些原本拿來做設備之間溝通的 HTTP API，未來應改成 ROS2 通訊
   - 幫我整理成一個表格，列出：
     - API 路由
     - 現用途
     - 建議未來角色（保留 / 改成 ROS2 / 合併 / 廢除）
     - 如需 refactor 也請簡述做法

5. 產生 `docs/MCP_TOOLS_DESIGN.md`
   - 假設未來會有一個 MCP Server，要讓 AI 可以操作這套系統
   - 請從 Orchestrator / API 中挑出適合公開給 AI 用的高階動作
     - 例如：建立工單、查詢站狀態、指派 AGV 任務、查詢某批料履歷
   - 對每個 Tool 說明：
     - Tool 名稱
     - 輸入參數
     - 要呼叫的後端 API 或 ROS2 Service
     - 預期的回傳結構

6. 產生 `ROS2_MIGRATION_PLAN.md`
   - 以分階段方式，規劃如何從現有架構遷移到 TARGET_ARCHITECTURE：
     - Phase 1：標記與命名調整（不改功能）
     - Phase 2：在現有服務外面加 ROS2 Adapter，不改 Domain
     - Phase 3：修改 Orchestrator，改從 ROS2 收各站狀態
     - Phase 4：把 MCP Server 工具實作起來（先從查詢類）
   - 每一階段請寫出具體要動哪些專案 / 檔案，以及風險點

在所有文件中，請盡量用清楚的目錄與小節，方便我之後在 ChatGPT 那邊貼給另一個模型審查與調整。
