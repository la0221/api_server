---
date: 2026-07-15
type: design
project: AIVision（.NET8 WPF 產線檢測 App）
title: Edge(WPF) ↔ API Server 整合與「打通示意」設計
status: approved（2026-07-15 使用者拍板：先驗收按鈕→再常駐燈；先手動開關→再自動降級）
tags: [整合, edge, server, 中央推論, UI, 狀態燈, 健康檢查, 降級]
---

# 設計書：Edge ↔ API Server 整合與「打通示意」

> 承接：`2026-07-14_api_infer_pair_contract.md`（端點契約）、`2026-07-12_api_server_deployment.md` §8（P1/P2）。
> 前提（2026-07-15 實測）：`POST /api/infer/pair` 讀值 180/180 正確，Release+Passes=1 = 191ms（wall 269ms），對 <400ms 節拍可行。

---

## 0. 一句話

Edge 新增一顆**遠端雙 head 辨識器**（HTTP 打 `/api/infer/pair`），先用**「測試中央推論」驗收按鈕**證明打通（不碰生產熱迴圈），再加**手動來源開關**與 **Shell SRV 常駐燈**，最後才上**自動降級**。

---

## 1. ⚠️ 先修正一個設計書的認知落差

部署書 §2.5 寫：「**省力點**：程式已有 HTTP 推論適配器（`AinaviAiInferencePort`/`SwitchableAiInferencePort`），中央推論≈重用它、指向自建端點。」

**這個說法不精確**：

| | `AinaviAiInferencePort` | 我們需要的 |
|---|---|---|
| 實作的 port | `IAiInferencePort` | `IMoldCodePairRecognizerPort` |
| 回傳型別 | `Prediction`（瑕疵檢測） | `PairObservation`（模號穴號 OCR） |
| 用途 | 瑕疵 | OCR（註解明言「OCR≠瑕疵」） |

→ **不能直接重用那個類別**。可重用的是「**typed HttpClient + 切換 + IsConnected/HealthCheck**」這套**範式**。
→ 必須**新寫** `RemotePairRecognizer : IMoldCodePairRecognizerPort`。

> 另注意（`App.xaml.cs:391-399` 註解）：`SwitchableAiInferencePort` 現已**不再綁為 `IAiInferencePort`**，推論一律走本地 ONNX；它只被 OfflineTest/OnlineModelManagement/ProjectInit 以具體型別注入。

---

## 2. 現有可沿用的慣例（探查結果，勿另創）

| 需求 | 既有慣例 | 位置 |
|---|---|---|
| 狀態燈 | `Ellipse 12×12 + Fill 綁 Brush + TextBlock + ToolTip`，四顆（PLC/CAM/LIGHT/AI） | `ShellView.xaml:126-180` |
| 燈色邏輯 | `GetStatusBrush(DeviceInitState)`：Success=LimeGreen / Error=Red / InProgress=Orange / Skipped=DarkGray / 其他=Gray | `ShellViewModel.cs:618-625` |
| 燈色屬性 | VM 直接暴露 `Brush` 屬性（**不用 Converter**） | `ShellViewModel.cs:331-374` |
| 狀態模型 | `ProjectInitializationStatus`（Plc/Camera/Light/AiModel 四項硬編） | `IProjectInitializationService.cs:43-124` |
| 狀態推播 | **C# event** `StatusChanged`（硬體狀態不走 Messenger） | `ShellViewModel.cs:582-602` |
| 「目前來源/版本」顯示 | `目前XXX：` + 粗體值 + `TargetNullValue='（尚未載入）'` | `MoldCodePairBatchView.xaml:11-15` |
| 選擇→commit→回報 | ComboBox 選 → 明確按鈕 commit → 底部 `StatusMessage` 條 | `MoldCodePairBatchView.xaml:17-33,78-80` |
| typed HttpClient | `AddHttpClient<T>((sp, client) => { client.Timeout = ...; })`（已 6 處） | `App.xaml.cs:88,104,228,346,375,382` |
| Options | `services.Configure<T>(GetSection(...))`（**不要學 `AinaviOptions` 硬編 Host**） | `App.xaml.cs:322,366` |

**缺口**：
- API **沒有健康檢查端點** → 燈號/測試都需要它 → 階段 0 補。
- `IMoldCodePairRecognizerPort` **只有 `Recognize()`，無連線概念**（不像 `IAiInferencePort` 有 `IsConnected`/`HealthCheckAsync`）→ 需另立來源抽象。
- `AinaviAiInferencePort.IsConnected` 是**手動 `SetConnectionStatus()`**、**無自動輪詢** → 常駐燈需要自己的健康檢查節奏。

---

## 3. 分階段路線（使用者已拍板順序）

| 階段 | 內容 | 碰生產熱迴圈？ | 你會看到 |
|---|---|---|---|
| **0** | API 加 `GET /api/infer/health` | ❌ | 無（前置） |
| **1** | `RemotePairRecognizer : IMoldCodePairRecognizerPort` + Options + DI | ❌ | 無（前置） |
| **2** | **驗收 UI**：「測試中央推論」按鈕（健康檢查 + 送圖驗讀值/延遲） | ❌ **完全不碰** | ✅ **打通的第一次證明** |
| **3** | **手動來源開關**（本機 ONNX / 中央伺服器）+ Shell **SRV 常駐燈** | ✅（可切回） | 日常運轉可見 |
| **4** | **自動降級**（優先 server、逾時切本機）+ 降級事件記錄 + 結果標示來源 | ✅ | 斷線不停線 |

> **階段 2 零風險**是刻意設計：驗收按鈕直接呼叫 `RemotePairRecognizer`，生產辨識仍走本機 ONNX。先證明路通，再談切換。

---

## 4. 階段 0：`GET /api/infer/health`

**為什麼需要**：燈號/測試連線都要一個便宜、不必送圖的探測。

回應（200）：
```json
{
  "status": "ready",
  "modelLoaded": true,
  "modelVersion": "baseline",
  "mohaoClassCount": 20,
  "xuehaoClassCount": 18,
  "serverTimeUtc": "2026-07-15T06:32:05Z"
}
```

| 欄位 | 意義 |
|---|---|
| `status` | `ready`（模型已載入可推論）｜`degraded`（server 活著但模型未載入） |
| `modelLoaded` | 雙 head 模型是否已載入 |
| `modelVersion` | 現用版本（`IMoldCodePairModelSwitch.CurrentVersionName`） |
| `mohao/xuehaoClassCount` | 類別數（供 edge 對版健檢） |

- **一律回 200**（即使 `degraded`）：讓 edge 能分辨「server 不可達（連不上/timeout）」vs「server 活著但沒模型」。前者才該降級。
- 不送圖、不跑推論 → 極輕量，可高頻輪詢。

## 5. 階段 1：`RemotePairRecognizer`

- **位置**：`AIVision.Infrastructure\MoldCode\RemotePairRecognizer.cs`（Infrastructure 已 ref Application，可實作該 port；**不需 ref MoldCode.Onnx**）。
- **實作**：`IMoldCodePairRecognizerPort`（`PairObservation Recognize(ImageData)`）+ 額外的連線/健檢成員。
- **送圖**：契約的 **`format=raw`**（edge 已有 `ImageData`，含 Bytes/Width/Height/PixelFormat）→ **省一次 PNG 編碼**，最省延遲。（`format=png` 留給跨網段省頻寬時再評估。）
- **同步困境**：port 是**同步** `Recognize`，HTTP 是非同步 → 內部用 `.GetAwaiter().GetResult()`；因辨識已在背景執行緒（handler 用 `Task.Run`），不會鎖 UI。**這是 port 既有簽章的限制，非本設計引入。**
- **fail-closed**：HTTP 失敗/逾時/非 2xx → 回 `PairObservation.Failed(原因)`，**不可回看似合法的碼**。
- **Options**（`InferenceServerOptions`，走 `Configure<T>(GetSection)`）：
  ```json
  "InferenceServer": {
    "BaseUrl": "http://192.168.1.x:5030",
    "TimeoutMs": 2000,
    "Enabled": false
  }
  ```
  ⚠ **`TimeoutMs` 分兩情境（2026-07-24 踩坑後修正）**：試模/驗收期用寬鬆值 **2000**——server 端 Passes=2 單張 ~385ms、冷啟首張 ~1.1s，原本照 Passes=1 量測值訂的 350 會**必逾時**（見 07-24 bug_notes 坑 1）。未來階段 3 接**生產實時**時，必須另設 < 產線節拍的緊逾時（如 350）才來得及降級——**兩情境分開設定，勿共用**。

## 6. 階段 2：驗收 UI（本次要做的示意）

**放哪**：`MoldCodePairBatchView`（雙軸模型管理/批量頁）——該頁已有「目前模型版本 + 載入 + StatusMessage」區塊，語意最貼近，且**已是工程師以上權限**。

**版面**（沿用該頁既有慣例）：
```
目前推論來源：本機 ONNX          ← 沿用「目前XXX：+粗體值」慣例
[測試中央推論]  Server: http://192.168.1.x:5030
─────────────────────────────────────────
✅ 健康檢查 200 OK (12ms) | 模型 baseline | 類別 20/18
✅ 測試讀值 M101 / 01  信心 1.000 / 0.998
   server 推論 191ms | 來回 269ms
```
- 按鈕行為：① `GET /health` → ② 取「目前選取的測試圖」（該頁已有圖清單）送 `/infer/pair` → ③ 把結果寫進既有 `StatusMessage` 風格的區塊。
- 失敗時明確顯示是哪一步失敗（連不上 / 模型未載入 / 讀值失敗）。

## 7. 階段 3：手動開關 + SRV 燈（規劃，本次不做）

- **來源選擇器** `PairInferenceSourceSelector : IMoldCodePairRecognizerPort`，建構子收**兩個 `IMoldCodePairRecognizerPort`**（local=`SwitchableTwoHeadRecognizer`、remote=`RemotePairRecognizer`），由 `App.xaml.cs` 以具體型別 factory 組裝 → **Infrastructure 不需 ref MoldCode.Onnx**。對 `VerifyMoldCodePairCycleCommandHandler` **完全透明**（它只認 port），熱迴圈不改。
- **Shell SRV 燈**：`ShellView.xaml` AI 燈後插入第五組（複製既有 StackPanel），綁 `ServerStatusColor`/`ServerStatusTooltip`，重用 `GetStatusBrush`。
- ⚠️ **狀態模型抉擇**：`ProjectInitializationStatus` 是**專案初始化一次性**的；server 連線是**持續性**（運轉中可能斷）。→ 若只掛 `StatusChanged`，斷線不會即時反映。**建議另立週期性健康檢查 + 專屬 event**，不要硬塞進 init 狀態。此點階段 3 再定案。

## 8. 階段 4：自動降級（規劃）

- 選擇器策略：優先 remote → 逾時(`TimeoutMs`)/失敗 → 立即改用 local，**該顆不重試**（節拍內來不及）。
- 記錄降級事件（時間/原因/持續時長），供現場排查。
- 半開恢復：降級後每 N 秒健檢一次，恢復才切回，避免抖動。
- ⚠️ **fail-closed 語意**：remote 回「NO OBJECT / 辨識失敗」是**有效觀測（200）**，**不可**當成 server 故障去降級（見契約 §4）。只有**連不上/逾時/5xx** 才降級。

---

## 9. 未解 / 待驗證（不可略過）

1. **⚠ `Passes=1` 未充分驗證**：整條可行性押在 191ms 上，但只驗過單一模號/單一 session/可能訓練同分布的 180 張。**跨模號/多 session 驗過才可改預設**（API 現保守維持 2 → 385ms）。
2. **⚠ edge `TimeBudgetMs=120` 矛盾**：edge 期望「120ms 跑 7 幀」（隱含每幀 20-40ms），實測單幀最快 191ms → **多幀投票實際恐只跑 1 幀**。三態決策的投票基礎待確認。**這會影響「送 1 幀 vs 批次」的多幀策略**（部署書 §2.5）。
3. **多線吞吐**：CPU 單線約 5 次推論/秒。幾條線共用一台 server 未定 → 決定是否仍需 GPU。
4. **前處理位置**：本設計假設 edge 送**已前處理（已裁）**的圖，故 server `Roi*=0`（見 2026-07-15 bug_notes 坑 1）。若改送全幅圖，server 需另配相機 ROI。
5. **安全**：階段 0-4 皆走 http 明文。上線前需 TLS + 認證（部署書 §7）。

---

## 10. 檔案地標

- 端點契約：`.ai\designs\2026-07-14_api_infer_pair_contract.md`
- 協定選型：`.ai\designs\2026-07-14_api_transport_protocol.md`
- 部署大方向：`.ai\designs\2026-07-12_api_server_deployment.md`（§2.5 中央推論、§8 P1/P2）
- API 端點：`AIVision.Api\Controllers\InferController.cs`
- 要新增（階段 1）：`AIVision.Infrastructure\MoldCode\RemotePairRecognizer.cs`
- Shell 燈號慣例：`AIVision.Presentation.Wpf\Views\ShellView.xaml:126-180`、`ViewModels\ShellViewModel.cs:331-374,618-625`
- 驗收 UI 落點：`AIVision.Presentation.Wpf\Views\MoldCodePairBatchView.xaml`、`ViewModels\MoldCodePairBatchViewModel.cs`
- DI 慣例：`AIVision.Presentation.Wpf\App.xaml.cs:346`（typed client）、`:322,366`（Configure<T>）
- 本機 port 介面：`AIVision.Application\Ports\MoldCode\IMoldCodePairRecognizerPort.cs`
