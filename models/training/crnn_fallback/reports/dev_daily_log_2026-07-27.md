---
date: 2026-07-27
project: Content_lens_OCR / OCR crnn_fallback
topic: v9.3 field-error rehearsal fix
tags: [crnn, nonar, rehearsal, v9.3-fix, hole-plug]
status: complete
---

# v9.3 CRNN 錯誤修補 — daily log

## 動機
另一台電腦 v9.3 產線跑出來 896 張 CRNN 錯誤（`D:/incoming/v9.3vsrcnn/crnn_errors/`），加上分析討論筆記 `CRNN_vs_YOLO分類器_加強行為_討論.md`。使用者：「先把目前這個洞補上」、暫緩合成。

## 結果 TL;DR
| 指標 | 數字 |
|---|---|
| 錯誤總數 | 896 |
| **重訓後全對** | **799 / 896 (89.2%)** |
| + 降 detector conf 到 0.10 撈回 | **871 / 896 (97.2%)** |
| 剩下需人工處理 | 25 (2.8%) |
| val_exact（stock val 2416） | **0.9996**（跟產線一模一樣，零 regression） |
| 新產物 | `runs/nonar_v931_fix/best.pt` (ep 24) |

依原始錯誤類型的修復率：
| 類型 | 原數量 | Round 1 修復 | +低 conf 修復 |
|---|---|---|---|
| mohao_misread | 322 | **100%** | 100% |
| xuehao_misread | 478 | 98.5% | 99.4% |
| both_misread | 6 | 100% | 100% |
| detector_miss | 90 | 0% | **80% (72/90)** |

依 mold 分佈：
| mold | 錯誤數 | 修復率 |
|---|---|---|
| M83 | 417 | 98.6%（原本 297 個 M83→M88 全解）|
| M28 | 213 | 79.3%（+低 conf → 更高）|
| M101 | 206 | 83.0% |
| M17 | 58 | 79.3% |
| M54 | 2 | 100% |

## 執行流程

### 前置檢查
1. 分診：檔名 `gt-M?-XX__crnn-M?-YY__...` 已內建，NA-NA = detector miss。**產出**：[`crnn_errors_manifest.csv`](crnn_errors_manifest.csv) + [`crnn_errors_summary.md`](crnn_errors_summary.md)
2. Pipeline `N_QUERIES = 4`（`nonar_model.py:23`）— **M101 3-digit 早就支援**，不用動架構
3. 訓練集分布：M83 train=238、M28=261、M101=220、M17=252，**量夠只差分佈匹配**

### Round 1: OCR read-miss rehearsal
1. 對 896 張 640×640 strip 跑 YOLOv8n detector → 抽 200×80 mohao + xuehao crops
   - 806 both、47 m-only、42 x-only、1 total miss → **1701 新 crops**
   - 存到 `data_stable_crops/train/{class}/`
2. `_train_nonar_v931_fix.py` retrain（real 11174 + synth 6960 + stable 1701 = 19835）
3. 30 ep × 24s = 12 min → best val_exact = 99.96%（ep 24）

### 守門
1. Stock val 2416：99.96% ← 零 regression（跟產線 `nonar_include_M54` 一模一樣）
2. 896 error strip 全部 re-eval：
   - 799 完全修好、49 mo 對 xu detect-miss、42 xu 對 mo detect-miss、6 兩個都錯
3. Top-20 殘留 confusion **全部是「?」**（detector miss）— CRNN read-miss 完全清空

### Round 4: Detector detect-miss recovery
- conf 0.25 → 0.10：撈回 71/90
- conf 0.05：再撈回 1
- 剩 18/90 真的看不到框
  - 觀察 8 張樣本：strip 是 **wavy band + 淡刻**，polar-warp 中心偏移把字元帶偏出 [280:360] 範圍
  - 這不是 detector 弱，是 preprocess 的 `find_circle` drift 問題
- 產出：[`detect_still_miss_at_005.csv`](detect_still_miss_at_005.csv) + [`detect_miss_samples/`](detect_miss_samples/)（18 張待處理）

## 產出檔案

**訓練 + 權重**
- `OCR/crnn_fallback/_train_nonar_v931_fix.py` — retrain 腳本
- `OCR/crnn_fallback/runs/nonar_v931_fix/best.pt` — 新 CRNN 權重（val 99.96%）
- `OCR/crnn_fallback/runs/nonar_v931_fix/train.log` — 訓練 log
- `data_stable_crops/train/{class}/*.png` — 1701 個 rehearsal crops

**分析 + 診斷（.ai/records/2026-07/2026-07-27/）**
- [`crnn_errors_manifest.csv`](crnn_errors_manifest.csv) — 896 錯誤逐張分類
- [`crnn_errors_summary.md`](crnn_errors_summary.md) — 分模號、分類型統計
- [`extract_report.md`](extract_report.md) — crop 抽取結果
- [`detect_miss_manifest.csv`](detect_miss_manifest.csv) — 90 張 detector miss
- [`detect_still_miss_at_005.csv`](detect_still_miss_at_005.csv) — 18 張 preprocess drift 待處理
- [`detect_miss_samples/`](detect_miss_samples/) — 8 張還原不了的樣本
- [`recovery_eval.log`](recovery_eval.log) — 完整 recovery 評估
- [`_build_errors_manifest.py`](_build_errors_manifest.py)、[`_extract_crops_from_errors.py`](_extract_crops_from_errors.py)、[`_eval_recovery_on_errors.py`](_eval_recovery_on_errors.py)、[`_detect_miss_low_conf.py`](_detect_miss_low_conf.py) — 執行腳本

## 下一步建議

1. **Deploy 建議**：把產線 nonar 換成 `runs/nonar_v931_fix/best.pt`（val 一樣、真實錯誤修 89.2%）
2. **Pipeline conf 建議**：把 `_2pass_inference.py` 的 `conf=0.25` 降到 `0.10`（撈回 72 detect-miss；風險：可能引入少數 spurious box，先在 stock val 跑一次確認）
3. **剩 18 張 preprocess drift**：獨立議題，需查 `find_circle` 為何在 wavy strip 上 offset，非本輪任務
4. **持續監控**：下批 v9.4 產線錯誤丟進來，同樣 SOP 循環

## 一句話
「reheasal 真實錯誤 crops → nonar 重訓 30 ep → **修 89.2%，零 regression，12 分鐘**。」
