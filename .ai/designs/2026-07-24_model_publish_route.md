---
date: 2026-07-24
type: design
project: AIVision（.NET8 WPF 產線檢測 App）
title: 模型發布 / 更新路線（本地優先，先不上 server）
status: proposal（規劃；本地 v6 路線已實測發布+驗證通過）
tags: [模型發布, 版本管理, ONNX, export, pairs 登錄夾, v6, v9, 主項1]
roadmap: 主項1（線上版本控管）
---

# 設計書：模型發布 / 更新路線（本地優先）

> 目標：把「訓練產出的模型 → 放進 AIVision 能用的登錄夾 → 驗證讀值」做成**可重複的發布動作**。
> 本階段**只做本地**（不碰 server/API 模型中樞——那是部署書 §2.6，之後才做）。
> 前處理/辨識正確性已於 2026-07-14 用 harness 對 v6.7.2 實測確認（M101/08 conf 1.00、混料/氣吹正確）。

---

## 0. 一句話

發布 = 把 `{mohao,xuehao}.onnx` 原子性放進 `D:\AIVisionModels\pairs\<版本>\` → AIVision 模型管理頁掃到即可載入。v6 有 onnx → 直接複製；v9 只有 .pt → 要先 export。本文件定義這條路的每一步、版本治理、與上線前檢查。

---

## 1. 登錄夾規格（AIVision 端的「發布目的地」）

- 路徑：`D:\AIVisionModels\pairs\<版本>\`
- 內容：`mohao.onnx` + `xuehao.onnx`（雙 head 各一）
- **類別**：雙 head 辨識器直接讀 **onnx 內嵌 `names` metadata**（`WarpPolarTwoHeadRecognizer.ReadNames`），**不需**旁置 `.names.json`。
- 現況已登錄：`v6.7`、`v6.7.1`、`v6.7.2`（機制已驗證可載入/讀值）。
- AIVision 消費方式：模型管理頁掃 `pairs\` → 選版本載入 → 熱切換辨識器（`SwitchableTwoHeadRecognizer.LoadVersion`）。

---

## 2. 發布路線（來源 → 登錄 → 驗證）

```
OCR repo 訓練產出
  │
  │  ① [僅 .pt 版本] export：.pt → .onnx（寫入 names + imgsz=640）   ← v9/v6.7.3 才需要
  ▼
  ② 上線前檢查（gate）：類別數/名稱、imgsz、md5、來源記錄
  ▼
  ③ 原子性落地：複製到 pairs\<版本>\{mohao,xuehao}.onnx（先寫暫存再改名，避免半成品被掃到）
  ▼
  ④ AIVision 模型管理頁載入該版本 → 熱切換
  ▼
  ⑤ 驗證：harness paircycle 對已知資料集跑 → 對讀值/conf/混料/fail-closed
```

---

## 3. 階段一：v6 本地發布（最簡單，先做這個）

**適用**：OCR repo 內**已有 onnx** 的完整 v6 對 → `V6.7`、`V6.7.2`（各含 mohao+xuehao onnx）。

步驟（最簡方式）：
1. 從 OCR repo 取來源 onnx：
   - `D:\Content_lens_OCR\OCR\yolo_a_V6.7.2\runs\mohao\weights\best.onnx`
   - `D:\Content_lens_OCR\OCR\yolo_a_V6.7.2\runs\xuehao\weights\best.onnx`
2. 記 md5 + 來源路徑（版本治理，見 §5）。
3. 複製到 `D:\AIVisionModels\pairs\<版本>\{mohao,xuehao}.onnx`（原子性：先 `.tmp` 再改名）。
4. harness 驗讀值（已知 conf 1.00）。
5. AIVision 模型管理頁載入、UI 抽測。

> 「最簡方式」= 一支小 **PowerShell 發布腳本**（複製 + md5 + 落地 + 印出結果），或先純手動走一遍記錄成 SOP。不需要程式改動、不需 server。

**注意**：v6.7/v6.7.1/v6.7.2 已在登錄夾，本階段主要是**把手動複製正規化成可重複腳本 + 補來源/md5 記錄**，並順手解掉 §5 的「兩份 V6.7.1」來源不明。

---

## 4. 階段二：v9 手動更新（路線順了之後）

**適用**：`V9 / V9.2 / V9.3 / V6.7.3` —— **只有 .pt、沒有 onnx**，且 OCR repo **無 export 腳本**。

多出來的關鍵步驟 = **export**：
1. 用 ultralytics 匯出：`yolo export model=<best.pt> format=onnx imgsz=640`（或 Python `model.export(format="onnx", imgsz=640)`）。
2. **確認 onnx 內嵌 `names`** 正確（AIVision 靠它讀類別）。
3. 之後接階段一步驟 ②③④⑤。

**v9 的兩個坑（上線前必檢）**：
- **穴號類別數**：新 .pt 是 **19 類（含 NG）**，舊 onnx 是 18 類（無 NG）→ export 後務必確認類別表與 NG index，別跟舊的混。
- **主線版本對應**：V9.2「穴號」在 `yolo_a_V9\runs_v92n\xuehao\`（不在 `V9.2\` 底下）；V9.3 只重訓穴號、模號沿用 v9.2 → 發布 V9.3 時 mohao 要取 v9.2 的。

---

## 5. 版本治理（避免再出現「兩份 V6.7.1」）

- **單一真實來源**：發布一律從 `D:\Content_lens_OCR\OCR`（現行 OCR 主 repo）取，不再從其他散落路徑複製。
- **每次發布記錄**：版本名、來源 .pt/.onnx 路徑、md5、export 指令（若有）、類別數、發布時間、harness 驗證結果。建議落成 `pairs\<版本>\_publish.json` 或集中 `pairs\_registry.md`。
- **既有疑點待清**：`D:\AIVisionModels\v671\mohao.onnx`(md5 d42bb1b7) vs `pairs\v6.7.1\mohao.onnx`(515a8271) 來源不同 → 確認哪個是正版、統一。

---

## 6. 上線前檢查清單（gate）

發布任何版本前逐項過：
- [ ] mohao + xuehao onnx 皆存在、可被 OnnxRuntime 載入
- [ ] onnx 內嵌 `names` 可讀、類別數正確（mohao=20 含 NG；xuehao 視版本 18 或 19）
- [ ] imgsz=640
- [ ] 前處理參數與訓練一致（RInner 0.6 / 2-pass 0°,90° / INTER_AREA / 白底255 / BGR→RGB /255）— 已由 code 固定，勿改
- [ ] harness paircycle 對已知資料集讀值正確（conf、混料、fail-closed）
- [ ] 來源 md5 + 記錄已寫

---

## 7. 明確不做（本階段範圍界線）

- **不上 server / 不做 API 模型中樞**（`GET/POST /api/models`、edge 拉版本）——留部署書 §2.6，之後才做。
- 不改辨識/前處理數值（已驗證一致）。
- 不動 v9 訓練；export 只是把既有 .pt 轉 onnx。

---

## 8. 檔案地標

- 登錄夾：`D:\AIVisionModels\pairs\<版本>\{mohao,xuehao}.onnx`；baseline `D:\AIVisionModels\v671\`
- OCR 來源（v6 有 onnx）：`D:\Content_lens_OCR\OCR\yolo_a_V6.7.2\runs\{mohao,xuehao}\weights\best.onnx`、`yolo_a_V6.7\...`
- OCR 來源（v9 僅 .pt）：`yolo_a_V9.2\runs\mohao\weights\best.pt`、`yolo_a_V9\runs_v92n\xuehao\weights\best.pt`
- 驗證 harness：`AIVision.MoldCode.Harness\bin\Debug\net8.0-windows\AIVision.MoldCode.Harness.exe paircycle <mohao.onnx> <xuehao.onnx> <資料集>`
- 辨識器/類別讀取：`AIVision.MoldCode.Onnx\WarpPolarTwoHeadRecognizer.cs`（`ReadNames`）
- 前處理（已對齊訓練）：`AIVision.MoldCode.Onnx\WarpPolarPreprocessor.cs`
- 相關：`.ai\designs\2026-07-12_api_server_deployment.md` §2.6（未來 server 模型中樞）

---

## 9. 建議執行順序
1. **本階段**：階段一 v6 —— 把發布正規化成可重複腳本 + 記錄來源/md5（先 v6.7.2，已驗證）。
2. 清掉 §5「兩份 V6.7.1」疑點。
3. 路線順了 → 階段二 v9：建 export 步驟，手動更新一個 v9 版本、過 §6 gate。
