---
date: 2026-07-01
type: reusable_flow
project: AIVision（.NET8 WPF 產線檢測 App）
tags: [除錯流程, onnx, 前處理一致性, 模型整合]
status: draft
candidate_prompt: true
candidate_sop: true
candidate_skill: false
---

# Reusable Flow - 2026-07-01

## Flow 1：「同權重不同結果」的前處理不一致定位法

### 1. 流程名稱

模型誤判定位：權重 vs 前處理 vs 模型（三方交叉比對）

### 2. 觸發情境

某環境（app）辨識誤判，但懷疑是權重/模型/前處理其一，無法確定。

### 3. 流程步驟

1. **先驗權重不是混檔**：md5 比對來源檔 vs 部署檔；讀 onnx metadata 的 `names` 確認類別沒搞反（如 mohao=模號、xuehao=穴號）、各版本是不同檔。
2. **用「已知正確前處理」跑同一份權重**（本案：Python engine + 同一份 onnx）→ 若結果正確 → 模型/權重沒問題。
3. **用另一個共用同辨識器但走預設參數的路徑**（本案：C# harness，預設 WarpPolarParams）→ 若正確 → 問題在「出錯環境的前處理參數」。
4. **比對出錯環境 vs 正確環境的前處理參數差異**（本案：appsettings 的相機 ROI）→ 找出被多套/少套的步驟。
5. 修正：讓出錯環境對齊訓練/正確前處理（本案：離線圖不套相機 ROI）。

### 4. 輸入資料

同一批影像、同一份權重、可讀的前處理設定（config/appsettings）。

### 5. 輸出結果

明確結論「是權重 / 模型 / 前處理哪一個」+ 具體差異點 + 修法。

### 6. 可否變成 Prompt？

- 結論：yes
- 理由：可固定成「三方交叉比對」的除錯提示（先驗權重 → 換正確前處理 → 換預設參數路徑 → 比對差異）。

### 7. 可否變成 SOP？

- 結論：yes
- 理由：任何「同權重不同結果」的部署誤判都適用，步驟固定、判讀明確。

### 8. 可否變成 Skill？

- [ ] 高頻　[x] 可重複　[x] 有明確 input/output　[ ] 需工具化
- 結論：no（屬人工除錯判讀，暫不工具化）。

### 9. Skill 名稱候選

—

### 10. 備註

本案結論：權重正確、模型正確，錯在 C# app 把相機 ROI 套到已裁圖（見 02_bug_notes Bug 1）。與 2026-06-04 Bug 3 同型，證明「前處理必與訓練一致」是反覆踩的坑。

---

## Flow 2：雙 head（模號+穴號）版本模型整合進 App

### 1. 流程名稱

雙 head ONNX 版本整合（.pt→.onnx→版本登錄→可切換辨識器→UI）

### 2. 觸發情境

有多版本 mohao/xuehao 訓練權重，要讓 App 能選版本、載入、離線測試、生產核對。

### 3. 流程步驟

1. 缺 `.onnx` 的用 ultralytics `model.export(format="onnx", imgsz=640)` 補齊。
2. 整理成版本登錄夾：`D:\AIVisionModels\pairs\<版本>\{mohao,xuehao}.onnx`。
3. 做「可切換辨識器」(`SwitchableTwoHeadRecognizer`)：對外同時是辨識 port + 版本切換 port，切換時 dispose 舊建新、上鎖。
4. UI：模型管理頁掃版本夾 → 載入為目前模型；離線測試頁沿用目前版本 + 記憶資料夾；批量頁承接版本做工單核對。
5. 驗證：harness `paircycle` 對已知資料集跑，確認讀值/準確率。

### 4. 輸入資料

各版本 mohao/xuehao 權重；有正解的測試影像。

### 5. 輸出結果

App 內可選版本、載入、逐張測試/視覺化、批量核對寫歷史。

### 6. 可否變成 Prompt？

- 結論：no（工程整合流程，非生成式）。

### 7. 可否變成 SOP？

- 結論：yes（每次新版本模型上線都走這套：匯出→登錄→驗證→掛 UI）。

### 8. 可否變成 Skill？

- [ ] 高頻　[x] 可重複　[x] 有明確 input/output　[ ] 需工具化
- 結論：no。

### 9. Skill 名稱候選

—

### 10. 備註

前處理參數（RInner/Imgsz/Hough/ROI）必須與訓練對齊；離線已裁圖用 RoiW=0。
