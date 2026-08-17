# AIVision 架構拆分對照（區 / 層 / 功能）

> 範圍：整個 `AIVision` 方案（全方案重新分層）
> 原則：依「實際相依關係」命名區與層，不受任何先入為主的分類限制。
> 狀態：**只描述，不改 code**。最後一節為建議的目標結構（待你決定後才動手）。

---

## 0. 一句話結論

這個方案**已經是標準的 Clean Architecture + MVVM**，相依方向乾淨（Domain 零依賴、Application 不碰 Infrastructure、Presentation 當組裝根）。
所以「拆分」的重點不是打掉重建，而是：**(1) 把散落的橫切職責收攏成「區」、(2) 補兩個缺口（控制收攏、樣式分層）、(3) 修兩個小滲漏。**

---

## 1. 兩個維度：先分清「層」與「區」

- **層（Layer）**＝由「誰可以依賴誰」決定的垂直堆疊。箭頭只能往內（往 Domain）。
- **區（Zone）**＝由「功能職責」決定的橫切分組，可以跨多個層。

同一個檔案一定屬於某一「層」，同時屬於某一「區」。

---

## 2. 現況：層（Layer）— 依相依關係

實際 `ProjectReference` 推導出的依賴圖（箭頭＝「依賴」）：

```
              ┌─────────────────────────────────────────────┐
              │  表現層 (Presentation)                       │
              │  AIVision.Presentation.Wpf (WinExe)          │
              │  AIVision.Api (HTTP)                         │
              └───────┬───────────────┬───────────┬─────────┘
                      │               │           │
        ┌─────────────▼───┐   ┌───────▼───────┐   │
        │ 轉接層           │   │ 基礎設施層     │   │
        │ InterfaceAdapters│   │ Infrastructure │   │
        └─────────┬───────┘   └───────┬───────┘   │
                  │                   │           │
        ┌─────────▼───┐       ┌───────▼────────┐  │
        │ 模型推論層   │       │  應用層         │◄─┘
        │ MoldCode.Onnx│──────►│  Application    │
        └─────────┬───┘       └───────┬────────┘
                  │                   │
                  └─────────┬─────────┘
                            ▼
                  ┌───────────────────┐
                  │ 核心模型層 Domain  │  ← 零依賴
                  └───────────────────┘

  測試：Application.Tests → Application / Domain / Infrastructure / MoldCode.Onnx
  工具：MoldCode.Harness (Exe) → MoldCode.Onnx
```

| 代號 | 層名稱 | 專案 | 職責 | 對外依賴 |
|---|---|---|---|---|
| L0 | **核心模型層** | `AIVision.Domain` | 實體、值物件、業務規則、列舉 | 無（純） |
| L1 | **應用層** | `AIVision.Application` | UseCase/Command Handler、Ports(介面)、Contracts(DTO) | →Domain |
| L2 | **轉接層** | `AIVision.InterfaceAdapters` | DTO ↔ Domain Mapper | →Application, Domain |
| L3 | **基礎設施層** | `AIVision.Infrastructure` | 相機/PLC/光源/DB/AI 推論的「實作」 | →Application, Domain |
| L4 | **模型推論層** | `AIVision.MoldCode.Onnx` | ONNX 模型載入與前處理/辨識 | →Application, Domain |
| L5 | **表現層** | `AIVision.Presentation.Wpf` / `AIVision.Api` | UI / HTTP，組裝根(DI) | →Application, Infra, Adapters, Onnx |
| — | 測試 | `AIVision.Application.Tests` | 單元測試 | 多 |
| — | 工具台 | `AIVision.MoldCode.Harness` | 命令列驗證/golden dump | →Onnx |

✅ 重點：**Application 沒有依賴 Infrastructure**（靠 Ports 反轉），這是整個架構最值錢的地方，務必維持。

---

## 3. 現況：區（Zone）— 依功能職責（橫切）

下面 6 個「區」是依實際資料夾內容歸納出來的功能群組。每個區都跨越多層。

### 區 A —— 模型區（Model）
> 你舉例的「模型區 model 層」。實際橫跨 Domain + ONNX。

- 業務模型：[Entities/](../AIVision/AIVision.Domain/Entities/)、[MoldCode/](../AIVision/AIVision.Domain/MoldCode/)、[Plc/](../AIVision/AIVision.Domain/Plc/)、[Shared/](../AIVision/AIVision.Domain/Shared/)
- AI 推論引擎：`AIVision.MoldCode.Onnx`（[OnnxMoldCodeRecognizer.cs](../AIVision/AIVision.MoldCode.Onnx/OnnxMoldCodeRecognizer.cs)、[MoldCodePreprocessor.cs](../AIVision/AIVision.MoldCode.Onnx/MoldCodePreprocessor.cs)、WarpPolar…）
- **功能**：缺陷/檢測/工單模型、模號穴號投票與驗證規則、PLC 訊號定義、模型推論。

### 區 B —— 裝置連接區（Device Connectivity）
> 你舉例的「連接層」的其中一半：把模型接上「那些鬼東東」。

- 契約（介面）：[Application/Ports/Devices/](../AIVision/AIVision.Application/Ports/Devices/)（Camera/Plc/Light/Ai…）
- 實作（驅動）：[Infrastructure/Devices/](../AIVision/AIVision.Infrastructure/Devices/)（Hik、IDS、Modbus PLC、LTS 光源、Fake…）+ Wpf 內的 [Adapters/Camera/](../AIVision/AIVision.Presentation.Wpf/Adapters/Camera/)（AForge）
- **功能**：相機擷取/探索、PLC 握手與訊號對應、光源控制、AI 推論埠（本地/HTTP/AINAVI 可切換）。

### 區 C —— 資料與設定區（Persistence & Config）
- 持久化：[Infrastructure/Persistence/](../AIVision/AIVision.Infrastructure/Persistence/)（SQLite + InMemory）
- 設定：[Application/Configuration/](../AIVision/AIVision.Application/Configuration/)、[Infrastructure/Configs/](../AIVision/AIVision.Infrastructure/Configs/)、[Services/ProjectConfigService.cs](../AIVision/AIVision.Infrastructure/Services/ProjectConfigService.cs)、各種 `*.json`（models.json、camera-ids.json…）
- **功能**：檢測/工單儲存、生產統計查詢、模型清單、專案設定載入。

### 區 D —— 應用流程區（Use Cases）
> 你舉例的「連接層」的另一半的上游：協調模型與裝置完成一件事。

- [Application/Inspection/Commands/](../AIVision/AIVision.Application/Inspection/Commands/)、[Application/MoldCode/](../AIVision/AIVision.Application/MoldCode/)、[Application/Services/](../AIVision/AIVision.Application/Services/)
- 轉接：[InterfaceAdapters/Inspection/InspectionResultMapper.cs](../AIVision/AIVision.InterfaceAdapters/Inspection/InspectionResultMapper.cs)
- **功能**：啟動檢測週期、切換模型、模號穴號（單張/配對）驗證流程、離線檢測、工單管理。

### 區 E —— 控制區（Control：權限 + 畫面控制）
> 你舉例的「控制區」。**目前散在 3 個地方，最值得收攏。**

- 權限：[Application/Ports/Services/IAuthService.cs](../AIVision/AIVision.Application/Ports/Services/IAuthService.cs) + [Infrastructure/Services/ConfigAuthService.cs](../AIVision/AIVision.Infrastructure/Services/ConfigAuthService.cs) + [Domain/User/UserRole.cs](../AIVision/AIVision.Domain/User/UserRole.cs)（Operator/Engineer/Vendor 三級）
- 登入：[ViewModels/LoginViewModel.cs](../AIVision/AIVision.Presentation.Wpf/ViewModels/LoginViewModel.cs)、[Views/LoginView.xaml.cs](../AIVision/AIVision.Presentation.Wpf/Views/LoginView.xaml.cs)
- 畫面控制 / 控板外框：[Services/Navigation/](../AIVision/AIVision.Presentation.Wpf/Services/Navigation/)、[ViewModels/ShellViewModel.cs](../AIVision/AIVision.Presentation.Wpf/ViewModels/ShellViewModel.cs)
- 組裝根：[App.xaml.cs](../AIVision/AIVision.Presentation.Wpf/App.xaml.cs)、[Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs](../AIVision/AIVision.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs)
- **功能**：登入/登出、角色權限判斷、視窗/對話框導覽、主控板分頁切換。

### 區 F —— 畫面區（Presentation UI：版面 + 風格）
> 你舉例的「畫面區 layout 風格控制」。

- 版面：[Views/](../AIVision/AIVision.Presentation.Wpf/Views/)（30+ 個 .xaml）
- 對應邏輯：[ViewModels/](../AIVision/AIVision.Presentation.Wpf/ViewModels/)（30+ 個）
- 風格/顯示轉換：[Converters/](../AIVision/AIVision.Presentation.Wpf/Converters/)（顏色、可見性、標題…）
- **功能**：相機、批次推論、歷史、生產統計、IO 面板、光源控制、模型/專案管理等畫面。
- ⚠️ **缺口**：沒有獨立的 `Themes/` 或集中 `Styles/ResourceDictionary`，「風格控制」目前只靠 Converters，沒有真正的樣式層。

### 區 G —— 對外介接區（External API）
- [AIVision.Api/Controllers/](../AIVision/AIVision.Api/Controllers/)（Ainavi、Inspection）、[Wpf/Services/AinaviApiClient.cs](../AIVision/AIVision.Presentation.Wpf/Services/AinaviApiClient.cs)
- **功能**：對外 HTTP（含 AINAVI 借鑒整合）。屬於 [TARGET_ARCHITECTURE.md](../TARGET_ARCHITECTURE.md) 未來 ROS2/MES 願景的接點。

---

## 4. 你的 4 塊 vs 現況（直接對位）

| 你的構想 | 對到現況的「區 / 層」 | 狀態 |
|---|---|---|
| 模型區 model 層 | 區 A（Domain L0 + Onnx L4） | ✅ 已具備，乾淨 |
| 連接層（接鬼東東 + 接畫面） | 拆成兩塊：**區 B 裝置連接**（Ports+Infra）＋**區 D 應用流程/轉接**（Application+Adapters） | ✅ 已具備，但你把兩種連接混為一談 |
| 控制區（權限 + 畫面控制） | 區 E | ⚠️ 已具備但**散在 3 處**，建議收攏 |
| 畫面區 layout 風格 | 區 F | ⚠️ Layout 有、**風格層缺**（無 Themes） |

> 額外冒出來、你沒列到的區：**區 C 資料與設定**、**區 G 對外 API**。這兩個確實存在，建議在你的心智模型裡補上。

---

## 5. 發現的問題（拆分時要順手處理）

1. **控制區散落**：權限在 Application、導覽在 Wpf/Services、Shell 在 ViewModels —— 沒有單一「控制」家。建議在 Wpf 下開 `Shell/`（或 `Control/`）集中。
2. **無樣式層**：缺 `Themes/`，風格無法集中換膚。
3. **Application 帶 UI 味道**：[Application/ViewModels/Camera/CameraDeviceVm.cs](../AIVision/AIVision.Application/ViewModels/Camera/CameraDeviceVm.cs) 出現在應用層 —— ViewModel 概念應屬表現層，建議搬走或改名為 DTO。
4. **TargetFramework 偏重**：`Domain`/`Application` 都掛 `net8.0-windows`。純業務理論上可降為 `net8.0`，與 Windows 解耦（非必要，但能保護 L0 純度）。
5. **Wpf 內也放了 Adapters**（AForge 相機）：與 Infrastructure 的裝置區重疊，建議統一搬到 Infrastructure 或明確標註「僅 WPF 專用相機」。

---

## 6. 建議的目標結構（樹狀，僅提案，未動 code）

維持現有 6 專案分層不變（已經正確），只在**表現層內部**與**心智分區**上做收攏：

```
AIVision.sln
│
├─ [L0] AIVision.Domain ........................ 區A 模型
│     Entities / MoldCode / Plc / User / Shared
│
├─ [L1] AIVision.Application ................... 區B(契約) 區C(設定) 區D(流程) 區E(權限契約)
│     Ports/ ── 裝置/持久/服務 介面（保持）
│     Inspection/ MoldCode/ Services/ ── UseCases
│     Configuration/ Contracts/
│     ⚠ 移除 ViewModels/（搬到表現層）
│
├─ [L2] AIVision.InterfaceAdapters ............ 區D Mapper
│
├─ [L3] AIVision.Infrastructure ............... 區B(實作) 區C(實作) 區E(權限實作)
│     Devices/ Persistence/ Services/ ── 保持
│     ◄ 收編 Wpf 的相機 Adapter（可選）
│
├─ [L4] AIVision.MoldCode.Onnx ................ 區A 推論
│
├─ [L5] AIVision.Presentation.Wpf ............. 區E 控制 + 區F 畫面
│     ├─ Shell/            ◄ 新增：收攏「控制區」
│     │     Navigation/ (現有)
│     │     Auth/         (登入 VM + 權限閘門)
│     │     ShellViewModel.cs (現有)
│     ├─ Views/           ── 區F 版面（保持）
│     ├─ ViewModels/      ── 區F 對應邏輯（保持，收 Application 搬來的 VM）
│     ├─ Themes/          ◄ 新增：收攏「風格區」(色票/字體/控件樣式)
│     ├─ Converters/      ── 風格輔助（保持）
│     └─ Adapters/        ── WPF 專用裝置接點
│
├─ [L5] AIVision.Api .......................... 區G 對外 HTTP
│
├─ AIVision.Application.Tests ................. 測試
└─ AIVision.MoldCode.Harness ................. 工具台
```

### 動作清單（待你點頭）
- [ ] 新增 `Presentation.Wpf/Shell/`，把 Navigation + 登入/權限相關 VM 移入（收攏控制區）
- [ ] 新增 `Presentation.Wpf/Themes/`，抽出色票/字體/控件樣式（建立風格層）
- [ ] 把 `Application/ViewModels/` 搬到表現層或改為 DTO（修滲漏）
- [ ] （可選）`Domain`/`Application` 降為 `net8.0`
- [ ] （可選）Wpf 相機 Adapter 併入 Infrastructure

---

## 7. 與 TARGET_ARCHITECTURE 的關係

[TARGET_ARCHITECTURE.md](../TARGET_ARCHITECTURE.md) 描述的是**更上層的多站/AGV/ROS2/MCP/MES 願景**，與本文件的「單一 App 內部分層」是**兩個不同尺度**：
- 本文件 = 一個服務「內部」怎麼分層分區（micro）。
- TARGET = 多個服務「之間」怎麼用 ROS2/HTTP/MCP 溝通（macro）。
- 兩者相容：本 App 未來會是 TARGET 裡的一個（或數個）「站別服務 / AI 服務」，區 G 即是它對外的接點。
```
