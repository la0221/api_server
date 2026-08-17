---
date: 2026-07-16
type: daily_log
project: AIVision（.NET8 WPF 產線檢測 App）— Edge↔Server 整合階段 0-2
tags: [AIVision, API, 整合, edge, 中央推論, 健康檢查, RemotePairRecognizer, 驗收UI]
status: draft
---

# Daily Log - 2026-07-16

## 1. 今日主題

開始把 **WPF(edge) 接上 API server**。使用者拍板兩個方向：**示意＝先驗收按鈕→再常駐燈**、**切換＝先手動開關→再自動降級**。今日完成**階段 0-2**：健康檢查端點、遠端辨識器、「測試中央推論」驗收按鈕。**刻意不碰生產熱迴圈**——實機辨識仍走本機 ONNX。

## 2. 進度

- **產出整合設計書**：`.ai/designs/2026-07-15_edge_server_integration.md`（分階段 0-4、示意方案、沿用慣例對照表、未解項）。
- **⚠ 修正設計書認知落差**：部署書 §2.5 稱「省力點＝重用既有 `AinaviAiInferencePort`」**不精確**——該類別實作 `IAiInferencePort` 回 `Prediction`（**瑕疵檢測**），我們要的是 `IMoldCodePairRecognizerPort` 回 `PairObservation`（**OCR**），是**不同 port**。可重用的是「typed HttpClient + 切換 + 健檢」**範式**，不是類別本身 → 必須新寫遠端適配器。
- **階段 0：`GET /api/infer/health`**（`InferController`）——不送圖不推論的輕量探測，回 `status(ready|degraded)` / `modelLoaded` / `modelVersion` / 類別數 / UTC。**一律回 200**（含 degraded），讓 edge 分辨「連不上」（該降級）vs「活著但沒模型」（不該當故障）。實測回 `ready / baseline / 20 / 18`。
- **階段 1：`RemotePairRecognizer`**（`AIVision.Infrastructure/MoldCode/`）——實作 `IMoldCodePairRecognizerPort`，送契約的 **`format=raw`**（edge 手上已是 `ImageData`，免一次 PNG 編碼）。fail-closed：連不上/逾時/非 2xx 一律回 `PairObservation.Failed`。另暴露 `LastCallFailed`/`LastModelVersion`/`LastServerElapsedMs` 供未來降級判斷。
  - `InferenceServerOptions`：`BaseUrl` / `TimeoutMs=350`（**必須 < 節拍**才來得及降級）/ `HealthTimeoutMs` / **`Enabled=false`**（預設不接生產）。
  - DI 走既有慣例：`AddHttpClient<T>` typed client + `Configure<T>(GetSection)`（**不學 `AinaviOptions` 硬編 Host**）。
- **階段 2：驗收 UI**——`MoldCodePairBatchView` 加「測試中央推論」區塊：① 健康檢查 → ② 送一張測試圖驗讀值/延遲，結果分行顯示（沿用該頁「目前XXX：+粗體值」與 `StatusMessage` 慣例、既有 `NullToVisibilityConverter`）。失敗明確指出卡在哪一步（連不上／模型未載入／未選資料夾／推論失敗）。
- **實機驗證**：
  - API Release 啟動 → `/api/infer/health` 回 `ready`；Swagger 見 `/api/infer/health` + `/api/infer/pair`。
  - **模擬 `RemotePairRecognizer` 的確切傳輸格式**（raw Bgr24 600×580、stride 1800 無 padding）→ 讀值 `M101/01` 信心 ~1.0 ✅。
  - WPF build 0 錯、App 啟動無 DI 崩潰；輸出目錄 `appsettings.json` 確認含 `InferenceServer` 區段。

## 3. 待辦 / 未決

- **階段 3（下一步）**：手動來源開關（`PairInferenceSourceSelector`，建構子收兩個 port 實例，由 `App.xaml.cs` 以具體型別組裝 → Infrastructure 不需 ref MoldCode.Onnx）＋ Shell 第五顆 **SRV 燈**（沿用四燈慣例、重用 `GetStatusBrush`）。
  - ⚠ **狀態模型抉擇未定**：`ProjectInitializationStatus` 是**初始化一次性**，而 server 連線是**持續性**（運轉中會斷）。只掛 `StatusChanged` 不會即時反映斷線 → 建議另立週期性健檢 + 專屬 event，不硬塞進 init 狀態。
- **階段 4**：自動降級（優先 server、逾時切本機、半開恢復、降級事件記錄）。
- **⚠ 仍未解（沿用 07-15）**：`Passes=1` 大樣本驗證（可行性押在 191ms 上）；edge `TimeBudgetMs=120` 與單幀 191ms 的矛盾；多線吞吐；模型版本漂移（v671 vs pairs/v6.7.1）。
- 安全：目前全程 http 明文，上線前需 TLS + 認證。

## 4. 產出

- Api：`Controllers/InferController.cs`（+`GET health`、+`InferHealthResponse`、raw 長度校驗改**精確比對**，見 02_bug_notes）。
- Infrastructure：**新增** `MoldCode/RemotePairRecognizer.cs`、`MoldCode/InferenceServerOptions.cs`。
- Presentation.Wpf：`App.xaml.cs`（+`Configure<InferenceServerOptions>`、+`AddHttpClient<RemotePairRecognizer>`、+using）；`ViewModels/MoldCodePairBatchViewModel.cs`（+驗收 command/屬性）；`Views/MoldCodePairBatchView.xaml`（+驗收區塊）；`appsettings.json`（+`InferenceServer`）。
- 文件：**新增** `.ai/designs/2026-07-15_edge_server_integration.md`。

## 5. 今日一句話總結

Edge↔Server 整合完成**階段 0-2**：健康檢查端點、`RemotePairRecognizer`（raw 免編碼）、「測試中央推論」驗收按鈕，**全程不碰生產熱迴圈**；實測 edge 的確切傳輸格式讀值 `M101/01` 正確，並順手補掉「錯報中繼會偽裝成 NO OBJECT」的診斷漏洞。
