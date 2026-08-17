---
date: 2026-07-01
type: daily_log
project: AIVision（.NET8 WPF 產線檢測 App）— 雙 head 模號穴號離線辨識整合
tags: [AIVision, WPF, warpPolar, 雙head, onnx, 離線測試, 批量推論, 歷史圖庫]
status: draft
---

# Daily Log - 2026-07-01

## 1. 今日主題

把「模號(mohao) + 穴號(xuehao) 雙 head warpPolar 辨識」整合進 AIVision WPF App 的離線流程：模型可選版本載入、離線逐張測試 + 辨識過程視覺化、批量推論改雙 head 並做工單核對寫歷史。過程中揪出並修掉一個關鍵誤判 bug（前處理相機 ROI）。

## 2. 今日完成事項

- **啟動 App / 環境**：AIVision 為 .NET8 WPF（clean architecture 多專案）；修掉「工作目錄不對 → appsettings 沒載到 → 登入清單為空」的啟動問題（改用 exe 目錄啟動）。
- **模型整合（離線）**：v6.7/v6.7.1/v6.7.2 的 mohao/xuehao `.pt` 匯出 `.onnx`（缺的用 ultralytics 補齊），整理成版本登錄 `D:\AIVisionModels\pairs\<版本>\{mohao,xuehao}.onnx`；baseline 走 appsettings `MoldCodeWarpPolar`（v671）。
- **可切換雙 head 辨識器** `SwitchableTwoHeadRecognizer`（實作 `IMoldCodePairRecognizerPort` + 新 `IMoldCodePairModelSwitch`），DI 綁定；baseline 缺檔不再讓 App 啟動失敗。
- **模號穴號模型管理 (雙head) 頁**：版本清單 →「載入為目前模型」；離線測試（選圖 → 逐張播放圓圖+結果 → 準確率）；雙擊列 → 辨識過程視覺化窗（原圖+Hough圓 → warpPolar字帶 → 模型輸入）。
- **批量推論頁改雙 head**：辨識改雙 head、比對「目前工單預期模號」→ Match/MixedAlarm(混料)，存圖 + 寫 `Inspection`（含 ExpectedCode/ReadCode/Outcome/OK-NG/AirBlown）→ 歷史圖庫可顯示。
- **工單建立**：AI 模型改選填（WorkOrder.modelName 本就可空），解掉「建工單卡在要選 AI 模型」。
- **UX 串接（保留各頁）**：模型管理 →[下一步：批量推論 →]→ 批量推論 →[查看歷史 →]，頁面各自獨立但動線一路往前，不用回選單。
- **警示字改浮層**：工單建立頁錯誤訊息改浮層，不再擠掉欄位。

## 3. 今日重要決策

- **離線已裁圖一律不套相機 ROI**（`WarpPolarParams` 預設 RoiW=0）；appsettings 的 `MoldCodeWarpPolar.Preprocess` 那組相機 ROI(240,0,700,680) 只給即時全幅相機用。對齊 Python `engine.predict(apply_roi=False)`。（見 02_bug_notes Bug 1，與 2026-06-04 Bug 3 同型）
- **多頁保留、用「下一步」串接**（使用者要求功能頁保留，不合併、不改 tab）。
- 版本比較/測試 = 模型管理頁；生產核對+寫歷史 = 批量推論頁（兩頁分工，避免功能重疊感）。
- **不依賴 AINAVI**：盤點後確認核心模號/穴號辨識走本地 ONNX 即可自足；AINavi(EdgeHub) 只是「線上模型」那條保留但未接進主檢測的選項。

## 4. 今日改動摘要（AIVision WPF）

- 新增：`MoldCode.Onnx/SwitchableTwoHeadRecognizer.cs`、`WarpPolarVisualizer.cs`；`Application/Ports/MoldCode/IMoldCodePairModelSwitch.cs`；`Wpf/Services/PairWorkflowState.cs`；`Wpf/Views/RecognitionProcessView.xaml(.cs)`+VM；`Wpf/Views/MoldCodePairBatchView.xaml(.cs)`+VM。
- 改：`App.xaml.cs`（DI：可切換雙 head、PairWorkflowState、批量頁改雙 head 相依）；`BatchInferenceViewModel/.xaml`（單 head→雙 head + 驗證 + 寫歷史 + 承接版本/資料夾）；`WorkOrderInputViewModel/.xaml`（AI 模型選填、錯誤浮層）；`ShellView.xaml`（選單）。
- 資產：`D:\AIVisionModels\pairs\{v6.7,v6.7.1,v6.7.2}\{mohao,xuehao}.onnx`。

## 5. 尚未完成 / 明日接續

- 工單相對落後 → 明日強化「工單創立/處理」（預期碼從模型類別選、目前工單持久化+同步、編輯工單…）。
- 歷史圖庫「只看混料 (MixedAlarm)」快速篩選鈕（資料層已帶 Outcome，UI 待補）。
- v6.7.1 穴號為借用 v6.7.2（v6.7.1 從未訓練 xuehao）——待確認是否可接受。
- `D:\AIVisionModels\v671`（來自另一專案根 d42bb1b7）與 pairs\v6.7.1（515a8271）是兩份不同 V6.7.1 mohao，待確認正版。

## 6. 今日一句話總結

雙 head 模號穴號離線流程（模型管理→逐張測試/視覺化→批量核對→歷史）已串成連貫動線，並揪出「相機 ROI 套到已裁圖 → M101 誤判 M60」的關鍵坑；線下模型使用感良好，工單待明日強化。
