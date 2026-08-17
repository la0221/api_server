---
date: 2026-07-06
type: design
project: AIVision（.NET8 WPF 產線檢測 App）
title: 雙 head（模號＋穴號）接即時相機/PLC 設計書
status: proposal（未實作，僅設計）
tags: [雙head, warpPolar, 相機, PLC, pair-cycle, fail-closed]
---

# 設計書：把雙 head 模號穴號辨識接進即時相機/PLC 流程

> 目標：讓「模號＋穴號雙 head 辨識」從目前只能跑離線圖片批次，變成可由相機/PLC 觸發的即時產線核對。
> 原則：**不新寫辨識邏輯**（handler 已完備），只補「觸發→執行→回報→顯示」這段接線；鏡像現有單 head `AreaRunService`。
> 本文件只做設計，不含實作 code。

---

## 0. 一句話

`VerifyMoldCodePairCycleCommandHandler` 這顆「拍照→多幀投票→三態判定→fail-closed 氣吹」的心臟**已經寫好且測試過**，缺的只是一個把它「派發出去」的服務 + UI 觸發，加上正確的相機 ROI 前處理。做法直接照抄單 head 的 `AreaRunService`。

---

## 1. 現況盤點（為什麼只差接線）

| 元件 | 狀態 | 位置 |
|---|---|---|
| 雙 head 辨識心臟 `VerifyMoldCodePairCycleCommandHandler` | ✅ 完備：PLC 取像指令 → `camera.CaptureOnceAsync` 自適應多幀 → `recognizer.Recognize` → `MoldCodePairVoter.Vote` → `MoldCodePairVerifier.Decide` 三態 → fail-closed 映射 PLC（放行/氣吹/NG） | `AIVision.Application/MoldCode/VerifyMoldCodePairCycleCommandHandler.cs` |
| 命令 record | ✅ `VerifyMoldCodePairCycleCommand(ExpectedMohao, ExpectedXuehao)` | 同資料夾 |
| 參數 | ✅ `MoldCodePairCycleOptions`（MaxFrames=7 / TimeBudgetMs=120 / MinConsensusVotes=3 / MoldThreshold=0.60 / CavityThreshold=0.85 / NgClassName=NG），已 Bind `MoldCodePairCycle` 區 | `MoldCodePairCycleOptions.cs`、`App.xaml.cs:190` |
| 辨識器（含相機 ROI 前處理） | ✅ `SwitchableTwoHeadRecognizer`（Singleton），DI 用 appsettings `MoldCodeWarpPolar.Preprocess` 建，**含相機 ROI** | `App.xaml.cs:179-189` |
| 相機 port | ✅ IDS `IdsCameraPort`（實機）/ `FakeCameraPort`（預設） | `App.xaml.cs:286-306` |
| PLC port | ✅ `IPlcPort`（`ModbusPlcPort` 實機 / `FakePlcPort` 假） | `App.xaml.cs:220,225` |
| **派發端（觸發）** | ❌ **不存在**：全專案沒有任何 `ISender.Send(new VerifyMoldCodePairCycleCommand(...))`，只有 harness/測試直接 new handler | — |
| **UI 顯示回饋** | ❌ 尚未有雙 head 即時結果的橫幅/歷史寫入 | — |

**對照範本（單 head 已上線）**：`AreaRunService`（`AIVision.Application/MoldCode/AreaRunService.cs`）
包住 `VerifyMoldCodeCycleCommandHandler`，提供 `RunOnceAsync`（手動/離線）、`StartAsync`（訂閱 `IPlcHandshakePort.CaptureRequested`）、`CycleCompleted` 事件、Inspection 持久化；`ShellViewModel` 設 `ExpectedCode`/`WorkOrderId`。雙 head 只要照抄。

---

## 2. ⚠️ 最關鍵的設計陷阱：離線與即時的 ROI 是「相反的」

這是本設計最容易踩、也最重要的一點：

| 路徑 | 影像來源 | 前處理 ROI | 用哪個辨識器實例 |
|---|---|---|---|
| **離線測試/批量**（現況） | 已裁好的單顆鏡片圖 | **不套** ROI（`new WarpPolarParams()`，RoiW=0） | 各頁**自建**新辨識器 |
| **即時相機**（本設計新增） | 相機**全幅**原圖 | **必須套**相機 ROI（`RoiX=240,RoiY=0,RoiW=700,RoiH=680`） | **直接用 DI 注入的** `SwitchableTwoHeadRecognizer`（已含 ROI） |

→ **好消息**：`VerifyMoldCodePairCycleCommandHandler` 注入的正是 DI 的 `_recognizer`（含相機 ROI），所以即時路徑**天生就是對的**，不需像離線頁那樣覆寫。這也解釋了為何離線頁要刻意 `new WarpPolarParams()`——它們是在「取消」DI 那個為相機準備的 ROI。

→ **必須驗證的風險（見 §7 open #1）**：模型管理頁 `LoadVersion` 熱切換版本時，換上的辨識器**是否仍帶相機 ROI 前處理**？若 `LoadVersion` 用了無 ROI 參數，會讓即時路徑在全幅圖上崩掉（重演 M101→M60）。上線前必須確認「載入版本後，即時用的辨識器前處理 = 相機 ROI」。

---

## 3. 目標資料流（即時雙 head）

```
工單(ExpectedMoldCode "M101/07")
        │  拆分 / - _ 空白
        ▼
  ExpectedMohao="M101"  ExpectedXuehao="07"
        │
[觸發源] 手動「單次核對」鈕  或  PLC 握手 CaptureRequested
        │  ISender.Send / 服務呼叫
        ▼
VerifyMoldCodePairCycleCommandHandler
   ├─ plc.WriteAsync(CaptureStart)
   ├─ 迴圈 camera.CaptureOnceAsync（全幅）→ recognizer.Recognize（套相機ROI→warpPolar→雙head）
   ├─ MoldCodePairVoter.Vote（自適應多幀，達共識或超時停）
   ├─ MoldCodePairVerifier.Decide（分軸三態：Match/TrustInput/MixedAlarm/Reject/Skip）
   └─ fail-closed → plc.WriteAsync(放行 / 氣吹Blow / NG)
        │
        ▼
MoldCodePairCycleResult（Outcome, 讀到的模號/穴號, 信心, 幀數, 票數, 是否氣吹, 耗時, 原因）
        │
        ├─→ UI 結果橫幅 + LiveBitmap 疊字
        └─→ Inspection 持久化（工單Id, Expected/Read, Outcome, 氣吹, 信心）→ 歷史圖庫可查
```

---

## 4. 需要新增/修改的元件（依 Clean Architecture 分層）

### 4.1 新增 `PairAreaRunService`（Application 層）— 鏡像 `AreaRunService`
職責與單 head 版一模一樣，只是換成雙 head 命令：
- 建構子注入 `IRequestHandler<VerifyMoldCodePairCycleCommand, MoldCodePairCycleResult>`（＋可選 `IPlcHandshakePort`、`IInspectionRepository`、`ILogger`）。
- 屬性：`ExpectedMohao` / `ExpectedXuehao` / `WorkOrderId` / `IsRunning`。
- `RunOnceAsync(mohao, xuehao, ct)`：手動/離線單次；跑一次 handler → 記 log → 寫 Inspection → 觸發 `CycleCompleted`。
- `StartAsync` / `StopAsync`：實機訂閱/取消 `IPlcHandshakePort.CaptureRequested`（沿用單 head 的暫行橋接註解，待三菱協定收斂）。
- 事件 `CycleCompleted(MoldCodePairCycleResult)` 交給 UI。
- Inspection 寫入沿用 `AreaRunService` 那段（modelVersion 改帶目前雙 head 版本名，如 `IMoldCodePairModelSwitch.CurrentVersionName`）。

> 為何不直接在 ShellViewModel 呼叫 `ISender`：與單 head 一致用「服務包 handler」，離線測試免 DI 容器就能直接 new 來端到端驗證（AreaRunService 註解明載此設計理由）。

### 4.2 DI 註冊（Presentation `App.xaml.cs`）
- `services.AddSingleton<PairAreaRunService>();`（緊接單 head `AreaRunService` 那行）。
- 其餘（辨識器/相機/PLC/Options）皆已註冊，無需新增。

### 4.3 `ShellViewModel`（Presentation 層）——加「辨識模式」切換
現況面掃流程 `StartAreaScanModeAsync` 綁的是單 head/瑕疵路徑。需引入一個模式選擇，決定觸發時派給哪顆 handler：
- 新增狀態：`RecognitionMode { SingleHeadDefect, DualHeadMoldCavity }`（可由「工單所選模型」或使用者手動切換決定；建議先做**手動下拉**，最直觀）。
- 進入運轉時：
  - 若雙 head 模式 → 拆工單 `ExpectedMoldCode` 成 mohao/xuehao → 設 `PairAreaRunService.ExpectedMohao/Xuehao/WorkOrderId`。
  - 觸發（PLC 或手動鈕）→ 呼叫 `PairAreaRunService`（**確保同一時間只有一個服務吃觸發**，避免單/雙 head 同時響應）。
- 訂閱 `PairAreaRunService.CycleCompleted` → 更新結果橫幅/LiveBitmap 疊字（讀到的 M??/?? + 三態顏色 + 氣吹標記）。

### 4.4 UI 觸發鈕（Presentation 層）——先手動、後 PLC
- **Phase 1**：主頁加一顆「單次核對（雙 head）」按鈕 → `PairAreaRunService.RunOnceAsync`。可用 IDS 相機（或 `FolderBurstCamera` 離線假相機）端到端驗證，**完全不依賴 PLC 協定**。
- **Phase 2**：接 PLC 握手（`StartAsync` 訂閱 `CaptureRequested`），實機自動觸發。

---

## 5. 分階段落地建議（降風險）

| 階段 | 內容 | 驗收 |
|---|---|---|
| **P1 服務層** | 新增 `PairAreaRunService` + DI 註冊 | build 0 錯；單元測試（可仿 `MoldCodePairCycleHandlerTests`）以 Fake PLC/相機跑 `RunOnceAsync` 通過 |
| **P2 手動觸發 UI** | Shell 加雙 head 模式 + 「單次核對」鈕 + 結果橫幅 + 歷史寫入 | 用 IDS 相機對已知樣品按一下 → 正確讀 M101/08、三態正確、Inspection 進歷史圖庫 |
| **P3 PLC 自動** | `StartAsync` 訂閱握手 + 收斂 IPlcPort 雙寫（見 §7 open #2） | 實機 PLC 觸發連續核對、氣吹動作正確 |
| **P4 觀測** | 節拍/準確率記錄、fail-closed 演練（故意遮擋→應 NG 不放行） | 節拍達標、fail-closed 驗證通過 |

---

## 6. 沿用既有、不重造的部分（省力點）
- **三態判定 / 投票 / fail-closed**：`MoldCodePairVerifier` / `MoldCodePairVoter` 已完成，直接用。
- **相機/PLC/辨識器**：全走既有 port，DI 已註冊。
- **持久化**：`Inspection` 實體與歷史圖庫已支援 Outcome/氣吹/Expected/Read（批量頁已在寫），`PairAreaRunService` 直接沿用同欄位。
- **工單同步**：`WorkOrderChangedMessage` 既有；預期碼拆分邏輯 `WorkOrderInputViewModel` 已有（`Split('/','-','_',' ')`）可抽共用。

---

## 7. 未決問題 / 風險（動工前需拍板）

1. **【必驗】版本熱切換後的即時 ROI**：`SwitchableTwoHeadRecognizer.LoadVersion` 換版本時，即時用的辨識器前處理是否仍帶相機 ROI？若否，即時全幅圖會誤判。→ 動工前先讀 `LoadVersion` 確認，必要時讓「即時用」與「離線用」各持一份前處理設定。
2. **【協定】PLC 雙寫收斂**：handler 內部已自寫 `IPlcPort`（CaptureStart/Blow/Result），而 `AreaRunService` 的握手橋接又會 `ReportResultAsync`。單 head 註解已標「待三菱 MELSEC 協定確定後收斂，避免雙重寫入」。雙 head 沿用同結構，需一併釐清：**到底由 handler 寫 IO，還是由握手服務回報**，二擇一。
3. **【模式互斥】** 單 head 與雙 head 不能同時吃同一個觸發。需要明確的模式切換（且切換時取消另一邊訂閱）。
4. **【預期碼來源】** 若工單選了「（不核對）」→ 對應軸不核對（Decide 需支援空預期＝TrustInput 邏輯，需確認 `MoldCodePairVerifier` 對空 expected 的行為）。
5. **【觸發模式決定】** RecognitionMode 要「跟著工單所選模型自動判定」還是「使用者手動下拉」？建議先手動，最直觀、最好驗。
6. **【相機 ROI 值】** appsettings 的 `RoiX=240,RoiY=0,RoiW=700,RoiH=680` 是否對應目前 IDS 相機的實際視野？換相機/鏡頭/工作距離就要重校。

---

## 8. 不做什麼（範圍界線）
- 不改辨識演算法、不動 warpPolar 前處理數值（除非 §7#1/#6 校正需要）。
- 不碰線上 AINAVI / EdgeHub 路徑。
- 不做訓練功能（另案）。
- P1/P2 不依賴 PLC，確保沒有實機也能推進到「手動單次核對可用」。

---

## 9. 檔案地標（動工時）
- 心臟：`AIVision.Application/MoldCode/VerifyMoldCodePairCycleCommandHandler.cs`、`VerifyMoldCodePairCycleCommand.cs`、`MoldCodePairCycleOptions.cs`、`MoldCodePairVoter.cs`、`MoldCodePairVerifier.cs`
- 範本：`AIVision.Application/MoldCode/AreaRunService.cs`（＋單 head handler）
- 辨識器：`AIVision.MoldCode.Onnx/SwitchableTwoHeadRecognizer.cs`、`WarpPolarTwoHeadRecognizer.cs`、`WarpPolarPreprocessor.cs`（ROI 邏輯）
- 接線：`AIVision.Presentation.Wpf/App.xaml.cs:169-191`、`ViewModels/ShellViewModel.cs`（面掃流程 1070+、預期碼設定 1527+）
- 測試範本：`AIVision.Application.Tests/MoldCode/MoldCodePairCycleHandlerTests.cs`；離線假相機：`AIVision.MoldCode.Harness/FolderBurstCamera.cs`
