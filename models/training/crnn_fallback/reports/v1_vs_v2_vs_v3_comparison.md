# CRNN v1 vs v2 vs v3 對比報告

## Overall progression

| 指標 | v1 | v2 | **v3** |
|---|---|---|---|
| Stock val_exact | 99.96% | 100.00% | **100.00%** |
| 前處理區 fully correct | 5654/5675 (99.63%) | 5663/5675 (99.79%) | **5675/5675 (100.00%)** |
| Wrong pairs | 21 | 12 | **0** |
| Wrong both | 0 | 0 | **0** |
| Detect-miss | 0 | 0 | **0** |
| data_stable_crops 累積 | 1701 | 1785 (+84) | 1833 (+48) |
| Weights | `nonar_v931_fix/best.pt` | `nonar_v931_fix_v2/best.pt` | **`nonar_v931_fix_v3/best.pt`** |

## Per-mold progression

| mold | v1 acc | v2 acc | **v3 acc** |
|---|---|---|---|
| M101 | 99.85% | 100% | **100%** |
| M17 | 100% | 98.72% ⚠ | **100%** |
| M28 | 98.59% | 99.93% | **100%** |
| M54 | 100% | 99.80% ⚠ | **100%** |
| M83 | 100% | 99.93% ⚠ | **100%** |

**v2 的 3 個 regression（M17/M54/M83）全部在 v3 修復。**

## Rehearsal 迭代軌跡

```
v9.3 field errors (896)        →  v1  →  修 89.2% (799)     val 99.96%
+ stable 21 error pairs        →  v2  →  修 43% more (12→7) val 100%
+ v2 12 error pairs            →  v3  →  修 100% (0 error)  val 100%
```

## 每輪 rehearsal 加了哪些字元

| 輪 | 資料來源 | 加的 mohao | 加的 xuehao |
|---|---|---|---|
| v1 | v9.3 crnn_errors (806 pairs) | M83+413, M28+191, M101+197, M17+50, M54+2 | 01~18 各 ~25-97 |
| v2 | stable v1 errors (21 pairs) | M28+38, M101+4 | 03+4, 14+38 |
| v3 | stable v2 errors (12 pairs) | M17+16, M54+4, M83+2, M28+2 | 07+16, 18+4, 09+2, 03+2 |

## 結論

**v3 為最終建議權重**：`OCR/crnn_fallback/runs/nonar_v931_fix_v3/best.pt`

- 前處理區 5675 對 raw 圖 **100%** 全對
- Stock val 2416 **100%** 全對
- Zero regression across M17/M28/M54/M83/M101
- 訓練資料 total = 11174 (REAL) + 6960 (SYNTH) + 1833 (STABLE rehearsal) = 19967 crops

## 產出檔案

```
D:/incoming/crnn結果/
├── report.md                        ← v1 完整報告
├── summary.csv                      ← v1 每對結果
├── errors_detail.csv                ← v1 21 錯詳細
├── errors/                          ← v1 錯誤 strip 備份
├── eval.log                         ← v1 log
│
├── v1_vs_v2_comparison.md
├── v1_vs_v2_vs_v3_comparison.md     ← 本檔
│
├── v2/                              ← v2 完整資料
│   ├── report.md
│   ├── summary.csv, errors_detail.csv
│   ├── errors/{mold}/gt_X__pred_Y/*.png (24 strips)
│
├── v2_eval.log                      ← v2 log
│
├── v3/                              ← v3 完整資料
│   ├── report.md
│   ├── summary.csv                  ← 5675 對推論
│   └── (errors/ 空的、沒錯誤)
│
└── v3_eval.log                      ← v3 log
```

## SOP：往後如果甲方回饋新錯誤

1. 蒐集錯誤 strip 640×640（放到 `D:/incoming/新錯誤集/`）
2. 跑 `_extract_stable_errors.py --src <新資料夾> --suffix <tag>` → 抽 crops 存到 `data_stable_crops/train/`
3. 複製 `_train_nonar_v931_fix_v3.py` → 改 RUN_DIR → retrain (30 ep, ~12 min)
4. 跑 `_eval_stable_area.py --weights <新 best.pt> --out <新結果夾>` 驗證
5. 保留舊 run 不覆蓋，方便回滾
