# CRNN 效果測試報告 — nonar_v931_fix on 前處理區

**Weights**: `D:\incoming\Content_lens_OCR\OCR\crnn_fallback\runs\nonar_v931_fix_v2\best.pt` (ep 21, val 100.00%)
**Detector**: `D:\incoming\Content_lens_OCR\OCR\crnn_fallback\runs\detector\weights\best.pt` conf=0.1
**Source**: `D:\incoming\模號穴號-穩定圖片區\前處理區`

## Overall (per raw image, i.e. per p0/p90 pair)

| 指標 | 數字 |
|---|---|
| Total pairs (raw images) | 5675 |
| **Fully correct** | **5663/5675 (99.79%)** |
| Partial (mo only) | 4/5675 (0.07%) |
| Partial (xu only) | 8/5675 (0.14%) |
| Wrong (both) | 0/5675 (0.00%) |
| Detect-miss (any '?') | 0/5675 (0.00%) |
| Overlap w/ training (was in rehearsal) | 92/5675 (1.62%) |

## Per-mold accuracy

| mold | total | full_correct | acc% |
|---|---|---|---|
| M101 | 1350 | 1350 | 100.00% |
| M17 | 625 | 617 | 98.72% |
| M28 | 1350 | 1349 | 99.93% |
| M54 | 1000 | 998 | 99.80% |
| M83 | 1350 | 1349 | 99.93% |

## Errors by mold (top-20 confusions)

### M17

| gt | pred | count |
|---|---|---|
| M17-07 | M1-07 | 4 |
| M17-07 | 11-07 | 3 |
| M17-07 | 17-07 | 1 |

### M28

| gt | pred | count |
|---|---|---|
| M28-09 | M28-05 | 1 |

### M54

| gt | pred | count |
|---|---|---|
| M54-18 | M54-16 | 2 |

### M83

| gt | pred | count |
|---|---|---|
| M83-03 | M83-05 | 1 |

## 產出檔案結構

```
D:\incoming\crnn結果\v2/
├── report.md            ← 本檔
├── summary.csv          ← 每對 raw 圖的推論結果
├── errors_detail.csv    ← 每個錯誤的詳細（gt/pred/信心/路徑）
├── errors/{mold}/gt_{X}__pred_{Y}/*.png   ← 錯誤 strip 對備份
└── detect_miss/{mold}/*.png                  ← detector 沒抓到框的 strip
```

## Notes

- 每對 raw 圖有兩個 strip（p0 和 p90），採 2-pass per-head 選信心高者
- Detector conf 用 0.10（比產線 0.25 低，今日測得可撈回 80% detect-miss）
- Overlap 標示：92 對 raw 圖曾用於 rehearsal 訓練，其餘為完全 unseen
