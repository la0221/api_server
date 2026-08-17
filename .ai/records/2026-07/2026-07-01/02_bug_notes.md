---
date: 2026-07-01
type: bug_notes
project: AIVision（.NET8 WPF 產線檢測 App）
tags: [warpPolar, ROI, 前處理一致性, onnx, WPF-MVVM]
status: draft
promote_to_pitfall: true
---

# Bug Notes - 2026-07-01

## Bug 1：C# app 把相機 ROI 套到「已裁的離線圖」→ 模號 M101 誤判成 M60（高信心）

### 1. 錯誤情境

批量/離線用 v6.7.2 對 `D:\OCR_demo\output\2026-06-26\M101\08`（已裁的單顆鏡片圖）辨識，模號多張讀成 M60。

### 2. 錯誤現象

app 讀 M60 (conf ~0.9+)；但同權重 harness 讀 M101 (1.00)、Python engine 也讀 M101 (1.00)。穴號多半正確、只有模號歪。

### 3. 已嘗試但失敗的方法

| 方法 | 結果 | 判斷 |
|---|---|---|
| md5 比對權重、確認 mohao/xuehao 沒搞反 | 檔案全對、三版不同檔 | 不是權重混檔 |
| Python 用同一份 onnx + 正確前處理 | 8/8 M101 正確 | 不是模型問題 |
| Python 複刻 annulus×0°/90° 四組 | 全 M101 | 不是 annulus / 多pass |
| C# harness（預設 WarpPolarParams）| M101/08 1.00 正確 | 是 app 端前處理參數不同 |

### 4. 最終原因

app 的 `SwitchableTwoHeadRecognizer` 吃 appsettings `MoldCodeWarpPolar.Preprocess`，其中 **RoiX=240,RoiY=0,RoiW=700,RoiH=680** 是「即時全幅相機」用的裁切框；離線圖本身已是判斷區域，再套一次 → 裁錯位 → Hough 找錯圓 → warpPolar 出壞字帶 → 模號誤判。harness 用預設 `WarpPolarParams`（RoiW=0）故正確。**與 2026-06-04 Bug 3 同型**（Python 端曾用 apply_roi 解過，C# 端重蹈）。

### 5. 最終解法

離線測試 / 批量推論一律用 `new WarpPolarParams()`（RoiW=0，不套相機 ROI）重建辨識器；模型管理頁加「套用相機ROI(全幅原圖才勾)」開關，預設關。對齊 Python `apply_roi=False`。

### 6. 下次遇到類似問題，AI 應先檢查

- 餵模型的圖是「相機全幅」還是「已裁判斷區域」？相機 ROI 只該對全幅做一次。
- 誤判但 harness/Python 正常 → 先查前處理參數來源（app 的 config 是否帶了不該套的 ROI），不要先懷疑權重。
- 高信心系統性誤判（M101→固定 M60）通常是「輸入被前處理弄壞」，非模型隨機錯。

### 7. 是否應升級成避坑指南？

- [x] 已驗證失敗
- [x] 容易重複踩坑（跨 Python/C# 兩端都踩過同型）
- [x] 未來應該排除
- [x] 對開發決策有約束價值

結論：yes（warpPolar 系統的通用陷阱：前處理必須與訓練一致，尤其 ROI 只對全幅套一次）。

---

## Bug 2：批量推論殘留單 head 模型下拉 → 使用者「已選模型還要再選」+ 權重路徑看起來有誤

### 1. 錯誤情境

模型管理頁載入雙 head 版本後，經「下一步」到批量推論，卻仍看到一個模型下拉、且顯示的路徑不像雙 head 權重。

### 2. 錯誤現象

批量頁的模型下拉綁的是舊「單 head 掃描清單」（`D:\AIVisionModels\*.onnx` 頂層 + names.json，實際為空/moldcode_v3），與雙 head 無關 → 使用者困惑要不要再選、路徑疑似錯。

### 3. 最終原因

批量頁是從單 head 時代改來的，模型下拉未移除；實際辨識已改吃「目前載入的雙 head 版本」(`IMoldCodePairModelSwitch.CurrentVersionName` → `pairs\<版本>`)，兩者不一致造成誤解。

### 4. 最終解法

移除單 head 下拉；改唯讀顯示「目前雙 head 版本 + 實際 mohao/xuehao .onnx 路徑」（`RefreshPairModelDisplay`）。版本由模型管理頁載入後跨頁沿用（Singleton 切換器），影像資料夾用 `PairWorkflowState` 記憶自動帶入 → 不用重選。

### 5. 下次遇到類似問題，AI 應先檢查

- 頁面改用途後，舊的輸入控制項（下拉/欄位）是否還綁著舊資料源 → 移除或改為承接前一頁上下文。
- 跨頁重複選擇 = 缺共用狀態；Singleton 服務（模型切換器 / workflow state）是最小解。

### 6. 是否應升級成避坑指南？

- [ ] 已驗證失敗
- [x] 容易重複踩坑
- [x] 未來應該排除
- [x] 對開發決策有約束價值

結論：部分（「頁面改用途要清乾淨舊 UI 綁定 + 用共用狀態避免重選」是通用 UX/重構原則，值得記；非嚴重 bug）。
